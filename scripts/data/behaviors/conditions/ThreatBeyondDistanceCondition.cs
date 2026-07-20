using Godot;

// Fires when the player is FARTHER than `distance` from the mob — the inverse
// of ThreatWithinDistanceCondition. Measures against the live player rather
// than requiring a still-triggered perception slot, so it keeps firing even
// after a fleeing mob has broken line of sight and its perception has decayed.
// Used to send a fleeing fairy into its escape (vanish) once it has put enough
// ground between itself and the player.
[GlobalClass]
public partial class ThreatBeyondDistanceCondition : BehaviorTransitionData
{
    [Export] public float distance = 25f;

    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        Player player = targetPerception.pawnTarget ?? me.Sim?.player;
        if (player == null)
        {
            return false;
        }
        return (player.GlobalPosition - me.GlobalPosition).LengthSquared() > distance * distance;
    }
}
