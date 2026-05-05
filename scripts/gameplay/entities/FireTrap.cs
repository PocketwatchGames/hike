using Godot;

public enum EFireTrapState
{
    Idle,
    Warning,
    Active,
}

// Self-driven cyclic hazard: warning -> column erupts for activeSeconds ->
// cooldown -> repeat. Each instance rolls a random phase offset at spawn so
// neighbouring traps fire on different beats — the Princess Bride "fire
// swamp" effect of pillars erupting around the player at unpredictable
// times.
//
// Unlike Trap (TriggerSource + ITriggerable), FireTrap is not body-driven
// and has no disarm interaction in this iteration; it's purely an
// environmental obstacle the player navigates around. The damage zone is
// gated by SetActive so the trap is harmless during Idle/Warning and only
// dangerous during the Active window.
[GlobalClass]
public partial class FireTrap : Node3D, IWorldEntity
{
    [Export] public FireTrapData data;
    [Export] private DamageZone _damageZone;

    private FireTrapSimState _simState;
    private EFireTrapState _state = EFireTrapState.Idle;
    private float _stateTimer;
    private Fx _columnLoop;

    public override void _Ready()
    {
        // Damage zone starts off — only Active enables it.
        _damageZone?.SetActive(false);
        if (_simState != null)
        {
            // WorldGen-spawned: persisted phase offset preserves rhythm across
            // save/load so authored encounters don't reshuffle on reload.
            _stateTimer = _simState.PhaseOffsetSeconds;
        }
        else if (data != null)
        {
            // Editor-placed: roll a fresh phase offset so multiple traps in a
            // hand-authored scene don't fire in lockstep.
            _stateTimer = (float)GD.RandRange(0.0, data.maxPhaseOffsetSeconds);
        }
    }

    public void OnSpawned(World world)
    {
    }

    public override void _PhysicsProcess(double delta)
    {
        if (data == null)
        {
            return;
        }
        float dt = (float)delta;
        _stateTimer -= dt;
        if (_stateTimer > 0f)
        {
            return;
        }
        switch (_state)
        {
            case EFireTrapState.Idle:
                EnterWarning();
                break;
            case EFireTrapState.Warning:
                EnterActive();
                break;
            case EFireTrapState.Active:
                EnterIdle();
                break;
        }
    }

    private void EnterWarning()
    {
        _state = EFireTrapState.Warning;
        _stateTimer = data.warningSeconds;
        if (data.warningEffect != null)
        {
            Fx.Create(data.warningEffect, this, Vector3.Zero);
        }
    }

    private void EnterActive()
    {
        _state = EFireTrapState.Active;
        _stateTimer = data.activeSeconds;
        _damageZone?.SetActive(true);
        if (data.igniteEffect != null)
        {
            Fx.Create(data.igniteEffect, this, Vector3.Zero);
        }
        if (data.columnLoopEffect != null && _columnLoop == null)
        {
            _columnLoop = Fx.Create(data.columnLoopEffect, this, Vector3.Zero);
        }
    }

    private void EnterIdle()
    {
        _state = EFireTrapState.Idle;
        _stateTimer = data.cooldownSeconds;
        _damageZone?.SetActive(false);
        if (_columnLoop != null)
        {
            _columnLoop.Stop();
            _columnLoop = null;
        }
    }

    public static FireTrap Create(World world, FireTrapSimState data)
    {
        var instance = data.Scene.Instantiate<FireTrap>();
        instance.Position = data.WorldPosition;
        instance._simState = data;
        world.AddChild(instance);
        return instance;
    }
}
