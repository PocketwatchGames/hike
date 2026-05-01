using Godot;

// Random patrol around the mob's current spot. Drives the navigator's
// Wander state and lets it pick + validate points using the walkability
// grid. The behavior itself only owns the "wander vs pause" cadence — the
// "where can I actually go" logic moved into MobNavigator/WalkabilityGrid
// so attack-reposition / encircle / investigate can all share it later.
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

        output.useTorch = me.ambientLight < MobSimState.TorchAmbientThreshold;

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

        // Tell the navigator we're wandering around the spawn anchor. Speed
        // is reduced from default so wander reads as ambient idle motion
        // rather than a deliberate trek; arrival/repath is the navigator's
        // problem now.
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
