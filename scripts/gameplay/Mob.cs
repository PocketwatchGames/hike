using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class Mob : RigidBody3D, IWorldEntity, IActionActor
{
    [Export] private CollisionShape3D _collisionShape;
    [Export] private AnimationPlayer _animationPlayer;
    [Export] private LitSpriteAnimator _animator;
    [Export] private Node3D _mesh;
    [Export] private Sprite3D _sprite;
    [Export] private HurtBox _hurtBox;
    [Export] public Node3D HudAnchor;
    [Export] public PackedScene HudScene;
    // Per-ground-type one-shot effect played at the mob's feet while moving
    // on solid ground. Authored in each mob .tscn; missing keys silently
    // emit nothing.
    [Export] private Godot.Collections.Dictionary<EGroundType, PackedScene> _footstepEffects;
    // One-shot blood spawned on a non-lethal hit. World-parented so the puff
    // stays where the hit landed even as the mob keeps moving.
    [Export] private PackedScene _bloodDamageEffect;
    // One-shot death blood. Per-mob in the .tscn so each species can pick the
    // appropriate small/medium/large variant from scenes/effects/.
    [Export] private PackedScene _deathEffect;
    // One-shot splash on the alive→in-water transition (voxel-detected).
    [Export] private PackedScene _waterEnterSplashEffect;
    // Continuous loop scenes (see EffectOneShot._loop). Parented to the mob
    // so they track the body; held alive while in the matching state and
    // Stop()'d when leaving.
    [Export] private PackedScene _waterMovementLoopEffect;
    [Export] private PackedScene _tallGrassMovementLoopEffect;
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
    private const float VisibilityFadeTime = 0.1f;

    public float perceptionProgress
    {
        get
        {
            float threshold = _world?.player?.data?.detectedThreshold ?? 0.1f;
            return Mathf.Clamp((_simState.PlayerPerception - threshold) / (1f - threshold), 0f, 1f);
        }
    }

    // Canonical sim state lives in MobSimState. The Mob node is a view; these
    // properties forward through so call sites and animations stay terse.
    public bool alive { get => _simState.Alive; set => _simState.Alive = value; }
    public float maxHealth => _simState.MaxHealth;
    public float health { get => _simState.Health; set => _simState.Health = value; }
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

    readonly List<TallGrass> _tallGrassCollisions = new();
    float _terrainSpeed = 1f;
    readonly WaterRippleEmitter _rippleEmitter = new();
    readonly FootstepEmitter _footstepEmitter = new();
    // Active loop instances. See Player for the lifecycle pattern — null
    // when the matching state isn't held; created on activation, Stop()'d
    // and dropped on deactivation.
    EffectOneShot _waterMovementLoop;
    EffectOneShot _tallGrassMovementLoop;
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

    // Active torch carrier light, present whenever the latest AIOutput
    // requested useTorch. Lives as a child of the mob so its GlobalPosition
    // follows the mob each frame; CarrierLight handles the per-tick
    // recompute / blend itself.
    private CarrierLight _torch;

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
        InitBehaviors();
        world.AddChild(this);
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

        UpdateWaterRipples();
        UpdateFootsteps();

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

            // Draw the navigator's current path when the debug CVar is on.
            // Single-frame lifetime — this is called every physics tick so
            // the path stays visible without accumulating stale segments.
            // Visualization: yellow from the mob to its next waypoint, then
            // green for upcoming waypoints, with a red sphere at the goal.
            if (CVars.debugMobPath.Value && _navigator != null)
            {
                DrawPathDebug();
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

            // Decide LinearDamp target without writing yet — we want one
            // gated assignment at the end rather than three branches each
            // hitting the setter.
            float linearDampTarget = 8f;
            if (aiOutput.pathTarget.HasValue)
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
                    Vector3 desiredVelocity = dir * _simState.MobData.maxSpeed * aiOutput.speed * _terrainSpeed * speedScale;
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

            if (targetYaw.HasValue)
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

                if (CVars.debugMobYaw.Value)
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
                }
                else if (burrowing && _world.GameTimeMs >= burrowTimeMs)
                {
                    burrowing = false;
                    burrowed = true;
                }
            }
            else
            {
                burrowing = false;
                burrowed = false;
            }

            // Torch visibility is gated on the player's memory of this mob —
            // once memory expires, the mob has left the player's awareness
            // sphere and there's no point keeping its light deposit alive
            // (the mob is also not visible, so the torch would have nothing
            // to illuminate from the player's perspective). Same condition
            // as the mesh-visibility gate in _Process.
            bool playerRemembers = _simState.DiscoveryState == EPlayerPerceptionState.Discovered
                && _simState.MemoryTimeMs > _world.GameTimeMs;
            if (aiOutput.useTorch && playerRemembers)
            {
                if (_torch == null && _simState.MobData?.torch != null)
                {
                    _torch = _simState.MobData.torch.Instantiate<CarrierLight>();
                    AddChild(_torch);
                }
            }
            else if (_torch != null)
            {
                _torch.QueueFree();
                _torch = null;
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

        UpdateAnimation();
    }

    public void ApplyImpulse(Vector3 impulse)
    {
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

    public void Hit(DamageData data, Node damageSource)
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

        Damage(data);
    }

    public void Damage(DamageData data)
    {
        if (data == null)
        {
            return;
        }

        health -= data.healthDamage;
        if (health <= 0f)
        {
            Die();
        }
        else
        {
            SpawnWorldEffect(_bloodDamageEffect);
        }
    }

    private void Die()
    {
        if (!alive)
        {
            return;
        }

        alive = false;
        SpawnWorldEffect(_deathEffect);
        AxisLockAngularY = false;
        if (Freeze)
        {
            Freeze = false;
        }
        PlayOneShot(EAnimation.Die);
    }

    // World-parented one-shot at the mob's feet — matches the footstep /
    // ripple convention so the puff stays put as the mob keeps moving.
    private void SpawnWorldEffect(PackedScene scene)
    {
        if (scene == null || _world == null)
        {
            return;
        }
        EffectOneShot.Create(scene, _world, GlobalPosition);
    }

    // Mirrors Player.UpdateLoopEffect — instantiate parented to the mob on
    // activation, Stop() and drop the reference on deactivation. The Stop()
    // path lets the trailing audio + particles wind down without snapping.
    private void UpdateLoopEffect(ref EffectOneShot instance, PackedScene scene, bool active)
    {
        if (active)
        {
            if (instance == null && scene != null)
            {
                instance = EffectOneShot.Create(scene, this, Vector3.Zero);
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
        _footstepEmitter.Update(_world, pos, walking, _footstepStride, ground, _footstepEffects);

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
    private void DrawPathDebug()
    {
        const float Lift = 0.15f;
        var waypoints = _navigator.Waypoints;
        Vector3 mobPos = GlobalPosition + new Vector3(0f, Lift, 0f);

        if (waypoints == null || waypoints.Count == 0)
        {
            // No path yet — if the navigator has a goal, draw a single
            // dashed-style segment from the mob to the goal so we can see
            // it's trying. Use a darker shade to distinguish from a real
            // routed path.
            if (_navigator.CurrentState != MobNavigator.State.Idle)
            {
                Vector3 goal = _navigator.Goal + new Vector3(0f, Lift, 0f);
                DebugDraw.Line(mobPos, goal, new Color(0.5f, 0.5f, 0.5f));
                DebugDraw.Sphere(_navigator.Goal + new Vector3(0f, Lift, 0f), 0.3f, new Color(1f, 0.3f, 0.3f));
            }
            return;
        }

        int idx = _navigator.WaypointIndex;
        // Mob → current waypoint: yellow (the segment actively being
        // walked). Subsequent waypoints: green polyline.
        Color current = new(1f, 0.85f, 0.1f);
        Color upcoming = new(0.2f, 0.9f, 0.3f);

        Vector3 prev = mobPos;
        for (int i = idx; i < waypoints.Count; i++)
        {
            Vector3 wp = waypoints[i] + new Vector3(0f, Lift, 0f);
            DebugDraw.Line(prev, wp, i == idx ? current : upcoming);
            prev = wp;
        }
        // Goal sphere at the last waypoint so it's easy to spot.
        if (waypoints.Count > 0)
        {
            DebugDraw.Sphere(waypoints[waypoints.Count - 1] + new Vector3(0f, Lift, 0f), 0.25f, new Color(1f, 0.3f, 0.3f));
        }
    }

}
