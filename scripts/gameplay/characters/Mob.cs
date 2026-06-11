using System;
using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class Mob : RigidBody3D, IWorldEntity, IActionActor, IInteractive
{
    [Export] private CollisionShape3D _collisionShape;
    // The mob's 3D skinned-model animator. Wired in every mob .tscn; _Ready
    // activates it as the live visual and subscribes its footstep hooks.
    [Export] private ModelAnimator _modelAnimator;
    [Export] private Node3D _mesh;
    // Optional in-hand prop renderer (hand-bone sockets). Only the held-torch
    // channel is used for mobs; null on species that never carry a held prop.
    [Export] private HeldItemVisual _heldVisual;
    [Export] private HurtBox _hurtBox;
    [Export] public Node3D HudAnchor;
    [Export] public PackedScene HudScene;
    // Per-species authored interaction verbs (e.g. species-specific Trade,
    // Inspect). Talk and GiveItem are NOT authored here — they auto-inject
    // from SimData for any mob whose SimState carries a Conversation; see
    // GetActions below. Empty on mobs that aren't interactable and don't
    // talk — the InteractiveBox on the mob's .tscn shouldn't be wired in
    // that case so the player never highlights them.
    [Export] private Godot.Collections.Array<InteractiveAction> _interactiveActions = new();

    [ExportGroup("FX")]
    // One-shot blood spawned on a non-lethal hit. World-parented so the puff
    // stays where the hit landed even as the mob keeps moving.
    [Export] private PackedScene _bloodDamageFx;
    // One-shot death blood. Per-mob in the .tscn so each species can pick the
    // appropriate small/medium/large variant from scenes/effects/.
    [Export] private PackedScene _deathFx;
    // One-shot splash on the alive→in-water transition (voxel-detected).
    [Export] private PackedScene _waterEnterSplashFx;
    // Continuous loop scenes (see Fx._loop). Parented to the mob
    // so they track the body; held alive while in the matching state and
    // Stop()'d when leaving.
    [Export] private PackedScene _waterMovementLoopFx;
    [Export] private PackedScene _foliageMovementLoopFx;
    // Burrow lifecycle effects. Loop runs while the mob is mid-descent
    // (`burrowing` flag); complete fires on the burrowing→burrowed transition;
    // emerge fires when the mob leaves either burrow state and re-surfaces.
    [Export] private PackedScene _burrowLoopFx;
    [Export] private PackedScene _burrowCompleteFx;
    [Export] private PackedScene _burrowEmergeFx;
    // Dirt-mound prop shown at the surface while the mob is fully burrowed.
    // Parented to the mob (not the world) so it tracks the mob and is freed
    // automatically the moment the mob emerges, dies, or despawns.
    [Export] private PackedScene _burrowMoundScene;
    // Per-anim-state loops. Driven by the loopAnim picked in UpdateAnimation —
    // exactly one (or none) is active at a time, swapped on state change.
    // Authored per-species so each mob can have its own breathing / footstep
    // signature.
    [Export] private PackedScene _idleLoopFx;
    [Export] private PackedScene _runLoopFx;
    [Export] private PackedScene _swimIdleLoopFx;
    // Per-species voice bank — the hurt / death / yell clips that ride on top
    // of the shared blood / death-splat / yell-particle effects. Same VoiceData
    // resource the player uses (the player keys a per-gender map; a mob has one
    // bank per species). Any slot may be null — the asset library doesn't
    // always include a hurt VO for every species, and not every species yells.
    // pitchShift on the bank re-voices shared clips per species / villager.
    [Export] private VoiceData _voice;
    // Armor lifecycle one-shots. See Player for the lifecycle: depleted on
    // the hit that drains the bar to zero; rechargeStart when the post-hit
    // delay elapses; recoverStart when the recharge follows a full depletion.
    [Export] private PackedScene _armorDepletedFx;
    [Export] private PackedScene _armorRechargeStartFx;
    [Export] private PackedScene _armorRecoverStartFx;

    [ExportGroup("Footsteps & Footprints")]
    // Per-ground-type one-shot effect played at the mob's feet on each
    // footfall. Authored in each mob .tscn; missing keys silently emit
    // nothing.
    [Export] private Godot.Collections.Dictionary<EGroundType, PackedScene> _footstepEffects;
    // Minimum horizontal speed² to count as "moving" for loop-FX gating
    // (water swim loop, tall-grass rustle). Footstep cadence itself is
    // frame-driven and ignores this.
    [Export] private float _movingMinSpeedSq = 0.25f;
    // Per-mob footprint texture projected onto the ground on each footfall.
    // Batched by FootprintScatter into one MultiMesh per texture; the shared
    // material, per-ground tints, and mob-print discovery gate live on SimData.
    [Export] private Texture2D _footprintTexture;
    // World-space size (meters) of the projected footprint decal — X is the
    // print's width (perpendicular to facing), Y is its length (along facing).
    [Export] private Vector2 _footprintSize = new(0.3f, 0.4f);

    // Seconds to lerp visibility/silhouette toward their target. 0.1s is
    // short enough that transitions read as "now" rather than a slow fade
    // (this is an awareness cue, not a visual flourish) while still giving
    // the dither pattern a chance to resolve rather than popping.
    private const float VisibilityFadeTime = 0.3f;

    // Height (base/mesh-local units) of the elite crown above the mob's HUD
    // anchor (≈ head top). Parented under the elite-scaled mesh, so this rides
    // the 25% bump into world space and the halo floats just over the head.
    private const float CrownHeadMargin = 0.4f;

    // Fallback outward arc speed for ejected loot when a mob has no MobData
    // (defensive — real mobs always carry one, but the elite-trophy drop path
    // must still pick a speed). Mirrors MobData.lootEjectSpeed's default.
    private const float DefaultLootEjectSpeed = 5f;

    public float discoveryProgress
    {
        get
        {
            MobData md = mobData;
            if (md == null)
            {
                return 0f;
            }
            float span = Mathf.Max(0.0001f, md.discoveredThreshold - md.detectedThreshold);
            return Mathf.Clamp((_simState.PlayerPerception - md.detectedThreshold) / span, 0f, 1f);
        }
    }

    // Canonical sim state lives in MobSimState. The Mob node is a view; these
    // properties forward through so call sites and animations stay terse.
    public bool alive { get => _simState.Alive; set => _simState.Alive = value; }
    public float maxHealth => _simState.MaxHealth;
    public float health { get => _simState.Health; set => _simState.Health = value; }
    public float maxArmor => mobData?.maxArmor ?? 0f;
    public float armor { get => _simState.Armor; set => _simState.Armor = value; }
    // Elite marker + the elite's signature status effect. Read by MobHUD to
    // badge the health bar with the effect's icon. The signature is the first
    // active effect tagged EEffectCategory.Elite (applied at spawn from the
    // zone pool); resolving from the live list rather than _simState keeps the
    // HUD keyed on the same categorization the strip filter uses. Immutable
    // after spawn, so the HUD samples it once at init.
    public bool IsElite => _simState?.Elite ?? false;
    public StatusEffectData EliteStatusEffect => _statusEffects?.FirstOfCategory(EEffectCategory.Elite);
    // True when at least one active status effect flags `incapacitates`.
    // Drives AI suppression and the no-yell-while-CC'd path. Dizzy authors
    // the flag; future Frozen / Knocked-Down would too without touching Mob.
    public bool incapacitated => _statusEffects?.Incapacitated ?? false;

    // Composite crit-vulnerability in [0, 1] from active status effects.
    // Folded into the crit decision against HitInfo.critRoll. Dizzy authors
    // vulnerable=1 so a dizzied mob always crits on a triggered hit.
    public float vulnerable => _statusEffects?.Vulnerable ?? 0f;
    public EPlayerPerceptionState playerPerceptionState { get => _simState.DiscoveryState; set => _simState.DiscoveryState = value; }
    // Circumstances this mob required to spawn (see ESpawnConditions). Read by
    // World.CleanupOffConditionMobs to despawn a mob whose conditions have
    // lapsed once the player is far and unaware. None = unconditional, never
    // cleaned up on this account.
    public ESpawnConditions spawnConditions => _simState.SpawnConditions;
    public MobData mobData => _simState.MobData;
    // Per-instance language override (set by WorldGen / world files) takes
    // precedence over MobData.language. Mirrors SpeakDialogue's resolution
    // order so dialogue and any other UI surface (merchant screen, etc.)
    // agree on which language to scramble against.
    public LanguageData SpokenLanguage => _simState?.Language ?? mobData?.language;
    public StringName defaultBehavior => _simState?.InitialBehavior ?? (mobData != null ? mobData.defaultBehavior : (StringName)"Idle");
    // True when this mob is the player's companion/pet (drives the follow/stay
    // brain and World companion tracking).
    public bool IsCompanion => mobData != null && mobData.isCompanion;
    // Companion follow/stay command state, read by the companion brain's
    // transition conditions. Backed by sim state so it persists across
    // streaming. Toggled by the player's command input.
    public bool StayCommanded
    {
        get => _simState != null && _simState.StayCommanded;
        set { if (_simState != null) { _simState.StayCommanded = value; } }
    }
    public void ToggleStayCommand()
    {
        StayCommanded = !StayCommanded;
    }
    // Per-instance merchant stock — the MerchantScreen reads this to
    // populate its shop side, filtering out secret entries. Null on mobs
    // that were never seeded with stock (any non-merchant mob).
    public List<MobInventoryItem> Inventory => _simState?.Inventory;
    public Vector3 weaponPosition => GlobalPosition;
    public Vector3 spawnPosition => _simState.SpawnPosition;
    public float spawnRotationY => _simState.SpawnRotationY;
    public InvestigateState? investigation { get => _simState.Investigation; set => _simState.Investigation = value; }
    public bool yelled { get => _simState.Yelled; set => _simState.Yelled = value; }
    public bool burrowed { get => _simState.Burrowed; set => _simState.Burrowed = value; }
    public bool burrowing { get => _simState.Burrowing; set => _simState.Burrowing = value; }
    // A mob can only dig into solid ground beneath it — not while swimming
    // and not mid-fall. Gates both the AI transition into the burrow behavior
    // (CanBurrowAndOutOfRangeCondition) and the per-frame descent start in
    // _PhysicsProcess, so a mob fleeing across water or off a ledge keeps
    // running instead of freezing into a do-nothing burrow pose.
    public bool CanBurrowNow => !_swimming && !IsInWater()
        && LinearVelocity.Y > -mobData.fallEnterSpeed;
    // Shared "is this a valid target for `weapon` right now" predicate used by
    // both the aiming reticle's mob-lock styling and the ranged auto-aim, so
    // the visual telegraph and the assist always agree. Hidden / Detected
    // mobs aren't yet "real" to the player's awareness so neither path should
    // acknowledge them; direct hits remain possible because the actual
    // hitscan/projectile code doesn't gate on this. Takes the weapon so
    // future weapon-specific targeting rules (e.g. a weapon that can lock
    // burrowed prey) can land here without churn at the call sites.
    public bool CanTarget(WeaponData weapon)
    {
        return alive && !burrowed && playerPerceptionState == EPlayerPerceptionState.Discovered;
    }
    public float skyBrightness => _simState.SkyBrightness;
    public float sunExposure => _simState.SunExposure;
    public float ambientLight => _simState.AmbientLight;
    // Whether this mob's behaviors should request a torch this frame. True
    // when local ambient is dark OR the world clock says it's night —
    // skyBrightness can stay non-trivial under moonlight, so the time-of-day
    // gate catches the "it's night and the mob isn't standing in a sunbeam"
    // case where local ambient alone wouldn't cross the threshold.
    //
    // Hysteresis: the ambient comparison uses MobTorchLightThreshold while
    // off and MobTorchDouseThreshold while on (Light < Douse), so ambient
    // drifting near a single cutoff doesn't flicker the torch on/off every
    // tick. Tunable from SimData.
    public bool ShouldUseTorch
    {
        get
        {
            // A burrowed mob has no business holding a torch — its mesh is
            // hidden, and the deposit would leak block light through solid
            // geometry. Short-circuit so behaviors querying ShouldUseTorch
            // (and the per-tick write to AIOutput.useTorch) douse on the
            // burrow transition without each behavior having to special-
            // case it.
            if (burrowed || burrowing) { return false; }
            WorldState ws = _world?.WorldState;
            SimData sim = ws?.SimData;
            float light = sim?.MobTorchLightThreshold ?? 0.20f;
            float douse = Mathf.Max(sim?.MobTorchDouseThreshold ?? 0.30f, light);
            float threshold = _torch != null ? douse : light;
            if (_simState.AmbientLight < threshold) { return true; }
            if (ws == null) { return false; }
            double tod = ws.TimeOfDay01;
            return tod < 0.25 || tod >= 0.75;
        }
    }
    public ulong burrowTimeMs { get => _simState.BurrowTimeMs; set => _simState.BurrowTimeMs = value; }
    public bool playerCanSee => _world.GameTimeMs < _simState.VisibleTimeMs;
    // Perception/triggered forward through the first perception slot (the player
    // in singleplayer). Multi-target logic operates directly on PerceptionTargets
    // in TickAI; these accessors exist for the HUD and inline Mob logic that only
    // cares about the primary target.
    public float perception
    {
        get => _simState.PerceptionTargets[0].perception;
        set => _simState.PerceptionTargets[0].perception = value;
    }
    public bool triggered
    {
        get => _simState.PerceptionTargets[0].triggered;
        set => _simState.PerceptionTargets[0].triggered = value;
    }

    // Per-mob per-frame perception breakdowns for the debug HUD overlay.
    // Written each perception tick from UpdatePerception; consumed by
    // MobHUD when CVars.debugPlayerPerception / debugMobPerception is set.
    public PerceptionDebug playerToMobDebug;
    public PerceptionDebug mobToPlayerDebug;

    // Alias for _modelAnimator, bound in _Ready. The EAnimation state machine
    // drives animation through this.
    private ModelAnimator _animator;

    private MobSimState _simState;
    World _world;
    public World World => _world;

    // Navigation controller — owns this mob's pathfinding/steering intent.
    // Behaviors call _navigator.Goto/Wander/Stop; the navigator writes
    // output.pathTarget at the bottom of TickAI. Lazily created the first
    // time TickAI runs after Initialize so it's available before the first
    // Behavior.Run call (mobData and World are required for construction).
    private MobNavigator _navigator;
    public MobNavigator Navigator => _navigator;

    // Shared action timeline runner (same class the player uses). Populated
    // each frame from AIOutput.attackProfile by BehaviorAttack; the runner
    // walks the timeline, fires combat events, and gates re-entry via its
    // own busy state. Per-attack cadence is enforced upstream by the
    // BehaviorAttack cooldown so mobs don't spam attacks every frame.
    private ActionRunner _runner;
    public ActionRunner Runner => _runner;

    // Status effects (poison, heal-over-time, hot, wet, ...). Same lifecycle
    // model as Player — multiple instances of the same data stack and tick
    // independently. Mob has no HUD currently, but the data is exposed for
    // future debug / HUD work. Wired in Initialize once `_world` is known.
    StatusEffectController _statusEffects;
    public IReadOnlyList<StatusEffectState> StatusEffects => _statusEffects.StatusEffects;

    // Rolls up HitInfo.dot per-frame damage / heal into one onDamage / onHeal
    // invocation per second. Same shape as the player's accumulator — a fast
    // poison or burn zone shouldn't spawn a floating number every physics
    // frame above the mob.
    readonly DotHudAccumulator _dotHud = new();

    readonly List<Foliage> _foliageCollisions = new();
    float _terrainSpeed = 1f;

    // Arrows currently stuck in this mob. Populated by StickArrow when the
    // player hits with a bow; drained by Die (each becomes loose loot with
    // an outward impulse, transferring the source-weapon binding) and by
    // _ExitTree (any remaining → ammo returns to the weapon, no loot).
    readonly List<ArrowStuck> _stuckArrows = new();
    readonly WaterRippleEmitter _rippleEmitter = new();
    // Active loop instances. See Player for the lifecycle pattern — null
    // when the matching state isn't held; created on activation, Stop()'d
    // and dropped on deactivation.
    Fx _waterMovementLoop;
    Fx _foliageMovementLoop;
    Fx _burrowLoop;
    // Live dirt-mound instance while burrowed (null otherwise).
    Node3D _burrowMound;
    // Single active anim-loop reference + the scene it was created from. We
    // swap wholesale on transitions instead of cross-fading — simple, and
    // the listener barely registers the gap in practice.
    Fx _animLoopFx;
    PackedScene _animLoopScene;
    // Previous-tick burrow flags so we can detect the false→true edges that
    // drive the complete and emerge one-shots.
    bool _prevBurrowing;
    bool _prevBurrowed;
    // Tracks the previous frame's water-at-feet sample so we can detect the
    // false→true transition and fire one splash, not one per frame.
    bool _wasInWaterPrev;
    // True when the water above this mob's feet is at least
    // MobData.swimDepthThreshold meters deep — swimming physics is applied
    // and nav clamps to swimSpeed. Wading (in water but below the
    // threshold) leaves both off; the existing footstep / loop fx still
    // run from the simpler voxel-at-feet check.
    bool _swimming;
    float _waterSurfaceY;
    // The perch this flying mob is resting on (or inbound to and has claimed),
    // or null when grounded / free-flying. Set by the flight behaviors via
    // SettleOnPerch / LeavePerch.
    private Perch _claimedPerch;
    public Perch ClaimedPerch => _claimedPerch;
    // True while resting on a perch — drives the per-frame re-snap to the
    // perch's (camera-dependent) visual landing point in _Process.
    private bool _perched;
    // True while the flier's movement collision is suppressed for flight (mask
    // zeroed). Edge-tracked so we only touch the mask on takeoff / landing.
    private bool _flightCollisionDisabled;
    // Reused scratch buffer for the yell broadcast's spatial-hash query so a
    // periodic alarm doesn't allocate a list every call.
    private readonly List<Mob> _yellReceivers = new();
    // Current fade values, stepped toward their target every _Process tick.
    // Start at 0 so a freshly-spawned mob dithers IN rather than popping on
    // its first frame; if it's already within visible time the target snaps
    // to 1 and the fade plays out over VisibilityFadeTime.
    private float _visibility;
    private float _silhouette;

    // Last values written through to Godot setters in _Process / PostTickMove.
    // Each Godot property assignment marshals into native — at high mob count
    // the bulk of these methods is the cost of setters firing every frame
    // even when the value hasn't changed since last frame. Cache + skip-on-
    // equal cuts the per-mob cost ~10× for stable mobs. NaN sentinels force
    // the first frame through so initial state is always pushed.
    private bool _lastMeshVisible;
    private bool _lastMeshVisibleInit;
    private bool _lastAnimProcess;
    private bool _lastAnimProcessInit;
    // Per-frame mob-animation census, published as readable gauges (mob_count /
    // mob_anim_active / mob_anim_frozen) — instantaneous counts, not the
    // per-window sums a plain counter gives. The first mob to run its gate each
    // process frame publishes the prior frame's tally and resets.
    private static ulong _animCensusFrame = ulong.MaxValue;
    private static int _animCensusActive;
    private static int _animCensusFrozen;
    private bool _lastHudVisible;
    private bool _lastHudVisibleInit;
    // Last discovery-presentation values pushed to the 3D model. The model push
    // isn't self-gating, so cache here and only push on change.
    private bool _lastModelVisualsInit;
    private float _lastModelVisibility;
    private float _lastModelSilhouette;
    private float _lastModelXray;
    private bool _lastModelCastShadow;
    // Spinning emissive halo over an elite mob; null on non-elites. Spawned in
    // Initialize, fed the same discovery presentation as the body each frame,
    // freed on death.
    private EliteCrown _crown;
    private float _lastLinearDamp = float.NaN;
    private float _lastGravityScale = float.NaN;
    // Authored GravityScale captured on first swim entry. The swim gate
    // sets GravityScale=0 (buoyancy + drag own vertical motion; without
    // this engine gravity bleeds in alongside, and the net force at
    // moderate depth wasn't enough to keep mobs from sinking to the
    // seafloor); on exit we restore this original value so a mob with a
    // non-default scale in its .tscn keeps it.
    private float _gravityScaleAuthored = 1f;
    private bool _gravityScaleCaptured;
    private bool _gravityScaleSwimActive;
    private bool _impulseApplied;
    // Tracks the last observed mob_physics CVar value so the bisection
    // toggle's force-freeze / force-unfreeze block only fires on actual
    // CVar transitions, not every physics tick. Without this, the block
    // unconditionally undoes the per-mob auto-freeze every tick (auto-
    // freeze sets Freeze=true at end of tick N, top of tick N+1 sees
    // physicsEnabled && Freeze and unfreezes — defeating the auto-freeze).
    // Initialized to the CVar default so a mob that boots with the CVar
    // already set doesn't trigger a spurious transition on first tick.
    private bool _lastMobPhysicsCvar = true;

    // Active torch carrier light, instantiated from MobData.torch when the
    // latest AIOutput requests useTorch and the player remembers this mob.
    // Same instantiate / QueueFree pattern as Player.RefreshCarriedLight —
    // the FX scenes (LightOn / LightOff / Loop) live on the carrier scene
    // itself, so the mob just owns presence/lifetime, not styling.
    private MovingLight _torch;

    // Latched one-shot animation. Same model as Player: PlayOneShot pins the
    // animator on a non-looping clip; UpdateAnimation defers the loop pick
    // until the animator's Finished flips. Behaviors emit via
    // AIOutput.oneShotAnim; combat events route here through PlayAnim.
    private EAnimation? _oneShotAnim;
    // Game-time at which the mob first started falling fast (vel.Y below the
    // FallEnterSpeed threshold). Cleared as soon as the body is no longer
    // descending. Used to gate the "fall" loop behind a sustained-fall grace
    // window — short pops off geometry while running don't earn the anim.
    private ulong _airborneStartMs;

    // Game-time at which the mob first went "intent-moving but pinned" — the
    // navigator still wants to move yet the body is sitting at ~0 speed. Cleared
    // the moment it makes progress (or stops wanting to move). Gates the
    // run-in-place masking behind a grace window so a brief jam against a wall
    // keeps the run anim, but a goblin genuinely wedged against the player / a
    // prop / an unreachable encircle slot falls back to idle instead of running
    // forever in place.
    private ulong _intentStuckStartMs;

    public static Mob Create(World world, MobSimState data)
    {
        var instance = data.Scene.Instantiate<Mob>();
        instance.Initialize(world, data);
        return instance;
    }

    public override void _Ready()
    {
        CollisionLayer = (uint)ECollisionLayer.Mob;
        // Mob-vs-mob is on so crowds physically separate — combined with
        // the existing LinearDamp the body falls into a stable pack with
        // its neighbors rather than overlapping or stacking. Player bit
        // is kept because the player's CollisionMask no longer carries
        // Mob; keeping it here is harmless and leaves a single audit
        // point if we ever want one-way physical interaction.
        CollisionMask = (uint)(ECollisionLayer.Solid | ECollisionLayer.Player | ECollisionLayer.Mob);
        AxisLockAngularY = true;

        if (_hurtBox != null)
        {
            _hurtBox.OnHit = Hit;
            _hurtBox.GetHitType = GetHitType;
            _hurtBox.GetHitTriggers = QueryHitTriggers;
            // Hit filter: ActorTeam is read per-hit so a tamed companion's
            // Friendly override (and any future runtime team change) applies.
            _hurtBox.CanHit = (hit) =>
                hit.friendlyFire || !Teams.AreAllied(hit.attackerTeam, ActorTeam);
            foreach (Node child in _hurtBox.GetChildren())
            {
                if (child is CollisionShape3D shape)
                {
                    _hurtBoxShape = shape;
                    break;
                }
            }
        }

        // Activate the model as the live visual. It lives under _mesh
        // (MeshContainer), so the discovery visibility/scale logic in _Process
        // still gates it when parented there.
        _animator = _modelAnimator;
        if (_modelAnimator != null)
        {
            _modelAnimator.SetActive(true);
            // Footfalls fire from a Call Method Track authored on the model's
            // movement clips (OnFootstep) at the exact foot-contact frame.
            _modelAnimator.OnFootstep += EmitFootstep;
        }
    }

    // Spawn one footstep + footprint at the current foot position, fired from
    // the model's foot-contact method track. Skip while
    // in water (the wading ripple covers it). The mob_footstep_fx CVar gates
    // the audible/visible FX puff for perf bisection; the perception gate
    // suppresses footsteps the player has no awareness of. Footprints are
    // always laid (subject to the discoverability gate) regardless of CVar.
    private void EmitFootstep()
    {
        if (_world == null)
        {
            return;
        }
        WorldState ws = _world.WorldState;
        if (ws == null)
        {
            return;
        }
        Vector3 pos = GlobalPosition;
        int fx = Mathf.FloorToInt(pos.X);
        int fy = Mathf.FloorToInt(pos.Y);
        int fz = Mathf.FloorToInt(pos.Z);
        bool inWater = ws.GetVoxelWorld(fx, fy, fz) == VoxelType.Water;
        if (inWater)
        {
            return;
        }
        EGroundType ground = GroundTypeResolver.Resolve(ws, pos);
        bool perceived = _simState.PlayerPerception > 0f;
        if (perceived && CVars.mobFootstepFx.Value)
        {
            FootstepEmitter.Emit(_world, pos, ground, _footstepEffects);
        }
        float fpAlphaMul = _statusEffects?.FoldStat(EStat.FootprintAlpha, 1f) ?? 1f;
        float fpDurMul = _statusEffects?.FoldStat(EStat.FootprintDuration, 1f) ?? 1f;
        bool perceivedAtEmit = _simState.PlayerPerception > 0f || _simState.MemoryTimeMs > _world.GameTimeMs;
        FootprintEmitter.Emit(_world, pos, GlobalRotation.Y, ground, _footprintTexture, _footprintSize, fpAlphaMul, fpDurMul, gated: !perceivedAtEmit);
    }

    public void OnSpawned(World world)
    {
        world.MobSpatialHash.Add(this);
        if (IsCompanion)
        {
            world.RegisterCompanion(this);
        }
        TreeExiting += () =>
        {
            SyncToSimState();
            world.MobSpatialHash.Remove(this);
            if (IsCompanion)
            {
                world.UnregisterCompanion(this);
            }
            // Release any encircle slot held against any target so the
            // ring doesn't keep a dead mob occupying a slot for the rest
            // of the encounter.
            world.EncircleAllocator.ReleaseSlot(this);
            world.onMobRemoved?.Invoke(this);
        };
        world.onMobSpawned?.Invoke(this);
    }

    public void Initialize(World world, MobSimState simState)
    {
        _world = world;
        _simState = simState;
        Position = simState.WorldPosition;
        Rotation = new Vector3(0, simState.RotationY, 0);
        // Navigator depends on mobData (for the traversal profile) and World
        // (for voxel queries), so construct here after both are wired.
        _navigator = new MobNavigator(this);
        _runner = new ActionRunner(this);
        _statusEffects = new StatusEffectController(this, world, ApplyStatusHealthDelta, ComposeMaskMul, DrainMaxHealth);
        InitBehaviors();
        world.AddChild(this);
        // A mob loaded mid-burrow (from save data) needs its rigid body +
        // collision layer to match — _Ready (run during AddChild above)
        // applied the default Mob/Env|Player setup, and SimState is the
        // only authority on burrow state at this point.
        if (burrowing || burrowed)
        {
            SetBurrowed(true);
        }
        // Elite mobs carry a single signature status effect drawn at spawn from
        // their zone's pool (MobSimState.EliteStatusEffect). Applied here, after
        // AddChild above, so the mob is already in the tree and the effect's
        // start/loop Fx parent and position correctly.
        if (_simState.Elite)
        {
            // Shared elite buff applied to every elite regardless of zone. It's
            // categorized Permanent (not Elite), so it never collides with the
            // zone signature effect that MobHUD surfaces as the elite icon.
            StatusEffectData sharedElite = _world?.SimData?.EliteStatusEffect;
            if (sharedElite != null)
            {
                _statusEffects.Add(sharedElite);
            }
            // Zone-specific signature effect (the one MobHUD shows as the icon).
            if (_simState.EliteStatusEffect != null)
            {
                _statusEffects.Add(_simState.EliteStatusEffect);
            }
            SpawnEliteCrown();
        }

        // Intrinsic spawn-time effect (e.g. a summoned minion's lifelong self-
        // drain). Applied here alongside the elite effects so it's in place
        // before vitals finalize and its start/loop Fx parent correctly. A
        // fresh spawn only — a mob restored from save already carries it on its
        // persisted status controller.
        if (!_simState.RestoredFromSave && mobData?.spawnStatusEffect != null)
        {
            _statusEffects.Add(mobData.spawnStatusEffect);
        }

        // Finalize vitals now that every spawn-time modifier is in place —
        // inherent MobData.modifiers plus any elite status effects added just
        // above — so a freshly-spawned mob (elites included) starts at its full
        // modified max. Mobs store MaxHealth rather than recomposing it per
        // access (unlike the player), so this is the one point those MaxHealth/
        // MaxArmor bonuses fold into the pool. A mob loaded from save keeps the
        // Health / MaxHealth / Armor it was persisted with.
        if (!_simState.RestoredFromSave)
        {
            _simState.MaxHealth = mobData.maxHealth + ComposeStat(EStat.MaxHealth);
            _simState.Health = _simState.MaxHealth;
            _simState.Armor = mobData.maxArmor + ComposeStat(EStat.MaxArmor);
        }
    }

    // Instantiate the spinning emissive halo over an elite mob. Parented under
    // the mesh container so it rides the body's transform, sitting just above
    // the head. Shares the mob's
    // render stack via crown_lit.tres, so it silhouettes / X-rays in lockstep —
    // the per-frame discovery push happens in _Process alongside the body's.
    // No-op (and no marker) when SimData authors no crown scene.
    private void SpawnEliteCrown()
    {
        PackedScene scene = _world?.SimData?.EliteCrownScene;
        if (scene == null || _mesh == null)
        {
            return;
        }
        _crown = scene.Instantiate<EliteCrown>();
        _mesh.AddChild(_crown);
        float headY = HudAnchor != null ? HudAnchor.Position.Y : 2f;
        _crown.Position = new Vector3(0f, headY + CrownHeadMargin, 0f);
    }

    // Grow a mob uniformly by `scale`. The visual mesh scales uniformly (it's
    // base-anchored at the mob origin, so it grows upward and the feet stay on
    // the ground). The body capsule scales the same amount but is re-grounded —
    // its bottom is pinned to its original Y so the larger mob still rests on
    // terrain rather than spawning half-buried or floating. The hurtbox is NOT
    // scaled by percent: it's resized to keep the same absolute clearance
    // (radial + top/bottom) it had over the body capsule before the bump, so the
    // "reach past the body to land a hit" margin is identical regardless of
    // scale. Both shapes are duplicated first because Godot shares a scene's
    // embedded sub-resources across every instance — mutating the shared shape
    // would resize every mob of this species.
    private void ScaleMob(float scale)
    {
        float k = scale;
        if (_mesh != null)
        {
            _mesh.Scale = Vector3.One * k;
        }

        CapsuleShape3D bodyCap = DuplicateCapsule(_collisionShape);
        if (_collisionShape == null || bodyCap == null)
        {
            return;
        }

        // Original body extents (capsule is centered on its CollisionShape3D's
        // local origin, whose parent shares the mob frame).
        float rBody = bodyCap.Radius;
        float hBody = bodyCap.Height;
        float bodyBottom = _collisionShape.Position.Y - hBody * 0.5f;

        // Capture the hurtbox's clearance over the body before resizing either.
        CapsuleShape3D hurtCap = DuplicateCapsule(_hurtBoxShape);
        float radialClearance = 0f;
        float bottomClearance = 0f;
        float topClearance = 0f;
        if (hurtCap != null)
        {
            float hurtBottom = _hurtBoxShape.Position.Y - hurtCap.Height * 0.5f;
            float hurtTop = _hurtBoxShape.Position.Y + hurtCap.Height * 0.5f;
            radialClearance = hurtCap.Radius - rBody;
            bottomClearance = bodyBottom - hurtBottom;
            topClearance = hurtTop - (_collisionShape.Position.Y + hBody * 0.5f);
        }

        // Scale the body, keeping its bottom grounded.
        float rBodyNew = rBody * k;
        float hBodyNew = hBody * k;
        bodyCap.Radius = rBodyNew;
        bodyCap.Height = hBodyNew;
        SetLocalY(_collisionShape, bodyBottom + hBodyNew * 0.5f);

        // Rebuild the hurtbox from the scaled body + the original clearances.
        if (hurtCap != null)
        {
            float hurtBottomNew = bodyBottom - bottomClearance;
            float hurtTopNew = (bodyBottom + hBodyNew) + topClearance;
            hurtCap.Radius = rBodyNew + radialClearance;
            hurtCap.Height = hurtTopNew - hurtBottomNew;
            SetLocalY(_hurtBoxShape, (hurtTopNew + hurtBottomNew) * 0.5f);
        }
    }

    // Per-instance copy of a CollisionShape3D's CapsuleShape3D, assigned back so
    // edits don't bleed into other instances sharing the embedded resource.
    // Returns null (and leaves the shape untouched) for a missing node or a
    // non-capsule shape, so a species authored with a different shape type just
    // skips the resize rather than crashing.
    private static CapsuleShape3D DuplicateCapsule(CollisionShape3D node)
    {
        if (node?.Shape is not CapsuleShape3D capsule)
        {
            return null;
        }
        var copy = (CapsuleShape3D)capsule.Duplicate();
        node.Shape = copy;
        return copy;
    }

    private static void SetLocalY(Node3D node, float y)
    {
        Vector3 p = node.Position;
        p.Y = y;
        node.Position = p;
    }

    // IActionActor — what ActionRunner and ItemEventHandlers read. Forward
    // is the mob's facing direction (basis Z, matching how Mob updates
    // Rotation.Y from its yaw target). AttackHurtboxMask matches the
    // player's hurtbox layer so mob attacks land on the player; SelfHurtBoxRid
    // excludes the mob's own hurtbox from its own attack queries.
    public Vector3 ActorWorldPosition => GlobalPosition;
    public Vector3 ActorForward => GlobalTransform.Basis.Z;
    public ulong GameTimeMs => _world?.GameTimeMs ?? 0;
    public uint AttackHurtboxMask => (uint)ECollisionLayer.HurtBox;
    public Rid? SelfHurtBoxRid => _hurtBox?.GetRid();

    // World-space body center used by the player's aim assist as the "where to
    // pull toward" point. Falls back to the body root if the hurtbox or its
    // collision shape isn't authored (early in setup, or a malformed mob).
    public Vector3 AimCenter
    {
        get
        {
            if (_hurtBoxShape != null)
            {
                return _hurtBoxShape.GlobalPosition;
            }
            return GlobalPosition;
        }
    }
    CollisionShape3D _hurtBoxShape;
    public Node3D AttackerNode => this;
    public void PlayAnim(EAnimation anim)
    {
        PlayOneShot(anim);
    }

    // Seeded from an ApplyMotion event in the mob's attack profile (e.g. a
    // goblin claw's dart). Direction is the mob's current forward at the
    // moment the event fires — the per-tick force in ApplyMotionPhysics
    // holds that vector for `duration` seconds even if BehaviorAttack keeps
    // rotating the body to track the player. Zero duration no-ops so a
    // profile authoring a motionless event doesn't pin the body.
    public void ApplyMotion(float speed, float duration, bool freezeGravity)
    {
        if (duration <= 0f || speed <= 0f)
        {
            return;
        }
        Vector3 forward = ActorForward;
        forward.Y = 0f;
        if (forward.LengthSquared() < 0.0001f)
        {
            return;
        }
        _simState.MotionVelocity = forward.Normalized() * speed;
        _simState.MotionTime = duration;
        _simState.MotionFreezeGravity = freezeGravity;
    }

    // Mobs don't have a stamina pool yet; attack tiers always pass the gate
    // and the spend is a no-op. If mob stamina is ever authored, both can
    // route into a MobSimState pool the same way the player's does.
    public bool HasStamina(float amount) => true;
    public void ConsumeStamina(float amount) { }

    // Mobs don't have a blood-mana pool — attack tiers with bloodCost
    // always pass the gate and the spend is a no-op.
    public bool HasBlood(float amount) => true;
    public void DrainBlood(float amount) { }

    // Mob locomotion has no swim/airborne distinction yet — surface stable
    // defaults so ActorStateRequirement evaluates cleanly on mob attacks
    // (forbidSwimming passes, requireGrounded passes, requireAirborne fails).
    public bool IsGrounded => true;
    public bool IsSwimming => false;

    public float OutgoingDamageMultiplier => _statusEffects?.FoldStat(EStat.OutgoingDamage, 1f) ?? 1f;

    // IActionActor — fire any active status effect's on-attack-impact burst
    // (elite lightning aura, etc.) at the swing/ray impact point. Forwarded to
    // the shared controller so mobs and the player run identical logic.
    public void TriggerAttackImpact(Vector3 position) => _statusEffects?.TriggerAttackImpact(this, position);

    // Compose a single stat across inherent MobData modifiers and active
    // status-effect modifiers. Mobs don't currently equip armor, so the
    // shape is one source list (MobData) + the controller's contribution.
    public float ComposeStat(EStat stat)
    {
        float value = StatModifierUtil.NeutralValue(stat);
        if (mobData?.modifiers != null)
        {
            value = StatModifierUtil.Fold(stat, mobData.modifiers, value);
        }
        value = _statusEffects?.FoldStat(stat, value) ?? value;
        return value;
    }

    // Multiplicative compose across all sources for a tag mask. Used at hit
    // application sites and routed through to the StatusEffectController as
    // the buildup / DoT resistance callback.
    public float ComposeMaskMul(EStat mask)
    {
        float product = 1f;
        if (mobData?.modifiers != null)
        {
            product = StatModifierUtil.FoldMask(mask, mobData.modifiers, product);
        }
        product = _statusEffects?.FoldMask(mask, product) ?? product;
        // Per-species Dizzy resistance (MobData.dizzyResistance). The buildup
        // feed scales by this product, so dividing by the resistance means a
        // resistance of 2 needs twice the buildup to land Dizzy. Only bites the
        // Dizzy buildup path — Dizzy isn't in DamageScaleTags, so no damage-
        // scaling site ever composes this mask.
        if ((mask & EStat.Dizzy) != 0 && mobData != null && mobData.dizzyResistance > 0f)
        {
            product /= mobData.dizzyResistance;
        }
        return product;
    }

    // Fold receiver resistances onto the live hit in place. Damage tags
    // (Damage / Fire / Magical / Poison / Electrical / Ranged / Melee) scale
    // healthDamage; ArmorPenetration scales bypass-chance; Blunt scales the armor-chip
    // multiplier; Knockback scales knockback distance and time. Mirrors the
    // Player-side ApplyResistance shape.
    private void ApplyResistance(ref HitInfo hit)
    {
        if (hit.tags == EStat.None)
        {
            return;
        }
        EStat damageTags = hit.tags & StatModifierUtil.DamageScaleTags;
        if (damageTags != EStat.None)
        {
            hit.healthDamage *= ComposeMaskMul(damageTags);
        }
        if ((hit.tags & EStat.ArmorPenetration) != 0)
        {
            hit.armorPenetration *= ComposeMaskMul(EStat.ArmorPenetration);
        }
        if ((hit.tags & EStat.Blunt) != 0)
        {
            hit.blunt *= ComposeMaskMul(EStat.Blunt);
        }
        if ((hit.tags & EStat.Knockback) != 0)
        {
            float scale = ComposeMaskMul(EStat.Knockback);
            hit.knockbackDistance *= scale;
            hit.knockbackTime *= scale;
        }
    }
    // Effective faction. A tamed companion overrides its authored wild team
    // (Prey) to Friendly — joining the player's side for friendly-fire and
    // HUD purposes (see Teams.AreAllied). All other mobs use MobData.team.
    public ETeam ActorTeam => (_simState != null && _simState.Tamed) ? ETeam.Friendly : (mobData?.team ?? ETeam.Hostile);

    public void PlayOneShot(EAnimation anim)
    {
        if (_animator == null || mobData == null)
        {
            return;
        }
        StringName name = mobData.GetAnimationName(anim);
        if (name == default || !_animator.HasAnimation(name))
        {
            return;
        }
        _oneShotAnim = anim;
        _animator.Play(name);
    }

    // IInteractive — only mobs with authored _interactiveActions surface as
    // interactable. The InteractiveBox on the mob's .tscn drives detection;
    // CanInteract / CanActorInteract gate the press itself.
    public Vector3 hudPosition => HudAnchor != null ? HudAnchor.GlobalPosition : GlobalPosition;

    public bool CanInteract()
    {
        if (!alive || burrowed || burrowing)
        {
            return false;
        }
        bool hasAuthored = _interactiveActions != null && _interactiveActions.Count > 0;
        bool hasConversation = _simState?.Conversation != null;
        return hasAuthored || hasConversation;
    }

    public bool CanActorInteract(Player player) => CanInteract();

    // Authored actions prepended with SimData.TalkAction + GiveItemAction
    // for any mob carrying a Conversation. Cached so we don't allocate the
    // merged array per HUD refresh — neither input mutates at runtime.
    private Godot.Collections.Array<InteractiveAction> _resolvedActions;
    private bool _resolvedActionsBuilt;

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        if (!CanActorInteract(player))
        {
            return null;
        }
        if (!_resolvedActionsBuilt)
        {
            _resolvedActionsBuilt = true;
            bool hasConversation = _simState?.Conversation != null;
            SimData sim = _world?.SimData;
            if (hasConversation && sim != null)
            {
                _resolvedActions = new Godot.Collections.Array<InteractiveAction>();
                if (sim.TalkAction != null)
                {
                    _resolvedActions.Add(sim.TalkAction);
                }
                // Trade replaces Give on merchants; the two are mutually
                // exclusive so the player only sees one shop verb per mob.
                InteractiveAction shopAction = _simState != null && _simState.WillTrade
                    ? sim.TradeAction
                    : sim.GiveItemAction;
                if (shopAction != null)
                {
                    _resolvedActions.Add(shopAction);
                }
                if (_interactiveActions != null)
                {
                    foreach (InteractiveAction a in _interactiveActions)
                    {
                        _resolvedActions.Add(a);
                    }
                }
            }
        }
        Godot.Collections.Array<InteractiveAction> result = _resolvedActions ?? _interactiveActions;
        if (result == null || result.Count == 0)
        {
            return null;
        }
        return result;
    }

    public void Complete(int actionIndex)
    {
        Godot.Collections.Array<InteractiveAction> actions = _resolvedActions ?? _interactiveActions;
        if (actions == null || actionIndex < 0 || actionIndex >= actions.Count)
        {
            return;
        }
        InteractiveAction action = actions[actionIndex];
        if (action == null)
        {
            return;
        }
        switch (action.verb)
        {
            case EActionVerb.Talk:
                SpeakDialogue();
                break;
            case EActionVerb.GiveItem:
                OpenMerchantScreen(trade: false, onClose: null);
                break;
            case EActionVerb.Trade:
                OpenMerchantScreen(trade: true, onClose: null);
                break;
        }
    }

    public void OpenMerchantScreen(bool trade, Action onClose)
    {
        GameClient gc = GameClient.Current;
        Player player = gc?.Player;
        if (gc?.merchantScreen == null || player == null)
        {
            return;
        }
        gc.merchantScreen.Open(player, this, trade, onClose);
        _simState.WillTrade |= trade;
    }

    // ---- Loyalty gifting ----
    // The merchant screen's gift and trade modes route through these.
    // Loyalty + remaining-gift state lives on MobSimState (so it persists
    // with the mob across chunk unloads / saves); the per-mob preference
    // math lives here so a species can override PerUnitValue with its own
    // taste model (vegetarian villager, hoarder dragon) without changing
    // the SimState shape.

    // Subjective worth (to this mob) of a single unit of `item`. Defaults
    // to the item's authored value. Override in a derived mob type if a
    // species values certain items differently. Items whose authored
    // value is 0 are uninteresting to the mob — CalculatePersonalValue and
    // AcceptableUnits short-circuit them to zero.
    public virtual int PerUnitValue(ItemData item)
    {
        return item != null ? item.value : 0;
    }

    // How many units of `offered` worth of `item` the mob will value right
    // now. Items past the per-type gift cap (3) are zero-value — once the
    // mob has already received MaxGiftsPerItemType of this kind, further
    // units don't count, so this returns 0. For a stack that partially
    // crosses the cap the caller is expected to split — accept this many
    // units, leave the rest in staging.
    public int AcceptableUnits(ItemData item, int offered)
    {
        if (item == null || _simState == null || offered <= 0)
        {
            return 0;
        }
        if (PerUnitValue(item) <= 0)
        {
            return 0;
        }
        _simState.GiftCounts.TryGetValue(item, out int already);
        int room = Mathf.Max(0, MobSimState.MaxGiftsPerItemType - already);
        return Mathf.Min(offered, room);
    }

    // Cap-aware total worth of an offering from the mob's perspective.
    // Sum across the offered stacks of PerUnitValue * AcceptableUnits, so
    // items past the per-type cap or with zero base value contribute
    // nothing. Used for the give-side of a transaction (gift loyalty, the
    // give half of a trade); the get-side of a trade should NOT route
    // through here because the cap only tracks the mob's incoming history.
    public virtual float CalculatePersonalValue(IEnumerable<ItemState> items)
    {
        if (items == null) { return 0f; }
        float total = 0f;
        foreach (ItemState s in items)
        {
            if (s == null || s.data == null) { continue; }
            int value = PerUnitValue(s.data);
            if (value <= 0) { continue; }
            total += value * AcceptableUnits(s.data, s.stackCount);
        }
        return total;
    }

    // True if the gift would teach only language components the player
    // already has. Item-bearing gifts are never redundant — even a known-
    // language gift still carries a real item payload.
    private bool IsGiftRedundant(LoyaltyGift gift, Player player)
    {
        if (gift == null) { return true; }
        if (gift.item != null) { return false; }
        if (gift.language == null || gift.languageComponents == ELanguageComponents.None) { return true; }
        if (player == null) { return false; }
        ELanguageComponents known = player.GetLearnedComponents(gift.language);
        return (gift.languageComponents & ~known) == ELanguageComponents.None;
    }

    // Any non-redundant gift left in the mob's reserve. Drives the "nothing
    // of value to give back" rejection case at the merchant screen — a mob
    // whose only remaining gifts are language components the player already
    // knows is functionally empty.
    public bool HasReciprocableGift(Player player)
    {
        if (_simState == null) { return false; }
        foreach (LoyaltyGift gift in _simState.LoyaltyGifts)
        {
            if (!IsGiftRedundant(gift, player)) { return true; }
        }
        return false;
    }

    // Records a successful gift offering: adds `loyaltyGained` to the
    // running Loyalty (the caller computes this — full personal value for
    // a one-sided gift, give-minus-get for an accepted trade), tallies the
    // item counts toward the per-type cap (using stackCount on each entry,
    // so the caller is expected to pre-split partial stacks down to just
    // the accepted units), and pops every loyalty gift whose threshold the
    // new total now crosses (skipping redundant language gifts the player
    // already covers). Returns the list of unlocked gifts in authored
    // order so MerchantScreen can hand them back; player-side application
    // (inventory add, language learn, HUD announcement) is the caller's job.
    public List<LoyaltyGift> AcceptGift(IList<ItemState> items, float loyaltyGained, Player player)
    {
        List<LoyaltyGift> awarded = new();
        if (_simState == null)
        {
            return awarded;
        }
        if (loyaltyGained > 0f)
        {
            _simState.Loyalty += loyaltyGained;
        }
        if (items != null)
        {
            foreach (ItemState s in items)
            {
                if (s?.data == null) { continue; }
                _simState.GiftCounts.TryGetValue(s.data, out int prior);
                _simState.GiftCounts[s.data] = prior + s.stackCount;
            }
        }
        for (int i = 0; i < _simState.LoyaltyGifts.Count;)
        {
            LoyaltyGift gift = _simState.LoyaltyGifts[i];
            if (gift == null)
            {
                _simState.LoyaltyGifts.RemoveAt(i);
                continue;
            }
            if (_simState.Loyalty < gift.requiredLoyalty)
            {
                i++;
                continue;
            }
            _simState.LoyaltyGifts.RemoveAt(i);
            if (!IsGiftRedundant(gift, player))
            {
                awarded.Add(gift);
            }
        }
        return awarded;
    }

    private void SpeakDialogue()
    {
        ConversationData conversation = _simState?.Conversation;
        if (conversation == null)
        {
            return;
        }
        // Per-instance Language on the SimState wins over MobData.language —
        // lets WorldGen pin a language onto a shared MobData without
        // mutating the resource. Branches with their own `language` field
        // override this; null branches fall back to it.
        LanguageData spokenLanguage = _simState?.Language ?? mobData?.language;
        ConversationContext ctx = new ConversationContext
        {
            world = _world,
            player = GameClient.Current?.Player,
            speaker = this,
            speakerLanguage = spokenLanguage,
        };
        GameClient.Current?.onConversation?.Invoke(conversation, ctx);
    }

    private void UpdateAnimation()
    {
        if (_animator == null || mobData == null)
        {
            return;
        }
        // Default the animator back to authored speed every tick — the
        // movement-loop branch below re-enables status retiming when (and
        // only when) it picks a speed-scaled loop. One-shots take the early
        // return below, so this default sticks for them.
        _animator.effectSpeedMultiplier = 1f;
        if (_oneShotAnim.HasValue)
        {
            EAnimation oneShot = _oneShotAnim.Value;
            // Hitstun is gated solely by HitstunTime — when the timer hits
            // zero the latch releases regardless of the clip's loop flag or
            // Finished state, so a looping hitstun clip doesn't trap the mob
            // in the anim past the flinch window. Other one-shots hold while
            // the animator says the clip is still playing.
            if (oneShot == EAnimation.Hitstun)
            {
                if (_simState.HitstunTime > 0f)
                {
                    return;
                }
                _oneShotAnim = null;
            }
            else
            {
                StringName oneShotName = mobData.GetAnimationName(oneShot);
                if (_animator.CurrentAnimation == oneShotName && !_animator.Finished)
                {
                    return;
                }
                _oneShotAnim = null;
            }
        }

        EAnimation loopAnim;
        EAnimation statusOverride = _statusEffects?.LoopAnimOverride ?? EAnimation.None;
        if (!alive)
        {
            loopAnim = EAnimation.Dead;
        }
        else if (statusOverride != EAnimation.None)
        {
            loopAnim = statusOverride;
        }
        else if (burrowed)
        {
            loopAnim = EAnimation.Burrowed;
        }
        else if (burrowing)
        {
            loopAnim = EAnimation.Burrowing;
        }
        else
        {
            Vector3 vel = LinearVelocity;
            Vector3 horizVel = new(vel.X, 0f, vel.Z);
            float horizSpeedSq = horizVel.LengthSquared();
            // Mob "intent to move" — navigator has an active goal/path AND
            // hasn't yet reached it. Lets a mob jammed against a wall while
            // pursuing keep playing the run anim (LinearVelocity may zero out
            // from collision, but the navigator still hasn't arrived), while
            // a mob holding station on its standoff slot drops back to idle.
            bool intentMoving = _navigator != null
                && _navigator.CurrentState != MobNavigator.State.Idle
                && !_navigator.HasArrived;

            // Sustained-fall tracking. Without the grace window, hopping over
            // small ledges or being shoved by another mob flickers the fall
            // anim for a frame.
            ulong now = _world?.GameTimeMs ?? 0;

            // Stuck-grace on the run-in-place masking. intentMoving deliberately
            // keeps Run playing while a pursuing mob is momentarily jammed
            // (collision zeroes LinearVelocity but the navigator hasn't arrived).
            // If the body stays pinned at ~0 speed past the grace window it's
            // genuinely wedged — drop intentMoving so the velocity-driven pick
            // below falls back to Idle instead of running forever in place.
            if (intentMoving && horizSpeedSq < StuckRunSpeedSq)
            {
                if (_intentStuckStartMs == 0)
                {
                    _intentStuckStartMs = now;
                }
                else if (now - _intentStuckStartMs >= StuckRunGraceMs)
                {
                    intentMoving = false;
                }
            }
            else
            {
                _intentStuckStartMs = 0;
            }
            bool fallingFast = vel.Y < -mobData.fallEnterSpeed;
            if (fallingFast)
            {
                if (_airborneStartMs == 0)
                {
                    _airborneStartMs = now;
                }
            }
            else
            {
                _airborneStartMs = 0;
            }
            ulong fallGraceMs = (ulong)(mobData.fallGraceTime * 1000f);
            bool fallReady = fallingFast && _airborneStartMs != 0 && now - _airborneStartMs >= fallGraceMs;

            if (_simState.Airborne)
            {
                // Flight is travel-only, so airborne always means flapping —
                // there's no in-air idle. Falls through to Fly regardless of
                // horizontal speed.
                loopAnim = EAnimation.Fly;
            }
            else if (IsInWater())
            {
                loopAnim = PickMoveLoop(horizSpeedSq, intentMoving, EAnimation.Swim, EAnimation.SwimIdle);
            }
            else if (fallReady)
            {
                loopAnim = EAnimation.Fall;
            }
            else
            {
                loopAnim = PickMoveLoop(horizSpeedSq, intentMoving, EAnimation.Run, EAnimation.Idle);
            }
        }
        StringName loopName = mobData.GetAnimationName(loopAnim);
        if (loopName != default && _animator.HasAnimation(loopName))
        {
            _animator.Play(loopName);
        }

        // Status retiming is gated per-anim by AnimationData — only loops
        // authored with affectedBySpeedMultiplier track statusAnimMul. Idle /
        // fall / burrow / dead / dizzy default to authored speed.
        if (mobData.IsAnimationSpeedAffected(loopAnim))
        {
            _animator.effectSpeedMultiplier = _statusEffects?.FoldStat(EStat.AnimSpeed, 1f) ?? 1f;
        }

        // Drive the anim-audio loop off the same loopAnim. Burrowing mobs
        // are mid-dig and shouldn't simultaneously hum the surface idle, so
        // the burrow flags suppress the anim-loop entirely until they
        // resurface.
        PackedScene animLoopTarget = null;
        if (alive && !burrowing && !burrowed)
        {
            // The idle loop is gated to a per-species time-of-day window
            // (MobData.IsIdleLoopActiveAt) and a rain ceiling (IdleLoopMaxRain)
            // so e.g. sparrows only chirp during the day and clam up once it's
            // more than a drizzle; the move/swim loops play regardless.
            if (loopAnim == EAnimation.Idle)
            {
                if (mobData.IsIdleLoopActiveAt(_world?.WorldState?.TimeOfDay01 ?? 0.0)
                    && (_world?.CurrentRainAmount() ?? 0f) <= mobData.IdleLoopMaxRain)
                {
                    animLoopTarget = _idleLoopFx;
                }
            }
            else if (loopAnim == EAnimation.Run) animLoopTarget = _runLoopFx;
            else if (loopAnim == EAnimation.SwimIdle) animLoopTarget = _swimIdleLoopFx;
        }
        UpdateAnimLoop(animLoopTarget);
    }

    // Swap the active anim-loop wholesale on state change. No-op when target
    // matches the currently-playing scene, so this is safe to call every frame.
    private void UpdateAnimLoop(PackedScene scene)
    {
        if (scene == _animLoopScene)
        {
            return;
        }
        // Only profiled on actual transitions so `calls` reflects loop churn,
        // not the (expected-cheap) per-frame no-op path. A high call count
        // here at high mob density is the smoking gun for state oscillation
        // — every swap tears down + spawns a fresh Fx (instantiate, AddChild,
        // wire audio + particles).
        using var _profSwap = Profiler.Sample("Mob.UpdateAnimLoop.Swap");
        if (!CVars.mobAnimLoopFx.Value)
        {
            _animLoopScene = scene;
            return;
        }
        if (_animLoopFx != null)
        {
            _animLoopFx.Stop();
            _animLoopFx = null;
        }
        if (scene != null)
        {
            _animLoopFx = Fx.Create(scene, this, Vector3.Zero);
        }
        _animLoopScene = scene;
    }

    // Hysteresis on the move-vs-idle pick — see Player.PickMoveLoop. Mob
    // navigators apply impulses every tick, so the body sits near the
    // friction floor a lot; without the dead band, idle/run flicker
    // every other frame.
    const float MoveLoopEnterSpeedSq = 0.01f;     // 0.1 m/s
    const float MoveLoopExitSpeedSq = 0.0001f;    // 0.01 m/s
    // A mob with an active nav goal moving slower than this counts as pinned,
    // not progressing — feeds the run-in-place stuck-grace in UpdateAnimation.
    const float StuckRunSpeedSq = 0.0025f;        // 0.05 m/s
    // How long a pinned-but-intent-moving mob tolerates the run anim before it
    // drops to idle. Long enough to ride out a momentary collision bump, short
    // enough that a genuinely wedged mob stops running on the spot promptly.
    const ulong StuckRunGraceMs = 300;
    private EAnimation PickMoveLoop(float speedSq, bool intentMoving, EAnimation moveAnim, EAnimation idleAnim)
    {
        if (intentMoving || speedSq > MoveLoopEnterSpeedSq)
        {
            return moveAnim;
        }
        if (speedSq < MoveLoopExitSpeedSq)
        {
            return idleAnim;
        }
        // Hold-current band — compare the animator's currently-playing clip
        // against each candidate's authored name to decide which side of the
        // band to stick to.
        StringName current = _animator.CurrentAnimation;
        if (current == mobData.GetAnimationName(moveAnim))
        {
            return moveAnim;
        }
        if (current == mobData.GetAnimationName(idleAnim))
        {
            return idleAnim;
        }
        return idleAnim;
    }

    // Cheap voxel sample at the body's feet — same data the ripple emitter
    // already reads. Used by UpdateAnimation to pick swim vs run; we don't
    // need the full surface-Y scan here, only "is the mob standing in water".
    private bool IsInWater()
    {
        WorldState ws = _world?.WorldState;
        if (ws == null)
        {
            return false;
        }
        Vector3 pos = GlobalPosition;
        return ws.GetVoxelWorld(
            Mathf.FloorToInt(pos.X),
            Mathf.FloorToInt(pos.Y),
            Mathf.FloorToInt(pos.Z)) == VoxelType.Water;
    }

    // Set _swimming when the contiguous water column at this mob's XZ is
    // at least swimDepthThreshold voxels deep. Walks the column up and
    // down from the feet voxel so the decision is independent of where the
    // mob currently sits within the column: a mob that's just splashed in
    // and hasn't risen to the surface yet still tests as swimming because
    // the column it landed in is deep. Probing only below feet (the prior
    // approach) failed at the seafloor — the voxel below is the floor, so
    // _swimming never turned on and buoyancy never fired. Matches the
    // pathfinder's wade/swim cost split in WalkabilityGrid.SampleColumn.
    private void UpdateWaterState()
    {
        WorldState ws = _world?.WorldState;
        MobData md = _simState?.MobData;
        if (ws == null || md == null)
        {
            _swimming = false;
            return;
        }
        Vector3 pos = GlobalPosition;
        int fx = Mathf.FloorToInt(pos.X);
        int fy = Mathf.FloorToInt(pos.Y);
        int fz = Mathf.FloorToInt(pos.Z);
        if (ws.GetVoxelWorld(fx, fy, fz) != VoxelType.Water)
        {
            _swimming = false;
            return;
        }
        int topY = fy;
        while (ws.GetVoxelWorld(fx, topY + 1, fz) == VoxelType.Water)
        {
            topY++;
        }
        int bottomY = fy;
        while (ws.GetVoxelWorld(fx, bottomY - 1, fz) == VoxelType.Water)
        {
            bottomY--;
        }
        int columnDepth = topY - bottomY + 1;
        int thresholdVoxels = Mathf.Max(1, Mathf.FloorToInt(md.swimDepthThreshold));
        _swimming = columnDepth >= thresholdVoxels;
        if (_swimming)
        {
            _waterSurfaceY = topY + 1;
        }
    }

    // Mirrors Player.ApplyWaterPhysics but applies forces as impulses
    // because Mob is a RigidBody3D. Buoyancy + a vertical drag damp the
    // mob to the water surface; SampleWaterCurrent drags horizontal
    // velocity toward the local current. The sink-speed clamp is a
    // direct LinearVelocity write — that path only triggers above the
    // auto-freeze threshold so it doesn't fight the idle freeze.
    private void ApplyWaterPhysics(float dt)
    {
        MobData md = _simState.MobData;
        Vector3 pos = GlobalPosition;
        Vector3 vel = LinearVelocity;
        Vector3 deltaVel = Vector3.Zero;

        float targetY = _waterSurfaceY - md.waterSurfaceOffset;
        float depthBelowSurface = targetY - pos.Y;
        if (depthBelowSurface > 0f)
        {
            deltaVel.Y += Mathf.Min(depthBelowSurface, 1f) * md.buoyancyAcceleration * dt;
        }
        else
        {
            deltaVel.Y -= md.buoyancyAcceleration * 0.5f * dt;
        }

        deltaVel.Y -= vel.Y * md.waterDrag * dt;

        Vector3 current = _world.WorldState.SampleWaterCurrent(pos);
        deltaVel.X += (current.X - vel.X) * md.waterCurrentDrag * dt;
        deltaVel.Z += (current.Z - vel.Z) * md.waterCurrentDrag * dt;

        ApplyImpulse(deltaVel * Mass);

        if (LinearVelocity.Y < -md.waterSinkSpeed)
        {
            Vector3 v = LinearVelocity;
            LinearVelocity = new Vector3(v.X, -md.waterSinkSpeed, v.Z);
        }
    }

    // Flight locomotion for canFly mobs while airborne (gravity is disabled by
    // the caller). Drives all three axes via a single impulse toward a desired
    // velocity: horizontal steering toward the path target, a vertical spring
    // onto the hover altitude, and a wind bias from the baked air-current field.
    private void ApplyFlightPhysics(float delta, in AIOutput aiOutput, float statusMoveMul, ref float? targetYaw)
    {
        MobData md = _simState.MobData;
        Vector3 pos = GlobalPosition;
        Vector3 currentVel = LinearVelocity;

        // Horizontal steering toward the path target (XZ only).
        Vector3 desiredHoriz = Vector3.Zero;
        if (aiOutput.pathTarget.HasValue)
        {
            Vector3 toTarget = aiOutput.pathTarget.Value - pos;
            toTarget.Y = 0f;
            float dist = toTarget.Length();
            float arrivalDist = Mathf.Max(aiOutput.pathSuccessDistance, 0.1f);
            if (dist > 0.01f)
            {
                Vector3 dir = toTarget / dist;
                float speedScale = Mathf.Clamp(dist / (arrivalDist + 1f), 0f, 1f);
                desiredHoriz = dir * md.flySpeed * aiOutput.speed * speedScale * statusMoveMul;
                if (!targetYaw.HasValue && dist > arrivalDist)
                {
                    targetYaw = Mathf.Atan2(dir.X, dir.Z);
                }
            }
        }

        // Wind: blend the local baked air current into the desired horizontal
        // velocity so birds get carried / fight headwinds (windInfluence tunes
        // how strongly per species).
        Vector3 wind = _world?.WorldState?.GetWindVelocityWorld(
            Mathf.FloorToInt(pos.X), Mathf.FloorToInt(pos.Y), Mathf.FloorToInt(pos.Z)) ?? Vector3.Zero;
        desiredHoriz += new Vector3(wind.X, 0f, wind.Z) * md.windInfluence;

        // Vertical: spring toward the look-ahead/ceiling-aware target altitude.
        float hoverH = aiOutput.flyAltitude ?? md.hoverHeight;
        float targetY = ComputeFlightAltitude(pos, currentVel, hoverH);
        float desiredVy = Mathf.Clamp((targetY - pos.Y) * md.hoverStiffness, -md.verticalSpeed, md.verticalSpeed);

        Vector3 desiredVel = new Vector3(desiredHoriz.X, desiredVy, desiredHoriz.Z);
        ApplyImpulse((desiredVel - currentVel) * Mass);
    }

    // Target flight altitude (world Y) for a bird at `pos` heading along `vel`:
    // the higher of the surface directly below and a look-ahead sample, plus
    // hoverHeight, capped just below any ceiling overhead and floored just
    // above the surface so the bird neither clips terrain nor dives into it.
    private float ComputeFlightAltitude(Vector3 pos, Vector3 vel, float hoverHeight)
    {
        WorldState ws = _world?.WorldState;
        if (ws == null)
        {
            return pos.Y;
        }
        int wx = Mathf.FloorToInt(pos.X);
        int wy = Mathf.FloorToInt(pos.Y);
        int wz = Mathf.FloorToInt(pos.Z);

        // Highest surface across our own column and several successive samples
        // along the flight heading. Two things keep the bird off cliff faces:
        // (1) multiple samples (not just one) so a cliff edge can't slip
        // between them, and (2) each column is scanned from well ABOVE the bird
        // downward, so terrain taller than the bird's current altitude is found
        // — scanning only downward from the bird missed rising cliffs entirely
        // and let it push into the wall.
        // Swimmers may land on (and hover over) water, so its surface counts as
        // ground; non-swimmers see through it to the bed below.
        bool waterIsSurface = _simState.MobData.canSwim;
        int surfHere = SurfaceTopAt(ws, wx, wz, wy, waterIsSurface);
        int surfMax = surfHere;
        Vector2 heading = new Vector2(vel.X, vel.Z);
        if (heading.LengthSquared() > 0.04f)
        {
            heading = heading.Normalized();
            for (int i = 1; i <= FlightLookAheadSamples; i++)
            {
                float d = _simState.MobData.flightLookAhead * i / FlightLookAheadSamples;
                int ax = Mathf.FloorToInt(pos.X + heading.X * d);
                int az = Mathf.FloorToInt(pos.Z + heading.Y * d);
                surfMax = Mathf.Max(surfMax, SurfaceTopAt(ws, ax, az, wy, waterIsSurface));
            }
        }

        float target = surfMax + hoverHeight;
        int ceiling = CeilingYAbove(ws, wx, wy + 1, wz);
        if (ceiling != int.MaxValue)
        {
            target = Mathf.Min(target, ceiling - 1f);
        }
        return Mathf.Max(target, surfHere + 1f);
    }

    // Number of look-ahead columns sampled along the flight heading (in
    // addition to the bird's own column), spread evenly out to flightLookAhead.
    private const int FlightLookAheadSamples = 4;
    // How far above / below the bird a surface scan looks. Starting above the
    // bird is what lets a rising cliff (terrain taller than the bird) register
    // so the bird climbs over it instead of into it.
    private const int FlightSurfaceLookUp = 32;
    private const int FlightSurfaceLookDown = 64;

    // Highest landable voxel's top face in column (wx, wz), scanning from
    // FlightSurfaceLookUp above centerY downward. Solid is always landable;
    // Water counts only when includeWater (the flier can swim), so swimmers
    // hover over and land on the water surface while others see the bed below.
    // Returns the lower bound if the column has nothing landable (open chasm /
    // unloaded), which simply doesn't raise the altitude target.
    private static int SurfaceTopAt(WorldState ws, int wx, int wz, int centerY, bool includeWater)
    {
        int startY = centerY + FlightSurfaceLookUp;
        int minY = centerY - FlightSurfaceLookDown;
        for (int y = startY; y > minY; y--)
        {
            VoxelType v = ws.GetVoxelWorld(wx, y, wz);
            if (VoxelTypeInfo.IsSolid(v) || (includeWater && v == VoxelType.Water))
            {
                return y + 1;
            }
        }
        return minY;
    }

    // Scan upward from startY for the first solid voxel; return the Y of its
    // bottom face, or int.MaxValue if none within MaxScan (open sky).
    private static int CeilingYAbove(WorldState ws, int wx, int startY, int wz)
    {
        const int MaxScan = 16;
        for (int y = startY; y < startY + MaxScan; y++)
        {
            if (VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(wx, y, wz)))
            {
                return y;
            }
        }
        return int.MaxValue;
    }

    // Land a flying mob on `perch`: claim it, freeze the body so it rests there
    // without falling (perches sit on branches / ledges with no collider), and
    // snap onto the perch's landing point. The point is a static 3D marker, so a
    // single placement holds — facing is then driven by BehaviorPerch through
    // AIOutput.yaw.
    public void SettleOnPerch(Perch perch)
    {
        if (perch == null)
        {
            return;
        }
        _claimedPerch = perch;
        perch.TryClaim(this);
        _perched = true;
        LinearVelocity = Vector3.Zero;
        Freeze = true;
        GlobalPosition = perch.WorldPosition;
    }

    // Take off / abandon the current perch: stop tracking, unfreeze the body,
    // and release the claim. Safe to call when not perched. Called on flight
    // takeoff and on any teardown so the perch frees up for another bird.
    public void LeavePerch()
    {
        _perched = false;
        if (Freeze)
        {
            Freeze = false;
        }
        if (_claimedPerch != null)
        {
            _claimedPerch.Release(this);
            _claimedPerch = null;
        }
    }

    // Writes the node's current transform back into the persistent sim state so
    // that when this Mob is freed (chunk unload, save), the saved position is
    // current rather than the original spawn position.
    private void SyncToSimState()
    {
        if (_simState == null)
        {
            return;
        }
        _simState.WorldPosition = Position;
        _simState.RotationY = Rotation.Y;
    }


    public override void _Process(double delta)
    {
        base._Process(delta);
        using var _profProcess = Profiler.Sample("Mob.Process");
        // While vanishing, the fairy orb's own fade owns its visuals; freeze
        // the discovery/HUD visibility updates so they don't fight it.
        if (_vanishing)
        {
            return;
        }
        if (_mesh == null)
        {
            return;
        }

        // Compute target state.
        bool discovered = _simState.DiscoveryState == EPlayerPerceptionState.Discovered && _simState.MemoryTimeMs > _world.GameTimeMs;
        if (CVars.revealMobs.Value)
        {
            discovered = true;
        }
        // A fully burrowed mob is underground — it should vanish, not linger as
        // a black memory silhouette. Force the fade-out (the descent while
        // `burrowing` stays visible so the dig-in still reads). The dirt mound
        // marks the spot in its place.
        if (burrowed)
        {
            discovered = false;
        }
        // Three-state visibility, driven off discovery:
        //   fully visible  → dithered to full, no silhouette
        //   silhouetted    → still dithered to full, silhouette ramps up
        //                    (player remembers it's there but can't see details)
        //   unknown / memory expired → dither back to 0
        // Step values move toward targets at 1 / VisibilityFadeTime per
        // second so both the pop-in and the transition to/from silhouette
        // are smooth rather than instant.
        bool withinVisibleTime = _world.GameTimeMs < _simState.VisibleTimeMs;
        float targetVisibility = discovered ? 1f : 0f;
        // Silhouette target only moves while we WANT to be visible — when
        // fading out, freeze it so a silhouetted mob whose memory just
        // expired stipples OUT as silhouette rather than briefly flashing
        // back to lit colors through the dither as both values race to 0.
        float targetSilhouette = _silhouette;
        if (targetVisibility >= 1f)
        {
            targetSilhouette = withinVisibleTime ? 0f : 1f;
        }
        float step = (float)delta / VisibilityFadeTime;
        _visibility = Mathf.MoveToward(_visibility, targetVisibility, step);
        _silhouette = Mathf.MoveToward(_silhouette, targetSilhouette, step);

        bool meshVisibleTarget = _visibility > 0f && CVars.mobVisible.Value;
        bool hudVisibleTarget = CVars.mobHud.Value;
        bool castsShadowTarget = _visibility > 0f && CVars.mobShadows.Value;

        // Push to Godot only when something actually changed. Each setter is
        // a managed→native marshal that pushes uniform updates / dirties the
        // transform, so skipping them on stable frames is the bulk of the
        // win. The cached "_last*Init" pairs force the first frame through
        // regardless of value so initial state is always applied.
        if (!_lastMeshVisibleInit || meshVisibleTarget != _lastMeshVisible)
        {
            _mesh.Visible = meshVisibleTarget;
            _lastMeshVisible = meshVisibleTarget;
            _lastMeshVisibleInit = true;
        }
        // Animation cull + cost diagnostic. freezable = not rendered AND in a
        // stationary loop (no footstep events to miss). mob_anim 0 freezes EVERY
        // mob (measures the total animation-cost ceiling); mob_anim_cull gates the
        // idle-only cull. The counters report how many mobs each frame are
        // actually frozen vs still animating, so the cull's reach is visible.
        // Freeze the skeleton (static pose → Godot skips the per-frame GPU
        // re-skin, the dominant visible-mob cost) for any mob the player can't
        // actually SEE right now. withinVisibleTime is the line-of-sight window
        // (VisibleTimeMs), refreshed only while the mob is in active visual
        // contact — so a discovered-but-occluded/offscreen mob, drawn as a static
        // memory silhouette, stops skinning. That's exactly the wasted work
        // Godot's frustum-only culling leaves in (it skins every in-frustum
        // visible mesh, occluded or not). mob_pose_distance optionally also
        // freezes still-in-sight mobs past a radius.
        bool tooFarToPose = false;
        float poseDist = CVars.mobPoseDistance.Value;
        if (poseDist > 0f && _world?.player != null)
        {
            tooFarToPose = (GlobalPosition - _world.player.GlobalPosition).LengthSquared() > poseDist * poseDist;
        }
        bool animProcessTarget = !CVars.mobAnimCull.Value || (withinVisibleTime && !tooFarToPose);
        // Per-frame census → readable gauges (mob_count / mob_anim_active /
        // mob_anim_frozen). The first mob each process frame publishes the prior
        // frame's tally (one-frame lag) and resets.
        ulong processFrame = Engine.GetProcessFrames();
        if (processFrame != _animCensusFrame)
        {
            Profiler.SetGauge("mob_count", _animCensusActive + _animCensusFrozen);
            Profiler.SetGauge("mob_anim_active", _animCensusActive);
            Profiler.SetGauge("mob_anim_frozen", _animCensusFrozen);
            _animCensusActive = 0;
            _animCensusFrozen = 0;
            _animCensusFrame = processFrame;
        }
        if (animProcessTarget) { _animCensusActive++; } else { _animCensusFrozen++; }
        if (_modelAnimator != null && (!_lastAnimProcessInit || animProcessTarget != _lastAnimProcess))
        {
            _modelAnimator.SetPoseProcessing(animProcessTarget);
            _lastAnimProcess = animProcessTarget;
            _lastAnimProcessInit = true;
        }
        if (HudAnchor != null && (!_lastHudVisibleInit || hudVisibleTarget != _lastHudVisible))
        {
            HudAnchor.Visible = hudVisibleTarget;
            _lastHudVisible = hudVisibleTarget;
            _lastHudVisibleInit = true;
        }
        // Push the discovery presentation onto the model's meshes (dither /
        // silhouette / X-ray fade / shadow). The push isn't free, so gate it on
        // change — during a fade _visibility/_silhouette move every frame, but a
        // settled mob pushes nothing. xray suppressed while burrowed so a buried
        // mob's through-cover silhouette doesn't show.
        if (_modelAnimator != null)
        {
            float modelXray = burrowed ? 0f : 1f;
            if (!_lastModelVisualsInit
                || _visibility != _lastModelVisibility
                || _silhouette != _lastModelSilhouette
                || modelXray != _lastModelXray
                || castsShadowTarget != _lastModelCastShadow)
            {
                _modelAnimator.SetDiscoveryVisuals(_visibility, _silhouette, modelXray, castsShadowTarget);
                _lastModelVisibility = _visibility;
                _lastModelSilhouette = _silhouette;
                _lastModelXray = modelXray;
                _lastModelCastShadow = castsShadowTarget;
                _lastModelVisualsInit = true;
            }
        }
        // Feed the elite crown the same discovery presentation as the body (it
        // gates internally, so this is cheap on settled frames; the crown carries
        // its own meshes). xray suppressed while burrowed, matching the body.
        _crown?.SetDiscoveryVisuals(_visibility, _silhouette, burrowed ? 0f : 1f);
    }

    override public void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);
        using var _profPhys = Profiler.Sample("Mob.PhysicsProcess");

        // Escape vanish (fairy getaway): a scripted rise + fade + permanent
        // despawn. Runs ahead of all normal physics/AI and short-circuits the
        // rest of the tick while active.
        if (TickVanish((float)delta))
        {
            return;
        }

        // mob_physics is a profiling bisection toggle — when off, freeze the
        // body and zero its layer/mask so Jolt's broadphase and contact
        // resolver see nothing. Tracks the CVar live so you can flip it
        // mid-session and watch _PhysicsProcess time change.
        // Edge-detect the bisection toggle. Acting on the CVar every tick
        // would clobber the per-mob auto-freeze that lives at the bottom of
        // this method — the auto-freeze sets Freeze=true on idle mobs, and
        // a per-tick "if CVar is on, unfreeze" block would undo it on the
        // very next tick. Only enforce the CVar's intent when it actually
        // changes; the auto-freeze owns the Freeze state outside transitions.
        bool physicsEnabled = CVars.mobPhysics.Value;
        if (physicsEnabled != _lastMobPhysicsCvar)
        {
            if (physicsEnabled)
            {
                Freeze = false;
                CollisionLayer = (uint)ECollisionLayer.Mob;
                CollisionMask = (uint)(ECollisionLayer.Solid | ECollisionLayer.Player | ECollisionLayer.Mob);
            }
            else
            {
                Freeze = true;
                CollisionLayer = 0;
                CollisionMask = 0;
            }
            _lastMobPhysicsCvar = physicsEnabled;
        }
        if (!physicsEnabled)
        {
            return;
        }

        using (Profiler.Sample("Mob.UpdateTerrainSpeed"))
        {
            UpdateTerrainSpeed();
        }

        using (Profiler.Sample("Mob.UpdateWaterRipples"))
        {
            UpdateWaterRipples();
        }
        using (Profiler.Sample("Mob.UpdateFootsteps"))
        {
            UpdateFootsteps();
        }

        // Keep the spatial hash up-to-date for the navigator's separation
        // query and any other neighbor-radius lookup. Update() short-
        // circuits when the mob hasn't crossed a cell boundary, so this is
        // effectively free for idle mobs.
        _world?.MobSpatialHash?.Update(this);

        // Perception is throttled — accumulate delta and only run when the
        // interval is reached, so per-mob raycast cost stays low at density.
        // The accumulated delta is passed in so the perception integrator
        // ramps over the same total time regardless of tick rate.
        _simState.PerceptionTickAccumulator += (float)delta;
        if (_simState.PerceptionTickAccumulator >= MobSimState.PerceptionTickInterval)
        {
            UpdatePerception(_simState.PerceptionTickAccumulator);
            _simState.PerceptionTickAccumulator = 0f;
        }

        _simState.LightSampleAccumulator += (float)delta;
        if (_simState.LightSampleAccumulator >= MobSimState.LightSampleInterval)
        {
            SampleAmbientLight();
            _simState.LightSampleAccumulator = 0f;
        }

        // Eye dilation — mirrors Player.UpdateEyeDilation. Driven by the cached
        // AmbientLight (refreshed above), normalized by TargetLightMax like the
        // player's visibilityLight, smoothed asymmetrically (dilate slow,
        // constrict fast). Updated every frame off the cached light so the curve
        // stays smooth despite the 0.75s light-sample throttle.
        MobData dilData = _simState.MobData;
        if (dilData != null)
        {
            float targetLightMax = _world?.SimData?.TargetLightMax ?? 0.75f;
            float mobLight01 = targetLightMax > 0f ? Mathf.Clamp(_simState.AmbientLight / targetLightMax, 0f, 1f) : 0f;
            float dilTarget = 1f - mobLight01;
            float dilTau = dilTarget > _simState.EyeDilation ? dilData.eyeDilationDilateSeconds : dilData.eyeDilationConstrictSeconds;
            float dilK = 1f - Mathf.Exp(-(float)delta / Mathf.Max(dilTau, 0.001f));
            _simState.EyeDilation = Mathf.Lerp(_simState.EyeDilation, dilTarget, dilK);
        }

        // Knockback timer + velocity force run on alive AND dead bodies — a
        // killing blow should still send the corpse flying for the authored
        // distance. TickHitstun also decrements HitstunTime, which is a no-op
        // for corpses (no anim to release) but harmless. TickMotion runs in
        // the same band so an in-flight dart ends cleanly when the mob dies.
        TickHitstun((float)delta);
        TickMotion((float)delta);

        if (alive)
        {
            TickArmor((float)delta);
            _statusEffects.Tick((float)delta);
            DotHudFlush dotFlush = _dotHud.Tick(_world?.GameTimeMs ?? 0, hudPosition);
            if (dotFlush.damage)
            {
                // Continuous damage authors no per-frame fx; its "ouch" rides
                // on the once-per-second HUD rollup instead.
                SpawnVoice(_voice?.hurt);
            }
            UpdateWaterState();
            // Engine gravity is owned by ApplyWaterPhysics while swimming —
            // disable Godot's default gravity application on this body so
            // buoyancy + drag can settle to their own equilibrium without
            // gravity also acting. Capture the authored scale once so a
            // non-default scene value (e.g. heavier-than-normal species) is
            // preserved across swim entries.
            if (!_gravityScaleCaptured)
            {
                _gravityScaleAuthored = GravityScale;
                _gravityScaleCaptured = true;
            }
            if (_swimming && !_gravityScaleSwimActive)
            {
                GravityScale = 0f;
                _gravityScaleSwimActive = true;
            }
            else if (!_swimming && _gravityScaleSwimActive)
            {
                GravityScale = _gravityScaleAuthored;
                _gravityScaleSwimActive = false;
            }
            // Dizzy and per-hit hitstun both freeze intentional behavior — no
            // path target, no attack request, no torch / yell / burrow output.
            // Physics, status ticks, and the action runner still run so an
            // in-flight attack can wind down naturally and gravity / impulses
            // still act on the body. Incapacitating effects (dizzy today)
            // are the heavy-meter states; hitstun is the short flinch window
            // between hits.
            AIOutput aiOutput;
            if (incapacitated || _simState.HitstunTime > 0f)
            {
                aiOutput = default;
            }
            else
            {
                TickAI((float)delta, out aiOutput);
            }

            // Drive the action runner from AIOutput. BehaviorAttack populates
            // attackProfile when in range and off cooldown; the runner gates
            // on its own busy state and per-tier cooldowns.
            if (aiOutput.attackProfile != null && _runner != null && !_runner.IsBusy)
            {
                _runner.TryStart(aiOutput.attackProfile, aiOutput.attackContext);
            }
            if (aiOutput.oneShotAnim.HasValue)
            {
                PlayOneShot(aiOutput.oneShotAnim.Value);
            }
            _runner?.Tick();

            // Draw the mob's active movement target when the debug CVar is on.
            // Single-frame lifetime — this is called every physics tick so
            // the line stays visible without accumulating stale segments.
            // Visualizes aiOutput.pathTarget (the single contract every
            // behavior writes to move the mob) so any new behavior shows
            // up here for free without its own debug code.
            if (CVars.mobDebugPath.Value)
            {
                DrawPathDebug(aiOutput.pathTarget);
            }

            using var _profPostTick = Profiler.Sample("Mob.PostTickMove");

            if (aiOutput.suspendTimeMs.HasValue)
            {
                _simState.SuspendAITimeMs = aiOutput.suspendTimeMs.Value;
            }

            // Distance LOD: throttle the AI tick rate on mobs that are far
            // from the player and not actively in combat. The triggered-
            // override in TickAI ensures a player walking into perception
            // range wakes the mob immediately, so this only stretches
            // intervals for genuinely idle / wandering distant mobs.
            // Only extends (never shortens) any pre-existing suspend.
            bool inCombat = !aiOutput.suspended && aiOutput.inCombat;
            if (!inCombat && _world?.player != null)
            {
                const float COLD_DISTANCE_SQ = 30f * 30f;
                const ulong COLD_AI_TICK_INTERVAL_MS = 250;
                float distSq = (float)GlobalPosition.DistanceSquaredTo(_world.player.GlobalPosition);
                if (distSq > COLD_DISTANCE_SQ)
                {
                    ulong nextTickMs = _world.GameTimeMs + COLD_AI_TICK_INTERVAL_MS;
                    if (nextTickMs > _simState.SuspendAITimeMs)
                    {
                        _simState.SuspendAITimeMs = nextTickMs;
                    }
                }
            }

            // An explicit aiOutput.yaw always wins so behaviors like BehaviorAttack
            // can keep facing the player while circling to a reposition point.
            // Otherwise, if we're walking toward a path target, face that direction.
            float? targetYaw = aiOutput.yaw;

            float statusMoveMul = _statusEffects?.FoldStat(EStat.MoveSpeed, 1f) ?? 1f;
            // Anim retiming is gated to movement-loop anims only — see
            // UpdateAnimation, which writes effectSpeedMultiplier per-frame
            // based on the currently-picked loopAnim. Attack / hitstun / die
            // one-shots play at authored speed regardless of status.

            // Skip the path / yaw blocks below while dug in. SetBurrowed
            // (called on the edges from the burrow transition block) owns
            // the actual freeze + velocity zero + layer swap; this flag
            // just keeps us from rotating a frozen body or writing
            // LinearDamp on a pinned one every tick. aiOutput.burrow is
            // OR'd in so the gate already covers the initial-burrow tick
            // before the transition block runs further down and flips the
            // `burrowing` flag.
            bool inBurrow = aiOutput.burrow || burrowing || burrowed;

            // Decide LinearDamp target without writing yet — we want one
            // gated assignment at the end rather than three branches each
            // hitting the setter. A locks-movement action (windup, recovery
            // tail) skips the path impulse so the default damp settles the
            // body in place; the in-between dart drives velocity through
            // ApplyMotionPhysics regardless.
            float linearDampTarget = 8f;
            bool actionLocksMovement = _runner != null && _runner.LocksMovement;
            // Flying mobs run a dedicated 3-axis steering+hover+wind pass while
            // the behavior layer wants them airborne; ground locomotion below is
            // skipped for them. Drive the sim-state flag so animation and the
            // gravity/damp decisions agree we're aloft.
            bool flying = _simState.MobData.canFly && aiOutput.airborne && !inBurrow && !actionLocksMovement;
            _simState.Airborne = flying;
            // Fliers pass through world geometry while airborne — hover physics
            // owns altitude, so colliding only snags them on cliffs/props and
            // blocks perch approaches. Drop the movement body's mask to nothing
            // on the airborne edge and restore it on landing (mirrors the burrow
            // layer swap). Gated on `alive` so Die()/burrow keep ownership of the
            // mask in their own states.
            if (_simState.MobData.canFly && alive)
            {
                if (flying && !_flightCollisionDisabled)
                {
                    CollisionMask = 0;
                    _flightCollisionDisabled = true;
                }
                else if (!flying && _flightCollisionDisabled)
                {
                    CollisionMask = (uint)(ECollisionLayer.Solid | ECollisionLayer.Player | ECollisionLayer.Mob);
                    _flightCollisionDisabled = false;
                }
            }
            if (flying)
            {
                // Flight owns its own drag (impulse toward desired velocity);
                // engine LinearDamp would fight it, so pin to zero.
                linearDampTarget = 0f;
                ApplyFlightPhysics((float)delta, in aiOutput, statusMoveMul, ref targetYaw);
            }
            else if (!inBurrow && !actionLocksMovement && aiOutput.pathTarget.HasValue)
            {
                Vector3 toTarget = aiOutput.pathTarget.Value - GlobalPosition;
                toTarget.Y = 0f;
                float dist = toTarget.Length();
                float arrivalDist = Mathf.Max(aiOutput.pathSuccessDistance, 0.1f);
                if (dist > arrivalDist)
                {
                    float speedScale = Mathf.Clamp(dist / (arrivalDist + 1f), 0f, 1f);
                    linearDampTarget = 0f;
                    Vector3 dir = toTarget / dist;
                    float maxSpd = _swimming ? _simState.MobData.swimSpeed : _simState.MobData.maxSpeed;
                    Vector3 desiredVelocity = dir * maxSpd * aiOutput.speed * _terrainSpeed * speedScale * statusMoveMul;
                    Vector3 currentVel = LinearVelocity;
                    Vector3 velocityChange = desiredVelocity - new Vector3(currentVel.X, 0f, currentVel.Z);
                    ApplyImpulse(new Vector3(velocityChange.X, 0f, velocityChange.Z) * Mass);

                    if (!targetYaw.HasValue)
                    {
                        targetYaw = Mathf.Atan2(dir.X, dir.Z);
                    }
                }
            }
            // While swimming, the explicit waterDrag in ApplyWaterPhysics
            // owns vertical damping and the current drag owns horizontal.
            // Letting the engine's LinearDamp run on top would muddy both
            // — pin it to zero so the physics block is the only damper.
            if (_swimming && !inBurrow)
            {
                linearDampTarget = 0f;
            }
            // Knockback forces velocity directly each tick (see ApplyKnockback
            // below) — leaving any LinearDamp engaged would compete with the
            // forced velocity and could make the body sub-step under-shoot.
            // Same applies to ApplyMotion's per-tick velocity force.
            if (_simState.KnockbackTime > 0f || _simState.MotionTime > 0f)
            {
                linearDampTarget = 0f;
            }
            if (linearDampTarget != _lastLinearDamp)
            {
                LinearDamp = linearDampTarget;
                _lastLinearDamp = linearDampTarget;
            }

            // Engine gravity continues to act on a RigidBody every tick.
            // Buoyancy in ApplyWaterPhysics caps at `buoyancyAcceleration`
            // upward, so any nonzero gravity-scale leaves a net downward
            // force at the surface (depth=0, buoyancy=0) and the mob sinks
            // until the depth-scaled buoyancy balances g. The player skips
            // gravity entirely while swimming; do the same here by zeroing
            // GravityScale, then restoring 1 on exit. A motionFreezeGravity
            // dart also gets gravity disabled so the lunge hangs level.
            bool motionHang = _simState.MotionTime > 0f && _simState.MotionFreezeGravity;
            float gravityScaleTarget = (motionHang || flying || (_swimming && !inBurrow)) ? 0f : 1f;
            if (gravityScaleTarget != _lastGravityScale)
            {
                GravityScale = gravityScaleTarget;
                _lastGravityScale = gravityScaleTarget;
            }

            if (_swimming && !inBurrow)
            {
                ApplyWaterPhysics((float)delta);
            }

            // Freeze facing alongside the pose while the player can only
            // remember the mob (out of the VisibleTimeMs line-of-sight
            // window → drawn as a static memory silhouette). The pose freeze
            // in _Process stops the skeleton re-skinning; without this gate the
            // body would keep rotating to track the player under that frozen
            // pose, so the silhouette's facing would drift while its animation
            // sat still. playerCanSee == withinVisibleTime, the same window.
            if (!inBurrow && targetYaw.HasValue && playerCanSee)
            {
                Vector3 currentRot = Rotation;
                float yawDelta = Mathf.Wrap(targetYaw.Value - currentRot.Y, -Mathf.Pi, Mathf.Pi);
                float maxStep = _simState.MobData.turnSpeed * (float)delta;
                float step = Mathf.Clamp(yawDelta, -maxStep, maxStep);
                // Skip the Rotation write when step is exactly zero — the
                // mob is already at the target yaw and writing the same
                // value still fires Godot's transform-dirty path.
                if (step != 0f)
                {
                    Rotation = new Vector3(currentRot.X, currentRot.Y + step, currentRot.Z);
                }

                if (CVars.mobDebugYaw.Value)
                {
                    GD.Print($"[yaw] {Name} target={targetYaw.Value:F3} cur={currentRot.Y:F3} delta={yawDelta:F3} step={step:F3} behavior={_curBehavior}");
                }
            }

            if (aiOutput.resetInvestigation)
            {
                _simState.Investigation = default;
            }
            else if (aiOutput.investigation.HasValue)
            {
                _simState.Investigation = aiOutput.investigation.Value;
            }
            if (aiOutput.yell)
            {
                Yell(aiOutput.targetPos);
            }
            // Two-phase burrow. When aiOutput.burrow first goes true we start
            // a Burrowing descent and arm burrowTime. Once the timer elapses
            // we flip to fully Burrowed. As soon as the behavior stops
            // requesting burrow we clear both flags and the mesh pops back up.
            // CanBurrowNow gates only the start edge (swimming / falling) — an
            // already in-progress burrow is pinned underground, so it can't be
            // in either state and stays uninterrupted.
            if (aiOutput.burrow && (burrowing || burrowed || CanBurrowNow))
            {
                if (!burrowing && !burrowed)
                {
                    burrowing = true;
                    burrowTimeMs = _world.GameTimeMs + (ulong)(_simState.MobData.burrowTime * 1000f);
                    SetBurrowed(true);
                    // The mob is diving underground — any arrows lodged in it
                    // can't follow it down, so they scatter at the surface
                    // (same outward arc as a mob's death drop) where it dug in.
                    EjectStuckArrows();
                }
                else if (burrowing && _world.GameTimeMs >= burrowTimeMs)
                {
                    burrowing = false;
                    burrowed = true;
                    // No SetBurrowed call — body stays pinned, layer stays
                    // Burrowed. burrowing → burrowed is internal to the
                    // "in burrow" state and doesn't touch the rigid body.
                }
            }
            else if (burrowing || burrowed)
            {
                burrowing = false;
                burrowed = false;
                SetBurrowed(false);
            }

            // Burrow-state effect transitions. The loop runs while actively
            // digging in (burrowing flag); the complete one-shot fires the
            // moment the descent finishes (burrowing→burrowed); the emerge
            // one-shot fires when leaving any burrow state — either the mob
            // popped back up from underground or its descent was interrupted
            // mid-dig.
            UpdateLoopEffect(ref _burrowLoop, _burrowLoopFx, burrowing);
            if (burrowed && !_prevBurrowed)
            {
                SpawnWorldEffect(_burrowCompleteFx);
            }
            if (!burrowing && !burrowed && (_prevBurrowing || _prevBurrowed))
            {
                SpawnWorldEffect(_burrowEmergeFx);
            }
            // Surface marker for a fully burrowed mob (the body itself fades
            // out of view while burrowed). Idempotent + self-healing: spawns
            // when burrowed, frees on emerge — and naturally restores the mound
            // for a mob loaded from save mid-burrow.
            UpdateBurrowMound(burrowed);
            _prevBurrowing = burrowing;
            _prevBurrowed = burrowed;


            // Torch visibility is gated on the player's memory of this mob —
            // once memory expires, the mob has left the player's awareness
            // sphere and there's no point keeping its light deposit alive
            // (the mob is also not visible, so the torch would have nothing
            // to illuminate from the player's perspective). Same condition
            // as the mesh-visibility gate in _Process. Death cleanup lives in
            // Die() since this block doesn't run for dead mobs.
            bool playerRemembers = _simState.DiscoveryState == EPlayerPerceptionState.Discovered
                && _simState.MemoryTimeMs > _world.GameTimeMs;
            if (CVars.mobDebugTorch.Value)
            {
                MobData md = _simState.MobData;
                string mdState = md == null ? "null" : (md.movingLightScene == null ? "data:non-null,torch:null" : $"data:non-null,torch:{md.movingLightScene.ResourcePath}");
                GD.Print($"[mob_torch] {Name} ambient={_simState.AmbientLight:F3} useTorch={aiOutput.useTorch} suspended={aiOutput.suspended} discovery={_simState.DiscoveryState} memMs={_simState.MemoryTimeMs} now={_world.GameTimeMs} remembers={playerRemembers} torch={_torch != null} mobData={mdState}");
            }
            // Skip torch toggling on suspended ticks — TickAI early-returned
            // with a default-constructed AIOutput, so aiOutput.useTorch is
            // meaningless (always false). Without this gate, BehaviorIdle's
            // 100ms suspend window would tear the torch down and re-create
            // it ~6×/sec, flickering both the LightOn/LightOff fx and the
            // block-light deposit.
            if (!aiOutput.suspended)
            {
                if (aiOutput.useTorch && playerRemembers)
                {
                    if (_torch == null && _simState.MobData?.movingLightScene != null)
                    {
                        _torch = _simState.MobData.movingLightScene.Instantiate<MovingLight>();
                        AddChild(_torch);
                        // Show the held torch prop (lit) in the mob's hand. The
                        // MovingLight is the invisible deposit; this is the visible
                        // model that now carries the flame fx.
                        if (_heldVisual != null && _simState.MobData.heldTorchScene != null)
                        {
                            _heldVisual.SetTorch(_simState.MobData.heldTorchScene);
                            _heldVisual.SetTorchLit(true);
                        }
                    }
                }
                else
                {
                    DespawnTorch();
                }
            }
        }
        else
        {
            // AxisLockAngularY is cleared in Die() so a corpse can tumble
            // when pushed, but a residual slow Y spin keeps re-snapping the
            // model's camera-relative facing facets, making the visual
            // oscillate. Heavy angular damp settles the tumble fast, and the
            // snap-to-zero below kills the last sub-flicker residual the
            // contact resolver leaves behind.
            AngularDamp = 5f;
            // Knockback owns linear damping while active — the per-tick
            // velocity force in ApplyKnockback wants no decay so the corpse
            // covers exactly the authored distance over the window. Once the
            // timer expires the corpse settles into the normal low-damp coast.
            // ApplyMotion gets the same treatment for the same reason.
            LinearDamp = (_simState.KnockbackTime > 0f || _simState.MotionTime > 0f) ? 0f : 0.25f;
            const float SettledAngularSpeedSq = 0.04f;
            Vector3 angVel = AngularVelocity;
            if (angVel.LengthSquared() < SettledAngularSpeedSq && angVel != Vector3.Zero)
            {
                AngularVelocity = Vector3.Zero;
            }
        }

        // Knockback velocity force runs on alive AND dead bodies — see the
        // TickHitstun lift above. Placed outside the alive/dead branch so a
        // killing blow's knockback carries the corpse for the authored time.
        // ApplyMotionPhysics runs first so a knockback landing mid-dart wins
        // the same tick — getting hit while lunging redirects the body.
        ApplyMotionPhysics();
        ApplyKnockback();

        // Auto-freeze on settle. Living mobs additionally gate on
        // SuspendAITimeMs (so we don't pin a mob that's waiting to make
        // its next AI decision); dead mobs only need to be at rest. A
        // corpse that died in motion (cliff fall, explosion knockback)
        // tumbles naturally, then this block pins it the tick its
        // velocity drops below threshold. Weapon hits unfreeze it again
        // by routing through ApplyImpulse → !_impulseApplied is false
        // that tick → re-pin happens once the body re-settles.
        bool wantsFreeze = !Freeze
            && !_impulseApplied
            && LinearVelocity.LengthSquared() < 0.01f
            && (!alive || _simState.SuspendAITimeMs > _world.GameTimeMs);
        if (wantsFreeze)
        {
            Freeze = true;
        }
        _impulseApplied = false;

        using (Profiler.Sample("Mob.UpdateAnimation"))
        {
            UpdateAnimation();
        }
    }

    public void ApplyImpulse(Vector3 impulse)
    {
        // Burrowed mobs are pinned — the per-tick Freeze + zero-velocity
        // block in _PhysicsProcess holds them put, and the inner
        // ApplyCentralImpulse below would also flip Freeze off as a side
        // effect, so reject impulses outright while dug in. The path
        // block above already gates on `burrowPin`; this guard catches
        // any external caller (e.g. Player.PushTouchedMobs).
        if (burrowing || burrowed)
        {
            return;
        }
        // ApplyCentralImpulse is a method, so caching its result
        // doesn't apply — but skipping it for an effectively-zero
        // impulse saves a Jolt call per stable mob per frame.
        if (impulse.X != 0f || impulse.Y != 0f || impulse.Z != 0f)
        {
            if (Freeze)
            {
                Freeze = false;
            }
            _impulseApplied = true;
            ApplyCentralImpulse(impulse);
        }
    }

    // Pure prediction — no state mutation. Mirrors the armor/health resolution
    // in Damage() so attackers can pick their impact effect before the damage
    // actually lands. Splitting this from Damage keeps damage application as a
    // one-way notification, which is the shape we want for networked play
    // (prediction on the client, authoritative apply on the server).
    public EHitResult GetHitType(HitInfo hit)
    {
        if (!alive || burrowed)
        {
            return EHitResult.None;
        }
        // Mirror Hit()'s swap so a crit (dizzy or unaware mob) is reflected
        // in the impact-effect pick (e.g. Lethal when crit damage finishes a
        // mob that the regular damage wouldn't have).
        hit = ApplyCrit(hit);
        hit = ApplyBackstab(hit);
        ApplyResistance(ref hit);
        float incoming = hit.healthDamage;
        if (incoming <= 0f)
        {
            return EHitResult.None;
        }
        // An armor-penetrating hit skips armor entirely and lands on health.
        // Otherwise armor (when present) absorbs the whole hit, matching the
        // legacy fully-absorbed semantics.
        if (armor > 0f && !hit.ArmorPenetrated)
        {
            return EHitResult.Armor;
        }
        return incoming >= health ? EHitResult.Lethal : EHitResult.Health;
    }

    public void Hit(HitInfo hit)
    {
        if (!alive || burrowed)
        {
            return;
        }

        // Crit: a dizzy or untriggered (unaware) mob takes the attacker's
        // crit payload, not the regular one. Swap before everything so armor,
        // health, status, and the in-Damage wake-up all read the crit values
        // consistently. Backstab folds on top — it's a subset of !triggered
        // that adds a positional gate, so OnCrit and OnBackstab can both
        // fire on the same hit.
        hit = ApplyCrit(hit);
        hit = ApplyBackstab(hit);

        // External-interrupt damage during an in-flight attack — interrupt
        // before applying damage so abortEvents fire on coherent pre-damage
        // state. Gated by the action's profile.interruptOnDamage and the
        // tier's canInterrupt; non-interruptible swings keep going.
        _runner?.TryInterrupt();

        // Becoming aware of the attacker. Singleplayer assumes slot[0]
        // tracks the player, so only player-sourced hits write a perception
        // edge here — non-player damage (DamageZone, traps) leaves the
        // perception array alone until it grows multi-source support. Done
        // before Damage so the trigger is recorded even on a killing blow.
        if (hit.source is Player attacker)
        {
            ref PerceptionState slot = ref _simState.PerceptionTargets[0];
            slot.target = attacker;
            slot.perception = 1f;
            slot.aggro = 1f;
            slot.triggered = true;
            slot.lastKnownPosition = attacker.GlobalPosition;
        }

        Damage(hit);

        if (hit.statusEffects != null)
        {
            for (int i = 0; i < hit.statusEffects.Count; i++)
            {
                AddStatusEffect(hit.statusEffects[i]);
            }
        }

        // First-hit yell so nearby mobs converge to investigate. After
        // Damage so a killing or incapacitating blow doesn't yell — CC'd
        // mobs are silent for the same reason TickAI suppresses their
        // AIOutput. Yell() flips _simState.Yelled, mirroring the
        // AIOutput-driven path from BehaviorAttack / BehaviorFlee.
        if (alive && !incapacitated && !_simState.Yelled && hit.source is Node3D sourceNode)
        {
            Yell(sourceNode.GlobalPosition);
        }
    }

    // Shared yell path used by both the AIOutput-driven yell (set by combat
    // behaviors on first sighting) and the damage-driven yell from Hit().
    // Owns the _simState.Yelled flip so callers never set it directly.
    private void Yell(Vector3 targetPos)
    {
        SpawnVoice(_voice?.yell);
        Discover();
        using (Profiler.Sample("Mob.YellBroadcast"))
        {
            // Only the mobs in nearby cells, not every mob in the world.
            _yellReceivers.Clear();
            _world.MobSpatialHash.QueryRadius(GlobalPosition, _simState.MobData.yellVolume, _yellReceivers, exclude: this);
            ETeam yellerTeam = _simState.MobData.team;
            foreach (Mob mob in _yellReceivers)
            {
                MobData receiverData = mob.mobData;
                // Allies investigate the alarm (walk over); everyone else only
                // glances toward it. Whether an ally actually investigates vs
                // looks is still up to its brain (does it wire an investigate
                // behavior) — see HasActionableInvestigationCondition.
                bool lookOnly = receiverData.team != yellerTeam;
                mob.Investigate(
                    targetPos,
                    receiverData.yellInvestigateRange,
                    (ulong)(receiverData.yellInvestigateCancelTime * 1000f),
                    (ulong)(receiverData.yellInvestigatePauseTime * 1000f),
                    lookOnly);
            }
        }
        _simState.Yelled = true;
    }

    // Crit decision — combines an unconditional "untriggered mob is always
    // crit" gate (sneak attack pre-aggro) with a probabilistic vulnerable
    // roll for triggered mobs (dizzy authors vulnerable=1, so a dizzied
    // triggered mob always crits). Reads HitInfo.critRoll so the attacker's
    // QueryHitTriggers prediction and the receiver's ApplyCrit agree on the
    // outcome of this swing. Composes a hypothetical base crit chance as
    // 1 - (1 - base) * (1 - vulnerable) — base is 1 for untriggered mobs
    // and 0 for triggered, leaving room to introduce per-attack critChance
    // later without changing the formula.
    private bool IsCritEligible(HitInfo hit)
    {
        if (!triggered)
        {
            return true;
        }
        float v = vulnerable;
        if (v >= 1f)
        {
            return true;
        }
        if (v <= 0f)
        {
            return false;
        }
        return hit.critRoll < v;
    }

    // Backstab geometry — the attacker is the player, this mob is still
    // untriggered, and the player attacked from within PlayerData.backstabAngle
    // of the mob's facing direction (mob facing away from the player). XZ
    // only; vertical offset doesn't change the directional intent.
    private bool IsBackstab(HitInfo hit)
    {
        if (triggered) { return false; }
        if (hit.source is not Player attacker) { return false; }
        PlayerData pd = attacker.data;
        if (pd == null) { return false; }
        Vector3 mobForward = GlobalTransform.Basis.Z;
        mobForward.Y = 0f;
        Vector3 toMob = GlobalPosition - attacker.GlobalPosition;
        toMob.Y = 0f;
        if (mobForward.LengthSquared() < 0.0001f) { return false; }
        if (toMob.LengthSquared() < 0.0001f) { return false; }
        float cosAngle = mobForward.Normalized().Dot(toMob.Normalized());
        return cosAngle >= Mathf.Cos(pd.backstabAngle);
    }

    // Receiver-side trigger prediction wired into HurtBox.GetHitTriggers —
    // the attacker reads these flags to spawn ItemAction.impactCritEffect /
    // impactBackstabEffect alongside the base impactHealth/Lethal/Armor cue.
    // Mirrors the conditions ApplyCrit / ApplyBackstab use; OnDizzy isn't
    // surfaced (depends on the post-hit dizzy-buildup cross, not predictable).
    public EDamageTriggerFlags QueryHitTriggers(HitInfo hit)
    {
        EDamageTriggerFlags flags = EDamageTriggerFlags.None;
        if (IsCritEligible(hit)) { flags |= EDamageTriggerFlags.Crit; }
        if (IsBackstab(hit)) { flags |= EDamageTriggerFlags.Backstab; }
        return flags;
    }

    // Fold the hit's OnCrit modifiers when the mob is in a crit-eligible
    // state. Mutates the passed-in HitInfo in place via ApplyTrigger and
    // returns it so GetHitType and Hit see the same numbers.
    private HitInfo ApplyCrit(HitInfo hit)
    {
        if (IsCritEligible(hit))
        {
            hit.ApplyTrigger(EDamageTrigger.OnCrit);
        }
        return hit;
    }

    // Fold the hit's OnBackstab modifiers when the geometry + awareness check
    // passes. Stacks with ApplyCrit — a backstab is by construction also a
    // crit, so authors can put generic unawareness bonuses on OnCrit and
    // backstab-specific bonuses on OnBackstab and both fire.
    private HitInfo ApplyBackstab(HitInfo hit)
    {
        if (IsBackstab(hit))
        {
            hit.ApplyTrigger(EDamageTrigger.OnBackstab);
        }
        return hit;
    }

    public void Damage(HitInfo hit)
    {
        // Bestiary kill credit: any damaging hit sourced from the player
        // latches the flag, even when armor soaks the payload. Status-
        // effect ticks and trap damage don't pass through here, so they
        // can't grant credit on their own — but a player setting a mob
        // on fire and then walking away still earns credit because the
        // initial player hit already flipped the flag.
        if (hit.source is Player)
        {
            _simState.DamagedByPlayer = true;
        }
        // Receiver-side resistance fold. Modulates healthDamage / armorPenetration /
        // blunt / knockback in place so all downstream uses (hit.ArmorPenetrated,
        // armor chip mult, knockback impulse) see the resistance-scaled
        // values without needing per-site composition.
        ApplyResistance(ref hit);
        float incoming = hit.healthDamage;
        // Armor handling. Bypass-aware split: a portion of `incoming` skips
        // armor entirely (discrete `ArmorPenetrated` = full bypass; continuous
        // `armorBypassFraction` = partial), the rest is "absorbable" and
        // piles onto the armor chip scaled by `1 + hit.blunt`. Overflow
        // doesn't bleed into health on the absorbed portion — only the
        // pre-resolved bypass lands. Recharge timer resets ONLY when armor
        // actually took a chip — a pure-penetration hit (continuous burn at
        // armorPenetration=1, etc.) shouldn't extend the depletion window since it
        // never touched the armor.
        float bypassFraction = hit.ArmorPenetrated ? 1f : hit.armorBypassFraction;
        float bypassed = incoming * bypassFraction;
        float absorbable = incoming - bypassed;
        float armorAbsorbed = 0f;
        if (armor > 0f && absorbable > 0f)
        {
            float armorDamage = absorbable * (1f + hit.blunt);
            float armorBefore = armor;
            armor = Mathf.Max(0f, armor - armorDamage);
            armorAbsorbed = armorBefore - armor;
            ulong now = _world?.GameTimeMs ?? 0;
            if (armor <= 0f && armorDamage > 0f)
            {
                _simState.ArmorDepleted = true;
                _simState.ArmorRechargeStartMs = now + (ulong)(mobData.armorRecoverTime * 1000f);
                SpawnWorldEffect(_armorDepletedFx);
            }
            else
            {
                _simState.ArmorDepleted = false;
                _simState.ArmorRechargeStartMs = now + (ulong)(mobData.armorRechargeDelay * 1000f);
            }
            _simState.ArmorRecharging = false;
            incoming = bypassed;
        }

        // Any hit wakes an incapacitated mob (the crit swap in Hit() has
        // already amplified the damage payload before we got here). Generic
        // over every effect that flags `incapacitates` — dizzy today, future
        // frozen / knocked-down without touching this branch. The buildup
        // pass still runs after, so a single fat-buildup hit can wake AND
        // re-apply on the same swing, which is consistent with the meter
        // model.
        _statusEffects.ClearIncapacitating();

        // Buildup contributions — funnel into per-effect meters and fold any
        // crossed-threshold effect's applyTrigger (Dizzy fires OnDizzy) back
        // onto the hit before the hitstun/knockback reads below so authored
        // "extra knockback on the hit that landed dizzy" overrides apply.
        _statusEffects.ApplyHitBuildups(ref hit);

        // Hitstun + knockback: stack on top of any buildup handling above so a
        // sub-threshold buildup hit still flinches and shoves. Direction comes
        // from the sender via HitInfo.hitDirection — a zero direction means
        // no knockback regardless of distance.
        if (hit.hitstun > 0f)
        {
            _simState.HitstunTime = Mathf.Max(_simState.HitstunTime, hit.hitstun);
            PlayOneShot(EAnimation.Hitstun);
        }
        float knockbackDistance = hit.knockbackDistance;
        if (knockbackDistance > 0f && hit.knockbackTime > 0f && hit.hitDirection != Vector3.Zero)
        {
            Vector3 dir = hit.hitDirection;
            dir.Y = 0f;
            if (dir.LengthSquared() > 0.0001f)
            {
                // Constant-velocity knockback: distance / time gives the
                // m/s the body needs to hold during the KnockbackTime
                // window to travel exactly `distance` meters. _PhysicsProcess
                // forces this onto LinearVelocity each tick and suppresses
                // LinearDamp so the integral lands on `distance`; the
                // trailing-edge snap below kills residual velocity once the
                // window expires. Beats impulse + damp integration because
                // the result is exact and trivially predictable.
                float speed = knockbackDistance / hit.knockbackTime;
                _simState.KnockbackVelocity = dir.Normalized() * speed;
                _simState.KnockbackTime = Mathf.Max(_simState.KnockbackTime, hit.knockbackTime);
                if (Freeze)
                {
                    Freeze = false;
                }
            }
        }

        health -= incoming;
        if (health <= 0f)
        {
            if (hit.source is Player killer)
            {
                killer.GrantEquippedExperience(mobData.exp);
            }
            Die();
        }
        // Per-hit blood / hurt VO. Suppressed for continuous DoT hits (the
        // owning DamageZone pulses these on its own fxIntervalSeconds via
        // OnHurtBoxFxPulse) so a smear-damage zone doesn't spawn a fresh
        // blood spurt every physics frame.
        else if (incoming > 0f && !hit.dot)
        {
            SpawnWorldEffect(_bloodDamageFx);
            SpawnVoice(_voice?.hurt);
        }

        // Floating-number HUD feedback. Armor chip and armor-penetrated health damage
        // both show — total = whatever the bar actually moved (capped by what
        // armor / health had to give). DoT hits route into the per-second
        // accumulator; one-shot hits fire onDamage immediately. Mirrors the
        // path in Player.OnHurtBoxHit so player + mob HUD text behaves the
        // same regardless of which actor took the hit.
        float totalShown = armorAbsorbed + Mathf.Max(0f, incoming);
        if (totalShown > 0f)
        {
            if (hit.dot)
            {
                _dotHud.AddDamage(totalShown);
            }
            else
            {
                GameClient.Current?.onDamage?.Invoke(hudPosition, totalShown, EHudTextType.DamageLight);
            }
        }
    }

    public StatusEffectState AddStatusEffect(StatusEffectData data) => _statusEffects.Add(data);

    public void RemoveStatusEffect(StatusEffectState state) => _statusEffects.Remove(state);

    public void RemoveStatusEffectsByTagMask(EStat mask) => _statusEffects.RemoveByTagMask(mask);

    // Signed HP delta from a status-effect tick. ArmorPenetration in [0, 1] controls
    // the armor split on the damage branch — 1 (the status-effect default)
    // drops the chunk straight onto health; less than 1 routes the
    // absorbable slice through armor and chips the bar. Heals skip armor
    // entirely. Skips Damage()'s blood / hurt-VO oneshots — those would spam
    // every tick.
    private void ApplyStatusHealthDelta(float delta, float armorPenetration)
    {
        if (delta == 0f || !alive)
        {
            return;
        }
        float before = health;
        if (delta > 0f)
        {
            health = Mathf.Min(maxHealth, health + delta);
        }
        else
        {
            float damage = -delta;
            float p = Mathf.Clamp(armorPenetration, 0f, 1f);
            float bypassed = damage * p;
            float absorbable = damage - bypassed;
            if (armor > 0f && absorbable > 0f)
            {
                armor = Mathf.Max(0f, armor - absorbable);
                ulong nowMs = _world?.GameTimeMs ?? 0;
                if (armor <= 0f)
                {
                    _simState.ArmorDepleted = true;
                    _simState.ArmorRechargeStartMs = nowMs + (ulong)(mobData.armorRecoverTime * 1000f);
                    SpawnWorldEffect(_armorDepletedFx);
                }
                else
                {
                    _simState.ArmorDepleted = false;
                    _simState.ArmorRechargeStartMs = nowMs + (ulong)(mobData.armorRechargeDelay * 1000f);
                }
                _simState.ArmorRecharging = false;
            }
            health = Mathf.Max(0f, health - bypassed);
            if (health <= 0f)
            {
                Die();
            }
        }
        // Status-effect ticks already fire at 1Hz from StatusEffectController,
        // so route directly through onDamage / onHeal — no DoT accumulation
        // needed. Use the realized HP change rather than `delta` so a heal
        // clamped at maxHealth (or a damage tick clamped at 0) only announces
        // what actually moved.
        float change = health - before;
        GameClient client = GameClient.Current;
        if (client != null)
        {
            if (change > 0f)
            {
                client.onHeal?.Invoke(hudPosition, change, EHudTextType.HealLight);
            }
            else if (change < 0f)
            {
                client.onDamage?.Invoke(hudPosition, -change, EHudTextType.DamageLight);
            }
        }
    }

    // Status-effect MAX-health decay callback (see
    // StatusEffectData.maxHealthDrainPerSecond). Shrinks the persisted MaxHealth,
    // clamps current Health down to follow, and dies when the max is exhausted.
    // Deliberately routes nothing through onDamage / the DoT path, so a withering
    // summon drains away with no floating damage number — the shrinking health
    // bar is the only feedback. Die() self-guards on !alive against re-entry.
    private void DrainMaxHealth(float amount)
    {
        if (amount == 0f || !alive)
        {
            return;
        }
        _simState.MaxHealth = Mathf.Max(0f, _simState.MaxHealth - amount);
        if (_simState.Health > _simState.MaxHealth)
        {
            _simState.Health = _simState.MaxHealth;
        }
        if (_simState.MaxHealth <= 0f)
        {
            Die();
        }
    }

    private void TickArmor(float dt)
    {
        float max = maxArmor;
        if (max <= 0f || armor >= max)
        {
            return;
        }
        ulong now = _world?.GameTimeMs ?? 0;
        if (now < _simState.ArmorRechargeStartMs)
        {
            return;
        }
        if (!_simState.ArmorRecharging)
        {
            _simState.ArmorRecharging = true;
            SpawnWorldEffect(_simState.ArmorDepleted ? _armorRecoverStartFx : _armorRechargeStartFx);
        }
        MobData md = mobData;
        float speed = md?.armorRechargeSpeed ?? 0f;
        armor = Mathf.Min(max, armor + speed * dt);
        if (armor >= max)
        {
            _simState.ArmorDepleted = false;
        }
    }

    // Counts the per-hit flinch + knockback windows down each physics tick.
    // The hitstun anim is a one-shot (latched via PlayOneShot in Damage), so
    // this method only owns the state-clear for that field — the animator
    // naturally falls out of the anim when it finishes or another PlayOneShot
    // replaces it. Knockback is two-phase: while KnockbackTime > 0 the body's
    // horizontal velocity is forced to KnockbackVelocity (see ApplyKnockback);
    // when the timer hits zero we snap horizontal velocity to zero so the body
    // doesn't coast past the authored distance.
    private void TickHitstun(float dt)
    {
        if (_simState.HitstunTime > 0f)
        {
            _simState.HitstunTime = Mathf.Max(0f, _simState.HitstunTime - dt);
        }
        if (_simState.KnockbackTime > 0f)
        {
            _simState.KnockbackTime = Mathf.Max(0f, _simState.KnockbackTime - dt);
            if (_simState.KnockbackTime <= 0f)
            {
                // Trailing edge — kill residual horizontal velocity so the
                // body stops cleanly at distance. Y is preserved so gravity
                // / buoyancy continue uninterrupted.
                LinearVelocity = new Vector3(0f, LinearVelocity.Y, 0f);
                _simState.KnockbackVelocity = Vector3.Zero;
            }
        }
    }

    // Forces horizontal velocity to the cached knockback vector each tick
    // during the window. Bypasses LinearDamp + path-driven impulses (the AI
    // gate above already returns aiOutput=default during hitstun, so there's
    // no pathTarget block running) so the body covers exactly `distance`
    // meters over `time` seconds.
    private void ApplyKnockback()
    {
        if (_simState.KnockbackTime <= 0f)
        {
            return;
        }
        if (Freeze)
        {
            Freeze = false;
        }
        _impulseApplied = true;
        Vector3 v = _simState.KnockbackVelocity;
        LinearVelocity = new Vector3(v.X, LinearVelocity.Y, v.Z);
    }

    // Mirrors TickHitstun for the ApplyMotion window. Counts MotionTime down
    // each tick; on the trailing edge snaps horizontal velocity to zero so a
    // dart ends crisp rather than coasting past the authored distance.
    private void TickMotion(float dt)
    {
        if (_simState.MotionTime <= 0f)
        {
            return;
        }
        _simState.MotionTime = Mathf.Max(0f, _simState.MotionTime - dt);
        if (_simState.MotionTime <= 0f)
        {
            LinearVelocity = new Vector3(0f, LinearVelocity.Y, 0f);
            _simState.MotionVelocity = Vector3.Zero;
            _simState.MotionFreezeGravity = false;
        }
    }

    // Mirrors ApplyKnockback for ApplyMotion. Forces horizontal velocity to
    // the cached motion vector each tick during the window so the dart
    // overrides any residual path impulse. Knockback wins ties because
    // ApplyKnockback runs after this in _PhysicsProcess and writes the same
    // velocity — getting hit mid-dart redirects the body.
    private void ApplyMotionPhysics()
    {
        if (_simState.MotionTime <= 0f)
        {
            return;
        }
        if (Freeze)
        {
            Freeze = false;
        }
        _impulseApplied = true;
        Vector3 v = _simState.MotionVelocity;
        float y = _simState.MotionFreezeGravity ? 0f : LinearVelocity.Y;
        LinearVelocity = new Vector3(v.X, y, v.Z);
    }

    // Deactivate fires the LightOff fx authored on the MovingLight as the
    // natural "torch goes out" cue, then QueueFree drops the node. No-op
    // when no torch is held.
    private void DespawnTorch()
    {
        if (_torch == null)
        {
            return;
        }
        // Hand off so the light fades out and frees itself rather than cutting.
        _torch.Deactivate(freeWhenDone: true);
        _torch = null;
        // Drop the visible held prop (its flame fx fades itself out via Stop).
        _heldVisual?.SetTorch(null);
    }

    private void Die()
    {
        if (!alive)
        {
            return;
        }

        alive = false;
        // Move the rigid body onto Dead and the hurtbox onto DeadHurtBox
        // so projectile sweeps / aim raycasts / melee + hitscan attack
        // masks (all keyed off Mob / HurtBox) stop picking up the corpse.
        // The body keeps Environment in its mask so it still rests on
        // terrain and knockback impulses carry the corpse through the rest
        // of the hitstun window. Live mobs no longer collide with corpses
        // as a side effect (their mask is Environment | Player | Mob),
        // which is fine — pathing already steers around the mob spatial
        // hash. Split into two layers (matching Burrowed/BurrowedHurtBox)
        // so future corpse-targeting tools can pick the hurtbox shape
        // without also catching the movement-collision volume.
        CollisionLayer = (uint)ECollisionLayer.Dead;
        CollisionMask = (uint)ECollisionLayer.Solid;
        if (_hurtBox != null)
        {
            _hurtBox.CollisionLayer = (uint)ECollisionLayer.DeadHurtBox;
        }
        // Fire the death event before the despawn / fx cascade below so
        // subscribers see a consistent snapshot (mob still in the world,
        // sim state intact). GameClient bridges this into bestiary kill
        // credit when DamagedByPlayer is set.
        GameClient.Current?.NotifyMobKilled(_simState.MobData, _simState.DamagedByPlayer);
        // The per-frame torch gating in _PhysicsProcess only runs while alive,
        // so a dead mob's lit torch would otherwise leak its light deposit
        // and loop FX.
        DespawnTorch();
        // Drop the elite crown — the marker is for live elites; a corpse keeps
        // no halo.
        if (_crown != null)
        {
            _crown.QueueFree();
            _crown = null;
        }
        // Drop every active status effect so any looping fx (dizzy stars,
        // burning crackle, wet drip) stops the moment the mob dies — same
        // "only runs while alive" rationale as the torch cleanup above.
        // Tick is already alive-gated, but loop fx instances would otherwise
        // persist on the corpse until QueueFree.
        _statusEffects.Clear();
        SpawnWorldEffect(_deathFx);
        SpawnVoice(_voice?.death);
        EjectLoot();
        EjectStuckArrows();
        AxisLockAngularY = false;
        // Don't unfreeze on death — a mob that was idle-pinned when it
        // died stays pinned. A mob that died mid-motion / from a hit
        // already has Freeze=false; the new auto-freeze branch above
        // re-pins it once it settles.
        PlayOneShot(EAnimation.Die);

        // Corpse-less species (the fairy orb): loot has been ejected and the
        // death fx fired above; now fade the body out in place and remove it
        // for good rather than leaving a resting corpse. Zero ascent so it
        // winks out where it died instead of rising like the escape vanish.
        if (mobData != null && mobData.despawnOnDeath)
        {
            BeginVanish(0f, mobData.deathDespawnSeconds);
        }
    }

    // Mirrors Chest.Complete's loot ejection: each ItemCount entry on MobData
    // fires `count` Loot instances outward on a 45° upward arc. Random
    // horizontal angle per item so a multi-drop carcass scatters rather than
    // dropping in a tight stack.
    private void EjectLoot()
    {
        if (_world == null)
        {
            return;
        }
        MobData md = mobData;
        bool hasSpeciesLoot = md?.loot != null && md.loot.Count > 0;
        // Elites drop the shared crown trophy on top of their species loot —
        // the same halo (SimData.EliteCrownScene) that marked them alive, now a
        // collectible. Authored once on SimData so it's species-agnostic, and
        // dropped even by an elite of a mob type with no authored loot.
        LootData eliteLoot = IsElite ? _world.SimData?.EliteLoot : null;
        if (!hasSpeciesLoot && eliteLoot == null)
        {
            return;
        }
        var rng = new Random();
        float ejectSpeed = md?.lootEjectSpeed ?? DefaultLootEjectSpeed;
        float horizontalSpeed = ejectSpeed * Mathf.Cos(Mathf.Pi / 4f);
        float verticalSpeed = ejectSpeed * Mathf.Sin(Mathf.Pi / 4f);
        if (hasSpeciesLoot)
        {
            for (int i = 0; i < md.loot.Count; i++)
            {
                ItemCount entry = md.loot[i];
                if (entry?.descriptor?.item == null)
                {
                    continue;
                }
                for (int n = 0; n < entry.count; n++)
                {
                    EjectLootPiece(entry.descriptor, horizontalSpeed, verticalSpeed, rng);
                }
            }
        }
        if (eliteLoot != null)
        {
            EjectLootPiece(eliteLoot, horizontalSpeed, verticalSpeed, rng);
        }
    }

    // Fire a single loot item outward on a 45° upward arc with a random
    // horizontal heading so a multi-drop carcass scatters rather than stacking.
    private void EjectLootPiece(ItemData item, float horizontalSpeed, float verticalSpeed, Random rng)
    {
        _world.SpawnLoot(GlobalPosition + Vector3.Up, BuildLootImpulse(horizontalSpeed, verticalSpeed, rng), item);
    }

    // Descriptor variant — composes the entry's permanent mods onto a fresh
    // state (e.g. a goblin that drops a Fragile bomb) and spawns that state so
    // the dropped item carries the mod. Each piece is its own stackCount=1 state.
    private void EjectLootPiece(ItemDescriptor descriptor, float horizontalSpeed, float verticalSpeed, Random rng)
    {
        _world.SpawnLoot(GlobalPosition + Vector3.Up, BuildLootImpulse(horizontalSpeed, verticalSpeed, rng), descriptor.CreateState());
    }

    // 45° upward arc on a random horizontal heading — shared so multi-drop
    // carcasses scatter rather than stacking.
    private static Vector3 BuildLootImpulse(float horizontalSpeed, float verticalSpeed, Random rng)
    {
        float angle = (float)(rng.NextDouble() * Mathf.Pi * 2f);
        return new Vector3(
            horizontalSpeed * Mathf.Cos(angle),
            verticalSpeed,
            horizontalSpeed * Mathf.Sin(angle)
        );
    }

    // Spawn an ArrowStuck child at the world-space hit point. Caller has
    // already verified `alive` and that the firing weapon authors an
    // arrowLootData. The stuck arrow registers with the source weapon's
    // outstandingArrows list so it counts against the cap and recovers
    // ammo via the standard OnArrowRemoved path when it leaves play.
    public void StickArrow(WeaponState sourceWeapon, ArrowLootData data, Vector3 worldHitPos, Vector3 hitDirection)
    {
        if (sourceWeapon == null || data == null)
        {
            return;
        }
        ArrowStuck stuck = ArrowStuck.Create(this, data, sourceWeapon, worldHitPos, hitDirection);
        if (stuck == null)
        {
            return;
        }
        _stuckArrows.Add(stuck);
        sourceWeapon.RegisterArrow(stuck);
    }

    // Called by ArrowStuck.Recover when the weapon's central ammo-recharge
    // timer reclaims this arrow while it's still embedded in this (live) mob.
    // Drops the arrow from _stuckArrows so the upcoming ReturnAmmoOnRemoval
    // doesn't get re-fired by _ExitTree later. Mirrors the "lost with the mob"
    // path: 1 ammo returns to the source weapon, no loose loot spawns.
    public void OnStuckArrowExpired(ArrowStuck stuck)
    {
        if (stuck == null)
        {
            return;
        }
        _stuckArrows.Remove(stuck);
        stuck.ReturnAmmoOnRemoval();
    }

    // Drop each stuck arrow as loose loot with the same 45° outward arc
    // EjectLoot uses for mob drops. Called from Die after EjectLoot so the
    // arrows scatter alongside the mob's authored loot. The stuck instance
    // hands its weapon binding off to the new ArrowLoot via DropAsLoot —
    // no ammo is bumped on the transition.
    private void EjectStuckArrows()
    {
        if (_stuckArrows.Count == 0)
        {
            return;
        }
        var rng = new Random();
        float ejectSpeed = mobData.lootEjectSpeed;
        float horizontalSpeed = ejectSpeed * Mathf.Cos(Mathf.Pi / 4f);
        float verticalSpeed = ejectSpeed * Mathf.Sin(Mathf.Pi / 4f);
        // Iterate a snapshot — DropAsLoot frees each ArrowStuck, which will
        // null out _sourceWeapon and remove the node from the tree.
        ArrowStuck[] snapshot = _stuckArrows.ToArray();
        _stuckArrows.Clear();
        for (int i = 0; i < snapshot.Length; i++)
        {
            float angle = (float)(rng.NextDouble() * Mathf.Pi * 2f);
            var impulse = new Vector3(
                horizontalSpeed * Mathf.Cos(angle),
                verticalSpeed,
                horizontalSpeed * Mathf.Sin(angle)
            );
            snapshot[i].DropAsLoot(impulse);
        }
    }

    // Mob is leaving the scene tree. Two cases:
    //   - Mob died: Die already drained _stuckArrows via EjectStuckArrows,
    //     so the list is empty and this loop is a no-op.
    //   - Mob unloaded mid-stuck (chunk eviction, despawn): each remaining
    //     stuck arrow returns 1 ammo to its source weapon. The visual is
    //     about to be freed anyway as a child of this mob, so no loot
    //     spawns — the arrow is treated as "lost with the mob."
    public override void _ExitTree()
    {
        if (_stuckArrows.Count > 0)
        {
            ArrowStuck[] snapshot = _stuckArrows.ToArray();
            _stuckArrows.Clear();
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i].ReturnAmmoOnRemoval();
            }
        }
        base._ExitTree();
    }

    // Dug up by the player's shovel (World.TryDig). Latches full awareness of
    // the digger onto the primary perception slot — same edge the hit handler
    // writes. For a burrowed mob this makes its Burrow behavior fail its
    // out-of-range gate and transition to attack/chase, which drops
    // aiOutput.burrow and lets the per-frame burrow block surface the mob and
    // fire the emerge fx (single, behavior-driven — clearing the flags here
    // would just be re-asserted by BehaviorBurrow next tick). For a mob freshly
    // spawned by a buried spot it simply makes it engage immediately. Relies on
    // every burrowing brain having an in-range exit from its Burrow node, which
    // it must (or the mob could never surface on its own).
    public void DigUp(Player digger)
    {
        if (!alive || digger == null)
        {
            return;
        }
        // Surface immediately — clear the burrow so the mesh pops above ground
        // and the body unfreezes, rather than waiting for the behavior tree to
        // transition out (which left a dug mob invisible underground). _prev*
        // are cleared so the per-frame burrow block doesn't re-fire the emerge.
        if (burrowing || burrowed)
        {
            burrowing = false;
            burrowed = false;
            SetBurrowed(false);
            SpawnWorldEffect(_burrowEmergeFx);
            _prevBurrowing = false;
            _prevBurrowed = false;
        }
        // A burrowed mob was perception-suppressed (Hidden), so it would dither
        // out even after surfacing — force discovery so the player sees what
        // they dug up right away.
        Discover();
        // Latch full awareness of the digger so the mob engages the moment the
        // stun wears off — same edge the hit handler writes.
        ref PerceptionState slot = ref _simState.PerceptionTargets[0];
        slot.target = digger;
        slot.perception = 1f;
        slot.aggro = 1f;
        slot.triggered = true;
        slot.lastKnownPosition = digger.GlobalPosition;
        // Brief per-species stun on emergence (dizzy) so the player gets a beat
        // before the mob attacks. incapacitates=true zeroes the AI output, which
        // also keeps BehaviorBurrow from re-asserting the burrow while stunned.
        // Null (e.g. an authored boss) = no stun.
        StatusEffectData stun = _simState.MobData?.dugUpStun;
        if (stun != null)
        {
            AddStatusEffect(stun);
        }
    }

    // Make the player instantly aware of this mob: set the discovery state,
    // record it on the world knowledge sim, and refresh memory so the mesh
    // renders solid instead of dithering out. Shared by the damage/yell path
    // and DigUp. Idempotent.
    public void Discover()
    {
        _simState.PlayerPerception = 1;
        _simState.DiscoveryState = EPlayerPerceptionState.Discovered;
        _world.WorldState?.SimState?.DiscoverMob(_simState.MobData);
        _simState.MemoryTimeMs = _world.GameTimeMs + (ulong)(_simState.MobData.MemoryStationaryTime * 1000);
    }

    // Called on the burrow edges (false→burrowing in the transition block,
    // burrowing/burrowed→false in the same block) and from Initialize so a
    // mob loaded in a saved burrow state ends up with the right rigid-body
    // + collision configuration without waiting a tick. The per-frame
    // `inBurrow` gate on path/yaw is independent — it skips work; this
    // sets up the underlying state.
    private void SetBurrowed(bool isBurrowed)
    {
        if (isBurrowed)
        {
            // Descent visual is the mesh drop in _Process, not physics
            // motion. Pin the body so behaviors / external impulses can't
            // shove it around (ApplyImpulse also early-returns while
            // burrowed for defense in depth).
            if (LinearVelocity != Vector3.Zero)
            {
                LinearVelocity = Vector3.Zero;
            }
            if (!Freeze)
            {
                Freeze = true;
            }
            // Player↔Mob is suppressed in both directions: Player.mask
            // doesn't include Burrowed, and the mob's own mask drops the
            // Player bit. Environment stays so the body still rests on
            // the ground.
            CollisionLayer = (uint)ECollisionLayer.Burrowed;
            CollisionMask = (uint)ECollisionLayer.Solid;
            // Hurtbox moves to its own BurrowedHurtBox layer so attack
            // raycasts (which mask ECollisionLayer.HurtBox) no longer hit
            // it. Keeping it separate from the body's Burrowed movement
            // layer lets a future scan / dig tool target underground
            // creatures (mask BurrowedHurtBox) without also picking up the
            // movement-collision volume.
            if (_hurtBox != null)
            {
                _hurtBox.CollisionLayer = (uint)ECollisionLayer.BurrowedHurtBox;
            }
        }
        else
        {
            // Restore default mob collision. Freeze is left as-is — the
            // next impulse from a behavior unfreezes naturally via
            // ApplyImpulse, and the auto-freeze block at the bottom of
            // _PhysicsProcess keeps it pinned if the mob has no movement
            // intent. Explicitly unfreezing here would race with that.
            CollisionLayer = (uint)ECollisionLayer.Mob;
            CollisionMask = (uint)(ECollisionLayer.Solid | ECollisionLayer.Player | ECollisionLayer.Mob);
            if (_hurtBox != null)
            {
                _hurtBox.CollisionLayer = (uint)ECollisionLayer.HurtBox;
            }
        }
    }

    // World-parented one-shot at the mob's feet — matches the footstep /
    // ripple convention so the puff stays put as the mob keeps moving.
    private void SpawnWorldEffect(PackedScene scene)
    {
        if (scene == null || _world == null)
        {
            return;
        }
        Fx.Create(scene, _world, GlobalPosition);
    }

    // Spawn a clip from this species' voice bank, applying its pitch shift.
    // No-ops on a null scene (a species without that vocalization) or before
    // the bank is wired.
    private void SpawnVoice(PackedScene scene)
    {
        if (scene == null || _world == null)
        {
            return;
        }
        Fx.Create(scene, _world, GlobalPosition, _voice?.pitchShift ?? 1f);
    }

    // Mirrors Player.UpdateLoopEffect — instantiate parented to the mob on
    // activation, Stop() and drop the reference on deactivation. The Stop()
    // path lets the trailing audio + particles wind down without snapping.
    private void UpdateLoopEffect(ref Fx instance, PackedScene scene, bool active)
    {
        if (active)
        {
            if (instance == null && scene != null)
            {
                instance = Fx.Create(scene, this, Vector3.Zero);
            }
        }
        else if (instance != null)
        {
            instance.Stop();
            instance = null;
        }
    }

    // Spawn / free the burrow dirt mound parented to the mob at its feet
    // (origin sits at ground contact). Tying it to the mob means it tracks the
    // pinned body and is freed automatically when the mob emerges, dies, or its
    // chunk unloads — no separate world entity to clean up.
    private void UpdateBurrowMound(bool active)
    {
        if (active)
        {
            if (_burrowMound == null && _burrowMoundScene != null)
            {
                _burrowMound = _burrowMoundScene.Instantiate<Node3D>();
                AddChild(_burrowMound);
            }
        }
        else if (_burrowMound != null)
        {
            _burrowMound.QueueFree();
            _burrowMound = null;
        }
    }

    private void UpdateTerrainSpeed()
    {
        _terrainSpeed = 1f;
        foreach (Foliage foliage in _foliageCollisions)
        {
            _terrainSpeed = Mathf.Min(_terrainSpeed, foliage.speed);
        }
    }

    // Emit a dynamic water ripple while wading/swimming. Sample the voxel at
    // the mob's feet — if it's water, scan upward to find the surface Y and
    // hand off to WaterRippleEmitter, which gates emission by a per-stride
    // distance threshold (so a stationary mob emits nothing).
    private void UpdateWaterRipples()
    {
        WorldState ws = _world?.WorldState;
        if (ws == null)
        {
            return;
        }
        Vector3 pos = GlobalPosition;
        int fx = Mathf.FloorToInt(pos.X);
        int fy = Mathf.FloorToInt(pos.Y);
        int fz = Mathf.FloorToInt(pos.Z);
        bool inWater = ws.GetVoxelWorld(fx, fy, fz) == VoxelType.Water;
        if (!inWater)
        {
            _rippleEmitter.Update(pos, false, 0f, 1f);
            return;
        }
        int scanY = fy;
        while (ws.GetVoxelWorld(fx, scanY, fz) == VoxelType.Water)
        {
            scanY++;
        }
        Vector3 ripplePos = new(pos.X, scanY, pos.Z);
        _rippleEmitter.Update(ripplePos, true, 0.8f, 0.5f);
    }

    // Footsteps and footprints emit from the model's foot-contact method track
    // (see EmitFootstep) rather than from this method. What stays here is the
    // water-enter splash + the water/tall-grass movement loop gates, which key
    // off the voxel-at-feet sample and the navigator's intent.
    private void UpdateFootsteps()
    {
        WorldState ws = _world?.WorldState;
        if (ws == null)
        {
            return;
        }
        Vector3 pos = GlobalPosition;
        int fx = Mathf.FloorToInt(pos.X);
        int fy = Mathf.FloorToInt(pos.Y);
        int fz = Mathf.FloorToInt(pos.Z);
        bool inWater = ws.GetVoxelWorld(fx, fy, fz) == VoxelType.Water;
        Vector2 horizVel = new(LinearVelocity.X, LinearVelocity.Z);
        float horizSpeedSq = horizVel.LengthSquared();

        // One splash at the moment the mob first dips into water. The
        // navigator can drag a mob through a water voxel on a single frame,
        // so the alive guard prevents a corpse from splashing every tick if
        // it's later kicked into water.
        if (inWater && !_wasInWaterPrev && alive)
        {
            SpawnWorldEffect(_waterEnterSplashFx);
        }
        _wasInWaterPrev = inWater;

        // Movement-gated loops. Navigator intent counts as "moving" even
        // when LinearVelocity hasn't built up yet — same reason Player keys
        // off _inputMove. A mob that has arrived at its goal (e.g. holding
        // an encircle slot) no longer counts as intent-moving even though
        // its state stays Goto until the behavior switches. Tall-grass and
        // water are mutually exclusive: if the mob's feet are wet, the
        // water loop wins.
        bool intentMoving = _navigator != null
            && _navigator.CurrentState != MobNavigator.State.Idle
            && !_navigator.HasArrived;
        bool moving = alive && (intentMoving || horizSpeedSq > _movingMinSpeedSq);
        bool waterLoopActive = moving && inWater;
        bool foliageLoopActive = moving && !inWater && _foliageCollisions.Count > 0;
        UpdateLoopEffect(ref _waterMovementLoop, _waterMovementLoopFx, waterLoopActive);
        UpdateLoopEffect(ref _foliageMovementLoop, _foliageMovementLoopFx, foliageLoopActive);
    }

    public void AddTerrainModifier(Foliage foliage)
    {
        _foliageCollisions.Add(foliage);
    }

    public void RemoveTerrainModifier(Foliage foliage)
    {
        _foliageCollisions.Remove(foliage);
    }

    // Render the navigator's active path as line segments via DebugDraw.
    // Lifted slightly off the surface so paths don't z-fight with the
    // ground mesh on flat terrain. Single-frame lifetime — relies on this
    // method being called every physics tick to stay on screen.
    private void DrawPathDebug(Vector3? pathTarget)
    {
        if (!pathTarget.HasValue)
        {
            return;
        }
        const float Lift = 0.15f;
        Vector3 mobPos = GlobalPosition + new Vector3(0f, Lift, 0f);
        Vector3 target = pathTarget.Value + new Vector3(0f, Lift, 0f);
        DebugDraw.Line(mobPos, target, new Color(1f, 0.85f, 0.1f));
        DebugDraw.Sphere(target, 0.25f, new Color(1f, 0.3f, 0.3f));
    }

}
