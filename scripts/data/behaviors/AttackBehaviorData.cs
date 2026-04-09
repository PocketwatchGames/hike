using Godot;

[GlobalClass]
public partial class AttackBehaviorData : BehaviorData
{
    // Distance at which the mob will fire its weapon (if it can see the target).
    [Export] public float maxAttackRange = 2.5f;
    // Path success distance when chasing and the target is visible — the mob
    // stops closing once it reaches this range so it can swing instead of
    // slamming into the player.
    [Export] public float desiredAttackRange = 1.75f;
    // Farthest the mob will chase the target before giving up on approach this
    // tick (the transition out of attack still runs via aggro-lost).
    [Export] public float approachRange = 30f;
    // Cooldown after firing before the mob can attack again. While on cooldown
    // the mob picks a reposition point near the target and backs off.
    [Export] public float attackCooldownSeconds = 1.5f;

    public override BehaviorBase CreateRuntime() => new BehaviorAttack(this);
}
