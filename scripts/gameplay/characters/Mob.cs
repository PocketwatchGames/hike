using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class Mob : RigidBody3D, IWorldEntity, IActionActor, IInteractive
{
    [Export] private CollisionShape3D _collisionShape;
    [Export] private LitSpriteAnimator _animator;
    [Export] private Node3D _mesh;
    [Export] private Sprite3D _sprite;
    [Export] private HurtBox _hurtBox;
    [Export] public Node3D HudAnchor;
    [Export] public PackedScene HudScene;
    // Authored interaction verbs surfaced when the player walks up to this
    // mob (Talk, Trade, etc.). First entry is the default action. Empty on
    // mobs that aren't interactable — the InteractiveBox on the mob's .tscn
    // shouldn't be wired in that case so the player never highlights them.
    [Export] private Godot.Collections.Array<InteractiveAction> _interactiveActions = new();
    // Per-ground-type one-shot effect played at the mob's feet while moving
    // on solid ground. Authored in each mob .tscn; missing keys silently
    // emit nothing.
    [Export] private Godot.Collections.Dictionary<EGroundType, PackedScene> _footstepEffects;
    // Per-mob footprint texture projected onto the ground at footstep
    // cadence. Shared mob footprint scene (with the Discoverable child)
    // and per-ground tints live on SimData.
    [Export] private Texture2D _footprintTexture;
    // One-shot blood spawned on a non-lethal hit. World-parented so the puff
    // stays where the hit landed even as the mob keeps moving.
    [Export] private PackedScene _bloodDamageEffect;
    // One-shot death blood. Per-mob in the .tscn so each species can pick the
    // appropriate small/medium/large variant from scenes/effects/.
    [Export] private PackedScene _deathEffect;
    // One-shot splash on the alive→in-water transition (voxel-detected).
    [Export] private PackedScene _waterEnterSplashEffect;
    // Continuous loop scenes (see Fx._loop). Parented to the mob
    // so they track the body; held alive while in the matching state and
    // Stop()'d when leaving.
    [Export] private PackedScene _waterMovementLoopEffect;
    [Export] private PackedScene _tallGrassMovementLoopEffect;
    // Fired the moment AIOutput.yell goes true — once per alert acquisition,
    // not per tick (the yell broadcast block below already runs once per
    // transition because nothing else flips _simState.Yelled back).
    [Export] private PackedScene _yellEffect;
    // Burrow lifecycle effects. Loop runs while the mob is mid-descent
    // (`burrowing` flag); complete fires on the burrowing→burrowed transition;
    // emerge fires when the mob leaves either burrow state and re-surfaces.
    [Export] private PackedScene _burrowLoopEffect;
    [Export] private PackedScene _burrowCompleteEffect;
    [Export] private PackedScene _burrowEmergeEffect;
    // Per-anim-state loops. Driven by the loopAnim picked in UpdateAnimation —
    // exactly one (or none) is active at a time, swapped on state change.
    // Authored per-species so each mob can have its own breathing / footstep
    // signature.
    [Export] private PackedScene _idleLoopEffect;
    [Export] private PackedScene _runLoopEffect;
    [Export] private PackedScene _swimIdleLoopEffect;
    // VO that plays on top of the shared blood/death scenes. Per-actor so
    // each species can carry its own voice without authoring per-actor blood
    // scenes. Either may be null — the asset library doesn't always include
    // a hurt VO for every species.
    [Export] private PackedScene _hurtVoEffect;
    [Export] private PackedScene _deathVoEffect;
    // Armor lifecycle one-shots. See Player for the lifecycle: depleted on
    // the hit that drains the bar to zero; rechargeStart when the post-hit
    // delay elapses; recoverStart when the recharge follows a full depletion.
    [Export] private PackedScene _armorDepletedEffect;
    [Export] private PackedScene _armorRechargeStartEffect;
    [Export] private PackedScene _armorRecoverStartEffect;
    // Distance the mob must travel in XZ between footstep effect emits.
    // Larger = slower step cadence.
    [Export] private float _footstepStride = 1.2f;
    // Minimum horizontal speed² to count as "walking" for footstep / loop
    // gating. Below this the mob is treated as standing still.
    [Export] private float _footstepMinSpeedSq = 0.25f;

    // Seconds to lerp visibility/silhouette toward their target. 0.1s is
    // short enough that transitions read as "now" rather than a slow fade
    // (this is an awareness cue, not a visual flourish) while still giving
    // the dither pattern a chance to resolve rather than popping.
    private const float VisibilityFadeTime = 0.3f;

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
    public EPlayerPerceptionState playerPerceptionState { get => _simState.DiscoveryState; set => _simState.DiscoveryState = value; }
    public MobData mobData => _simState.MobData;
    public StringName defaultBehavior => _simState?.InitialBehavior ?? (mobData != null ? mobData.defaultBehavior : (StringName)"Idle");
    public Vector3 weaponPosition => GlobalPosition;
    public Vector3 spawnPosition => _simState.SpawnPosition;
    public float spawnRotationY => _simState.SpawnRotationY;
    public InvestigateState? investigation { get => _simState.Investigation; set => _simState.Investigation = value; }
    public bool yelled { get => _simState.Yelled; set => _simState.Yelled = value; }
    public bool burrowed { get => _simState.Burrowed; set => _simState.Burrowed = value; }
    public bool burrowing { get => _simState.Burrowing; set => _simState.Burrowing = value; }
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

    readonly List<TallGrass> _tallGrassCollisions = new();
    float _terrainSpeed = 1f;
    readonly WaterRippleEmitter _rippleEmitter = new();
    readonly FootstepEmitter _footstepEmitter = new();
    // Spawns persistent ground decals at the same cadence as the FX emitter.
    // Independent stride memory so the prints don't get desynced from the
    // FX puffs across walking → idle → walking transitions.
    readonly FootprintEmitter _footprintEmitter = new();
    // Active loop instances. See Player for the lifecycle pattern — null
    // when the matching state isn't held; created on activation, Stop()'d
    // and dropped on deactivation.
    Fx _waterMovementLoop;
    Fx _tallGrassMovementLoop;
    Fx _burrowLoop;
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
    // Captured in _Ready so burrow can drop the mesh and restore it. The drop
    // is sized from the collision capsule so kun_kun (short) and goblin (tall)
    // both end up about 3/4 of their body underground.
    private Vector3 _meshRestPosition;
    private float _meshBurrowDrop;
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
    // the first frame through so initial state is always pushed. Note:
    // LitSprite's Visibility / Silhouette / CastsShadow setters are equality-
    // checked inside the property itself, so Mob doesn't track them here.
    private bool _lastAlive;
    private bool _lastAliveInit;
    private bool _lastMeshVisible;
    private bool _lastMeshVisibleInit;
    private bool _lastHudVisible;
    private bool _lastHudVisibleInit;
    private float _lastBurrowT = float.NaN;
    private float _lastLinearDamp = float.NaN;
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
    // Same instantiate / QueueFree pattern as Player.SetMovingLightActive —
    // the FX scenes (LightOn / LightOff / Loop) live on the carrier scene
    // itself, so the mob just owns presence/lifetime, not styling.
    private MovingLight _torch;

    // Latched one-shot animation. Same model as Player: PlayOneShot pins the
    // animator on a non-looping clip; UpdateAnimation defers the loop pick
    // until LitSpriteAnimator.Finished flips. Behaviors emit via
    // AIOutput.oneShotAnim; combat events route here through PlayAnim.
    private StringName _oneShotAnim;
    // Game-time at which the mob first started falling fast (vel.Y below the
    // FallEnterSpeed threshold). Cleared as soon as the body is no longer
    // descending. Used to gate the "fall" loop behind a sustained-fall grace
    // window — short pops off geometry while running don't earn the anim.
    private ulong _airborneStartMs;

    public static Mob Create(World world, MobSimState data)
    {
        var instance = data.Scene.Instantiate<Mob>();
        instance.Initialize(world, data);
        return instance;
    }

    public override void _Ready()
    {
        CollisionLayer = (uint)ECollisionLayer.Mob;
        CollisionMask = (uint)(ECollisionLayer.Environment | ECollisionLayer.Player);
        AxisLockAngularY = true;

        if (_hurtBox != null)
        {
            _hurtBox.OnHit = Hit;
            _hurtBox.GetHitType = GetHitType;
        }

        if (_mesh != null)
        {
            _meshRestPosition = _mesh.Position;
        }
        float capsuleHeight = 1.5f;
        if (_collisionShape?.Shape is CapsuleShape3D capsule)
        {
            capsuleHeight = capsule.Height;
        }
        _meshBurrowDrop = capsuleHeight * 0.75f;
    }

    public void OnSpawned(World world)
    {
        world.MobSpatialHash.Add(this);
        TreeExiting += () =>
        {
            SyncToSimState();
            world.MobSpatialHash.Remove(this);
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
        _statusEffects = new StatusEffectController(this, world, ApplyStatusHealthDelta);
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
    public Node3D AttackerNode => this;
    public void PlayAnim(EAnimation anim)
    {
        PlayOneShot(anim);
    }

    public void PlayOneShot(EAnimation anim) => PlayOneShot(AnimationNames.Get(anim));

    public void PlayOneShot(StringName name)
    {
        if (_animator == null || name == default || !_animator.HasAnimation(name))
        {
            return;
        }
        _oneShotAnim = name;
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
        return _interactiveActions != null && _interactiveActions.Count > 0;
    }

    public bool CanActorInteract(Player player) => CanInteract();

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        if (!CanActorInteract(player))
        {
            return null;
        }
        return _interactiveActions != null && _interactiveActions.Count > 0 ? _interactiveActions : null;
    }

    public void Complete(int actionIndex)
    {
        if (_interactiveActions == null || actionIndex < 0 || actionIndex >= _interactiveActions.Count)
        {
            return;
        }
        InteractiveAction action = _interactiveActions[actionIndex];
        if (action == null)
        {
            return;
        }
        switch (action.verb)
        {
            case EActionVerb.Talk:
                SpeakChatter();
                break;
        }
    }

    private void SpeakChatter()
    {
        MobData md = mobData;
        if (md == null || md.chatterLocKey == default || md.chatterLocKey == "")
        {
            return;
        }
        string line = Loc.Get(md.chatterLocKey);
        ulong durationMs = (ulong)Mathf.Max(0f, md.chatterDurationSeconds * 1000f);
        GameClient.Current?.onMobChatter?.Invoke(this, line, durationMs);
    }

    private void UpdateAnimation()
    {
        if (_animator == null)
        {
            return;
        }
        if (_oneShotAnim != default)
        {
            if (_animator.CurrentAnimation == _oneShotAnim && !_animator.Finished)
            {
                return;
            }
            _oneShotAnim = default;
        }

        StringName loopAnim;
        if (!alive)
        {
            loopAnim = AnimationNames.Dead;
        }
        else
        {
            Vector3 vel = LinearVelocity;
            Vector3 horizVel = new(vel.X, 0f, vel.Z);
            float horizSpeedSq = horizVel.LengthSquared();
            // Mob "intent to move" — navigator has an active goal/path. Lets
            // a mob jammed against a wall while pursuing keep playing the run
            // anim instead of snapping to idle when LinearVelocity zeros out.
            bool intentMoving = _navigator != null && _navigator.CurrentState != MobNavigator.State.Idle;

            // Sustained-fall tracking. Without the grace window, hopping over
            // small ledges or being shoved by another mob flickers the fall
            // anim for a frame.
            ulong now = _world?.GameTimeMs ?? 0;
            bool fallingFast = vel.Y < -FallEnterSpeed;
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
            bool fallReady = fallingFast && _airborneStartMs != 0 && now - _airborneStartMs >= FallGraceMs;

            if (IsInWater())
            {
                loopAnim = PickMoveLoop(horizSpeedSq, intentMoving, AnimationNames.Swim, AnimationNames.SwimIdle);
            }
            else if (fallReady)
            {
                loopAnim = AnimationNames.Fall;
            }
            else
            {
                loopAnim = PickMoveLoop(horizSpeedSq, intentMoving, AnimationNames.Run, AnimationNames.Idle);
            }
        }
        if (_animator.HasAnimation(loopAnim))
        {
            _animator.Play(loopAnim);
        }

        // Drive the anim-audio loop off the same loopAnim. Burrowing mobs
        // are mid-dig and shouldn't simultaneously hum the surface idle, so
        // the burrow flags suppress the anim-loop entirely until they
        // resurface.
        PackedScene animLoopTarget = null;
        if (alive && !burrowing && !burrowed)
        {
            if (loopAnim == AnimationNames.Idle) animLoopTarget = _idleLoopEffect;
            else if (loopAnim == AnimationNames.Run) animLoopTarget = _runLoopEffect;
            else if (loopAnim == AnimationNames.SwimIdle) animLoopTarget = _swimIdleLoopEffect;
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

    const float FallEnterSpeed = 1f;
    const ulong FallGraceMs = 400;

    // Hysteresis on the move-vs-idle pick — see Player.PickMoveLoop. Mob
    // navigators apply impulses every tick, so the body sits near the
    // friction floor a lot; without the dead band, idle/run flicker
    // every other frame.
    const float MoveLoopEnterSpeedSq = 0.01f;     // 0.1 m/s
    const float MoveLoopExitSpeedSq = 0.0001f;    // 0.01 m/s
    private StringName PickMoveLoop(float speedSq, bool intentMoving, StringName moveAnim, StringName idleAnim)
    {
        if (intentMoving || speedSq > MoveLoopEnterSpeedSq)
        {
            return moveAnim;
        }
        if (speedSq < MoveLoopExitSpeedSq)
        {
            return idleAnim;
        }
        StringName current = _animator.CurrentAnimation;
        if (current == moveAnim || current == idleAnim)
        {
            return current;
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
        if (_mesh == null)
        {
            return;
        }

        // Compute target state.
        bool aliveState = alive;
        bool discovered = _simState.DiscoveryState == EPlayerPerceptionState.Discovered && _simState.MemoryTimeMs > _world.GameTimeMs;
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

        // Burrow visual: fully burrowed mobs sit 3/4 underground, and
        // burrowing mobs lerp smoothly from rest to that depth over the
        // mob's burrowTime window. Dropping the mesh in local space (not
        // the rigid body) keeps physics and collision put — the mob is
        // still standing, just hiding.
        float burrowT = 0f;
        if (burrowed)
        {
            burrowT = 1f;
        }
        else if (burrowing)
        {
            float totalMs = _simState.MobData.burrowTime * 1000f;
            if (totalMs > 0f)
            {
                ulong now = _world.GameTimeMs;
                float remaining = burrowTimeMs > now ? burrowTimeMs - now : 0f;
                burrowT = Mathf.Clamp(1f - remaining / totalMs, 0f, 1f);
            }
            else
            {
                burrowT = 1f;
            }
        }

        // Push to Godot only when something actually changed. Each setter is
        // a managed→native marshal that pushes uniform updates / dirties the
        // transform, so skipping them on stable frames is the bulk of the
        // win. The cached "_last*Init" pairs force the first frame through
        // regardless of value so initial state is always applied.
        if (!_lastAliveInit || aliveState != _lastAlive)
        {
            _mesh.Scale = aliveState ? new Vector3(1f, 1f, 1f) : new Vector3(1f, 0.25f, 1f);
            _lastAlive = aliveState;
            _lastAliveInit = true;
        }
        if (!_lastMeshVisibleInit || meshVisibleTarget != _lastMeshVisible)
        {
            _mesh.Visible = meshVisibleTarget;
            _lastMeshVisible = meshVisibleTarget;
            _lastMeshVisibleInit = true;
        }
        if (HudAnchor != null && (!_lastHudVisibleInit || hudVisibleTarget != _lastHudVisible))
        {
            HudAnchor.Visible = hudVisibleTarget;
            _lastHudVisible = hudVisibleTarget;
            _lastHudVisibleInit = true;
        }
        if (_sprite is LitSprite litSprite)
        {
            // LitSprite's Visibility / Silhouette / CastsShadow setters all
            // short-circuit on equal value internally, so we can write them
            // unconditionally and not pay the shader-uniform push for stable
            // frames. mob_shadows is a profiling bisection toggle baked into
            // castsShadowTarget upstream.
            litSprite.Visibility = _visibility;
            litSprite.Silhouette = _silhouette;
            litSprite.CastsShadow = castsShadowTarget;
        }

        if (burrowT != _lastBurrowT)
        {
            Vector3 meshPos = _meshRestPosition;
            meshPos.Y -= _meshBurrowDrop * burrowT;
            _mesh.Position = meshPos;
            _lastBurrowT = burrowT;
        }
    }

    override public void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);
        using var _profPhys = Profiler.Sample("Mob.PhysicsProcess");

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
                CollisionMask = (uint)(ECollisionLayer.Environment | ECollisionLayer.Player);
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

        if (alive)
        {
            TickArmor((float)delta);
            _statusEffects.Tick((float)delta);
            TickAI((float)delta, out AIOutput aiOutput);

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

            // An explicit aiOutput.yaw always wins so behaviors like BehaviorAttack
            // can keep facing the player while circling to a reposition point.
            // Otherwise, if we're walking toward a path target, face that direction.
            float? targetYaw = aiOutput.yaw;

            _statusEffects.GetMovementMultipliers(out float statusMoveMul, out float statusAnimMul);
            if (_animator != null)
            {
                _animator.effectSpeedMultiplier = statusAnimMul;
            }

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
            // hitting the setter.
            float linearDampTarget = 8f;
            if (!inBurrow && aiOutput.pathTarget.HasValue)
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
                    Vector3 desiredVelocity = dir * _simState.MobData.maxSpeed * aiOutput.speed * _terrainSpeed * speedScale * statusMoveMul;
                    Vector3 currentVel = LinearVelocity;
                    Vector3 velocityChange = desiredVelocity - new Vector3(currentVel.X, 0f, currentVel.Z);
                    ApplyImpulse(new Vector3(velocityChange.X, 0f, velocityChange.Z) * Mass);

                    if (!targetYaw.HasValue)
                    {
                        targetYaw = Mathf.Atan2(dir.X, dir.Z);
                    }
                }
            }
            if (linearDampTarget != _lastLinearDamp)
            {
                LinearDamp = linearDampTarget;
                _lastLinearDamp = linearDampTarget;
            }

            if (!inBurrow && targetYaw.HasValue)
            {
                Vector3 currentRot = Rotation;
                float yawDelta = Mathf.Wrap(targetYaw.Value - currentRot.Y, -Mathf.Pi, Mathf.Pi);
                const float MaxTurnSpeed = 6f;
                float maxStep = MaxTurnSpeed * (float)delta;
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
                SpawnWorldEffect(_yellEffect);
                _simState.PlayerPerception = 1;
                _simState.DiscoveryState = EPlayerPerceptionState.Discovered;
                _simState.MemoryTimeMs = _world.GameTimeMs + (ulong)(_simState.MobData.MemoryStationaryTime * 1000);
                float yellVolumeSq = _simState.MobData.yellVolume * _simState.MobData.yellVolume;
                using (Profiler.Sample("Mob.YellBroadcast"))
                {
                    foreach (Mob mob in _world.GetEntities<Mob>())
                    {
                        if (mob == this)
                        {
                            continue;
                        }
                        if (GlobalPosition.DistanceSquaredTo(mob.GlobalPosition) < yellVolumeSq)
                        {
                            mob.Investigate(aiOutput.targetPos, 8, 30000, 3000);
                        }
                    }
                }
                _simState.Yelled = true;
            }
            // Two-phase burrow. When aiOutput.burrow first goes true we start
            // a Burrowing descent and arm burrowTime. Once the timer elapses
            // we flip to fully Burrowed. As soon as the behavior stops
            // requesting burrow we clear both flags and the mesh pops back up.
            if (aiOutput.burrow)
            {
                if (!burrowing && !burrowed)
                {
                    burrowing = true;
                    burrowTimeMs = _world.GameTimeMs + (ulong)(_simState.MobData.burrowTime * 1000f);
                    SetBurrowed(true);
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
            UpdateLoopEffect(ref _burrowLoop, _burrowLoopEffect, burrowing);
            if (burrowed && !_prevBurrowed)
            {
                SpawnWorldEffect(_burrowCompleteEffect);
            }
            if (!burrowing && !burrowed && (_prevBurrowing || _prevBurrowed))
            {
                SpawnWorldEffect(_burrowEmergeEffect);
            }
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
                    }
                }
                else if (_torch != null)
                {
                    _torch.Deactivate();
                    _torch.QueueFree();
                    _torch = null;
                }
            }
        }
        else
        {
            AngularDamp = 0.25f;
            LinearDamp = 0.25f;
        }

        if (!Freeze
        && !_impulseApplied
        && alive
        && LinearVelocity.LengthSquared() < 0.01f
        && _simState.SuspendAITimeMs > _world.GameTimeMs)
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
        float incoming = hit.healthDamage;
        if (incoming <= 0f)
        {
            return EHitResult.None;
        }
        if (armor > 0f)
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

        // External-interrupt damage during an in-flight attack — interrupt
        // before applying damage so abortEvents fire on coherent pre-damage
        // state. Gated by the action's profile.interruptOnDamage and the
        // tier's canInterrupt; non-interruptible swings keep going.
        _runner?.TryInterrupt();

        Damage(hit);

        if (hit.statusEffects != null)
        {
            for (int i = 0; i < hit.statusEffects.Count; i++)
            {
                AddStatusEffect(hit.statusEffects[i]);
            }
        }
    }

    public void Damage(HitInfo hit)
    {
        float incoming = hit.healthDamage;
        // Armor absorbs the full hit when present — even an overflow drop to
        // zero leaves health untouched. The recharge timer is rearmed on every
        // absorbing hit; a hit that takes armor to zero arms the longer
        // recover window via ArmorDepleted.
        if (armor > 0f && incoming > 0f)
        {
            armor -= incoming;
            ulong now = _world?.GameTimeMs ?? 0;
            MobData md = mobData;
            if (armor <= 0f)
            {
                armor = 0f;
                _simState.ArmorDepleted = true;
                _simState.ArmorRechargeStartMs = now + (ulong)((md?.armorRecoverTime ?? 0f) * 1000f);
                SpawnWorldEffect(_armorDepletedEffect);
            }
            else
            {
                _simState.ArmorDepleted = false;
                _simState.ArmorRechargeStartMs = now + (ulong)((md?.armorRechargeDelay ?? 0f) * 1000f);
            }
            _simState.ArmorRecharging = false;
            incoming = 0f;
        }

        health -= incoming;
        if (health <= 0f)
        {
            Die();
        }
        else if (incoming > 0f)
        {
            SpawnWorldEffect(_bloodDamageEffect);
            SpawnWorldEffect(_hurtVoEffect);
        }
    }

    public StatusEffectState AddStatusEffect(StatusEffectData data) => _statusEffects.Add(data);

    public void RemoveStatusEffect(StatusEffectState state) => _statusEffects.Remove(state);

    // Signed HP delta from a status-effect tick. Bypasses armor (per-second
    // poison ticks aren't supposed to be soaked) and skips Damage()'s blood /
    // hurt-VO oneshots — those would spam every tick.
    private void ApplyStatusHealthDelta(float delta)
    {
        if (delta == 0f || !alive)
        {
            return;
        }
        if (delta > 0f)
        {
            health = Mathf.Min(maxHealth, health + delta);
        }
        else
        {
            health = Mathf.Max(0f, health + delta);
            if (health <= 0f)
            {
                Die();
            }
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
            SpawnWorldEffect(_simState.ArmorDepleted ? _armorRecoverStartEffect : _armorRechargeStartEffect);
        }
        MobData md = mobData;
        float speed = md?.armorRechargeSpeed ?? 0f;
        armor = Mathf.Min(max, armor + speed * dt);
        if (armor >= max)
        {
            _simState.ArmorDepleted = false;
        }
    }

    private void Die()
    {
        if (!alive)
        {
            return;
        }

        alive = false;
        // The per-frame torch gating in _PhysicsProcess only runs while alive,
        // so a dead mob's lit torch would otherwise leak its light deposit
        // and loop FX. Deactivate + free explicitly — Deactivate fires the
        // LightOff fx authored on the MovingLight as the natural "torch
        // goes out" cue, then QueueFree drops the node.
        if (_torch != null)
        {
            _torch.Deactivate();
            _torch.QueueFree();
            _torch = null;
        }
        SpawnWorldEffect(_deathEffect);
        SpawnWorldEffect(_deathVoEffect);
        AxisLockAngularY = false;
        if (Freeze)
        {
            Freeze = false;
        }
        PlayOneShot(EAnimation.Die);
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
            CollisionMask = (uint)ECollisionLayer.Environment;
        }
        else
        {
            // Restore default mob collision. Freeze is left as-is — the
            // next impulse from a behavior unfreezes naturally via
            // ApplyImpulse, and the auto-freeze block at the bottom of
            // _PhysicsProcess keeps it pinned if the mob has no movement
            // intent. Explicitly unfreezing here would race with that.
            CollisionLayer = (uint)ECollisionLayer.Mob;
            CollisionMask = (uint)(ECollisionLayer.Environment | ECollisionLayer.Player);
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

    private void UpdateTerrainSpeed()
    {
        _terrainSpeed = 1f;
        foreach (TallGrass grass in _tallGrassCollisions)
        {
            _terrainSpeed = Mathf.Min(_terrainSpeed, grass.speed);
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

    // Spawn a footstep one-shot at the mob's feet at a fixed stride. Skipped
    // when standing in water — UpdateWaterRipples already covers wading. The
    // emitter does its own stride gating, so a stationary mob won't emit.
    // Also drives the water-enter splash + the water/tall-grass movement
    // loops, which all key off the same voxel-at-feet sample so we only do
    // one lookup per tick.
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
        bool walking = !inWater && horizSpeedSq > _footstepMinSpeedSq;
        EGroundType ground = GroundTypeResolver.Resolve(ws, pos);
        // mob_footstep_fx is a bisection toggle — when off, the per-stride
        // footstep one-shots are suppressed but the rest of UpdateFootsteps
        // (water-enter splash, water/tall-grass loop gating) still runs so
        // we can isolate the footstep emit cost specifically.
        bool footstepFxEnabled = CVars.mobFootstepFx.Value;
        // Player-perception gate: a mob the player has no awareness of doesn't
        // emit audible footsteps. Once perception is non-zero, footsteps emit
        // unconditionally — even when the mob is not yet Discovered or is
        // out of sight — so the player can hear an unseen mob approaching
        // and use that as a perception cue.
        bool perceived = _simState.PlayerPerception > 0f;
        _footstepEmitter.Update(_world, pos, walking && footstepFxEnabled && perceived, _footstepStride, ground, _footstepEffects);
        // Footprint decals. Gated on the same `walking` predicate as the FX
        // emitter (no prints in water — splash covers the disturbance) but
        // not on mob_footstep_fx, since that CVar is for bisecting FX cost
        // and the footprint cost belongs in a separate measurement.
        _statusEffects.GetFootprintMultipliers(out float fpAlphaMul, out float fpDurMul);
        // Per-print awareness gate. If the player has any current perception
        // of this mob, or still holds an active memory window on it, the
        // print is laid as the ungated player-style decal — visible the
        // moment it touches ground (you saw / are sensing the mob walk).
        // If awareness is fully cold, the print uses the Discoverable-gated
        // mob scene so the player has to come back and notice the decal
        // itself for it to fade in. Decision is made at emit time and
        // baked into the spawned print; later changes in awareness don't
        // retroactively reveal already-laid prints.
        bool perceivedAtEmit = _simState.PlayerPerception > 0f || _simState.MemoryTimeMs > _world.GameTimeMs;
        _footprintEmitter.Update(_world, pos, GlobalRotation.Y, walking, _footstepStride, ground, _footprintTexture, fpAlphaMul, fpDurMul, gated: !perceivedAtEmit);

        // One splash at the moment the mob first dips into water. The
        // navigator can drag a mob through a water voxel on a single frame,
        // so the alive guard prevents a corpse from splashing every tick if
        // it's later kicked into water.
        if (inWater && !_wasInWaterPrev && alive)
        {
            SpawnWorldEffect(_waterEnterSplashEffect);
        }
        _wasInWaterPrev = inWater;

        // Movement-gated loops. Navigator intent counts as "moving" even
        // when LinearVelocity hasn't built up yet — same reason Player keys
        // off _inputMove. Tall-grass and water are mutually exclusive: if
        // the mob's feet are wet, the water loop wins.
        bool intentMoving = _navigator != null && _navigator.CurrentState != MobNavigator.State.Idle;
        bool moving = alive && (intentMoving || horizSpeedSq > _footstepMinSpeedSq);
        bool waterLoopActive = moving && inWater;
        bool tallGrassLoopActive = moving && !inWater && _tallGrassCollisions.Count > 0;
        UpdateLoopEffect(ref _waterMovementLoop, _waterMovementLoopEffect, waterLoopActive);
        UpdateLoopEffect(ref _tallGrassMovementLoop, _tallGrassMovementLoopEffect, tallGrassLoopActive);
    }

    public void AddTerrainModifier(TallGrass tallGrass)
    {
        _tallGrassCollisions.Add(tallGrass);
    }

    public void RemoveTerrainModifier(TallGrass tallGrass)
    {
        _tallGrassCollisions.Remove(tallGrass);
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
