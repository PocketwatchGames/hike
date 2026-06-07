using System;
using System.Collections.Generic;
using Godot;

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

    // --- Environmental sampling (weather + voxel-light driven) -----------
    // Pure queries over the current weather (SkyController) and the voxel
    // sunlight BFS (WorldState). Live on World, not the client, because the
    // sim (Player thermal/wetness, perception, scent) is the primary
    // consumer; the debug `temp` CVar reads the breakdown too.

    // Sample wind speed in m/s at `worldPos`. Returns 0 when the voxel sun
    // BFS reports no skylight at all — a stand-in for "the player is in a
    // cave or under a roof", where the open-sky wind from the weather
    // system shouldn't reach them. Permissive: BFS spreads sideways from
    // open columns, so a cave mouth or doorway still seeps wind. Same
    // shape as SampleAirTemperature so callers can ignore wind whenever
    // they ignore weather.
    public float SampleWindSpeed(Vector3 worldPos)
    {
        SkyController sky = SkyController.Current;
        if (sky?.Weather == null) { return 0f; }
        float wind = sky.Weather.windSpeed;
        if (wind <= 0f) { return 0f; }

        if (_worldState != null && _worldState.GetSkyLight01(worldPos) <= 0f)
        {
            return 0f;
        }
        return wind;
    }

    // Per-component breakdown of the air-temperature sample. The `temp`
    // console CVar prints these so weather / lighting / occlusion can be
    // inspected independently. Final temperature is `Total`.
    public struct AirTemperatureSample
    {
        public float air;             // weather.airTemperature (°F, base ambient)
        public float sunTemperature;  // weather.sunTemperature (°F, max sun add)
        public float sunFactor;       // sky.SunFactor (time-of-day, 0..1)
        public float cloudCover;      // weather.cloudCover (0..1)
        public float fog;             // sky.Palette.Fog (0..1)
        public float skyTransmission; // 1 − clamp(cloudCover + fog, 0, 1)
        public float sunMask;         // sunBfs / LightEngine.MAX_LIGHT (0..1)

        public readonly float SunContribution => sunTemperature * sunFactor * skyTransmission * sunMask;
        public readonly float Total => air + SunContribution;
    }

    // Sample environmental air temperature in degrees F at `worldPos`.
    // airTemperature flows through unconditionally; sunTemperature stacks on
    // scaled by (a) sun strength now, (b) atmospheric transmission (clouds +
    // fog), and (c) the voxel sunlight BFS mask at the sample point — so
    // overhangs, caves, and foliage shade the sun's heating exactly the way
    // the world's lighting pass already classifies them. Player.cs adds its
    // own warmth-zone bonus on top of this — campfires are not sampled here
    // because the player tracks zone enter/exit directly.
    public float SampleAirTemperature(Vector3 worldPos)
    {
        return SampleAirTemperatureBreakdown(worldPos).Total;
    }

    public AirTemperatureSample SampleAirTemperatureBreakdown(Vector3 worldPos)
    {
        AirTemperatureSample s = default;
        SkyController sky = SkyController.Current;
        if (sky == null) { s.air = 64.4f; return s; }
        WeatherData weather = sky.Weather;
        if (weather == null) { s.air = 64.4f; return s; }

        s.air = weather.airTemperature;
        s.sunTemperature = weather.sunTemperature;
        s.sunFactor = sky.SunFactor;
        s.cloudCover = weather.cloudCover;
        s.fog = sky.Palette.Fog;
        // Atmospheric attenuation. Cloud cover (weather) and fog (palette,
        // derived from humidity + cool diurnal) each occlude the sun
        // independently; their sum is clamped to 1 so a fully overcast OR
        // fully foggy sky drives the multiplier to 0 without going negative
        // when both pile up.
        s.skyTransmission = 1f - Mathf.Clamp(s.cloudCover + s.fog, 0f, 1f);

        s.sunMask = 1f;
        if (_worldState != null)
        {
            s.sunMask = _worldState.GetSkyLight01(worldPos);
        }
        return s;
    }

    // Spherical radius (in chunks) for spawning entities around the player. Must be
    // <= ChunkManager.NEARBY_RADIUS so chunk collision is guaranteed to exist when
    // an entity spawns. Kept symmetric in world space (not frustum-culled) so
    // rotating the camera never reveals un-spawned entities.
    private const int ENTITY_LOAD_RADIUS = 5;
    private const int ENTITY_LOAD_RADIUS_SQ = ENTITY_LOAD_RADIUS * ENTITY_LOAD_RADIUS;
    // Reduced radius used for the initial spawn-time fill. The loading screen is
    // opaque so the player can't see past the inner sphere; the outer shell
    // streams in over the next few seconds after the fade via the normal
    // per-frame drain. Sphere counts: r=3 → ~110 chunks vs r=5 → ~520, so the
    // loading wait drains ~5x fewer entities before handing control back.
    private const int INITIAL_ENTITY_LOAD_RADIUS = 3;
    private const int INITIAL_ENTITY_LOAD_RADIUS_SQ = INITIAL_ENTITY_LOAD_RADIUS * INITIAL_ENTITY_LOAD_RADIUS;

    public IReadOnlyDictionary<Vector3I, List<Node3D>> ActiveEntities => _activeEntities;

    public Action<Mob> onMobSpawned;
    public Action<Mob> onMobRemoved;
    public Action<Discoverable> onDiscoverableSpawned;
    public Action<Discoverable> onDiscoverableRemoved;
    // Fires after LoadEntitiesForChunk has spawned the chunk's entity nodes.
    // Used by the minimap to stamp prop foliage once the trees / props are
    // actually in the scene (the chunk-mesh-loaded event fires earlier, when
    // entities don't exist yet).
    public Action<Vector3I> onChunkEntitiesLoaded;

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

    // Rain intensity (blended WeatherData.rainAmount, 0..1) at or above which
    // ESpawnConditions.Clear entries refuse to spawn. Sampled from the live
    // player-blended weather; since spawn gating only runs at chunk activation
    // (which streams around the player), this is the right gameplay read.
    public const float RainSpawnThreshold = 0.2f;

    // Current rain intensity from the live blended weather, or 0 when no
    // SkyController is up (editor / headless). Used by the spawn gate.
    public float CurrentRainAmount()
    {
        return SkyController.Current?.Weather?.rainAmount ?? 0f;
    }

    // True iff every circumstance required by `conditions` currently holds, so
    // a gated mob/chest may materialize. None always passes. See ESpawnConditions.
    public bool SpawnConditionsMet(ESpawnConditions conditions)
    {
        if (conditions == ESpawnConditions.None)
        {
            return true;
        }
        bool night = WorldState.IsNight(_worldState.TimeOfDay01);
        if (conditions.HasFlag(ESpawnConditions.Day) && night)
        {
            return false;
        }
        if (conditions.HasFlag(ESpawnConditions.Night) && !night)
        {
            return false;
        }
        if (conditions.HasFlag(ESpawnConditions.Clear) && CurrentRainAmount() >= RainSpawnThreshold)
        {
            return false;
        }
        if (conditions.HasFlag(ESpawnConditions.NotHeavyRain) && CurrentRainAmount() >= SimData.HeavyRainSpawnThreshold)
        {
            return false;
        }
        return true;
    }

    private readonly Dictionary<Vector3I, List<Node3D>> _activeEntities = new();
    private readonly HashSet<Vector3I> _desiredEntityChunks = new();

    // Per-frame budget for entity instantiation. The hitch detector caught
    // 26ms+ C# spikes from chunks containing 8 mobs (goblins) + their movinglight
    // torches all instantiating on the same frame, plus another ~40ms of
    // post-_Process gap from Jolt broadphase insertion and GpuParticles
    // first-render setup. Spreading at 8/frame, typical chunks (5-30 entities)
    // spawn in 1-4 frames — visually a brief pop-in of mobs/props, dramatically
    // better than a 130ms freeze.
    public const int DEFAULT_MAX_ENTITIES_PER_FRAME = 8;
    // Settable so the loading sequence can burst the drain rate while the
    // overlay is opaque (no visible frame cost). Reset to default before the
    // fade so in-game streaming keeps its hitch-free 8/frame cadence.
    public int MaxEntitiesPerFrame { get; set; } = DEFAULT_MAX_ENTITIES_PER_FRAME;
    // Cleared by ExpandToFullEntityRadius once the loading screen is ready to
    // fade. While true, RebuildDesiredEntityChunks uses INITIAL_ENTITY_LOAD_RADIUS
    // so SetPlayer's initial sync only enqueues the inner sphere.
    private bool _useInitialEntityRadius = true;

    private readonly struct PendingSpawn
    {
        public readonly Vector3I ChunkCoord;
        public readonly EntitySimState State;
        public PendingSpawn(Vector3I chunkCoord, EntitySimState state)
        {
            ChunkCoord = chunkCoord;
            State = state;
        }
    }

    private readonly Queue<PendingSpawn> _spawnQueue = new();
    // Per-chunk pending-entity count. Decremented as DrainSpawnQueue creates
    // each entity; on hitting zero we fire onChunkEntitiesLoaded so the
    // minimap (and any future consumer of "chunk's entities are fully in
    // the tree") sees the right edge. Cleaned up when chunks unload
    // mid-spawn — the corresponding queue entries get dropped at dequeue
    // by the _activeEntities-presence check.
    private readonly Dictionary<Vector3I, int> _spawningRemaining = new();

    // Per-cell refcount of pathfinding blockers contributed by spawned
    // entities (trees, chests, etc.). Refcounted so multiple entities sharing
    // a cell — or lifetime overlap during respawn — don't drop the block
    // prematurely. Queried by WalkabilityGrid.SampleColumn so mobs route
    // around props the voxel grid alone can't see.
    private readonly Dictionary<Vector3I, int> _pathBlockers = new();
    // Tracks the previous night state so Tick can detect the moment tod
    // crosses sunset and spawn SpawnAtNight entities on already-active
    // chunks. Without this, night-only goblins / chests stay missing on
    // chunks that loaded during the day until the player walks far enough
    // to evict and reload them. Only the sunset edge matters — sunrise
    // does not despawn anything; existing night mobs ride out daytime
    // until their chunk evicts.
    private bool _wasNight;
    // Time since the last off-condition mob cleanup sweep (see Tick /
    // CleanupOffConditionMobs). Throttles the per-mob walk to once per
    // SimData.SpawnCleanupIntervalSeconds.
    private float _spawnCleanupAccumulator;
    // Reused scratch list so the cleanup sweep doesn't allocate each interval.
    private readonly List<Mob> _cleanupScratch = new();
    private WorldState _worldState;
    private ChunkManager _chunkManager;
    private WorldDetailScatter _detailScatter;
    private WorldPropScatter _propScatter;
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
    private bool _editorMode;
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

    public Player player => _player;

    public void Initialize(WorldState worldState, Vector3 spawnPosition, GameCamera camera, ShaderMaterial fogMaterial, Func<Vector3> getPlayerPosition)
    {
        _worldState = worldState;
        _camera = camera;
        _lastEntityChunkCoord = WorldToChunkCoord(spawnPosition);
        _wasNight = WorldState.IsNight(worldState.TimeOfDay01);

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

        CreateWorldBoundary();
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

    // Tri-state result of the foliage-occlusion probe. Tight = at least
    // one cluster overlaps the tight (visible) cutaway radius — player is
    // actively obscured. Wide = nothing in the tight radius but something
    // sits in the wider "neighborhood" radius — player just stepped into
    // a small clearing inside the forest, hold cutaway at a small minimum
    // so re-expansion is instant when they round another tree. None = no
    // fading foliage anywhere nearby — drop the cutaway to zero hard.
    // Ordered Tight > Wide > None so the probe can early-out on Tight and
    // a "max so far" merge over multiple props is just Math.Max.
    public enum FadeProbeResult
    {
        None = 0,
        Wide = 1,
        Tight = 2,
    }

    // Per-frame CPU probe — classifies the camera→player capsule volume
    // against nearby fade-eligible foliage clusters. Returns BOTH a
    // tier classification (Tight / Wide / None) AND the count of unique
    // PROPS with at least one fading cluster inside the wider probe
    // radius. GameClient uses the tier to pick a target band (full /
    // minimum / off) and the prop count to scale the full target by
    // local cover density — so one tree gives a small cutaway and a
    // thicket of trees gives a bigger one even when only one is directly
    // behind the player.
    //
    // The count is intentionally per-PROP, not per-cluster: every tree
    // has 3-6 authored clusters (trunk-base, mid-canopy, top, etc.) and
    // counting each as a separate hit would saturate the density scale
    // after just a single isolated tree. Counting props matches the
    // intuition "how many trees are nearby", which is what we actually
    // want to drive the cutaway size.
    //
    // The scan walks every entity bucket but early-outs per-prop on a
    // squared XZ distance gate. Within a prop, we early-break the cluster
    // loop on the first Tight hit (can't escalate further) but otherwise
    // scan all clusters to find one that might be tight even if an
    // earlier one was only wide.
    public FadeProbeResult ProbeFadeVolume(Vector3 cameraPos, Vector3 capsuleFeet, Vector3 capsuleHead, float tightRadius, float wideRadius, float scanRange, out int nearbyPropCount)
    {
        nearbyPropCount = 0;
        Vector3 segDir = capsuleHead - cameraPos;
        float segLenSq = segDir.LengthSquared();
        if (segLenSq < 1e-4f)
        {
            return FadeProbeResult.None;
        }
        // Horizontal-only cull around the player's ground position — trees
        // sit at ground level (prop.WorldPosition.Y ≈ player feet Y), while
        // the camera→head midpoint floats meters up. A 3D-distance gate
        // there would burn most of the scan range on vertical separation
        // and reject trees that are right next to the player horizontally.
        // XZ distance to the player is the actual signal: any tree close
        // enough horizontally to plausibly intercept the segment gets the
        // per-cluster test.
        float scanRangeSq = scanRange * scanRange;

        Vector3 playerAxis = capsuleHead - capsuleFeet;
        float playerAxisLenSq = Mathf.Max(playerAxis.LengthSquared(), 1e-4f);

        // Horizontal half-space: anything past the player along the
        // camera→player ground vector is behind the player from the camera's
        // POV and doesn't obscure the silhouette. Mirrors the shader's
        // t_horiz <= 1.0 gate so probe + render judgments stay in sync.
        float camToPlayerXzX = capsuleFeet.X - cameraPos.X;
        float camToPlayerXzZ = capsuleFeet.Z - cameraPos.Z;
        float camToPlayerXzLenSq = Mathf.Max(camToPlayerXzX * camToPlayerXzX + camToPlayerXzZ * camToPlayerXzZ, 1e-4f);

        FadeProbeResult best = FadeProbeResult.None;
        foreach (List<EntitySimState> bucket in _worldState._entities.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                if (bucket[i] is not PropSimState prop)
                {
                    continue;
                }
                float dx = prop.WorldPosition.X - capsuleFeet.X;
                float dz = prop.WorldPosition.Z - capsuleFeet.Z;
                if (dx * dx + dz * dz > scanRangeSq)
                {
                    continue;
                }
                FoliageOccluder[] occluders = FoliageOccluderCache.GetOccluders(prop.Scene);
                if (occluders.Length == 0)
                {
                    continue;
                }
                float cos = Mathf.Cos(prop.RotationY);
                float sin = Mathf.Sin(prop.RotationY);
                // Per-prop accumulators — a prop counts as ONE hit
                // regardless of how many of its clusters land in range.
                bool propHasTight = false;
                bool propHasWide = false;
                for (int o = 0; o < occluders.Length; o++)
                {
                    FoliageOccluder occ = occluders[o];
                    if (!occ.FadesWhenOccludingPlayer)
                    {
                        continue;
                    }
                    // Rotate occluder local pos around Y by prop's
                    // rotation, then translate to world — matches the
                    // FoliageStamper transform path so the test sees the
                    // same cluster center the renderer does.
                    float rx = cos * occ.CenterLocal.X + sin * occ.CenterLocal.Z;
                    float rz = -sin * occ.CenterLocal.X + cos * occ.CenterLocal.Z;
                    Vector3 centerWorld = new Vector3(
                        prop.WorldPosition.X + rx,
                        prop.WorldPosition.Y + occ.CenterLocal.Y,
                        prop.WorldPosition.Z + rz);

                    // Horizontal half-space test — skip clusters past the
                    // player along the camera→player ground vector.
                    float tHoriz = ((centerWorld.X - cameraPos.X) * camToPlayerXzX
                                  + (centerWorld.Z - cameraPos.Z) * camToPlayerXzZ) / camToPlayerXzLenSq;
                    if (tHoriz > 1f)
                    {
                        continue;
                    }

                    // Same geometry the shader runs: project onto player
                    // capsule axis, then test against the camera→that-axis-
                    // point segment. Keeps probe + shader judgments in sync.
                    float tAxis = Mathf.Clamp(
                        (centerWorld - capsuleFeet).Dot(playerAxis) / playerAxisLenSq, 0f, 1f);
                    Vector3 axisPt = capsuleFeet + playerAxis * tAxis;

                    Vector3 segToAxis = axisPt - cameraPos;
                    float segToAxisLenSq = Mathf.Max(segToAxis.LengthSquared(), 1e-4f);
                    float tSeg = Mathf.Clamp(
                        (centerWorld - cameraPos).Dot(segToAxis) / segToAxisLenSq, 0f, 1f);
                    Vector3 closest = cameraPos + segToAxis * tSeg;
                    float dist = (centerWorld - closest).Length();

                    float clusterMax = Mathf.Max(occ.Radii.X, Mathf.Max(occ.Radii.Y, occ.Radii.Z));
                    if (dist < clusterMax + tightRadius)
                    {
                        propHasTight = true;
                        propHasWide = true;
                        // Tight is the strongest classification this prop
                        // can hit — no point checking its other clusters.
                        break;
                    }
                    if (dist < clusterMax + wideRadius)
                    {
                        propHasWide = true;
                        // Keep scanning — a later cluster on the same prop
                        // could escalate the prop to Tight.
                    }
                }

                if (propHasTight)
                {
                    nearbyPropCount++;
                    best = FadeProbeResult.Tight;
                }
                else if (propHasWide)
                {
                    nearbyPropCount++;
                    if (best == FadeProbeResult.None)
                    {
                        best = FadeProbeResult.Wide;
                    }
                }
            }
        }
        return best;
    }

    public void UpdateEntityLoading(Vector3 center)
    {
        Vector3I currentCoord = WorldToChunkCoord(center);
        if (currentCoord == _lastEntityChunkCoord)
        {
            return;
        }
        _lastEntityChunkCoord = currentCoord;

        RebuildDesiredEntityChunks(currentCoord);
        SyncEntitiesToDesired();
    }

    public void EnableEditorMode()
    {
        _editorMode = true;
    }

    private void RebuildDesiredEntityChunks(Vector3I center)
    {
        int radius = _useInitialEntityRadius ? INITIAL_ENTITY_LOAD_RADIUS : ENTITY_LOAD_RADIUS;
        int radiusSq = _useInitialEntityRadius ? INITIAL_ENTITY_LOAD_RADIUS_SQ : ENTITY_LOAD_RADIUS_SQ;
        _desiredEntityChunks.Clear();
        for (int x = -radius; x <= radius; x++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                for (int z = -radius; z <= radius; z++)
                {
                    if (x * x + y * y + z * z > radiusSq)
                    {
                        continue;
                    }
                    _desiredEntityChunks.Add(center + new Vector3I(x, y, z));
                }
            }
        }
    }

    // Switch from the initial (small) entity-load radius to the full radius
    // and enqueue spawns for the newly-desired outer-shell chunks. Called by
    // GameClient once the inner sphere has drained and the loading screen is
    // about to fade — the outer shell pops in over the next few seconds via
    // the normal DrainSpawnQueue budget.
    public void ExpandToFullEntityRadius()
    {
        if (!_useInitialEntityRadius)
        {
            return;
        }
        _useInitialEntityRadius = false;
        if (_player == null)
        {
            return;
        }
        RebuildDesiredEntityChunks(WorldToChunkCoord(_player.GlobalPosition));
        SyncEntitiesToDesired();
    }

    private void SyncEntitiesToDesired()
    {
        // Despawn entities in chunks that left range
        UnloadEntitiesOutsideSet(_desiredEntityChunks, _activeEntities);

        // Spawn entities in chunks that are in range and already have their mesh loaded.
        // Chunks whose mesh hasn't loaded yet will get picked up by OnChunkLoaded.
        foreach (Vector3I coord in _desiredEntityChunks)
        {
            if (_activeEntities.ContainsKey(coord))
            {
                continue;
            }
            if (!_chunkManager.IsChunkLoaded(coord))
            {
                continue;
            }
            LoadEntitiesForChunk(coord);
        }
    }

    private void OnChunkLoaded(Vector3I coord)
    {
        if (!_editorMode && _player == null)
        {
            return;
        }
        if (!_desiredEntityChunks.Contains(coord))
        {
            return;
        }
        if (_activeEntities.ContainsKey(coord))
        {
            return;
        }
        LoadEntitiesForChunk(coord);
    }

    private void OnChunkUnloaded(Vector3I coord)
    {
        // Drop any pending-spawn bookkeeping first so DrainSpawnQueue's
        // _activeEntities-presence check skips any of this chunk's still-in-
        // queue entities.
        _spawningRemaining.Remove(coord);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            return;
        }
        foreach (Node3D node in entities)
        {
            node.QueueFree();
        }
        _activeEntities.Remove(coord);
    }

    public bool IsSpawnChunkReady(Vector3 spawnPosition)
    {
        return _chunkManager.IsSpawnChunkReady(spawnPosition);
    }

    // True once every entity-eligible chunk around the player has finished
    // streaming its entities out of _spawnQueue. ChunkManager's initial-load
    // pass fills the full mesh sphere (NEARBY_RADIUS = 6) synchronously before
    // IsSpawnChunkReady flips, so by the time SetPlayer runs every chunk
    // inside the active entity radius has its mesh and gets LoadEntitiesForChunk
    // called. GameClient holds the spawn-fade opaque until this returns true so
    // tallgrass / props / knowledge stones don't pop in over the reveal —
    // during the initial load the active radius is INITIAL_ENTITY_LOAD_RADIUS,
    // so this becomes true once only the inner sphere has drained; the outer
    // shell is enqueued by ExpandToFullEntityRadius right before the fade.
    public bool AreEntitySpawnsDrained()
    {
        return _spawnQueue.Count == 0 && _spawningRemaining.Count == 0;
    }

    // Drives the loading screen's entity-spawn phase progress bar. Sampled
    // once after SetPlayer (peak) and each frame during the drain (current);
    // (peak - current) / peak is the fraction complete.
    public int PendingEntitySpawnCount => _spawnQueue.Count;

    public void UpdateLighting(List<Vector3I> changedPositions)
    {
        _chunkManager.UpdateLighting(changedPositions);
    }

    public void AddLightSource(LightSource source)
    {
        _chunkManager.AddLightSource(source);
    }

    public void RemoveLightSource(LightSource source)
    {
        _chunkManager.RemoveLightSource(source);
    }

    public void SetLightAmplitude(LightSource source, float amplitude)
    {
        _chunkManager.SetLightAmplitude(source, amplitude);
    }

    public void SetFogDebugMode(int mode)
    {
        _chunkManager?.SetFogDebugMode(mode);
    }

    public void SetFogEnabled(bool enabled)
    {
        _chunkManager?.SetFogEnabled(enabled);
    }

    public void SetFogVolumetricEnabled(bool enabled)
    {
        _chunkManager?.SetFogVolumetricEnabled(enabled);
    }

    public void RebuildNearbyChunkMeshes(Vector3 worldPos, List<Vector3I> changedPositions)
    {
        _chunkManager.RebuildNearbyChunkMeshes(worldPos, changedPositions);
    }

    // Constants for the sun-reach raycast. Origin is offset off the surface so
    // a query point sitting on a face doesn't self-hit. Distance only needs to
    // clear nearby occluders (cliffs, tree trunks, cave roofs) — the sun is
    // infinitely far but a few dozen voxels of clearance is enough to know
    // whether we're in the open.
    private const float SUN_RAY_ORIGIN_OFFSET = 0.05f;
    private const float SUN_RAY_DISTANCE = 64f;

    // True if a ray cast from `pos` toward the sun reaches open sky without
    // hitting environment geometry. Mirrors the directional-shadow term used
    // by the shaders; gameplay code should call this per-actor (not per-voxel)
    // because each call costs one Jolt query.
    public bool IsPointInDirectionalSun(Vector3 pos)
    {
        Vector3 toSun = -_worldState.ShadowLightDirection;
        Vector3 from = pos + toSun * SUN_RAY_ORIGIN_OFFSET;
        Vector3 to = from + toSun * SUN_RAY_DISTANCE;
        using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Solid);
        var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        return result.Count == 0;
    }

    // Convenience: perceived brightness at `pos` matching the shader model,
    // including the directional-shadow term. Skip the raycast with
    // `checkDirectionalShadow = false` for cheap callers (e.g. plant growth)
    // that don't care whether direct sun is geometrically blocked.
    public float GetPerceivedLight(Vector3 pos, bool checkDirectionalShadow = true)
    {
        bool inSun = !checkDirectionalShadow || IsPointInDirectionalSun(pos);
        return _worldState.GetPerceivedLightWorld(pos, inSun);
    }

    public static Vector3I WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector3I(
            Mathf.FloorToInt(worldPos.X / ChunkState.SIZE),
            Mathf.FloorToInt(worldPos.Y / ChunkState.SIZE),
            Mathf.FloorToInt(worldPos.Z / ChunkState.SIZE)
        );
    }

    public Loot SpawnLoot(Vector3 position, Vector3 impulse, ItemData item)
    {
        if (item == null)
        {
            return null;
        }
        GameClient gc = GameClient.Current;
        PackedScene scene = gc?.lootScene;
        if (scene == null)
        {
            return null;
        }
        var simState = new LootSimState(position, item);
        _worldState.AddEntity(simState);
        Loot loot = Loot.Create(this, simState, scene, impulse);

        Vector3I coord = WorldToChunkCoord(position);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            entities = new List<Node3D>();
            _activeEntities[coord] = entities;
        }
        RegisterEntity(loot, entities, simState);

        return loot;
    }

    // Spawn an arrow drop at the impact point of a hitscan shot. The arrow
    // binds back to the firing WeaponState — recovering it (player pickup,
    // 30s LootData.removeTimeMs timeout) routes through ArrowLootSimState
    // and returns 1 ammo to the source weapon. The weapon also tracks the
    // arrow in its outstandingArrows list so the binding survives the
    // player dropping the bow (the weapon instance lives in inventory and
    // outlives the bow's equip state).
    public Loot SpawnArrowLoot(Vector3 position, Vector3 impulse, ArrowLootData data, WeaponState sourceWeapon)
    {
        if (data == null || sourceWeapon == null)
        {
            return null;
        }
        GameClient gc = GameClient.Current;
        PackedScene scene = gc?.lootScene;
        if (scene == null)
        {
            return null;
        }
        var simState = new ArrowLootSimState(position, data, sourceWeapon);
        _worldState.AddEntity(simState);
        sourceWeapon.RegisterArrow(simState);
        Loot loot = Loot.Create(this, simState, scene, impulse);

        Vector3I coord = WorldToChunkCoord(position);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            entities = new List<Node3D>();
            _activeEntities[coord] = entities;
        }
        RegisterEntity(loot, entities, simState);

        return loot;
    }

    // Spawn a pickup carrying a specific ItemState (player-dropped item path).
    // requireInteract latches the dropped pile into "press Interact to pick
    // up" mode so the player doesn't immediately re-pick up what they just
    // threw. Loot.Create swaps in the item's worldSprite on spawn.
    public Loot DropItem(ItemState item, Vector3 position, Vector3 impulse, bool requireInteract = false)
    {
        if (item == null || item.data == null)
        {
            return null;
        }
        GameClient gc = GameClient.Current;
        PackedScene scene = gc?.lootScene;
        if (scene == null)
        {
            return null;
        }

        var simState = new LootSimState(position, item.data);
        simState.Item = item;
        simState.RequireInteract = requireInteract;
        _worldState.AddEntity(simState);
        Loot pickup = Loot.Create(this, simState, scene, impulse);

        Vector3I coord = WorldToChunkCoord(position);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            entities = new List<Node3D>();
            _activeEntities[coord] = entities;
        }
        RegisterEntity(pickup, entities, simState);

        return pickup;
    }

    // Spawn a transient footprint decal at `position`. Parented directly to
    // World (not registered in _activeEntities) because footprints have no
    // persistent sim state and self-despawn via QueueFree once their fade
    // hits zero. The two shared scenes (player / mob) live on SimData;
    // `gated` picks the perception-gated variant for mob-laid prints.
    // `yaw` rotates the decal box around Y so the texture aligns with the
    // direction the actor is facing — toe of the print points where they
    // were walking.
    public Footprint SpawnFootprint(Texture2D texture, Vector2 size, Color tint, Vector3 position, float yaw, float durationSeconds, bool gated)
    {
        SimData sim = SimData;
        if (sim == null || texture == null)
        {
            return null;
        }
        PackedScene scene = gated ? sim.FootprintDiscoverable : sim.FootprintVisible;
        if (scene == null)
        {
            return null;
        }
        Footprint fp = scene.Instantiate<Footprint>();
        // Set transform before AddChild so the Discoverable's _Ready light
        // sample (perception tick) reads the correct world-space coordinate
        // on the first tick rather than seeing origin.
        fp.Position = position;
        fp.Rotation = new Vector3(0f, yaw, 0f);
        AddChild(fp);
        fp.Initialize(this, texture, size, tint, durationSeconds);
        return fp;
    }

    // Single iteration primitive for "all loaded entities of type T". Call sites
    // should use this rather than walking _activeEntities directly, so a future
    // typed cache (e.g. List<Mob>) can be swapped in here without touching them.
    public IEnumerable<T> GetEntities<T>() where T : Node3D
    {
        foreach (List<Node3D> entities in _activeEntities.Values)
        {
            foreach (Node3D entity in entities)
            {
                if (entity is T t)
                {
                    yield return t;
                }
            }
        }
    }

    public void RemoveEntity(Node3D entity)
    {
        foreach (List<Node3D> entities in _activeEntities.Values)
        {
            if (entities.Remove(entity))
            {
                break;
            }
        }
    }

    public void UnloadChunkEntities(Vector3I coord)
    {
        OnChunkUnloaded(coord);
    }

    public void LoadChunkEntities(Vector3I coord)
    {
        if (!_chunkManager.IsChunkLoaded(coord))
        {
            return;
        }
        if (_activeEntities.ContainsKey(coord))
        {
            return;
        }
        LoadEntitiesForChunk(coord);
    }

    private void LoadEntitiesForChunk(Vector3I coord)
    {
        using var _prof = Profiler.Sample("World.LoadChunkEntities");
        // Register the chunk in _activeEntities immediately, even though
        // entities will trickle in over the next several frames via
        // DrainSpawnQueue. Keeps OnChunkLoaded / SyncEntitiesToDesired
        // idempotent — second call for the same coord sees the entry and
        // skips. Consumers that walk _activeEntities (GetEntities<T>,
        // RemoveEntity) see entities as they appear; only difference is
        // that onChunkEntitiesLoaded (minimap stamp) fires when the queue
        // finishes the chunk, not at enqueue time.
        var entities = new List<Node3D>();
        _activeEntities[coord] = entities;
        List<EntitySimState> states = _worldState.GetEntities(coord);
        if (states == null || states.Count == 0)
        {
            onChunkEntitiesLoaded?.Invoke(coord);
            return;
        }
        _spawningRemaining[coord] = states.Count;
        foreach (EntitySimState state in states)
        {
            _spawnQueue.Enqueue(new PendingSpawn(coord, state));
        }
    }

    private void DrainSpawnQueue()
    {
        using var _prof = Profiler.Sample("World.DrainSpawnQueue");
        int spawned = 0;
        int budget = MaxEntitiesPerFrame;
        while (spawned < budget && _spawnQueue.Count > 0)
        {
            PendingSpawn pending = _spawnQueue.Dequeue();
            // Chunk could have been unloaded between enqueue and now; drop
            // the entity silently. _activeEntities is the single source of
            // truth for "is this chunk still alive."
            if (!_activeEntities.TryGetValue(pending.ChunkCoord, out List<Node3D> entities))
            {
                _spawningRemaining.Remove(pending.ChunkCoord);
                continue;
            }
            Node3D entity = pending.State.CreateEntity(this);
            if (entity != null)
            {
                // Per-type spawn counter — surfaces under engine monitors so
                // a hitch dump shows e.g. "spawn.Mob 8" right at the boundary.
                Profiler.IncrementCounter("spawn." + entity.GetType().Name);
                RegisterEntity(entity, entities, pending.State);
            }
            spawned++;
            // Decrement chunk's pending count; fire onChunkEntitiesLoaded on
            // the last entity so the minimap stamp pass sees the full set.
            if (_spawningRemaining.TryGetValue(pending.ChunkCoord, out int remaining))
            {
                remaining--;
                if (remaining <= 0)
                {
                    _spawningRemaining.Remove(pending.ChunkCoord);
                    onChunkEntitiesLoaded?.Invoke(pending.ChunkCoord);
                }
                else
                {
                    _spawningRemaining[pending.ChunkCoord] = remaining;
                }
            }
        }
    }

    private void RegisterEntity(Node3D entity, List<Node3D> entities, EntitySimState state = null)
    {
        if (entity is IWorldEntity worldEntity)
        {
            worldEntity.OnSpawned(this);
        }
        // Porous props / interactives: move their default-layer colliders onto
        // Porous so smell, sound, perched vision, and flight pass through while
        // movement and grounded sight still block. One shared concept (IPorous)
        // and one application site for props and interactives alike.
        if (entity is IPorous porous && porous.Porous)
        {
            PorousColliders.Apply(entity);
        }
        if (state != null)
        {
            state.RuntimeNode = entity;
            // Clear the back-reference whenever the node leaves the tree
            // (chunk eviction, day/night despawn, mob death). RefreshTimeOfDayEntities
            // uses RuntimeNode to detect which states currently have a live
            // node — without this, a freed but still-referenced node would
            // make the state look "already spawned" forever.
            entity.TreeExiting += () =>
            {
                if (state.RuntimeNode == entity)
                {
                    state.RuntimeNode = null;
                }
            };
        }
        if (state != null)
        {
            // Refcounted: each blocker entity adds 1 to every cell it
            // occupies, and removes 1 on TreeExiting. Overlapping props (a
            // chest tucked next to a tree, two adjacent trees sharing a cell)
            // keep the cell blocked until the last owner leaves.
            List<Vector3I> blockerCells = new();
            state.GetPathBlockerCells(entity, blockerCells);
            if (blockerCells.Count > 0)
            {
                for (int i = 0; i < blockerCells.Count; i++)
                {
                    AddPathBlocker(blockerCells[i]);
                }
                // Capture so removal is automatic regardless of why the node
                // leaves the tree (chunk eviction, editor delete, scene
                // teardown). World outlives its child entities, so the
                // closure's implicit `this` is safe.
                entity.TreeExiting += () =>
                {
                    for (int i = 0; i < blockerCells.Count; i++)
                    {
                        RemovePathBlocker(blockerCells[i]);
                    }
                };
            }
        }
        entities.Add(entity);
    }

    // Despawns loaded mobs whose ESpawnConditions no longer hold (a night
    // goblin caught past dawn, a clear-day sparrow once rain starts), but only
    // when the encounter is "cold": the mob is far from the player, the player
    // has lost track of it (DiscoveryState back to Hidden), and the mob isn't
    // aware of / hunting the player. This is a presence gate that complements
    // the spawn gate — a goblin caught out at dawn keeps hunting as long as it
    // can see the player or the player can see it, and only quietly vanishes
    // once everyone has disengaged and walked away. The MobSimState persists in
    // WorldState, so the mob respawns naturally when its conditions return and
    // its chunk is active (same path as RefreshTimeOfDayEntities). Despawn is
    // identical to a chunk eviction: QueueFree → TreeExiting syncs the node
    // back to its sim state. Called from Tick on an interval.
    private void CleanupOffConditionMobs()
    {
        if (_player == null)
        {
            return;
        }
        using var _prof = Profiler.Sample("World.CleanupOffConditionMobs");
        float distance = _worldState.SimData?.SpawnCleanupDistance ?? 50f;
        float distanceSq = distance * distance;
        Vector3 playerPos = _player.GlobalPosition;

        // Collect first, mutate after — RemoveEntity edits the lists that
        // GetEntities<Mob> walks.
        _cleanupScratch.Clear();
        foreach (Mob mob in GetEntities<Mob>())
        {
            // Unconditional spawns and corpses are never cleaned up on this
            // account; corpses have their own lifecycle (loot, chunk eviction).
            if (!mob.alive || mob.spawnConditions == ESpawnConditions.None)
            {
                continue;
            }
            // Conditions still hold — the mob legitimately belongs here.
            if (SpawnConditionsMet(mob.spawnConditions))
            {
                continue;
            }
            // Player still knows about it (Discovered with live memory, or
            // mid-Detected) — let memory lapse before we touch it.
            if (mob.playerPerceptionState != EPlayerPerceptionState.Hidden)
            {
                continue;
            }
            // Mob is aware of the player (alerted or investigating) — a goblin
            // mid-hunt doesn't blink out at dawn.
            if (mob.triggered || mob.investigation != null)
            {
                continue;
            }
            // Close enough that despawning could be seen.
            if ((mob.GlobalPosition - playerPos).LengthSquared() < distanceSq)
            {
                continue;
            }
            _cleanupScratch.Add(mob);
        }

        for (int i = 0; i < _cleanupScratch.Count; i++)
        {
            Mob mob = _cleanupScratch[i];
            RemoveEntity(mob);
            mob.QueueFree();
        }
        _cleanupScratch.Clear();
    }

    // Walks active chunks and spawns any night-only entities whose chunk is
    // active when night begins. The reverse direction — despawning entities
    // whose conditions lapsed — is handled separately by CleanupOffConditionMobs,
    // which removes a SpawnAtNight mob only once the player is far and unaware;
    // a goblin caught out at dawn keeps hunting until then. Non-night entities
    // override ShouldSpawn => true unconditionally and are unaffected. Called
    // from Tick on day↔night transitions.
    private void RefreshTimeOfDayEntities()
    {
        foreach (var pair in _activeEntities)
        {
            List<EntitySimState> states = _worldState.GetEntities(pair.Key);
            if (states == null)
            {
                continue;
            }
            List<Node3D> nodes = pair.Value;
            foreach (EntitySimState state in states)
            {
                if (state.RuntimeNode != null)
                {
                    continue;
                }
                if (!state.ShouldSpawn(this))
                {
                    continue;
                }
                Node3D entity = state.CreateEntity(this);
                if (entity != null)
                {
                    RegisterEntity(entity, nodes, state);
                }
            }
        }
    }

    public void AddPathBlocker(Vector3I cell)
    {
        _pathBlockers.TryGetValue(cell, out int count);
        _pathBlockers[cell] = count + 1;
    }

    public void RemovePathBlocker(Vector3I cell)
    {
        if (!_pathBlockers.TryGetValue(cell, out int count))
        {
            return;
        }
        if (count <= 1)
        {
            _pathBlockers.Remove(cell);
        }
        else
        {
            _pathBlockers[cell] = count - 1;
        }
    }

    public bool IsPathBlocked(int wx, int wy, int wz)
    {
        return _pathBlockers.ContainsKey(new Vector3I(wx, wy, wz));
    }

    private void UnloadEntitiesOutsideSet(HashSet<Vector3I> desired, Dictionary<Vector3I, List<Node3D>> loaded)
    {
        var toRemove = new List<Vector3I>();
        foreach (Vector3I coord in loaded.Keys)
        {
            if (!desired.Contains(coord))
            {
                toRemove.Add(coord);
            }
        }
        foreach (Vector3I coord in toRemove)
        {
            foreach (Node3D node in loaded[coord])
            {
                node.QueueFree();
            }
            loaded.Remove(coord);
            // Pending-spawn bookkeeping shares the chunk-coord key; drop
            // it so DrainSpawnQueue ignores any queue entries still
            // pointing at this chunk.
            _spawningRemaining.Remove(coord);
        }
    }

    private void CreateWorldBoundary()
    {
        Vector3 minWorld = new Vector3(
            _worldState.Min.X * ChunkState.SIZE,
            _worldState.Min.Y * ChunkState.SIZE,
            _worldState.Min.Z * ChunkState.SIZE
        );
        Vector3 maxWorld = new Vector3(
            (_worldState.Max.X + 1) * ChunkState.SIZE,
            (_worldState.Max.Y + 1) * ChunkState.SIZE,
            (_worldState.Max.Z + 1) * ChunkState.SIZE
        );
        Vector3 center = (minWorld + maxWorld) / 2f;
        Vector3 size = maxWorld - minWorld;

        const float WALL_THICKNESS = 1f;
        float wallHeight = size.Y;

        // North wall (+Z)
        AddBoundaryWall(new Vector3(center.X, center.Y, maxWorld.Z + WALL_THICKNESS / 2f),
            new Vector3(size.X + WALL_THICKNESS * 2f, wallHeight, WALL_THICKNESS));

        // South wall (-Z)
        AddBoundaryWall(new Vector3(center.X, center.Y, minWorld.Z - WALL_THICKNESS / 2f),
            new Vector3(size.X + WALL_THICKNESS * 2f, wallHeight, WALL_THICKNESS));

        // East wall (+X)
        AddBoundaryWall(new Vector3(maxWorld.X + WALL_THICKNESS / 2f, center.Y, center.Z),
            new Vector3(WALL_THICKNESS, wallHeight, size.Z + WALL_THICKNESS * 2f));

        // West wall (-X)
        AddBoundaryWall(new Vector3(minWorld.X - WALL_THICKNESS / 2f, center.Y, center.Z),
            new Vector3(WALL_THICKNESS, wallHeight, size.Z + WALL_THICKNESS * 2f));

        // Floor (-Y)
        AddBoundaryWall(new Vector3(center.X, minWorld.Y - WALL_THICKNESS / 2f, center.Z),
            new Vector3(size.X + WALL_THICKNESS * 2f, WALL_THICKNESS, size.Z + WALL_THICKNESS * 2f));
    }

    private void AddBoundaryWall(Vector3 position, Vector3 size)
    {
        var body = new StaticBody3D();
        body.Position = position;

        var shape = new BoxShape3D();
        shape.Size = size;

        var collisionShape = new CollisionShape3D();
        collisionShape.Shape = shape;

        body.AddChild(collisionShape);
        AddChild(body);
    }
}
