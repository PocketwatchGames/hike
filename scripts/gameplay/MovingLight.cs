using System;
using System.Collections.Generic;
using Godot;

// A moving light source (carried torch, etc.). On each voxel crossing it floods
// a fresh field (LightEngine.ComputeFloodField) and re-shades it per frame from
// the fractional position for smooth sub-voxel motion. Optional flicker re-scales
// the deposited footprint on a timer (cheap — no reflood/reshade).
[GlobalClass]
public partial class MovingLight : Node3D
{
    // This light's falloff. Distance (reach, voxels) + Falloff (curve shape) also
    // size its flood radius, so a tight torch floods a small, cheap ball;
    // Brightness is the open-space core intensity (1 ≈ white). See
    // LightEngine.ResolveTuning.
    [Export(PropertyHint.Range, "1,32,0.5")] public float Distance = 10f;
    [Export(PropertyHint.Range, "0.3,4,0.05")] public float Falloff = 1.25f;
    [Export(PropertyHint.Range, "0,3,0.01")] public float Brightness = 0.9f;
    [Export] public Color LightColor = new(1f, 0.75f, 0.4f);

    // Opt-in flicker — re-scales the deposited footprint by a random amplitude
    // in [FlickerMin, FlickerMax] every 1/FlickerHz seconds. Cheap: it only
    // re-deposits the cached footprint (O(footprint) array writes), no reflood
    // or reshade. Wider min↔max = more dramatic; 10–15 Hz reads as a flame.
    [Export] public bool Flicker = false;
    [Export(PropertyHint.Range, "0,2,0.01")] public float FlickerMin = 0.4f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float FlickerMax = 1.0f;
    [Export(PropertyHint.Range, "0.1,30,0.1")] public float FlickerHz = 12f;

    // Fade envelope — the deposited light ramps from 0→full over FadeInDuration
    // on Activate and full→0 over FadeOutDuration on Deactivate (then the deposit
    // is dropped). Set either to 0 for an instant snap. Combines multiplicatively
    // with flicker, so a torch still flickers while it fades in.
    [Export(PropertyHint.Range, "0,5,0.05")] public float FadeInDuration = 0.5f;
    [Export(PropertyHint.Range, "0,5,0.05")] public float FadeOutDuration = 0.5f;

    [Export] public bool Active { get; set; } = true;
    [Export] public PackedScene LightOnEffectScene;
    [Export] public PackedScene LightOffEffectScene;
    [Export] public PackedScene LightLoopEffectScene;

    private List<(Vector3I pos, ushort r, ushort g, ushort b)> _currentDeposit = new();
    // Cached flood field (reachable voxels + path optical depth), recomputed
    // only on voxel crossing. Re-shaded from the sub-voxel position each frame.
    private readonly List<(Vector3I pos, float opticalDepth, float ao)> _cells = new();
    // Resolved falloff + flood radius, recomputed alongside _cells on crossing
    // and reused by every per-frame reshade (must match the field's radius).
    private LightEngine.BlockLightTuning _tuning;
    private bool _registered;
    private Vector3I _lastVoxel;
    private Vector3 _lastShadeSub;
    // Effective amplitude currently applied to the deposit (the world holds
    // footprint × _amplitude = _fade × _flickerAmp). Only changed bracketed by
    // remove/deposit so the subtract always matches the add. 1 = full brightness.
    private float _amplitude = 1f;
    // Flicker (1 = no dim) and fade (0 = off, 1 = full) factors, multiplied into
    // _amplitude. _fade eases toward _fadeTarget over Fade{In,Out}Duration.
    private float _flickerAmp = 1f;
    private float _fade = 1f;
    private float _fadeTarget = 1f;
    // True while ramping out to zero before Cleanup — keeps the node registered
    // and processing so the fade can finish, then tears down.
    private bool _deactivating;
    // When true, the node QueueFrees itself once its fade-out lands (set by a
    // Deactivate(freeWhenDone: true) hand-off).
    private bool _freeOnFadeOut;
    private float _flickerTimer;
    private Fx _loopEffect;

    public override void _Ready()
    {
        // Deferred so Activate's Fx.Create calls run after the parent
        // (Mob / Player) finishes its own add_child cycle. Synchronous
        // invocation here triggers Godot's "Parent node is busy setting
        // up children" rejection — the Fx still ends up parented via
        // Fx.Create's deferred fallback, but the spurious error spams
        // the console at every spawn. Deferring the whole activation
        // keeps the log clean and registers the light one frame later,
        // which is invisible.
        if (Active) { CallDeferred(MethodName.Activate); }
    }

    public override void _PhysicsProcess(double delta)
    {
        using var _prof = Profiler.Sample("MovingLight.PhysicsProcess");

        if (Active && !_registered)
        {
            Activate();
            return;
        }
        if (!_registered) { return; }

        World world = World.Current;
        if (world == null) { return; }

        Vector3 pos = GlobalPosition;
        Vector3I voxel = new Vector3I(
            Mathf.FloorToInt(pos.X),
            Mathf.FloorToInt(pos.Y),
            Mathf.FloorToInt(pos.Z));
        Vector3 sub = new Vector3(pos.X - voxel.X, pos.Y - voxel.Y, pos.Z - voxel.Z);

        // The flood field (reachable set + optical depth) is keyed to the
        // integer voxel, so it only changes on crossing. The shade (falloff from
        // the fractional position) updates per frame for smooth sub-voxel motion.
        if (voxel != _lastVoxel)
        {
            // If the source voxel can't emit (carrier clipped into geometry, or
            // its chunk isn't resident yet), keep the previous field/deposit
            // rather than blanking the light. Don't advance _lastVoxel — retry
            // the recompute next frame until we land in an open voxel again.
            if (LightEngine.CanEmitFrom(world.WorldState, voxel))
            {
                _lastVoxel = voxel;
                using (Profiler.Sample("MovingLight.FloodRecompute"))
                {
                    _tuning = LightEngine.ResolveTuning(world.WorldState, Distance, Falloff, Brightness);
                    LightEngine.ComputeFloodField(world.WorldState, voxel, _tuning.FloodRadius, _cells);
                }
                Reshade(world.WorldState, pos);
                _lastShadeSub = sub;
            }
        }
        // Reshade on meaningful sub-voxel motion (smooth glide); skip below ~1/16
        // voxel where the deposit delta is below the LightMap's byte quantization.
        else
        {
            const float SHADE_MOTION_THRESHOLD = 1f / 16f;
            if (Mathf.Abs(sub.X - _lastShadeSub.X) >= SHADE_MOTION_THRESHOLD
                || Mathf.Abs(sub.Y - _lastShadeSub.Y) >= SHADE_MOTION_THRESHOLD
                || Mathf.Abs(sub.Z - _lastShadeSub.Z) >= SHADE_MOTION_THRESHOLD)
            {
                _lastShadeSub = sub;
                using (Profiler.Sample("MovingLight.Reshade"))
                {
                    Reshade(world.WorldState, pos);
                }
            }
        }

        // Flicker: re-scale the deposited footprint on a timer, independent of
        // motion (so it pulses while standing still). Just an O(footprint)
        // re-deposit at a new amplitude — no reflood, no reshade.
        if (Flicker && _currentDeposit.Count > 0)
        {
            _flickerTimer -= (float)delta;
            if (_flickerTimer <= 0f)
            {
                _flickerTimer = 1f / Mathf.Max(FlickerHz, 0.01f);
                using (Profiler.Sample("MovingLight.Flicker"))
                {
                    // Far lights hold steady — re-deposit only near the player,
                    // where flicker is visible. Carried torches sit at distance ~0
                    // so they always flicker.
                    float amp = WithinFlickerRange(world) ? (float)GD.RandRange(FlickerMin, FlickerMax) : 1f;
                    if (amp != _flickerAmp)
                    {
                        _flickerAmp = amp;
                        UpdateAmplitude(world.WorldState);
                    }
                }
            }
        }

        // Fade envelope: ease the deposit toward _fadeTarget over the tunable
        // duration (instant if the duration is 0). When a fade-out reaches zero,
        // finish the deferred teardown.
        if (_fade != _fadeTarget)
        {
            float dur = _fadeTarget > _fade ? FadeInDuration : FadeOutDuration;
            float step = dur > 0f ? (float)delta / dur : 1f;
            _fade = Mathf.MoveToward(_fade, _fadeTarget, step);
            UpdateAmplitude(world.WorldState);
        }
        if (_deactivating && _fade <= 0f)
        {
            FinishDeactivate();
        }
    }

    private bool WithinFlickerRange(World world)
    {
        Player p = world.player;
        if (p == null) { return true; }
        float cull = world.WorldState.SimData.BlockLightFlickerCullDistance;
        return (p.GlobalPosition - GlobalPosition).LengthSquared() <= cull * cull;
    }

    public void SetActive(bool active)
    {
        Active = active;
        if (active) { Activate(); } else { Deactivate(); }
    }

    public void Activate()
    {
        World world = World.Current;
        if (world == null) { return; }

        // Re-activated while still registered (toggled back on mid fade-out):
        // reverse the envelope toward full and keep the existing field/deposit.
        if (_registered)
        {
            if (_deactivating)
            {
                _deactivating = false;
                Active = true;
                _fadeTarget = 1f;
                if (LightOnEffectScene != null)
                {
                    Fx.Create(LightOnEffectScene, GetParent() ?? this, GlobalPosition);
                }
            }
            return;
        }

        Vector3 pos = GlobalPosition;
        var voxel = new Vector3I(
            Mathf.FloorToInt(pos.X),
            Mathf.FloorToInt(pos.Y),
            Mathf.FloorToInt(pos.Z));

        _lastVoxel = voxel;
        _registered = true;
        Active = true;
        _deactivating = false;
        // Start dark and ramp up — the per-frame envelope drives the fade-in.
        _flickerAmp = 1f;
        _fade = 0f;
        _fadeTarget = 1f;
        _amplitude = 0f;
        _flickerTimer = 0f;
        _tuning = LightEngine.ResolveTuning(world.WorldState, Distance, Falloff, Brightness);
        LightEngine.ComputeFloodField(world.WorldState, voxel, _tuning.FloodRadius, _cells);
        Reshade(world.WorldState, pos);
        _lastShadeSub = new Vector3(pos.X - voxel.X, pos.Y - voxel.Y, pos.Z - voxel.Z);

        if (LightOnEffectScene != null)
        {
            Fx.Create(LightOnEffectScene, GetParent() ?? this, GlobalPosition);
        }
        if (_loopEffect == null && LightLoopEffectScene != null)
        {
            _loopEffect = Fx.Create(LightLoopEffectScene, this, Vector3.Zero);
        }
    }

    // freeWhenDone lets a caller that owns the node lifecycle (the player-carried
    // torch, recreated per toggle) hand the node off to fade out and QueueFree
    // itself, instead of freeing it immediately and cutting the fade short.
    public void Deactivate(bool freeWhenDone = false)
    {
        if (_deactivating)
        {
            _freeOnFadeOut = _freeOnFadeOut || freeWhenDone;
            return;
        }
        if (!_registered)
        {
            if (freeWhenDone) { QueueFree(); }
            return;
        }
        _freeOnFadeOut = freeWhenDone;
        Active = false;
        _deactivating = true;
        _fadeTarget = 0f;
        // The off-cue fires as the light begins to die out.
        if (LightOffEffectScene != null)
        {
            Fx.Create(LightOffEffectScene, GetParent() ?? this, GlobalPosition);
        }
        // No fade window — drop the deposit immediately. Otherwise the per-frame
        // envelope ramps _fade to 0 and FinishDeactivate runs when it lands.
        if (FadeOutDuration <= 0f)
        {
            _fade = 0f;
            FinishDeactivate();
        }
    }

    // Completes a faded-out Deactivate: drops the deposit, stops the loop fx, and
    // frees the node if the caller handed off ownership via freeWhenDone.
    private void FinishDeactivate()
    {
        _deactivating = false;
        Cleanup();
        if (_freeOnFadeOut)
        {
            _freeOnFadeOut = false;
            QueueFree();
        }
    }

    public override void _ExitTree()
    {
        // Tear down state without spawning the LightOff transition fx —
        // we're already leaving the tree and Fx.Create's AddChild would
        // either fail outright or leak a node into a dying parent. The
        // off-cue is the explicit Deactivate caller's responsibility.
        Cleanup();
    }

    // Drops the light deposit from the world and stops the loop fx without
    // firing any transition cue. Shared by Deactivate (player-initiated)
    // and _ExitTree (despawn / shutdown).
    private void Cleanup()
    {
        if (!_registered) { return; }
        World world = World.Current;
        if (world != null)
        {
            RemoveCurrentDeposit(world.WorldState);
        }
        _currentDeposit.Clear();
        _registered = false;
        Active = false;
        _deactivating = false;
        if (_loopEffect != null)
        {
            _loopEffect.Stop();
            _loopEffect = null;
        }
    }

    // Remove the previous deposit, re-shade the cached flood field from the
    // light's current (fractional) position, and deposit the result. Cheap
    // enough to run per frame — no re-flooding, just the falloff evaluation.
    private void Reshade(WorldState worldState, Vector3 pos)
    {
        RemoveCurrentDeposit(worldState);
        LightEngine.ShadeFloodField(
            worldState, _cells, pos, LightColor, _tuning, _currentDeposit, out _, out _);
        DepositScaled(worldState);
    }

    // Re-apply the deposit at the current effective amplitude (fade × flicker),
    // bracketed remove→deposit so the subtract always matches the prior add.
    private void UpdateAmplitude(WorldState worldState)
    {
        float amp = _fade * _flickerAmp;
        if (amp == _amplitude) { return; }
        RemoveCurrentDeposit(worldState);
        _amplitude = amp;
        DepositScaled(worldState);
    }

    // Deposit the cached footprint scaled by the current effective amplitude.
    private void DepositScaled(WorldState worldState)
    {
        // Moving-light re-deposit volume per window (reshades + flicker rolls).
        Profiler.IncrementCounter("light_deposit_voxels", _currentDeposit.Count);
        for (int i = 0; i < _currentDeposit.Count; i++)
        {
            var (pos, r, g, b) = _currentDeposit[i];
            worldState.AddBlockLightWorld(pos.X, pos.Y, pos.Z, ScaleAmp(r), ScaleAmp(g), ScaleAmp(b));
        }
    }

    private void RemoveCurrentDeposit(WorldState worldState)
    {
        for (int i = 0; i < _currentDeposit.Count; i++)
        {
            var (pos, r, g, b) = _currentDeposit[i];
            worldState.SubtractBlockLightWorld(pos.X, pos.Y, pos.Z, ScaleAmp(r), ScaleAmp(g), ScaleAmp(b));
        }
    }

    private ushort ScaleAmp(ushort v)
    {
        int s = (int)(v * _amplitude + 0.5f);
        return (ushort)Math.Min(ushort.MaxValue, s);
    }
}
