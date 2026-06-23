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
    // Wrapper holding the model mesh AND the HudAnchor as siblings; this is the
    // node frozen for the memory silhouette so the HUD anchor rides the freeze
    // while _mesh.Visible can still toggle independently. Optional — species
    // without a freezing model (e.g. the fairy orb) leave it null and the pin
    // falls back to _mesh, preserving the live-tracking HUD.
    [Export] private Node3D _visuals;
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
    // appropriate small/medium/large variant from scenes/fx/.
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
    // Live cap — the drainable, persisted base (_simState.MaxHealth, shrunk by
    // DrainMaxHealth's withering mechanic) plus any MaxHealth stat modifier from
    // active status effects, so a buff applied mid-life raises the cap and HUD
    // bar immediately. The per-frame clamp in Tick pulls current health down
    // when the buff expires and this shrinks.
    public float maxHealth => _simState.MaxHealth + ComposeStat(EStat.MaxHealth);
    public float health { get => _simState.Health; set => _simState.Health = value; }
    // Live cap — base armor plus any MaxArmor stat modifier from active status
    // effects (e.g. Stoneskin), so a buff applied mid-life raises the recharge
    // ceiling and the HUD bar immediately. TickArmor clamps current armor down
    // when the buff expires and this shrinks.
    public float maxArmor => (mobData?.maxArmor ?? 0f) + ComposeStat(EStat.MaxArmor);
    public float armor { get => _simState.Armor; set => _simState.Armor = value; }
    // Elite marker, authored on the spawning MobDescriptor. Drives the crown,
    // shared elite buff, and crown-trophy loot; the signature effect rides
    // StatusEffects and the HUD icon rides Badge. Immutable after spawn.
    public bool IsElite => _simState?.Elite ?? false;
    // HUD badge icon authored on the spawning MobDescriptor (null = none), read
    // once by MobHUD at init.
    public Texture2D Badge => _simState?.Badge;

    // Runtime WeaponState per WeaponData this mob attacks with (claw, battle cry,
    // bite, ...). Created lazily by GetWeapon the first time BehaviorAttack swings
    // that weapon, then set as the attack action's primaryItem so the item-side
    // damage + weapon-mod path (chain lightning, pierce, on-hit enchants) fires
    // for mobs exactly as it does for the player. The weapons are authored next to
    // the mob's data resource and referenced from the brain's AttackBehaviorData.
    private readonly Dictionary<WeaponData, WeaponState> _weapons = new();
    // Elite signature weapon-mod (or null) applied to every weapon this mob wields.
    // Captured at spawn (Initialize) before any WeaponState exists, then composed
    // onto each as GetWeapon creates it.
    private StatusEffectData _eliteWeaponMod;

    // Resolve — creating + caching on first use — the runtime WeaponState backing
    // one of this mob's authored weapons. Null WeaponData yields null. A pending
    // elite weapon-mod is composed onto the state on creation so its on-attack
    // payload (e.g. Lightning) rides the mob's attacks.
    public WeaponState GetWeapon(WeaponData data)
    {
        if (data == null)
        {
            return null;
        }
        if (!_weapons.TryGetValue(data, out WeaponState state))
        {
            state = (WeaponState)data.CreateState();
            if (_eliteWeaponMod != null)
            {
                state.statusEffects.AddWeaponMod(_eliteWeaponMod, EWeaponModScope.AllAttacks, 0);
            }
            _weapons[data] = state;
        }
        return state;
    }
    // True when at least one active status effect flags `incapacitates`.
    // Drives AI suppression and the no-yell-while-CC'd path. Dizzy authors
    // the flag; future Frozen / Knocked-Down would too without touching Mob.
    public bool incapacitated => _statusEffects?.Incapacitated ?? false;

    // Composite crit-vulnerability in [0, 1] from active status effects.
    // Folded into the crit decision against HitInfo.critRoll. Dizzy authors
    // vulnerable=1 so a dizzied mob always crits on a triggered hit.
    public float vulnerable => _statusEffects?.Vulnerable ?? 0f;
    public EPlayerPerceptionState playerPerceptionState { get => _simState.DiscoveryState; set => _simState.DiscoveryState = value; }
    // True when this mob is an active threat to the player: alive, dangerous, and
    // on a team hostile to the player, AND either triggered (it has noticed the
    // player and gone on combat alert) or currently visible to the player. Read
    // by World.IsDangerPresent to forbid "safe" actions like cooking while a
    // threat is around. Mirrors the discovery test in the visibility update —
    // Discovered with unexpired memory — so a dangerous mob the player has just
    // seen (even if it ducked behind cover) still counts.
    public bool IsThreateningPlayer
    {
        get
        {
            if (!alive || mobData == null || !mobData.dangerous)
            {
                return false;
            }
            if (Teams.AreAllied(ActorTeam, ETeam.Player))
            {
                return false;
            }
            PerceptionState[] targets = _simState.PerceptionTargets;
            bool triggered = targets != null && targets.Length > 0 && targets[0].triggered;
            bool visibleToPlayer = _simState.DiscoveryState == EPlayerPerceptionState.Discovered
                && _simState.MemoryTimeMs > _world.GameTimeMs;
            return triggered || visibleToPlayer;
        }
    }
    // Circumstances this mob required to spawn (see ESpawnConditions). Read by
    // World.CleanupOffConditionMobs to despawn a mob whose conditions have
    // lapsed once the player is far and unaware. None = unconditional, never
    // cleaned up on this account.
    public ESpawnConditions spawnConditions => _simState.SpawnConditions;
    public MobData mobData => _simState.MobData;
    // The persistent sim state backing this mob. Exposed so World's companion
    // chunk-unload rescue can re-file the state under a new chunk (see
    // World.RescueCompanion / WorldState.MoveEntityToChunk).
    public MobSimState SimState => _simState;
    // Per-instance language override (set by WorldGen / world files) takes
    // precedence over MobData.language. Mirrors SpeakDialogue's resolution
    // order so dialogue and any other UI surface (merchant screen, etc.)
    // agree on which language to scramble against.
    public LanguageData SpokenLanguage => _simState?.Language ?? mobData?.language;
    public StringName defaultBehavior => _simState?.InitialBehavior ?? (mobData != null ? mobData.defaultBehavior : (StringName)"Idle");
    // True when this mob is the player's companion/pet (drives the follow/stay
    // brain and World companion tracking). Companion-ness IS being tamed: a mob
    // becomes a companion the moment its loyalty crosses MobData.tameLoyalty
    // (or it spawns pre-tamed, like the starter pet). See Tame / ActorTeam.
    public bool IsCompanion => _simState != null && _simState.Tamed;
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
    // True when any perception slot has latched the combat-alert (triggered)
    // state — i.e. this mob is actively engaged with a target. Read by
    // companion threat acquisition (ThreatScan) so a dog only attacks enemies
    // that are in combat, not ones idling unaware.
    public bool IsTriggered
    {
        get
        {
            PerceptionState[] targets = _simState?.PerceptionTargets;
            if (targets == null)
            {
                return false;
            }
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i].triggered)
                {
                    return true;
                }
            }
            return false;
        }
    }

    // ---- Companion threat awareness (MobData.threatTeam) ----
    // Accumulated by MobAI.AccumulateThreatPerception against the nearest enemy
    // mob; read by the companion brain's tier conditions and BehaviorWary /
    // BehaviorDogAttack so the wary/attack response and the target agree.
    public Mob ThreatTarget
    {
        get
        {
            // Null out a dead / freed target so behaviors never swing at or stare
            // down a corpse in the window between a kill (60Hz) and the next
            // perception tick (~10Hz) clearing the slot. See AccumulateThreatPerception.
            Mob t = _simState?.ThreatPerception.target as Mob;
            return (t != null && GodotObject.IsInstanceValid(t) && t.alive) ? t : null;
        }
    }
    // Perception has crossed the lower "wary" tier (but not necessarily Alert).
    public bool ThreatWary => mobData != null && _simState != null
        && _simState.ThreatPerception.perception >= mobData.PerceptionThresholdWary;
    // Perception has latched the full alert/combat tier (>= PerceptionThresholdAlert).
    public bool ThreatTriggered => _simState != null && _simState.ThreatPerception.triggered;
    public bool ThreatCanSee => _simState != null && _simState.ThreatPerception.canSee;
    public Vector3 ThreatLastKnownPosition => _simState?.ThreatPerception.lastKnownPosition ?? GlobalPosition;

    // ---- Aggro (per-enemy threat priority, MobSimState.Aggro) ----
    // Damage-driven, decoupled from perception: who has hurt this mob (or its
    // master) most. Added from Mob.Damage and the player→companion relay,
    // decayed each perception tick, and read by target selection.
    public void AddAggro(Node3D source, float amount) => _simState?.Aggro.Add(source, amount);
    public float GetAggro(Node3D target) => _simState?.Aggro.Get(target) ?? 0f;

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

    // Catch up status effects by `dt` seconds in one call — the sleep
    // time-skip (World.AdvanceTime) bulk-ticks every loaded mob over the
    // skipped span. Same path as the per-frame tick, just one coarse step.
    public void TickStatusEffects(float dt) => _statusEffects?.Tick(dt);

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

    // Memory-silhouette position pin. Once _silhouette ramps fully to black the
    // mob reads as a static "last seen here" marker, so we decouple the visual
    // subtree (the _visuals wrapper — model mesh AND HudAnchor as siblings —
    // falling back to _mesh) from the still-simulating body (TopLevel) and freeze
    // it at the world pose it had when it went black. Because HudAnchor lives
    // under that frozen wrapper, the on-screen HUD tracks the silhouette for free.
    // _meshPinnedLocal stores the normal body-relative local transform to restore
    // when the pin releases (LOS regained). See UpdateVisibility.
    private bool _meshPinned;
    private Transform3D _meshPinnedLocal;

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
            // Biome-variant recolor: one model, many palettes (null = untouched).
            // The descriptor's per-instance override wins; else the species default.
            _modelAnimator.ApplyPalette(_simState.Palette ?? mobData?.palette);
            // Footfalls fire from a Call Method Track authored on the model's
            // movement clips (OnFootstep) at the exact foot-contact frame.
            _modelAnimator.OnFootstep += EmitFootstep;
        }

        // Held weapon prop, instanced in-hand at spawn (the mob analog of the
        // player's HeldItemVisual.SetWeapon). The scene is latched even before
        // the hand sockets finish their deferred build. The prop is the held model
        // of the mob's primary weapon — so a spawn loadout that swaps the claw for
        // a burning torch (MobSimState.Weapons) shows the torch automatically.
        WeaponData primary = PrimaryHeldWeapon();
        if (_heldVisual != null && primary?.heldModel != null)
        {
            _heldVisual.SetWeapon(primary.heldModel, primary.wieldHand);
            // An elite signature with an idleFx (e.g. a Flaming elite's flame)
            // shows on the in-hand prop. The mob's per-weapon WeaponState isn't
            // built until its first attack, so read the captured elite mod
            // directly rather than through a controller.
            PackedScene eliteIdleFx = _eliteWeaponMod?.weaponMod?.idleFx;
            if (eliteIdleFx != null)
            {
                _heldVisual.SetWeaponIdleFx(new Godot.Collections.Array<PackedScene> { eliteIdleFx });
            }
        }
    }

    // The mob's weapon loadout, stamped onto MobSimState.Weapons at spawn from
    // MobDescriptor.weapons (weapons are spawn composition, not a species trait).
    // Read by BehaviorAttack and the held-prop pick. Null/empty = never attacks.
    public Godot.Collections.Array<WeaponData> Weapons => _simState?.Weapons;

    // The mob's weapon that supplies its in-hand prop: the highest-priority
    // weapon (WeaponData.priority) that defines a held model. Null when the mob
    // has no weapons or none carries a held model (an empty-handed / natural-
    // attack creature).
    private WeaponData PrimaryHeldWeapon()
    {
        Godot.Collections.Array<WeaponData> weapons = Weapons;
        if (weapons == null)
        {
            return null;
        }
        WeaponData best = null;
        for (int i = 0; i < weapons.Count; i++)
        {
            WeaponData w = weapons[i];
            if (w == null || w.heldModel == null)
            {
                continue;
            }
            if (best == null || w.priority > best.priority)
            {
                best = w;
            }
        }
        return best;
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
        // Elite mobs get the shared elite buff + crown. The elite's own signature
        // effect rides the descriptor StatusEffects list applied just below.
        // Applied here, after AddChild above, so the mob is already in the tree and
        // the effect's start/loop Fx parent and position correctly.
        if (_simState.Elite)
        {
            // Shared elite buff applied to every elite. It's categorized Permanent
            // (not Elite), so it never collides with a signature Elite-category
            // effect from the descriptor.
            StatusEffectData sharedElite = _world?.SimData?.EliteStatusEffect;
            if (sharedElite != null)
            {
                _statusEffects.Add(sharedElite);
            }
            SpawnEliteCrown();
        }

        // Per-instance status effects authored on the spawning MobDescriptor
        // (MobSimState.StatusEffects) — a buff/aura channel independent of the
        // elite signature, applied whether or not the mob is elite. Routed the
        // same way (weapon-mod onto weapons, else onto the body) and re-applied
        // every spawn since the status controller isn't serialized.
        if (_simState.StatusEffects != null)
        {
            foreach (StatusEffectData effect in _simState.StatusEffects)
            {
                ApplySpawnStatusEffect(effect);
            }
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
            // Store the drainable BASE max only — the MaxHealth/MaxArmor stat
            // modifiers fold in live through the maxHealth/maxArmor properties,
            // so a buff gained or lost later moves the cap. Fill current vitals
            // to the full modified cap at spawn.
            _simState.MaxHealth = mobData.maxHealth;
            _simState.Health = maxHealth;
            _simState.Armor = maxArmor;
        }
    }

    // Apply one spawn-time status effect, routed by kind: a weapon-mod effect
    // (e.g. Lightning) is composed onto the mob's natural weapons — the same
    // item-side path the player's modded weapons use, so its on-attack payload
    // fires through the weapon rather than as a body aura — while any other
    // effect is added to the mob's own status controller. Null is a no-op.
    private void ApplySpawnStatusEffect(StatusEffectData effect)
    {
        if (effect == null)
        {
            return;
        }
        if (effect.weaponMod != null)
        {
            // Stamped onto each weapon as GetWeapon creates it (no weapon states
            // exist yet at spawn — they're built on first attack).
            _eliteWeaponMod = effect;
        }
        else
        {
            _statusEffects.Add(effect);
        }
    }

    // Instantiate the spinning emissive halo over an elite mob. Parented under
    // the mesh container so it rides the body's transform, sitting just above
    // the head. Shares the mob's
    // render stack via crown_lit.tres, so it silhouettes / X-rays in lockstep —
    // the per-frame discovery push happens in _Process alongside the body's.
    // The elite's own descriptor (EliteMobDescriptor.crownScene) overrides the
    // shared SimData.EliteCrownScene when set, so a signature can carry its own
    // crown. No-op (and no marker) when neither authors a crown scene.
    private void SpawnEliteCrown()
    {
        PackedScene scene = _simState.EliteCrownScene ?? _world?.SimData?.EliteCrownScene;
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

    // Mob locomotion has no airborne distinction yet, so IsGrounded is a
    // stable default. IsSwimming reflects the real water state (_swimming,
    // set in UpdateWaterState) so ActorStateRequirement.forbidSwimming gates
    // mob attacks the same as the player's.
    public bool IsGrounded => true;
    public bool IsSwimming => _swimming;
    public bool HasDamagingStatusEffect => _statusEffects?.HasDamagingEffect ?? false;

    public float OutgoingDamageMultiplier => _statusEffects?.FoldStat(EStat.OutgoingDamage, 1f) ?? 1f;

    // IActionActor — fire any active status effect's on-attack-impact burst
    // (elite lightning aura, etc.) at the swing/ray impact point. Forwarded to
    // the shared controller so mobs and the player run identical logic.
    public void TriggerAttackImpact(Vector3 position) => _statusEffects?.TriggerAttackImpact(this, position);

    // Compose a single stat across inherent MobData modifiers, the species
    // variant's own modifiers, and active status-effect modifiers. Mobs don't
    // currently equip armor, so the shape is two source lists (the base MobData
    // and the SpeciesData variant) + the controller's contribution.
    public float ComposeStat(EStat stat)
    {
        float value = StatModifierUtil.NeutralValue(stat);
        if (mobData?.modifiers != null)
        {
            value = StatModifierUtil.Fold(stat, mobData.modifiers, value);
        }
        if (_simState?.Species?.modifiers != null)
        {
            value = StatModifierUtil.Fold(stat, _simState.Species.modifiers, value);
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
        if (_simState?.Species?.modifiers != null)
        {
            product = StatModifierUtil.FoldMask(mask, _simState.Species.modifiers, product);
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
        // restart: a re-fired one-shot (e.g. repeated attacks on the same clip)
        // must replay from the start rather than no-op on the in-flight clip.
        _animator.Play(name, restart: true);
    }

    // IInteractive — only mobs with authored _interactiveActions surface as
    // interactable. The InteractiveBox on the mob's .tscn drives detection;
    // CanInteract / CanActorInteract gate the press itself.
    public Vector3 hudPosition => HudAnchor != null ? HudAnchor.GlobalPosition : GlobalPosition;

    // A dead tamed companion surfaces the shared revive verb instead of its
    // live actions. Gated on SimData.ReviveAction so a null authoring slot
    // disables revival cleanly (CanInteract falls through to the live path,
    // which is already false for a corpse).
    private bool CanRevive => !alive && IsCompanion && _world?.SimData?.ReviveAction != null;

    public bool CanInteract()
    {
        if (CanRevive)
        {
            return true;
        }
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
    private Godot.Collections.Array<InteractiveAction> _reviveActions;

    // Resolves the action list for the mob's current state. A dead companion
    // returns the single shared revive verb; a live mob returns its authored
    // actions merged with any conversation verbs. GetActions and Complete both
    // route through here so the index Complete receives indexes the same list
    // GetActions surfaced to the player.
    private Godot.Collections.Array<InteractiveAction> ResolveActions()
    {
        if (CanRevive)
        {
            if (_reviveActions == null)
            {
                _reviveActions = new Godot.Collections.Array<InteractiveAction> { _world.SimData.ReviveAction };
            }
            return _reviveActions;
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
        return _resolvedActions ?? _interactiveActions;
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        if (!CanActorInteract(player))
        {
            return null;
        }
        Godot.Collections.Array<InteractiveAction> result = ResolveActions();
        if (result == null || result.Count == 0)
        {
            return null;
        }
        return result;
    }

    public void Complete(int actionIndex)
    {
        Godot.Collections.Array<InteractiveAction> actions = ResolveActions();
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
            case EActionVerb.Revive:
                PerformPlayerRevive();
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

    // Subjective worth (to this mob) of a single unit of `item`. Starts from
    // the item's authored value and folds this species' taste model over it
    // (MobData.itemPreferences, keyed on ItemData.typeTags) — so a dog values
    // only Meat and a villager has layered likes/dislikes without any per-mob
    // code. Items whose authored value is 0, or whose preference-folded value
    // drops to 0, are uninteresting to the mob — CalculatePersonalValue and
    // AcceptableUnits short-circuit them to zero. Still virtual so a derived
    // mob type can replace the taste model wholesale if it ever needs to.
    public virtual float PerUnitValue(ItemData item)
    {
        if (item == null)
        {
            return 0f;
        }
        MobData md = mobData;
        return md != null ? md.ApplyItemPreferences(item.value, item.typeTags) : item.value;
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
            float value = PerUnitValue(s.data);
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
            MaybeTame();
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

    // Flips this mob to tamed once its loyalty crosses MobData.tameLoyalty.
    // Call after any change to _simState.Loyalty. No-op for untameable mobs
    // (tameLoyalty <= 0) and for mobs already tamed.
    private void MaybeTame()
    {
        if (_simState == null || _simState.Tamed || mobData == null || mobData.tameLoyalty <= 0f)
        {
            return;
        }
        if (_simState.Loyalty >= mobData.tameLoyalty)
        {
            Tame();
        }
    }

    // Makes this mob the player's companion: effective team flips to Friendly
    // (Mob.ActorTeam reads _simState.Tamed) and it registers as World's command
    // target. Used by MaybeTame at runtime; the starter pet spawns with Tamed
    // already set and registers via OnSpawned instead.
    public void Tame()
    {
        if (_simState == null || _simState.Tamed)
        {
            return;
        }
        _simState.Tamed = true;
        _world?.RegisterCompanion(this);
        // Lift this mob out of chunk streaming into the persistent store so it
        // can't be destroyed when its spawn chunk evicts (it's now player-
        // attached state, not world content). Starter pets spawn pre-tamed
        // straight into that store, so they never hit this path.
        _world?.PromoteCompanionToPersistent(this);
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

        // Local baked air current at the bird, sampled once for both the
        // along-heading speed modulation and the carry blend below.
        Vector3 wind = _world?.WorldState?.GetWindVelocityWorld(
            Mathf.FloorToInt(pos.X), Mathf.FloorToInt(pos.Y), Mathf.FloorToInt(pos.Z)) ?? Vector3.Zero;
        Vector3 windXZ = new Vector3(wind.X, 0f, wind.Z);

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
                // Head/tailwind modulation: the wind component ALONG the flight
                // heading (dir · windXZ, m/s) scales cruise speed by windDragXZ
                // per m/s, clamped symmetrically to ±windFlySpeedCap. A tailwind
                // (positive dot) speeds the bird up, a headwind slows it down —
                // at the default cap a headwind floors it at 50% of flySpeed.
                float windAlong = dir.Dot(windXZ);
                float windSpeedFactor = Mathf.Clamp(
                    windAlong * md.windDragXZ, -md.windFlySpeedCap, md.windFlySpeedCap);
                float effectiveFlySpeed = md.flySpeed * (1f + windSpeedFactor);
                desiredHoriz = dir * effectiveFlySpeed * aiOutput.speed * speedScale * statusMoveMul;
                if (!targetYaw.HasValue && dist > arrivalDist)
                {
                    targetYaw = Mathf.Atan2(dir.X, dir.Z);
                }
            }
        }

        // Wind carry: blend the local air current into the desired horizontal
        // velocity so birds get carried / fight headwinds (windInfluence tunes
        // how strongly per species). Layered on top of the speed modulation
        // above — windInfluence drifts the bird sideways with the wind while
        // windDragXZ/windFlySpeedCap govern its along-heading propulsion.
        desiredHoriz += windXZ * md.windInfluence;

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

    // Relocate the body to a new world position in one shot: zero the velocity
    // so the physics engine doesn't carry momentum across the jump, move the
    // node, and write the new position straight into the persistent sim state.
    // Used by the companion chunk-unload rescue (World.RescueCompanion) to move
    // a pet that would otherwise be destroyed with its evicting chunk. Mirrors
    // the perch-claim teleport pattern (LinearVelocity zero + GlobalPosition set).
    //
    // fadeIn drops _visibility to 0 so the body dithers back in over
    // VisibilityFadeTime (the same pixelated reveal a discovered mob plays) —
    // used by the companion catch-up rescue so a pet that lands on-screen
    // resolves in rather than popping.
    public void Teleport(Vector3 worldPos, bool fadeIn = false)
    {
        LinearVelocity = Vector3.Zero;
        GlobalPosition = worldPos;
        if (_simState != null)
        {
            _simState.WorldPosition = Position;
        }
        if (fadeIn)
        {
            _visibility = 0f;
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
        // Mobs on the player's side (minions, tamed pets, friendly NPCs) are
        // never perception-gated — the player always knows where their own team
        // is, so they stay fully visible regardless of line of sight / memory.
        bool playerSide = Teams.AreAllied(ActorTeam, ETeam.Player);
        if (playerSide)
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
        // A corpse the player has actually laid eyes on (CorpseDiscovered) is
        // pinned fully visible — full color, never the black memory silhouette
        // and never dithered out. A motionless body, once seen, is something
        // the player keeps seeing; perception toward it only ever rose (see
        // Mob.UpdatePerception, which early-outs once this latch is set). Mirror
        // the player-side treatment: force-visible and always "within visible
        // time" so no silhouette ramps up.
        bool corpseSeen = _simState.DiscoveryState == EPlayerPerceptionState.CorpseDiscovered;
        if (corpseSeen)
        {
            discovered = true;
        }
        // Three-state visibility, driven off discovery:
        //   fully visible  → dithered to full, no silhouette
        //   silhouetted    → still dithered to full, silhouette ramps up
        //                    (player remembers it's there but can't see details)
        //   unknown / memory expired → dither back to 0
        // Step values move toward targets at 1 / VisibilityFadeTime per
        // second so both the pop-in and the transition to/from silhouette
        // are smooth rather than instant.
        // Player-side mobs read as fully lit, never the "remembered" black
        // silhouette, since they're always actively seen by their own side.
        bool withinVisibleTime = playerSide || corpseSeen || _world.GameTimeMs < _simState.VisibleTimeMs;
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
        // Status-effect loop fx parent to the mob root (a sibling of _mesh, not
        // under it), so hiding the mesh doesn't hide them. Gate them on the body
        // actually being seen as a live mesh — drawn AND within the line-of-sight
        // window — so a culled or remembered-silhouette mob carries no live flame.
        _statusEffects?.SetLoopFxVisible(meshVisibleTarget && withinVisibleTime);
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
        // Animate only while the mob is actually being drawn as a live body:
        // within the line-of-sight visible window, or mid-silhouette-fade (the
        // fade reads as motion, so keep skinning while _silhouette ramps —
        // freezing mid-ramp is what made an occluded mob slide as a static
        // pose). Both the fully-remembered (silhouette ramped to black, pinned
        // in place below) AND the never-seen case (silhouette stuck at 0, not
        // within visible time — the bulk of mobs at spawn) then freeze; keying
        // the freeze on fullyRemembered alone let every undiscovered offscreen
        // mob keep skinning.
        bool fullyRemembered = _silhouette >= 1f;
        bool fading = _silhouette > 0f && _silhouette < 1f;
        bool animProcessTarget = !CVars.mobAnimCull.Value
            || (!tooFarToPose && (withinVisibleTime || fading));
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
        // Position pin, paired with the pose freeze above: a fully-black, still-
        // alive mob is a "last seen here" memory marker. Decouple the visual
        // wrapper (_visuals — model mesh + HudAnchor — or _mesh on species without
        // one) from the body (TopLevel) and hold the world pose it had when it
        // went black, so the live body simulates on invisibly without dragging the
        // frozen silhouette (or its HUD) with it. Releasing (LOS regained, or
        // death) re-parents it, snapping back onto the live body's true position.
        Node3D pinTarget = _visuals != null ? _visuals : _mesh;
        bool shouldPin = fullyRemembered && alive;
        if (shouldPin && !_meshPinned)
        {
            _meshPinnedLocal = pinTarget.Transform;
            Transform3D pinnedWorld = pinTarget.GlobalTransform;
            pinTarget.TopLevel = true;
            pinTarget.GlobalTransform = pinnedWorld;
            _meshPinned = true;
        }
        else if (!shouldPin && _meshPinned)
        {
            pinTarget.TopLevel = false;
            pinTarget.Transform = _meshPinnedLocal;
            _meshPinned = false;
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
            // A +MaxHealth buff expiring (processed in the status tick above)
            // shrinks the live cap; clamp current health down so it can't sit
            // above max. Increases leave health alone — heals own the climb,
            // mirroring the armor clamp in TickArmor.
            if (health > maxHealth)
            {
                health = maxHealth;
            }
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

            ReportPlayerCombat(in aiOutput);

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

                    // The impulse above is horizontal only, so a voxel riser in
                    // the path would wedge the capsule against its face and stall
                    // the mob. Lift the body over climbable steps. Skipped while
                    // swimming (buoyancy owns vertical) and during knockback /
                    // motion darts (those force velocity later this tick and must
                    // win — a step shouldn't redirect a lunge or knockback arc).
                    if (!_swimming && _simState.KnockbackTime <= 0f && _simState.MotionTime <= 0f)
                    {
                        TryStepUp(dir);
                    }

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
            // Falling wind drift — parallels PlayerData.windDragXZ. While a
            // non-flying mob is in genuine free fall with nothing else driving
            // it (no flight / swim / path steering / knockback this tick, so the
            // default LinearDamp is still in force), wind carries its horizontal
            // velocity toward (sampled wind × windDragXZ). Applying an
            // acceleration of (LinearDamp × drift) makes that drift the
            // equilibrium of the engine's exponential damp, so the fall
            // asymptotes to the drift target exactly like the player's airborne
            // drift — no extra smoothing knob needed.
            if (!inBurrow && !flying && linearDampTarget > 0f
                && _simState.MobData.windDragXZ > 0f
                && LinearVelocity.Y < -_simState.MobData.fallEnterSpeed)
            {
                Vector3 pos = GlobalPosition;
                Vector3 wind = _world?.WorldState?.GetWindVelocityWorld(
                    Mathf.FloorToInt(pos.X), Mathf.FloorToInt(pos.Y), Mathf.FloorToInt(pos.Z)) ?? Vector3.Zero;
                Vector3 windDrift = new Vector3(wind.X, 0f, wind.Z) * _simState.MobData.windDragXZ;
                if (windDrift.LengthSquared() > 0f)
                {
                    ApplyImpulse(linearDampTarget * windDrift * Mass * (float)delta);
                }
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
            // sat still. Player-side allies are exempt: they render fully lit
            // regardless of line of sight (UpdateVisibility's withinVisibleTime
            // ORs in playerSide), so freezing their facing on the narrower
            // playerCanSee window would leave a companion visibly trotting one
            // way while still facing its old heading whenever it slips behind
            // the player. Mirror the rendering condition here so an always-shown
            // mob always turns to face its movement direction.
            bool facingShown = playerCanSee || Teams.AreAllied(ActorTeam, ETeam.Player);
            if (!inBurrow && targetYaw.HasValue && facingShown)
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

    // Step-up probe geometry. The body capsule's bottom sits at the mob's
    // origin (GlobalPosition.Y), so probe heights are measured from the feet.
    private const float StepFootProbeHeight = 0.15f;   // ankle ray — above the floor the mob already stands on
    private const float StepLookahead = 0.25f;         // probe reach beyond the capsule radius
    private const float StepClearanceMargin = 0.2f;    // headroom above the step top that must be open
    private const float StepFallGate = -1.0f;          // don't climb while descending faster than this (m/s)

    // Step-up assist for the RigidBody locomotion path (see the call site in
    // _PhysicsProcess). A grounded mob is shoved purely horizontally toward its
    // path target; this lets it clear an upward voxel riser the way the
    // CharacterBody3D player's explicit step-up does, without giving up the
    // RigidBody behaviours (crowd separation, knockback, corpse tumble, Freeze)
    // that a full conversion would lose. Two forward rays: a low one detecting
    // an obstacle directly ahead, and one at the step-top + margin confirming
    // the space above is clear — a step, not an unclimbable wall. When both
    // pass, drive the body upward at stepClimbSpeed; the rise self-terminates
    // once the foot probe clears the riser, dropping the mob onto the ledge.
    // Up-steps only — descending slopes are handled by gravity.
    private void TryStepUp(Vector3 dir)
    {
        MobData data = _simState.MobData;
        if (data == null || data.maxStepHeight <= 0 || data.stepClimbSpeed <= 0f)
        {
            return;
        }
        // A ledge drop or knockback arc shouldn't be mistaken for walking into
        // a step — only assist when not significantly descending.
        if (LinearVelocity.Y < StepFallGate)
        {
            return;
        }

        float radius = _collisionShape?.Shape is CapsuleShape3D cap ? cap.Radius : 0.4f;
        float reach = radius + StepLookahead;
        Vector3 feet = GlobalPosition;

        // Obstacle directly ahead at ankle height?
        Vector3 footFrom = feet + Vector3.Up * StepFootProbeHeight;
        if (!RaycastSolid(footFrom, footFrom + dir * reach))
        {
            return;
        }

        // Is the space above the step top open? If this also hits, the obstacle
        // is taller than one step — a wall, not a curb — so refuse the lift.
        float clearHeight = data.maxStepHeight + StepClearanceMargin;
        Vector3 headFrom = feet + Vector3.Up * clearHeight;
        if (RaycastSolid(headFrom, headFrom + dir * reach))
        {
            return;
        }

        // Climbable step: set vertical velocity directly (like ApplyKnockback)
        // so the rise dominates gravity this tick. The horizontal impulse
        // already applied carries the body forward onto the ledge as it clears.
        Vector3 v = LinearVelocity;
        LinearVelocity = new Vector3(v.X, data.stepClimbSpeed, v.Z);
    }

    // Diagnostic probe (companion_debug): reports what's directly ahead in
    // `dir` using the same two rays TryStepUp uses. obstacleAhead = something at
    // ankle height blocks forward motion; wallAbove = the space at
    // maxStepHeight+margin is ALSO blocked, i.e. a wall the step-up refuses
    // rather than a curb it would climb. So a stalled follower's log can tell
    // "pathed into a wall" (both true → nav routed it badly) from "step too
    // tall / step-up declined" (obstacleAhead only). dir need not be normalized.
    public void ProbeForwardObstacle(Vector3 dir, out bool obstacleAhead, out bool wallAbove, out float maxStep)
    {
        obstacleAhead = false;
        wallAbove = false;
        maxStep = _simState?.MobData?.maxStepHeight ?? 0f;
        dir.Y = 0f;
        if (dir.LengthSquared() < 0.0001f)
        {
            return;
        }
        dir = dir.Normalized();
        float radius = _collisionShape?.Shape is CapsuleShape3D cap ? cap.Radius : 0.4f;
        float reach = radius + StepLookahead;
        Vector3 feet = GlobalPosition;
        Vector3 footFrom = feet + Vector3.Up * StepFootProbeHeight;
        obstacleAhead = RaycastSolid(footFrom, footFrom + dir * reach);
        Vector3 headFrom = feet + Vector3.Up * (maxStep + StepClearanceMargin);
        wallAbove = RaycastSolid(headFrom, headFrom + dir * reach);
    }

    // Single forward ray against terrain/props only (Solid). Mobs live on the
    // Mob layer, so this never self-hits or catches a neighbour — the step
    // probe climbs geometry, not crowds.
    private bool RaycastSolid(Vector3 from, Vector3 to)
    {
        using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Solid);
        return GetWorld3D().DirectSpaceState.IntersectRay(query).Count > 0;
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
        // Otherwise armor (when present) absorbs the whole hit.
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
            if (!slot.triggered)
            {
                slot.triggeredTimeMs = _world.GameTimeMs;
            }
            slot.triggered = true;
            slot.lastKnownPosition = attacker.GlobalPosition;
        }
        // Struck by an enemy mob (e.g. the player's companion) — latch immediate
        // threat awareness toward it so a threat-scanning mob retaliates without
        // waiting for perception to accumulate, mirroring the player-slot edge
        // above. Gated to the team this mob actually scans for, so the latch
        // agrees with what AccumulateThreatPerception will track next tick;
        // no-op for mobs that don't scan threats. Aggro is credited in Damage.
        else if (hit.source is Mob mobAttacker
            && _simState.MobData != null
            && mobAttacker.ActorTeam == _simState.MobData.threatTeam)
        {
            ref PerceptionState slot = ref _simState.ThreatPerception;
            slot.target = mobAttacker;
            slot.perception = 1f;
            slot.triggered = true;
            slot.canSee = true;
            slot.lastKnownPosition = mobAttacker.GlobalPosition;
        }

        Damage(hit);

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
        if (hit.source is Player attackingPlayer)
        {
            _simState.DamagedByPlayer = true;
            // The player swinging at a mob counts as entering combat — releases a
            // guard companion to escalate from wary to attacking (Player.CombatEngaged).
            attackingPlayer.NotifyCombatEngaged();
        }
        // Receiver-side resistance fold. Modulates healthDamage / armorPenetration /
        // blunt / knockback in place so all downstream uses (hit.ArmorPenetrated,
        // armor chip mult, knockback impulse) see the resistance-scaled
        // values without needing per-site composition.
        ApplyResistance(ref hit);
        float incoming = hit.healthDamage;
        // Aggro: credit the attacker with (resisted) health damage * the hit's
        // aggroMultiplier so this mob's target selection favors whoever has hurt
        // it most. Uses the pre-armor figure — chipping armor is still aggression
        // — and is independent of awareness/perception (the two are separate
        // mechanics; see AggroTracker).
        if (hit.source is Node3D aggroSource && hit.aggroMultiplier > 0f && incoming > 0f)
        {
            AddAggro(aggroSource, incoming * hit.aggroMultiplier);
        }
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
        // frozen / knocked-down without touching this branch. A non-lethal
        // fat-buildup hit can wake AND re-apply on the same swing (the buildup
        // pass below still runs), which is consistent with the meter model.
        _statusEffects.ClearIncapacitating();

        // Buildup contributions — funnel into per-effect meters / immediate
        // applies and fold any crossed-threshold effect's applyTrigger (Dizzy
        // fires OnDizzy) back onto the hit before the hitstun/knockback reads
        // below so authored "extra knockback on the hit that landed dizzy"
        // overrides apply. Skipped on a lethal hit: a corpse shouldn't accrue
        // meters or catch fire/poison. Die() clears effects anyway, so this
        // mainly avoids the wasted apply-then-wipe — and keeps a killing blow
        // from firing an OnDizzy fold the dead mob can't use. Predicted from
        // `health - incoming` (post-armor), mirroring the health<=0 Die check
        // below, so survivors still get the full early fold.
        if (health - incoming > 0f)
        {
            _statusEffects.ApplyHitBuildups(ref hit);
        }

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

    // Mirrors Player.AddStatusEffect: run apply-time payloads (heal, cleanse), then
    // keep the lingering state unless the effect is instantaneous. A mob can roll a
    // fairy boon via the random pick, so Restore heals + cleanses it.
    public StatusEffectState AddStatusEffect(StatusEffectData data)
    {
        if (data == null)
        {
            return null;
        }
        if (data.instantHealPercent > 0f)
        {
            health = Mathf.Min(maxHealth, health + maxHealth * data.instantHealPercent);
        }
        if (data.instantaneous)
        {
            // One-shot blessing: no lingering state, but still honor its
            // removesOnApply cleanse (Add, which normally runs it, is skipped).
            _statusEffects.ApplyRemovesOnApply(data);
            return null;
        }
        return _statusEffects.Add(data);
    }

    public void RemoveStatusEffect(StatusEffectState state) => _statusEffects.Remove(state);

    public void RemoveStatusEffectsByTagMask(EStat mask) => _statusEffects.RemoveByTagMask(mask);

    // IActionActor — restore HP, clamped at maxHealth. Routes through the
    // status-health path so a vampiric mob heal shows its floating heal number
    // the same way a heal-over-time tick does. Heals skip armor (armorPenetration 1).
    public void Heal(float amount)
    {
        if (amount <= 0f || !alive)
        {
            return;
        }
        ApplyStatusHealthDelta(amount, 1f);
    }

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
        // Clamp against the live cap (base + active modifiers), not the raw base,
        // so a +MaxHealth buff keeps its headroom while the base withers. Death
        // still triggers when the drainable base is exhausted.
        float liveMax = maxHealth;
        if (_simState.Health > liveMax)
        {
            _simState.Health = liveMax;
        }
        if (_simState.MaxHealth <= 0f)
        {
            Die();
        }
    }

    private void TickArmor(float dt)
    {
        float max = maxArmor;
        // A shrinking cap (a +MaxArmor buff expiring) can leave current armor
        // above max; clamp down before the recharge early-out so it can't
        // strand there until the next hit. Increases leave armor alone — the
        // recharge below owns the climb to the new max, matching the player.
        if (armor > max)
        {
            armor = max;
        }
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
        GameClient.Current?.NotifyMobKilled(_simState.Species, _simState.DamagedByPlayer);
        // End combat immediately if this was the last perceived threat (vs the
        // run-away grace). Routed here with the live instance + time because
        // NotifyMobKilled only carries the species.
        GameClient.Current?.Combat?.OnMobDied(this, _world.GameTimeMs);
        // Hide the held weapon prop — a corpse shouldn't brandish its weapon — and
        // douse it if it's a lit torch so the corpse goes dark instead of burning on.
        _heldVisual?.ExtinguishWeaponTorch();
        _heldVisual?.SetWeaponConcealed(true);
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

    // Player health cost to revive this corpse, read by ReviveBloodRequirement
    // (press-time affordability gate) and PerformPlayerRevive (the spend). 0
    // when unauthored or no data.
    public float ReviveHealthCost => mobData?.reviveHealthCost ?? 0f;

    // Player-driven revive (the Revive interactive verb's completion). Spends
    // the player's blood by reviveHealthCost, then restores the corpse.
    // ReviveBloodRequirement already gated affordability at press, but the 3s
    // channel can change the player's health (a hostile striking mid-revive),
    // so re-check here — a now-unaffordable revive fizzles without draining or
    // reviving. RecallForSleep calls Revive() directly, bypassing the cost.
    private void PerformPlayerRevive()
    {
        float cost = ReviveHealthCost;
        if (cost > 0f)
        {
            Player player = GameClient.Current?.Player;
            if (player == null || !player.HasBlood(cost))
            {
                return;
            }
            player.DrainBlood(cost);
        }
        Revive();
    }

    // Bring a dead companion back to life — the resolution of the Revive
    // interactive verb (see Complete). Undoes the state changes Die() made:
    // restores the alive flag, the live collision layers (so the body moves
    // and is targetable again), AxisLockAngularY, and health. The revive
    // audiovisual cue is the InteractiveAction's completion-event fx; this
    // method only touches gameplay state. AI resumes on its own — every AI /
    // movement gate keys off `alive` per tick, so flipping it is enough.
    private void Revive()
    {
        if (alive)
        {
            return;
        }
        alive = true;
        // Mirror _Ready's live layer setup (and the auto-freeze live branch):
        // back onto Mob / the live mask, hurtbox back onto HurtBox so attacks
        // and the player's targeting see it as a live mob again.
        CollisionLayer = (uint)ECollisionLayer.Mob;
        CollisionMask = (uint)(ECollisionLayer.Solid | ECollisionLayer.Player | ECollisionLayer.Mob);
        if (_hurtBox != null)
        {
            _hurtBox.CollisionLayer = (uint)ECollisionLayer.HurtBox;
        }
        AxisLockAngularY = true;
        // Die() may have left the corpse pinned by the auto-freeze; unfreeze so
        // it stands and the per-tick freeze logic re-evaluates from a live state.
        Freeze = false;
        _simState.Health = Mathf.Clamp(mobData?.reviveHealth ?? maxHealth, 1f, maxHealth);
        // The corpse read as CorpseDiscovered; a revived companion is a live,
        // known mob again.
        _simState.DiscoveryState = EPlayerPerceptionState.Discovered;
        // Release the Die one-shot so UpdateAnimation resumes the live
        // idle/locomotion loop instead of holding the death pose.
        _oneShotAnim = null;
    }

    // Recall the companion to the player's side: revived if it died, healed to
    // full, and teleported in — regardless of where it wandered or fell. Used by
    // both the sleep time-skip and player respawn (free of the revive blood cost,
    // unlike the Revive interactive).
    public void RecallToPlayer(Vector3 worldPos)
    {
        if (!alive)
        {
            Revive();
        }
        Teleport(worldPos);
        health = maxHealth;
    }

    // Mirrors Chest.Complete's loot ejection: each ItemCount entry (stamped onto
    // the sim state from the spawning SpeciesData.loot) fires `count` Loot
    // instances outward on a 45° upward arc. Random horizontal angle per item so
    // a multi-drop carcass scatters rather than dropping in a tight stack.
    private void EjectLoot()
    {
        if (_world == null)
        {
            return;
        }
        MobData md = mobData;
        Godot.Collections.Array<ItemCount> loot = _simState?.Loot;
        bool hasSpeciesLoot = loot != null && loot.Count > 0;
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
            for (int i = 0; i < loot.Count; i++)
            {
                ItemCount entry = loot[i];
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
        if (!slot.triggered)
        {
            slot.triggeredTimeMs = _world.GameTimeMs;
        }
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
        _world.WorldState?.SimState?.DiscoverSpecies(_simState.Species);
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

    // Public hook for behaviors that need to emit a one-shot audio-visual cue at
    // the mob (e.g. BehaviorWary's periodic growl). World-parented so it stays
    // put as the mob keeps moving, matching the footstep / voice convention.
    public void PlayWorldEffect(PackedScene scene)
    {
        SpawnWorldEffect(scene);
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
        float modifier = mobData != null ? mobData.foliageSpeedModifier : 1f;
        foreach (Foliage foliage in _foliageCollisions)
        {
            // Scale the foliage slow by this mob's susceptibility: 1 = full
            // slow, 0 = unaffected (kunkuns, sparrows), intermediate = partial
            // (dogs at 0.5).
            float slowed = Mathf.Lerp(1f, foliage.speed, modifier);
            _terrainSpeed = Mathf.Min(_terrainSpeed, slowed);
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
