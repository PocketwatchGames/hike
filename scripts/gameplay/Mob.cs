using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class Mob : RigidBody3D, IWorldEntity
{
    [Export] private CollisionShape3D _collisionShape;
    [Export] private AnimationPlayer _animationPlayer;
    [Export] private Node3D _mesh;
    [Export] private Sprite3D _sprite;
    [Export] private HurtBox _hurtBox;
    [Export] public Node3D HudAnchor;
    [Export] public PackedScene HudScene;

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

    readonly List<TallGrass> _tallGrassCollisions = new();
    float _terrainSpeed = 1f;
    readonly WaterRippleEmitter _rippleEmitter = new();
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
        TreeExiting += () =>
        {
            SyncToSimState();
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
        InitBehaviors();
        world.AddChild(this);
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

        if (alive)
        {
            TickAI((float)delta, out AIOutput aiOutput);

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
    }

    private void Die()
    {
        if (!alive)
        {
            return;
        }

        alive = false;
        AxisLockAngularY = false;
        if (Freeze)
        {
            Freeze = false;
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

    public void AddTerrainModifier(TallGrass tallGrass)
    {
        _tallGrassCollisions.Add(tallGrass);
    }

    public void RemoveTerrainModifier(TallGrass tallGrass)
    {
        _tallGrassCollisions.Remove(tallGrass);
    }

}
