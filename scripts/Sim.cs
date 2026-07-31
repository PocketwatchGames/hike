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
    public double TimeOfDayAbsolute => _worldState.TimeOfDayAbsolute;
    // Normalized awake-day clock (0 = sunrise … 1 = midnight). Paired with
    // DayNumber by TimeOfDay-expiring status effects, which can't use the summed
    // TimeOfDayAbsolute alone (midnight and the next sunrise share one value).
    public double TimeOfDay01 => _worldState.TimeOfDay01;

    // Fired once when the day advances at sunrise (a sleep-to-sunrise). A shared
    // day-cadence hook — forge reactivation, daily weather re-roll, permanent
    // removal of fallen party members, spawn/quest bookkeeping — so those don't
    // each poll the clock. Passes the new DayNumber.
    public event Action<int> OnNewDay;

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

    // Explicit whole-day counter, only advanced by AdvanceToNextSunrise (the day
    // cycle no longer rolls over on its own — it pauses at midnight until sleep).
    // Dawn-expiring deadlines compare against this (there is no wall-clock sunrise
    // to project toward now).
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
        // fairySpawnDescriptor wired up.
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

        // Advance normalized time-of-day toward midnight, CLAMPING there —
        // the celestial cycle pauses at midnight and only a sleep advances to
        // the next day's sunrise (Sim.AdvanceToNextSunrise). time_scale lets
        // the player fast-forward the cycle without disturbing GameTimeMs (which
        // drives cooldowns and AI timers that should stay at real speed).
        // Frozen while the player rests at a camp (CampScreen sets the flag).
        float dayLength = _worldState.SimData?.dayLengthSeconds ?? 600f;
        if (dayLength > 0f && !TimeOfDayFrozen && _worldState.TimeOfDay01 < WorldState.MidnightTimeOfDay01)
        {
            double todDelta = delta * CVars.timeScale.Value / dayLength;
            double tod = System.Math.Min(WorldState.MidnightTimeOfDay01, _worldState.TimeOfDay01 + todDelta);
            _worldState.TimeOfDay01 = tod;
            _worldState.TimeOfDayAbsolute = _worldState.DayNumber + tod;
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

        // Retire any fallen member whose revive deadline the day cycle just passed
        // (client frees the body via onPartyMemberExpired), before quests tick so a
        // same-frame retirement fails that member's rescue quest this frame.
        CheckReviveDeadlines();
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
            Vector3 p = _player.GlobalPosition;
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

    // In-world hours in the AWAKE portion of a day (sunrise → midnight), mapped
    // onto TimeOfDay01 [0, 1], and the elided pre-dawn gap (midnight → sunrise)
    // that a sleep-to-sunrise skips. 18 + 6 = a 24-hour day; only the 18h awake
    // window is ever on the clock.
    private const double AwakeHoursPerDay = 18.0;
    private const double PreDawnHours = 6.0;

    // Short in-day rest ("Sleep 1 hour"): fast-forwards `hours` of the awake day
    // in one-second steps, replaying the status-effect tick path so timed effects
    // expire and damage-over-time integrates over the skipped span. Steps stop at
    // the instant of a lethal DoT so the player wakes (or dies) then. NEVER rolls
    // the day — the advance is capped at midnight (only AdvanceToNextSunrise
    // crosses to the next day). Returns the in-world hours actually advanced.
    public double AdvanceTime(double hours)
    {
        if (hours <= 0.0 || _player == null || _worldState == null)
        {
            return 0.0;
        }

        float dayLength = _worldState.SimData?.dayLengthSeconds ?? 600f;
        double requestedSeconds = dayLength > 0f ? hours / AwakeHoursPerDay * dayLength : 0.0;
        // A nap can't pass midnight — cap the advance at the awake day's end.
        double secondsToMidnight = dayLength > 0f
            ? (WorldState.MidnightTimeOfDay01 - _worldState.TimeOfDay01) * dayLength
            : 0.0;
        double totalSeconds = System.Math.Min(requestedSeconds, System.Math.Max(0.0, secondsToMidnight));
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
            ApplyNightEdge(isNight);
        }
        CleanupOffConditionMobs();
        // NOTE: a short nap deliberately does NOT reset the world's spawns — only
        // rolling over to the next day does (see AdvanceToNextSunrise). Napping an
        // hour shouldn't repopulate a camp the player just cleared.
        return dayLength > 0f ? advanced / dayLength * AwakeHoursPerDay : 0.0;
    }

    // Sleep-to-sunrise: the ONLY path that advances the day. Jumps straight to
    // the next day's sunrise, rolls fresh day/night weather, and fires OnNewDay.
    // Loaded mobs are caught up over the whole skipped span (rest of the awake
    // day + the pre-dawn gap), but the PLAYER is deliberately NOT integrated
    // here — the sleep caller (GameClient.PerformSleepAdvance) clears the
    // player's status effects and full-heals instead, so a DoT can never chip or
    // kill them in their sleep. Returns the real-time seconds skipped (for
    // GameTimeMs-aged cooldowns). Also used by the death "sleep off a fallen
    // member" flow.
    public double AdvanceToNextSunrise()
    {
        if (_worldState == null)
        {
            return 0.0;
        }
        float dayLength = _worldState.SimData?.dayLengthSeconds ?? 600f;
        double awakeRemaining = dayLength > 0f
            ? (WorldState.MidnightTimeOfDay01 - _worldState.TimeOfDay01) * dayLength
            : 0.0;
        double preDawnSeconds = dayLength > 0f ? dayLength * (PreDawnHours / AwakeHoursPerDay) : 0.0;
        double skippedSeconds = awakeRemaining + preDawnSeconds;

        _worldState.GameTimeMs += (ulong)(skippedSeconds * 1000.0);
        _worldState.DayNumber += 1;
        _worldState.TimeOfDay01 = WorldState.SunriseTimeOfDay01;
        _worldState.TimeOfDayAbsolute = _worldState.DayNumber + WorldState.SunriseTimeOfDay01;
        _worldState.RollDailyWeather();
        // Spoil perishables sitting in the shared party stashes (the backpack is
        // swept continuously by Player.TickItemExpiry).
        _worldState.SimState?.PruneExpiredPerishables(_worldState.DayNumber);

        foreach (Mob mob in GetEntities<Mob>())
        {
            mob.TickStatusEffects((float)skippedSeconds);
        }

        _wasNight = WorldState.IsNight(_worldState.TimeOfDay01);
        RefreshTimeOfDayEntities();
        // Roster day-roll: draw the day's well-rested member BEFORE OnNewDay
        // fires, so the client's node-refresh subscriber (well-rested buff +
        // lantern refuel) reads the updated PlayerState flags.
        Party party = _worldState?.SimState?.Party;
        party?.AdvanceRestAndPickWellRested(_wellRestedRng);
        // A new day resets the camp's leader + spell pick (the spell attunement is
        // cleared per-member in the client's OnNewDay node-refresh), so the next camp
        // forces a fresh choice.
        party?.RequireLeaderChoice();
        OnNewDay?.Invoke(_worldState.DayNumber);
        CleanupOffConditionMobs();
        // A full day rolled over — reset the world's encounters to their spawn
        // state (mobs home + full-health, killed ones revived, dropped loot/arrows
        // swept). Covers both sleep-to-sunrise and the death day-roll.
        ResetSpawns();
        return skippedSeconds;
    }

    private void AdvanceClocks(double seconds, float dayLength)
    {
        _worldState.GameTimeMs += (ulong)(seconds * 1000.0);
        if (dayLength > 0f)
        {
            double todDelta = seconds / dayLength;
            double tod = System.Math.Min(WorldState.MidnightTimeOfDay01, _worldState.TimeOfDay01 + todDelta);
            _worldState.TimeOfDay01 = tod;
            _worldState.TimeOfDayAbsolute = _worldState.DayNumber + tod;
        }
    }

    public override void _Process(double delta)
    {
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

        if (CVars.navGridDebug.Value)
        {
            NavGridDebug.Draw(this, _player.GlobalPosition);
        }
    }
}
