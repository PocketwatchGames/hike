using System;
using Godot;

// Central hub for all world simulation. The class is split across several
// partial files by concern:
//   World.cs                 — this file: lifecycle/orchestration + owned sub-objects
//   World.EntityStreaming.cs — chunk-driven entity load/unload + the spawn queue
//   World.SpawnLifecycle.cs  — spawn-condition gating + day/night refresh + cleanup
//   World.Spawning.cs        — loot / drop / footprint spawn factories
//   World.Environment.cs     — weather + voxel-light sampling queries
//   World.Chunks.cs          — thin delegation to ChunkManager (lighting, fog, coords)
// A few self-contained pieces live as their own classes that World owns:
//   FoliageCutawayProbe, PathBlockerGrid, and the static WorldBoundary helper.
public partial class World : Node3D
{
    // Reference to the active world, used by static contexts (CVars, etc.)
    // that need to reach into the running game without a node-tree lookup.
    // Set in Initialize, cleared on tree exit. Only one game world is active
    // at a time so a single static slot is sufficient.
    public static World Current { get; private set; }

    public SimData SimData => _worldState.SimData;
    public WorldState WorldState => _worldState;
    public ulong GameTimeMs => _worldState.GameTimeMs;
    public double TimeOfDayAbsolute => _worldState.TimeOfDayAbsolute;

    // Halts the per-frame day/night clock advance in Tick while the player rests
    // at a camp (set by CampScreen). The sim clock (GameTimeMs) and sleep's
    // AdvanceTime skip are unaffected — only the ambient time-of-day holds.
    public bool TimeOfDayFrozen;

    // Spatial hash for cheap "mobs within radius" queries — used by
    // separation steering and (later) encircle-slot allocation. Lives on
    // World rather than each Mob so multiple consumers share one index.
    private readonly MobSpatialHash _mobSpatialHash = new();
    public MobSpatialHash MobSpatialHash => _mobSpatialHash;

    // Registry of perch markers (landing spots on props/interactives). Perch
    // nodes self-register on tree-enter and unregister on tree-exit, so this
    // tracks exactly the perches in currently-loaded chunks. Flying mobs query
    // it to pick a place to land when fleeing.
    private readonly PerchRegistry _perches = new();
    public PerchRegistry Perches => _perches;

    // Registry of in-flight projectiles. Projectiles self-register on tree-enter
    // and unregister on tree-exit. Mobs query it to react to incoming shots
    // (the dodge / perch-flee reaction).
    private readonly ProjectileRegistry _projectiles = new();
    public ProjectileRegistry Projectiles => _projectiles;

    // Coordinator for "where should each mob stand around the player /
    // other targets" — hands out angular standoff slots so a swarm fans
    // out instead of stacking. Slots are leased per-mob and survive
    // across repaths; explicit Release on aggro-loss / death.
    private readonly EncircleSlotAllocator _encircleAllocator = new();
    public EncircleSlotAllocator EncircleAllocator => _encircleAllocator;

    // Per-frame foliage-occlusion probe driving the canopy cutaway. Owned here
    // (constructed in Initialize once WorldState exists) so it shares the live
    // entity index; GameClient reads it via FadeProbe.
    private FoliageCutawayProbe _fadeProbe;
    public FoliageCutawayProbe FadeProbe => _fadeProbe;

    private WorldState _worldState;
    private ChunkManager _chunkManager;
    private WorldDetailScatter _detailScatter;
    private WorldPropScatter _propScatter;
    private FootprintScatter _footprintScatter;
    private GroundShadowScatter _groundShadowScatter;
    private AmbienceController _ambienceController;
    private ThunderScheduler _thunderScheduler;
    private LightningFlasher _lightningFlasher;
    private WeatherLightningSpawner _weatherLightningSpawner;
    private ChunkAmbienceSpawner _chunkAmbienceSpawner;
    private Minimap _minimap;
    public Minimap Minimap => _minimap;
    private HeatField _heatField;
    public HeatField HeatField => _heatField;
    private GameCamera _camera;
    public GameCamera Camera => _camera;
    private Player _player;
    private Vector3I _lastEntityChunkCoord;

    // Global manager for per-chunk detail-sprite scatter. Replaces the prior
    // per-chunk MultiMeshInstance3D layout with one MultiMesh per DetailEntry,
    // world-wide. Chunks post their contributions via SetChunk and clear them
    // via RemoveChunk on eviction.
    public WorldDetailScatter DetailScatter => _detailScatter;
    public ChunkManager ChunkManager => _chunkManager;

    // Global manager for static-prop sprite multimeshes. Each
    // MultimeshPropSprite registers itself in _Ready and unregisters in
    // _ExitTree, so the manager stays consistent with the active prop set
    // through chunk eviction without an explicit chunk-coord index.
    public WorldPropScatter PropScatter => _propScatter;

    // Batched renderer for transient footprint ground marks — one MultiMesh
    // per actor footprint texture, owning its own per-print lifetime fade and
    // mob-print discovery gate. World.SpawnFootprint routes prints to it.
    public FootprintScatter FootprintScatter => _footprintScatter;

    public Player player => _player;

    // The player's active companion (pet), if one is currently spawned. A Mob
    // registers here when it becomes tamed (Mob.Tame, or on spawn if it spawned
    // pre-tamed) so player command input (follow/stay toggle) can reach it
    // without a scene-tree search.
    private Mob _companion;
    public Mob Companion => _companion;
    public void RegisterCompanion(Mob companion) => _companion = companion;
    public void UnregisterCompanion(Mob companion)
    {
        if (_companion == companion)
        {
            _companion = null;
        }
    }

    public void Initialize(WorldState worldState, Vector3 spawnPosition, GameCamera camera, ShaderMaterial fogMaterial, Func<Vector3> getPlayerPosition)
    {
        _worldState = worldState;
        _camera = camera;
        _lastEntityChunkCoord = WorldToChunkCoord(spawnPosition);
        _wasNight = WorldState.IsNight(worldState.TimeOfDay01);
        _fadeProbe = new FoliageCutawayProbe(worldState);

        // Set Current BEFORE constructing children that may dereference it.
        // ChunkManager.Initialize triggers synchronous chunk builds which call
        // World.Current?.DetailScatter?.SetChunk — if Current is still null
        // those scatter posts are silently dropped and the initial chunk
        // load's detail sprites never appear.
        Current = this;

        _detailScatter = new WorldDetailScatter();
        _detailScatter.Name = "DetailScatter";
        AddChild(_detailScatter);

        _propScatter = new WorldPropScatter();
        _propScatter.Name = "PropScatter";
        AddChild(_propScatter);

        _footprintScatter = new FootprintScatter();
        _footprintScatter.Name = "FootprintScatter";
        AddChild(_footprintScatter);

        _groundShadowScatter = new GroundShadowScatter();
        _groundShadowScatter.Name = "GroundShadowScatter";
        AddChild(_groundShadowScatter);

        _chunkManager = new ChunkManager();
        AddChild(_chunkManager);
        _chunkManager.onChunkLoaded += OnChunkLoaded;
        _chunkManager.onChunkUnloaded += OnChunkUnloaded;
        _chunkManager.Initialize(worldState, spawnPosition, camera, fogMaterial, getPlayerPosition);

        // Spawned programmatically here rather than authored into game.tscn
        // because AmbienceController is pure logic — no [Export] node refs
        // to wire and no audio assets yet.
        _ambienceController = new AmbienceController();
        _ambienceController.Name = "AmbienceController";
        AddChild(_ambienceController);

        // Lightning flash intensity producer. Must exist before
        // ThunderScheduler so the first strike's TriggerFlash hits a
        // live LightningFlasher.Current. SkyController reads
        // LightningFlasher.Current.Intensity each frame to boost
        // directional light energy + blank cloud-shadow attenuation.
        _lightningFlasher = new LightningFlasher();
        _lightningFlasher.Name = "LightningFlasher";
        AddChild(_lightningFlasher);

        // Distant rolling-thunder scheduler. Reads
        // AmbienceController.Current.State.LightningIntensity, fires
        // one-shot far-thunder claps at exponentially-jittered intervals
        // proportional to that intensity. Triggers a LightningFlasher
        // flash NOW on every strike and queues the audible clap to fire
        // after a per-strike audio-visual lag. Dormant when SimData has
        // no thunder data wired up.
        _thunderScheduler = new ThunderScheduler();
        _thunderScheduler.Name = "ThunderScheduler";
        AddChild(_thunderScheduler);

        // Damaging lightning strikes around the player. Reads the
        // same AmbienceController lightning intensity ThunderScheduler
        // does, but spawns LightningStrike entities on a separate
        // (much rarer) cadence — distant rumbles for atmosphere vs
        // near strikes for gameplay. Dormant when SimData has no
        // weatherLightning data wired up.
        _weatherLightningSpawner = new WeatherLightningSpawner();
        _weatherLightningSpawner.Name = "WeatherLightningSpawner";
        AddChild(_weatherLightningSpawner);

        _chunkAmbienceSpawner = new ChunkAmbienceSpawner();
        _chunkAmbienceSpawner.Name = "ChunkAmbienceSpawner";
        AddChild(_chunkAmbienceSpawner);
        _chunkAmbienceSpawner.Bind(this);

        // Minimap and HeatField are authored as embedded child scenes under
        // GameClient in game.tscn (so their tuning is inspector-visible); World
        // just references and initializes them, it doesn't own their lifetime.
        GameClient gc = GameClient.Current;
        _minimap = gc?.minimap;
        _minimap?.Initialize(this);

        _heatField = gc?.heatField;
        _heatField?.Initialize(this);

        WorldBoundary.Create(this, _worldState);
    }

    public override void _ExitTree()
    {
        if (Current == this)
        {
            Current = null;
        }
    }

    public void SetPlayer(Player player)
    {
        _player = player;
        Vector3I center = WorldToChunkCoord(_player.GlobalPosition);
        _lastEntityChunkCoord = center;
        RebuildDesiredEntityChunks(center);
        SyncEntitiesToDesired();
        // Bring the persistent companion into the world once the spawn sphere's
        // collision is ready (GameClient gates SetPlayer on IsSpawnChunkReady).
        SpawnPersistentEntities();
    }

    // Advances simulation time. Called by GameClient each unpaused frame so the
    // sim clock freezes when the game is paused. Persistent storage lives in
    // WorldState so the clock survives save/load.
    public void Tick(double delta)
    {
        _worldState.GameTimeMs += (ulong)(delta * 1000.0);

        // Advance normalized time-of-day. time_scale lets the player
        // fast-forward the cycle without disturbing GameTimeMs (which
        // drives cooldowns and AI timers that should stay at real speed).
        // Frozen while the player rests at a camp (CampScreen sets the flag) so
        // the day/night clock holds. Sleeping still advances time — that runs
        // through AdvanceTime, not this per-frame path.
        float dayLength = _worldState.SimData?.dayLengthSeconds ?? 600f;
        if (dayLength > 0f && !TimeOfDayFrozen)
        {
            double todDelta = delta * CVars.timeScale.Value / dayLength;
            _worldState.TimeOfDayAbsolute += todDelta;
            double tod = _worldState.TimeOfDay01 + todDelta;
            tod -= System.Math.Floor(tod);
            _worldState.TimeOfDay01 = tod;
        }

        bool isNight = WorldState.IsNight(_worldState.TimeOfDay01);
        if (isNight != _wasNight)
        {
            _wasNight = isNight;
            RefreshTimeOfDayEntities();
        }

        // Complement to RefreshTimeOfDayEntities: periodically despawn loaded
        // mobs whose spawn conditions have lapsed. Runs on an interval (not the
        // night edge) because weather-gated conditions (Clear / NotHeavyRain)
        // drift continuously, not just at dawn/dusk.
        float cleanupInterval = _worldState.SimData?.spawnCleanupIntervalSeconds ?? 2f;
        _spawnCleanupAccumulator += (float)delta;
        if (_spawnCleanupAccumulator >= cleanupInterval)
        {
            _spawnCleanupAccumulator = 0f;
            CleanupOffConditionMobs();
        }

        // Record the player's path, then leash the persistent companion: a
        // following pet that fell outside the loaded world snaps onto a recent
        // off-screen footstep; a stay-commanded one freezes until its chunk
        // reloads (see TickCompanionRescueHistory / TickCompanionLeash).
        TickCompanionRescueHistory((float)delta);
        TickCompanionLeash((float)delta);

        _heatField?.Tick();
    }

    // Sleep / rest time-skip. Fast-forwards the simulation by `hours` in-world
    // hours in a single call (vs Tick's per-frame slices), replaying the same
    // status-effect tick path so timed effects expire, "till sunrise" boons
    // lapse, and damage-over-time integrates over the skipped span. The player
    // is advanced in one-second steps so an integrated DoT that turns lethal
    // stops the skip at the instant of death — the player wakes (or dies) at
    // that time rather than sleeping through the full duration. Loaded mobs are
    // caught up in a single bulk step over the time the player actually
    // survived. Returns the in-world hours actually advanced (< `hours` only if
    // the player died mid-skip).
    public double AdvanceTime(double hours)
    {
        if (hours <= 0.0 || _player == null || _worldState == null)
        {
            return 0.0;
        }

        // GameTimeMs and the day cycle advance together during normal play at a
        // rate of DayLengthSeconds real-seconds per in-world day, so a 6-hour
        // skip is (6/24) * DayLengthSeconds of GameTimeMs — keeping the two
        // clocks consistent with how Tick advances them at timeScale 1. Status
        // durations are authored against GameTimeMs, so they age by this amount.
        float dayLength = _worldState.SimData?.dayLengthSeconds ?? 600f;
        double totalSeconds = dayLength > 0f ? hours / 24.0 * dayLength : 0.0;
        bool wasNight = WorldState.IsNight(_worldState.TimeOfDay01);

        const double stepSeconds = 1.0;
        double advanced = 0.0;
        while (advanced < totalSeconds && !_player.IsDead)
        {
            double step = System.Math.Min(stepSeconds, totalSeconds - advanced);
            AdvanceClocks(step, dayLength);
            _player.TickStatusEffects((float)step);
            advanced += step;
        }

        // Catch every loaded mob up over the span the player actually survived.
        // A DoT that kills a mob here runs its normal death cascade inside Tick.
        foreach (Mob mob in GetEntities<Mob>())
        {
            mob.TickStatusEffects((float)advanced);
        }

        bool isNight = WorldState.IsNight(_worldState.TimeOfDay01);
        if (isNight != wasNight)
        {
            _wasNight = isNight;
            RefreshTimeOfDayEntities();
        }
        CleanupOffConditionMobs();
        return dayLength > 0f ? advanced / dayLength * 24.0 : 0.0;
    }

    private void AdvanceClocks(double seconds, float dayLength)
    {
        _worldState.GameTimeMs += (ulong)(seconds * 1000.0);
        if (dayLength > 0f)
        {
            double todDelta = seconds / dayLength;
            _worldState.TimeOfDayAbsolute += todDelta;
            double tod = _worldState.TimeOfDay01 + todDelta;
            tod -= System.Math.Floor(tod);
            _worldState.TimeOfDay01 = tod;
        }
    }

    public override void _Process(double delta)
    {
        if (_player == null)
        {
            return;
        }

        DrainSpawnQueue();
        UpdateEntityLoading(_player.GlobalPosition);

        if (CVars.navGridDebug.Value)
        {
            NavGridDebug.Draw(this, _player.GlobalPosition);
        }
    }
}
