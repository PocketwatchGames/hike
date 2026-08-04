using Godot;
using System.Collections.Generic;

// Drives all wind-blown ambient particles: leaves shed from trees, sand kicked
// up over open desert ground, and foam blown off water. One manager lives in
// the game scene.
//
// Two source models:
//   - LEAVES are authored per tree. Each tree scene holds a lightweight
//     WindEmitterSource (position + a leaf emitter scene). The manager pools a
//     handful of real GpuParticles3D per distinct leaf scene and leases them to
//     the nearest in-range sources. Live particle-system count stays flat no
//     matter how many (batch-rendered) trees are resident.
//   - SAND and FOAM are procedural — no authored nodes. The manager owns a pool
//     for each and repositions them over the matching voxel surface near the
//     player at irregular intervals (sand over VoxelType.Desert, foam over
//     VoxelType.Water), the way RainEffect disc-scatters ground splashes.
//
// All three share one gate: active only when windSpeed > WindThreshold AND
// rainAmount < RainSuppressThreshold; emission frequency (AmountRatio) scales
// with wind speed. Wind/rain/direction are read from SkyController / WorldState
// exactly as RainEffect does.
[GlobalClass]
public partial class WindParticleManager : Node3D
{
    public static WindParticleManager Current { get; private set; }

    // Procedural emitter scenes (roots = GPUParticles3D). Null disables that kind.
    [Export] public PackedScene SandEmitterScene { get; set; }
    [Export] public PackedScene FoamEmitterScene { get; set; }

    // Wind speed (m/s) at/below which nothing emits, and the speed at which
    // emission frequency saturates. windFactor lerps between the two.
    [Export] public float WindThreshold { get; set; } = 6f;
    [Export] public float WindFullSpeed { get; set; } = 18f;
    // Foam needs more wind than leaves/sand — it only appears once wind clears
    // this. ~10 m/s matches the water shader's rippleWindRef, where wave /
    // whitecap energy saturates (WeatherDerivation.RippleWindRef).
    [Export] public float FoamWindThreshold { get; set; } = 10f;
    // Single GLOBAL on-screen burst frequency (bursts/second across ALL kinds):
    // the rate at a steady breeze (windFactor 0) and at full wind (windFactor 1).
    // Each leased emitter is a one-shot burst; the manager fires one random
    // leased emitter each time the budget tops 1, so frequency is decoupled from
    // particle lifetime and bursts naturally desync.
    [Export] public float BurstsPerSecondMin { get; set; } = 0.7f;
    [Export] public float BurstsPerSecondMax { get; set; } = 1.6f;
    // Rain dampens leaves + sand progressively (rate scales by
    // 1 - rain/RainSuppressThreshold) rather than hard-cutting, reaching zero
    // at RainSuppressThreshold. Foam ignores rain entirely (whitecaps in storms).
    [Export] public float RainSuppressThreshold { get; set; } = 0.2f;
    // Max world distance from the player a source / scatter point can be and
    // still get a pooled emitter.
    [Export] public float LeaseRange { get; set; } = 22f;
    // Pool size per distinct emitter scene. Caps the live particle-system count.
    [Export] public int MaxEmittersPerScene { get; set; } = 6;
    // Leaf lease re-evaluation cadence (s). The wind/rain gate runs every frame.
    [Export] public float ReevaluateInterval { get; set; } = 0.25f;
    // Sand/foam reposition interval range (s) — the "irregular intervals" look.
    [Export] public float ScatterRepositionMin { get; set; } = 1.5f;
    [Export] public float ScatterRepositionMax { get; set; } = 4.0f;
    // Skip sources / scatter points not open to the sky.
    [Export] public bool SuppressIndoors { get; set; } = true;
    // Column scan extent (voxels) above/below the player when locating a
    // sand/foam surface under a sampled XZ.
    [Export] public int SurfaceScanUp { get; set; } = 24;
    [Export] public int SurfaceScanDown { get; set; } = 24;

    private class PooledEmitter
    {
        public GpuParticles3D Node;
        public ParticleProcessMaterial Proc;
        public bool Leased;
        // True for leaves/sand (rain-dampened), false for foam (rain-exempt).
        public bool RainGated;
        public float RepositionTimer;
    }

    // Authored leaf sources, registered as their tree scenes load.
    private readonly List<WindEmitterSource> _leafSources = new();
    // One pool per distinct leaf emitter scene seen among sources.
    private readonly Dictionary<PackedScene, List<PooledEmitter>> _leafPools = new();
    private List<PooledEmitter> _sandPool;
    private List<PooledEmitter> _foamPool;

    private readonly List<WindEmitterSource> _scratch = new();
    private readonly List<PooledEmitter> _burstScratch = new();
    private readonly RandomNumberGenerator _rng = new();
    private float _reevalAccum;
    // Single global fractional burst budget (accumulates at bursts/sec, fires on >=1).
    private float _globalBudget;

    public override void _Ready()
    {
        Current = this;
        _rng.Randomize();
    }

    public override void _ExitTree()
    {
        if (Current == this) { Current = null; }
    }

    public void RegisterSource(WindEmitterSource src)
    {
        if (src != null && !_leafSources.Contains(src)) { _leafSources.Add(src); }
    }

    public void UnregisterSource(WindEmitterSource src)
    {
        _leafSources.Remove(src);
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("WindParticleManager.Process");

        float dt = (float)delta;
        SkyController sky = SkyController.Current;
        Sim sim = Sim.Current;
        WorldState ws = sim?.WorldState;
        Node3D player = sim?.player;
        if (sky?.Weather == null || ws == null || player == null)
        {
            DebugLog(dt, "world not ready (sky/worldstate/player null)");
            ParkAll();
            return;
        }

        float forced = CVars.windForce.Value;
        float wind = forced >= 0f ? forced : sky.Weather.windSpeed;
        float rain = sky.Weather.rainAmount;
        // Wind is the base requirement for every kind. Rain additionally
        // suppresses leaves and sand (wet ground / soggy leaves don't blow),
        // but NOT foam — wind whips up whitecaps in a storm too, so foam stays
        // active whenever it's windy.
        bool windActive = CVars.fxParticles.Value && wind > WindThreshold;
        if (!windActive)
        {
            DebugLog(dt, $"idle: wind={wind:0.0} <= threshold={WindThreshold} "
                       + $"(fxParticles={CVars.fxParticles.Value}, sources={_leafSources.Count})");
            ParkAll();
            return;
        }
        float windFactor = Mathf.Clamp(
            (wind - WindThreshold) / Mathf.Max(0.01f, WindFullSpeed - WindThreshold), 0f, 1f);
        // Single GLOBAL on-screen burst rate from wind (one rate across all
        // kinds, not per-kind). rainDamp ramps 1 -> 0 across
        // [0, RainSuppressThreshold]; it's applied per-candidate so leaves/sand
        // thin out with rain while foam (rain-exempt) keeps going.
        float baseRate = Mathf.Lerp(BurstsPerSecondMin, BurstsPerSecondMax, windFactor);
        float rainDamp = Mathf.Clamp(1f - rain / Mathf.Max(0.0001f, RainSuppressThreshold), 0f, 1f);

        // Downwind direction (horizontal), shared by all emitters this frame.
        Vector3 wd = ws.WindDirection;
        var wxz = new Vector2(wd.X, wd.Z);
        Vector3 dir = wxz.LengthSquared() > 1e-4f
            ? new Vector3(wxz.X, 0f, wxz.Y).Normalized()
            : new Vector3(0f, 0f, 1f);
        Vector3 pp = player.GlobalPosition;

        _reevalAccum += dt;
        bool reeval = _reevalAccum >= ReevaluateInterval;
        if (reeval) { _reevalAccum = 0f; }

        // Position emitters at their lease targets (trees / scatter points).
        if (reeval)
        {
            EnsureLeafPools();
            foreach (var kv in _leafPools)
            {
                AssignLeafLeases(kv.Key, kv.Value, pp, ws);
            }
        }
        UpdateScatter(ref _sandPool, SandEmitterScene, true, pp, ws, dt);
        UpdateScatter(ref _foamPool, FoamEmitterScene, false, pp, ws, dt);

        // Keep drift direction current on every pooled emitter.
        ApplyDirection(dir);

        // Build the combined leased-candidate set for the global burst budget.
        // Leaves + sand are rain-gated; foam is rain-exempt but only joins once
        // wind clears FoamWindThreshold (matches where the water shader's
        // wave/whitecap energy becomes meaningful — rippleWindRef ~10 m/s).
        _burstScratch.Clear();
        foreach (var kv in _leafPools) { CollectLeased(kv.Value, _burstScratch, true); }
        CollectLeased(_sandPool, _burstScratch, true);
        if (wind > FoamWindThreshold) { CollectLeased(_foamPool, _burstScratch, false); }
        FireBursts(_burstScratch, ref _globalBudget, baseRate, rainDamp, dt);

        if (CVars.windParticleDebug.Value)
        {
            int leaf = 0;
            foreach (var kv in _leafPools) { leaf += CountLeased(kv.Value); }
            DebugLog(dt, $"active: wind={wind:0.0} factor={windFactor:0.00} rate={baseRate:0.00}/s "
                       + $"rain={rain:0.00} rainDamp={rainDamp:0.00} | "
                       + $"leafLeased={leaf}/{_leafSources.Count}src "
                       + $"sand={CountLeased(_sandPool)} foam={CountLeased(_foamPool)}");
        }
    }

    // Accumulate the single global budget and fire one random leased emitter
    // each time it crosses 1. Rain-gated candidates (leaves/sand) are skipped
    // with probability (1 - rainDamp), so heavier rain progressively drops their
    // bursts while foam keeps emitting. Capped so a hitch can't dump a backlog.
    private void FireBursts(List<PooledEmitter> leased, ref float budget, float rate, float rainDamp, float dt)
    {
        if (leased.Count == 0 || rate <= 0f) { budget = 0f; return; }
        budget += rate * dt;
        if (budget > 4f) { budget = 4f; }
        while (budget >= 1f)
        {
            budget -= 1f;
            PooledEmitter e = leased[_rng.RandiRange(0, leased.Count - 1)];
            if (e.RainGated && _rng.Randf() >= rainDamp) { continue; }
            e.Node.Restart();
        }
    }

    private static void CollectLeased(List<PooledEmitter> pool, List<PooledEmitter> into, bool rainGated)
    {
        if (pool == null) { return; }
        foreach (PooledEmitter e in pool)
        {
            if (!e.Leased) { continue; }
            e.RainGated = rainGated;
            into.Add(e);
        }
    }

    private float _dbgAccum;

    // Throttled debug line (once/sec) gated on the wind_particle_debug CVar.
    private void DebugLog(float dt, string msg)
    {
        if (!CVars.windParticleDebug.Value) { _dbgAccum = 0f; return; }
        _dbgAccum += dt;
        if (_dbgAccum < 1f) { return; }
        _dbgAccum = 0f;
        GD.Print("[windfx] " + msg);
    }

    private static int CountLeased(List<PooledEmitter> pool)
    {
        if (pool == null) { return 0; }
        int n = 0;
        foreach (PooledEmitter e in pool) { if (e.Leased) { n++; } }
        return n;
    }

    private void EnsureLeafPools()
    {
        for (int i = 0; i < _leafSources.Count; i++)
        {
            PackedScene scene = _leafSources[i]?.EmitterScene;
            if (scene == null || _leafPools.ContainsKey(scene)) { continue; }
            _leafPools[scene] = BuildPool(scene);
        }
    }

    private List<PooledEmitter> BuildPool(PackedScene scene)
    {
        var list = new List<PooledEmitter>(MaxEmittersPerScene);
        for (int i = 0; i < MaxEmittersPerScene; i++)
        {
            var node = scene.Instantiate<GpuParticles3D>();
            AddChild(node);
            // One-shot emitters: dormant until the manager Restart()s them.
            node.Emitting = false;
            list.Add(new PooledEmitter
            {
                Node = node,
                Proc = node.ProcessMaterial as ParticleProcessMaterial,
                Leased = false,
                RepositionTimer = 0f,
            });
        }
        return list;
    }

    private void AssignLeafLeases(PackedScene scene, List<PooledEmitter> pool, Vector3 pp, WorldState ws)
    {
        _scratch.Clear();
        float rangeSq = LeaseRange * LeaseRange;
        for (int i = 0; i < _leafSources.Count; i++)
        {
            WindEmitterSource s = _leafSources[i];
            if (s == null || !IsInstanceValid(s) || s.EmitterScene != scene) { continue; }
            Vector3 sp = s.GlobalPosition;
            if (pp.DistanceSquaredTo(sp) > rangeSq) { continue; }
            if (SuppressIndoors && ws.GetSkyLight01(sp) <= 0f) { continue; }
            _scratch.Add(s);
        }
        _scratch.Sort((a, b) =>
            pp.DistanceSquaredTo(a.GlobalPosition).CompareTo(pp.DistanceSquaredTo(b.GlobalPosition)));

        for (int i = 0; i < pool.Count; i++)
        {
            PooledEmitter e = pool[i];
            if (i < _scratch.Count)
            {
                e.Node.GlobalPosition = _scratch[i].GlobalPosition;
                e.Leased = true;
            }
            else
            {
                e.Leased = false;
            }
        }
    }

    // sand=true scatters over sand ground (resolved via GroundTypeResolver —
    // desert ground is VoxelType.Terrain with a desert terrain id, NOT
    // VoxelType.Desert, so a raw voxel-type check would never match). sand=false
    // is foam, which sits on top of water voxels.
    private void UpdateScatter(ref List<PooledEmitter> pool, PackedScene scene, bool sand,
                               Vector3 pp, WorldState ws, float dt)
    {
        if (scene == null) { return; }
        if (pool == null) { pool = BuildPool(scene); }

        foreach (PooledEmitter e in pool)
        {
            e.RepositionTimer -= dt;
            if (e.Leased && e.RepositionTimer > 0f) { continue; }

            bool placed = false;
            for (int attempt = 0; attempt < 6 && !placed; attempt++)
            {
                float r = LeaseRange * Mathf.Sqrt(_rng.Randf());
                float th = _rng.Randf() * Mathf.Tau;
                float x = pp.X + r * Mathf.Cos(th);
                float z = pp.Z + r * Mathf.Sin(th);
                if (!TryFindSurface(ws, x, z, pp.Y, out Vector3 hit, out VoxelType topType)) { continue; }

                bool match = sand
                    ? GroundTypeResolver.Resolve(ws, hit) == EGroundType.Sand
                    : topType == VoxelType.Water;
                if (!match) { continue; }
                if (SuppressIndoors && ws.GetSkyLight01(hit) <= 0f) { continue; }

                e.Node.GlobalPosition = hit;
                e.Leased = true;
                placed = true;
            }
            if (!placed) { e.Leased = false; }
            e.RepositionTimer = _rng.RandfRange(ScatterRepositionMin, ScatterRepositionMax);
        }
    }

    // Scan the column at (x,z) top-down; the first non-air voxel is the surface.
    // Returns the world point on its top face plus that voxel's type. The caller
    // decides whether it's a valid surface for the variant.
    private bool TryFindSurface(WorldState ws, float x, float z, float centerY, out Vector3 pos, out VoxelType topType)
    {
        int wx = Mathf.FloorToInt(x);
        int wz = Mathf.FloorToInt(z);
        int top = Mathf.FloorToInt(centerY) + SurfaceScanUp;
        int bottom = Mathf.FloorToInt(centerY) - SurfaceScanDown;
        for (int y = top; y >= bottom; y--)
        {
            VoxelType t = ws.GetVoxelWorld(wx, y, wz);
            if (t == VoxelType.Air) { continue; }
            pos = new Vector3(x, y + 1f, z);
            topType = t;
            return true;
        }
        pos = default;
        topType = VoxelType.Air;
        return false;
    }

    // Keep every pooled emitter's drift direction current. Bursts are fired
    // separately via the per-kind budget, so this only touches direction.
    private void ApplyDirection(Vector3 dir)
    {
        foreach (var kv in _leafPools) { DirectionPool(kv.Value, dir); }
        DirectionPool(_sandPool, dir);
        DirectionPool(_foamPool, dir);
    }

    private static void DirectionPool(List<PooledEmitter> pool, Vector3 dir)
    {
        if (pool == null) { return; }
        foreach (PooledEmitter e in pool)
        {
            if (e.Proc != null) { e.Proc.Direction = dir; }
        }
    }

    // Release all leases (no new bursts fire). In-flight one-shot bursts finish
    // naturally; budgets reset so activation doesn't dump a backlog.
    private void ParkAll()
    {
        foreach (var kv in _leafPools) { ParkPool(kv.Value); }
        ParkPool(_sandPool);
        ParkPool(_foamPool);
        _globalBudget = 0f;
    }

    private static void ParkPool(List<PooledEmitter> pool)
    {
        if (pool == null) { return; }
        foreach (PooledEmitter e in pool) { e.Leased = false; }
    }
}
