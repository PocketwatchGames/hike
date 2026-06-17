using Godot;

// Random patrol around the mob's spawn anchor. The behavior owns only the
// "wander vs pause" cadence; point picking/validation against the
// walkability grid lives in MobNavigator/WalkabilityGrid.
public partial class BehaviorWander : BehaviorBase
{
    private const float WanderRange = 15f;

    private readonly WanderBehaviorData _data;
    private ulong _pauseUntilMs;
    private bool _wandering;

    public BehaviorWander(WanderBehaviorData data)
    {
        _data = data;
    }

    // Reset cross-tick state on every (re-)entry so a behavior switch out
    // and back doesn't leave us thinking we're still mid-wander while the
    // navigator is now pointed somewhere else (e.g. a combat reposition
    // goal set by BehaviorAttack just before we got control again).
    public override void OnEnter(Mob me, ulong time)
    {
        _wandering = false;
        _pauseUntilMs = 0;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }


        // Pause window between wander legs. While paused we leave the
        // navigator idle so the mob's impulse code coasts to a stop.
        if (time < _pauseUntilMs)
        {
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        if (!_wandering)
        {
            me.Navigator.Wander(me.spawnPosition, WanderRange);
            _wandering = true;
        }

        // Reduced speed so wander reads as ambient idle motion rather than a
        // deliberate trek.
        if (me.Navigator.HasArrived || me.Navigator.IsBlocked)
        {
            double pauseSeconds = GD.RandRange((double)_data.pauseTimeRange.X, (double)_data.pauseTimeRange.Y);
            _pauseUntilMs = time + (ulong)(pauseSeconds * 1000.0);
            _wandering = false;
            me.Navigator.Stop();
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        output.speed = 0.25f;
        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
