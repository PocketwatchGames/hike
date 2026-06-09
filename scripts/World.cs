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

        _chunkManager = new ChunkManager();
        AddChild(_chunkManager);
        _chunkManager.onChunkLoaded += OnChunkLoaded;
        _chunkManager.onChunkUnloaded += OnChunkUnloaded;
        _chunkManager.Initialize(worldState, spawnPosition, camera, fogMaterial, getPlayerPosition);

        // Spawned programmatically here rather than authored into game.tscn
        // because AmbienceController is pure logic — no [Export] node refs
        // to wire and no audio assets yet. Once the global ambience layer
        // AudioStreamPlayers (rain / wind / insect bed) get authored in the
        // scene, they'll likely move under this node and AmbienceController
        // will graduate to a scene-tree node with [Export] field references.
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
        // no thunder data wired up. Spawned here rather than as a child
        // of AmbienceController so the scheduler can live across
        // whatever future restructuring AmbienceController gets.
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
        float dayLength = _worldState.SimData?.DayLengthSeconds ?? 600f;
        if (dayLength > 0f)
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
        float cleanupInterval = _worldState.SimData?.SpawnCleanupIntervalSeconds ?? 2f;
        _spawnCleanupAccumulator += (float)delta;
        if (_spawnCleanupAccumulator >= cleanupInterval)
        {
            _spawnCleanupAccumulator = 0f;
            CleanupOffConditionMobs();
        }

        _heatField?.Tick();
    }

    public override void _Process(double delta)
    {
        if (_player == null)
        {
            return;
        }

        DrainSpawnQueue();
        UpdateEntityLoading(_player.GlobalPosition);
    }
}
