using System;
using System.Collections.Generic;
using Godot;

// Central hub for all world simulation. The class is split across several
// partial files by concern:
//   Sim.cs                 — this file: lifecycle/orchestration + owned sub-objects
//   Sim.EntityStreaming.cs — chunk-driven entity load/unload + the spawn queue
//   Sim.SpawnLifecycle.cs  — spawn-condition gating + day/night refresh + cleanup
//   Sim.Spawning.cs        — loot / drop / footprint spawn factories
//   Sim.Environment.cs     — weather + voxel-light sampling queries
//   Sim.Chunks.cs          — thin delegation to ChunkManager (lighting, fog, coords)
// A few self-contained pieces live as their own classes that Sim owns:
//   FoliageCutawayProbe, PathBlockerGrid, and the static WorldBoundary helper.
public partial class Sim : Node3D
{
    // Reference to the active world, used by static contexts (CVars, etc.)
    // that need to reach into the running game without a node-tree lookup.
    // Set in Initialize, cleared on tree exit. Only one game world is active
    // at a time so a single static slot is sufficient.
    public static Sim Current { get; private set; }

    public SimData SimData => _worldState.SimData;
    public WorldState WorldState => _worldState;
    public ulong GameTimeMs => _worldState.GameTimeMs;
    // Normalized day clock (0 = sunrise, wrapping at the next sunrise).
    public double TimeOfDay01 => _worldState.TimeOfDay01;
    // The absolute in-world clock every in-world deadline is measured on (see
    // WorldState.WorldClockDays).
    public double WorldClockDays => _worldState.WorldClockDays;

    // Fired each time the in-world clock crosses a sunrise — in normal play, a
    // nap, or the sleep skip, once per dawn crossed. The day's weather has
    // already been rolled when it fires.
    public event Action OnDawn;

    // Fired when the party rests (sleep to sunrise, the Ruby Rosaries, the death wake),
    // after the skip's own OnDawn. What a night's sleep resets — spawns, the
    // well-rested pick, summoned pets — runs here, not at dawn: a
    // party that stays up through a sunrise keeps its day.
    public event Action OnRest;

    // Fired after every deadline sweep (Sim.SweepDeadlines: the housekeeping
    // interval and each clock jump). Stations listen to flip their ready visual
    // once their regrow deadline passes, without each polling the clock.
    public event Action OnDeadlinesSwept;

    // Fired on the day->night (dusk) edge, so systems can react to nightfall
    // without polling the clock. Drives the "Return to Camp" quest trigger.
    public event Action OnNightfall;

    // Fired the moment a mob dies, with the per-instance DamagedByPlayer flag —
    // the SIM-side kill signal (quest kill counters). Distinct from
    // GameClient.onMobKilled, which drives the client bestiary / combat bridges,
    // so sim reactors don't depend on the client. Both fire from Mob.Die.
    public event Action<SpeciesData, bool> onMobKilled;
    public void NotifyMobKilled(SpeciesData species, bool damagedByPlayer)
    {
        if (species == null)
        {
            return;
        }
        onMobKilled?.Invoke(species, damagedByPlayer);
    }

    // Whole days elapsed. Kept for future use — nothing times itself by it; an
    // in-world deadline is a WorldClockDays value.
    public int DayNumber => _worldState.DayNumber;

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

    // Corpses in loaded chunks — mobs that have died and have not yet been
    // removed. Registered by Mob.Die, unregistered when the body leaves the
    // tree. A flat list rather than a spatial structure because it holds a
    // handful of bodies at most: the corpse-discovery scan in Mob walks it per
    // perception tick, and the empty case has to cost nothing.
    private readonly List<Mob> _corpses = new();
    public IReadOnlyList<Mob> Corpses => _corpses;

    public void RegisterCorpse(Mob corpse)
    {
        if (corpse != null && !_corpses.Contains(corpse))
        {
            _corpses.Add(corpse);
        }
    }

    public void UnregisterCorpse(Mob corpse)
    {
        _corpses.Remove(corpse);
    }

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
    private NightMobSpawner _nightMobSpawner;
    private FairySpawner _fairySpawner;
    private ChunkAmbienceSpawner _chunkAmbienceSpawner;

    // Darkness dwell [0,1]: how "charged" the local darkness around the player is,
    // updated each Tick (UpdateNightDarkness). Eases up over nightDarkRiseSeconds
    // toward how dark the spot is (total sky+block light vs nightDarkThreshold) and
    // back down over nightDarkFallSeconds in the light — so lurking in the dark, a
    // cave, or a dungeon draws the gellies, day or night. The night spawner maxes
    // this against a time-of-day term for its single danger scalar. Transient; not
    // saved.
    private float _darknessDwell;
    public float DarknessDwell => _darknessDwell;
    // Block light at the player [0,1] (peak channel / targetLightMax), cached each
    // Tick. Slime vision reads this directly (the player's concealment axis, kept
    // separate from spawn danger): they see a moonlit or dark player well but a
    // fire/lantern-lit one poorly.
    private float _playerBlockLight01;
    public float PlayerBlockLight01 => _playerBlockLight01;
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

    // The invisible walls and floor boxing the world in. Kept so world queries
    // that pick a point to ACT on can exclude them — they're Environment-layer
    // solid, so a ray out over open air hits one instead of missing.
    public List<Rid> BoundaryRids { get; private set; } = new List<Rid>();

    // Global manager for static-prop sprite multimeshes. Each
    // MultimeshPropSprite registers itself in _Ready and unregisters in
    // _ExitTree, so the manager stays consistent with the active prop set
    // through chunk eviction without an explicit chunk-coord index.
    public WorldPropScatter PropScatter => _propScatter;

    // Batched renderer for transient footprint ground marks — one MultiMesh
    // per actor footprint texture, owning its own per-print lifetime fade and
    // mob-print discovery gate. Sim.SpawnFootprint routes prints to it.
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

    private Func<Vector3> _getViewCenter;

    // Where the world is centred: the player in game, the editor cursor in the
    // editor. Anything that wants "the point the world is streaming around"
    // must read this rather than `player.GlobalPosition` — the editor runs a
    // player-less Sim, and falling back to the origin there resolves the wrong
    // zone (and so the wrong sky) for wherever you're actually working.
    public Vector3 ViewCenter => _getViewCenter != null ? _getViewCenter() : Vector3.Zero;

    public void Initialize(WorldState worldState, Vector3 spawnPosition, GameCamera camera, ShaderMaterial fogMaterial, Func<Vector3> getPlayerPosition)
    {
        _worldState = worldState;
        _getViewCenter = getPlayerPosition;
        _camera = camera;
        _lastEntityChunkCoord = WorldToChunkCoord(spawnPosition);
        _wasNight = WorldState.IsNight(worldState.TimeOfDay01);
        _fadeProbe = new FoliageCutawayProbe(worldState);
        IndexDeathSacks();

        // "Return to Camp" is added on the dusk edge; sleeping to sunrise clears it.
        OnNightfall += AddReturnToCampQuest;

        // Set Current BEFORE constructing children that may dereference it.
        // ChunkManager.Initialize triggers synchronous chunk builds which call
        // Sim.Current?.DetailScatter?.SetChunk — if Current is still null
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

        // Ambient after-dark spawner: keeps a live population of night mobs
        // (gellies) in dark spots around the player, denser as midnight nears.
        // Dormant when SimData has no nightSpawnMobs wired up.
        _nightMobSpawner = new NightMobSpawner();
        _nightMobSpawner.Name = "NightMobSpawner";
        AddChild(_nightMobSpawner);

        // Ambient daytime spawner: puts a few fairies near the player at points
        // across the day, in zones flagged for them. Dormant when SimData has no
        // fairySpawnSpecies wired up.
        _fairySpawner = new FairySpawner();
        _fairySpawner.Name = "FairySpawner";
        AddChild(_fairySpawner);

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

        BoundaryRids = WorldBoundary.Create(this, _worldState);
    }

    public override void _ExitTree()
    {
        UnbindWorldScript();
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

        // Advance the in-world clock. time_scale fast-forwards it without
        // disturbing GameTimeMs (which drives cooldowns and AI timers that should
        // stay at real speed). Frozen while the player rests at a camp (CampScreen
        // sets the flag).
        float dayLength = _worldState.SimData?.dayLengthSeconds ?? 600f;
        if (dayLength > 0f && !TimeOfDayFrozen)
        {
            AdvanceWorldClock(delta * CVars.timeScale.Value / dayLength);
        }

        bool isNight = WorldState.IsNight(_worldState.TimeOfDay01);
        if (isNight != _wasNight)
        {
            ApplyNightEdge(isNight);
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
            SweepDeadlines();
            // Same cadence, same "periodic housekeeping" band: drop walkability
            // cache entries past their TTL. This used to hang off Profiler.Tick,
            // which is [Conditional("PROFILE")] AND only reached while the F3
            // overlay is open — so in a shipping build the cache never evicted
            // at all.
            SharedWalkabilityCache.SweepStale();
        }

        DebugDangerScan(delta);

        // Record the player's path, then leash the persistent companion: a
        // following pet that fell outside the loaded world snaps onto a recent
        // off-screen footstep; a stay-commanded one freezes until its chunk
        // reloads (see TickCompanionRescueHistory / TickCompanionLeash).
        TickCompanionRescueHistory((float)delta);
        TickCompanionLeash((float)delta);

        UpdateNightDarkness((float)delta);

        _heatField?.Tick();

        TickQuests();
    }

    // Integrate the night-creature exposure meters from the two light channels at
    // the player. See the meter field declarations for the split rationale.
    private void UpdateNightDarkness(float delta)
    {
        SimData data = _worldState?.SimData;
        if (data == null)
        {
            return;
        }

        // Block light at the player (torch/campfire/lantern), normalized the same
        // way the player's perceived-light factor is, so both live on [0,1]. Cached
        // for slime vision (the concealment axis).
        float targetLightMax = data.targetLightMax > 0f ? data.targetLightMax : 0.75f;
        float block01 = 0f;
        if (_player != null)
        {
            Vector3 p = _player.GlobalPosition + Vector3.Up * (_player.data?.lightSampleHeight ?? 1f);
            _worldState.GetBlockLightWorld(Mathf.FloorToInt(p.X), Mathf.FloorToInt(p.Y), Mathf.FloorToInt(p.Z),
                out int r, out int g, out int b);
            block01 = Mathf.Clamp(Mathf.Max(r, Mathf.Max(g, b)) / 255f / targetLightMax, 0f, 1f);
        }
        _playerBlockLight01 = block01;

        // Darkness dwell — eases toward how dark the spot is right now. total01 is
        // the player's perceived light (sky + block, so daylight/moonlight AND fire
        // all lighten it); darkTarget is 1 in pitch black, 0 once the spot reaches
        // nightDarkThreshold of light. Rise/fall are separate so darkness takes a
        // while to charge up and the light clears it a bit faster.
        //
        // ...then scaled by the SUN-SHADE falloff so darkness never accrues where a
        // slime would burn: an open-sky daytime clearing reads dim to perceived-
        // light under cloud/fog, yet the sun still cooks a slime there, so SunShade01
        // (the same exposure signal the sunburn DoT uses) smoothly pulls darkTarget
        // toward 0 as the sun climbs. Cover / night have no sun → shade 1 → caves
        // and the real night are unaffected.
        float total01 = _player?.visibilityLight ?? 1f;
        float darkFromLight = data.nightDarkThreshold > 0f
            ? Mathf.Clamp((data.nightDarkThreshold - total01) / data.nightDarkThreshold, 0f, 1f)
            : (total01 <= 0f ? 1f : 0f);
        float shade = _player != null ? SunShade01(_player.GlobalPosition + Vector3.Up) : 1f;
        float darkTarget = darkFromLight * shade;
        if (darkTarget > _darknessDwell)
        {
            float step = data.nightDarkRiseSeconds > 0f ? delta / data.nightDarkRiseSeconds : 1f;
            _darknessDwell = Mathf.Min(darkTarget, _darknessDwell + step);
        }
        else
        {
            float step = data.nightDarkFallSeconds > 0f ? delta / data.nightDarkFallSeconds : 1f;
            _darknessDwell = Mathf.Max(darkTarget, _darknessDwell - step);
        }
    }

    // In-world hours spanned by TimeOfDay01 [0, 1]. The clock is a full 24-hour
    // cycle (sunrise → the next sunrise), so this is the whole day.
    private const double HoursPerDay = 24.0;

    // Short rest ("Sleep 1 hour"): fast-forwards `hours` in one-second steps,
    // replaying the status-effect tick path so timed effects expire and
    // damage-over-time integrates over the skipped span. Steps stop at the instant
    // of a lethal DoT so the player wakes (or dies) then. A nap that crosses a
    // sunrise rolls that dawn on the way, but it is never a REST — the party's
    // day (spawns, picks) stands. Returns the in-world hours actually advanced.
    public double AdvanceTime(double hours)
    {
        if (hours <= 0.0 || _player == null || _worldState == null)
        {
            return 0.0;
        }

        float dayLength = _worldState.SimData?.dayLengthSeconds ?? 600f;
        double totalSeconds = dayLength > 0f ? hours / HoursPerDay * dayLength : 0.0;

        const double stepSeconds = 1.0;
        double advanced = 0.0;
        while (advanced < totalSeconds && !_player.IsDead)
        {
            double step = System.Math.Min(stepSeconds, totalSeconds - advanced);
            _worldState.GameTimeMs += (ulong)(step * 1000.0);
            AdvanceWorldClock(step / dayLength);
            _player.TickStatusEffects((float)step);
            advanced += step;
        }

        // Catch every loaded mob up over the span the player actually survived.
        // A DoT that kills a mob here runs its normal death cascade inside Tick.
        foreach (Mob mob in GetEntities<Mob>())
        {
            mob.TickStatusEffects((float)advanced);
        }

        SyncNightEdge();
        SweepDeadlines();
        CleanupOffConditionMobs();
        return dayLength > 0f ? advanced / dayLength * HoursPerDay : 0.0;
    }

    // Debug (`time_of_day`, `next_day`): move the clock forward to the next time
    // the day reaches `timeOfDay01`, rolling any dawn crossed.
    public void AdvanceClockToTimeOfDay(float timeOfDay01)
    {
        if (_worldState == null)
        {
            return;
        }
        JumpClockTo(_worldState.NextClockAt(timeOfDay01));
        CleanupOffConditionMobs();
    }

    // Jump the clock to exactly the next sunrise — a save is a wake at sunrise,
    // and the load reproduces it from the whole number.
    private void SkipToNextSunrise()
    {
        JumpClockTo(_worldState.NextClockAt(WorldState.SunriseTimeOfDay01));
    }

    // Jump the in-world clock to `target`, rolling every dawn crossed. Loaded
    // mobs are caught up over the skipped span, but the PLAYER is deliberately
    // NOT integrated — a jump is not a nap: the rest path (RestToSunrise) clears
    // the player's transient effects and full-heals instead, so a DoT can never
    // chip or kill them in their sleep.
    private void JumpClockTo(double target)
    {
        float dayLength = _worldState.SimData?.dayLengthSeconds ?? 600f;
        double skippedSeconds = dayLength > 0f ? (target - _worldState.WorldClockDays) * dayLength : 0.0;
        _worldState.GameTimeMs += (ulong)(System.Math.Max(0.0, skippedSeconds) * 1000.0);
        AdvanceWorldClockTo(target);
        foreach (Mob mob in GetEntities<Mob>())
        {
            mob.TickStatusEffects((float)skippedSeconds);
        }
        SyncNightEdge();
        SweepDeadlines();
    }

    // Move the in-world clock forward, rolling a dawn for every sunrise crossed —
    // the ONE place the clock advances, so no path can skip a dawn. Each dawn runs
    // with the clock sitting exactly on its sunrise, so the day's weather and the
    // RNG seeds see the new day.
    private void AdvanceWorldClock(double days)
    {
        if (days > 0.0)
        {
            AdvanceWorldClockTo(_worldState.WorldClockDays + days);
        }
    }

    private void AdvanceWorldClockTo(double to)
    {
        double from = _worldState.WorldClockDays;
        if (to <= from)
        {
            return;
        }
        double firstDawn = System.Math.Floor(from) + 1.0;
        for (double dawn = firstDawn; dawn <= to; dawn += 1.0)
        {
            _worldState.WorldClockDays = dawn;
            RollDawn();
        }
        _worldState.WorldClockDays = to;
    }

    // A sunrise crossed: the day's weather, then everyone listening for dawn
    // (world scripts, the fairy budget). Deadlines need nothing here — they are
    // clock values and expire on their own (see SweepDeadlines).
    private void RollDawn()
    {
        _worldState.RollDailyWeather();
        OnDawn?.Invoke();
    }

    // Apply a day<->night change the clock made outside the per-frame poll.
    private void SyncNightEdge()
    {
        bool isNight = WorldState.IsNight(_worldState.TimeOfDay01);
        if (isNight != _wasNight)
        {
            ApplyNightEdge(isNight);
        }
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("Sim.Process");

        if (_player == null)
        {
            // The editor streams entities without a player, so its spawn queue
            // still has to drain — LoadEntitiesForChunk only enqueues, and an
            // undrained queue leaves every chunk registered with an empty entity
            // list. Recentering is driven by the editor cursor calling
            // UpdateEntityLoading, not by a player position.
            if (_editorMode)
            {
                DrainSpawnQueue();
            }
            return;
        }

        DrainSpawnQueue();
        UpdateEntityLoading(_player.GlobalPosition);
        TickSettle();

        if (CVars.debugNavGrid.Value)
        {
            NavGridDebug.Draw(this, _player.GlobalPosition, _player.TraversalProfileForQuery());
        }

        if (CVars.debugMelee.Value)
        {
            HurtBoxDebug.Draw(this, _player.GlobalPosition);
        }
    }

}
