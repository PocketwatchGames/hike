using Godot;

// Behavior-level positioning for a mob engaging a target. The mob's weapons —
// which attack to fire and at what range / cooldown / ally count — live on the
// mob itself (MobData.weapons), each WeaponData carrying its own AI engagement
// tuning; BehaviorAttack reads them off the mob. This data holds only the
// chase / encircle geometry that's the same regardless of which weapon swings.
[GlobalClass]
public partial class AttackBehaviorData : BehaviorData
{
    // Farthest the mob will chase the target before giving up on approach this
    // tick (the transition out of attack still runs via aggro-lost).
    [Export] public float approachRange = 30f;

    // Number of angular standoff slots around a target on the encircle ring.
    // A swarm of N mobs fanning out around the player should set this to a
    // value >= N so every mob gets its own angle. Higher = more spread,
    // lower = mobs cluster closer together. 8 reads as "ring around player";
    // 4 reads as "cardinal sides only"; 1 disables encircle (all mobs
    // converge from whichever side they're on).
    [Export] public int encircleSlotCount = 8;

    // Distance from the target the mob holds while on cooldown / encircling.
    // <= 0 falls back to the closest desiredAttackRange among the mob's weapons
    // so the ring sits at attack range; bump it out for skirmishers that prefer
    // to disengage between swings.
    [Export] public float encircleDistance = -1f;

    public override BehaviorBase CreateRuntime() => new BehaviorAttack(this);
}
