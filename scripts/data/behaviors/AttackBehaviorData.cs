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

    // Number of angular standoff slots around a target on the encircle ring.
    // A swarm of N mobs fanning out around the player should set this to a
    // value >= N so every mob gets its own angle. Higher = more spread,
    // lower = mobs cluster closer together. 8 reads as "ring around player";
    // 4 reads as "cardinal sides only"; 1 disables encircle (all mobs
    // converge from whichever side they're on).
    [Export] public int encircleSlotCount = 8;

    // Distance from the target the mob holds while on cooldown / encircling.
    // Defaults to desiredAttackRange so the encircle ring sits exactly at
    // attack range; bump it out for skirmishers that prefer to disengage
    // between swings.
    [Export] public float encircleDistance = -1f;

    // Action profile run through the mob's ActionRunner when an attack fires.
    // The profile's events should carry their own DamageData (via ItemEvent.damageData)
    // since mobs aren't backed by a WeaponState. Charging / queueing / multi-tier
    // all work the same as for the player; for typical mobs author a single tier
    // with chargeTime=0 and autoActivateAtMax=true (immediate-fire).
    [Export] public ItemActionProfile actionProfile;

    public override BehaviorBase CreateRuntime() => new BehaviorAttack(this);
}
