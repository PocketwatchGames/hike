using Godot;

// Behavior-level positioning for a mob engaging a target. The mob's weapons —
// which attack to fire and at what range / cooldown / ally count — come from the
// species (SpeciesData.weapons, via Mob.Weapons), each WeaponData carrying its
// own AI engagement tuning; BehaviorAttack reads them off the mob.
// This data holds only the chase / encircle geometry that's the same regardless
// of which weapon swings.
[GlobalClass]
public partial class AttackBehaviorData : BehaviorData
{
    public AttackBehaviorData() { behaviorFlags = EBehaviorFlags.Engaging; }

    // Farthest the mob will chase the target before giving up on approach this
    // tick (the transition out of attack still runs via aggro-lost).
    [Export] public float approachRange = 30f;

    // Everyone engaging a target spreads evenly around it (EncircleAllocator).
    // Minimum arc (meters) between neighbours on the ring — once that many
    // mobs no longer fit at encircleDistance, the overflow holds on a ring
    // encircleOverflowGap further out until a place opens. Set it to the
    // mob's body width plus elbow room.
    [Export(PropertyHint.Range, "0.25,20,0.05,or_greater")] public float encircleMinSpacing = 1.5f;
    [Export(PropertyHint.Range, "0.25,20,0.05,or_greater")] public float encircleOverflowGap = 2f;

    // Distance from the target the mob holds *between* swings (while every weapon
    // is on cooldown). When a weapon comes off cooldown the mob closes to that
    // weapon's desiredAttackRange to attack, then falls back here — so this may
    // sit at or beyond maxAttackRange to make a skirmisher disengage between
    // swings without ever stalling the attack. <= 0 falls back to the closest
    // desiredAttackRange among the mob's weapons, collapsing the ring onto attack
    // range (no disengage).
    [Export] public float encircleDistance = -1f;

    // Max angle (degrees) between the mob's facing and the direction to the
    // target for it to commit a swing — it won't initiate an attack while turned
    // further off-axis than this, so swings don't fire sideways. Pairs with the
    // weapon's turn-lock grace window (ItemAction.turnLockDelaySeconds), which
    // finishes the aim during the swing's opening. 180 (default) = no facing
    // requirement. Bypassed when the mob's facing is frozen off-screen (it can't
    // turn to satisfy the gate), so it never deadlocks an unseen attacker.
    [Export] public float attackFacingToleranceDegrees = 180f;

    // Additional pause (seconds) AFTER a weapon's fixed cooldown elapses before
    // the mob commits its next swing. The weapon's cooldownSeconds is the hard
    // floor between attacks; this pause is layered on top as a behavior beat
    // during which the mob keeps circling the encircle ring — so the cadence
    // isn't a tight cooldown loop and there's a clear, readable window between
    // swings (the window mobs dodge in). attackPauseRandomSeconds adds 0..N on
    // top of attackPauseSeconds each cycle so the cadence isn't metronomic; 0/0
    // = swing the instant the cooldown clears (the old fixed cadence).
    [Export] public float attackPauseSeconds = 0f;
    [Export] public float attackPauseRandomSeconds = 0f;

    public override BehaviorBase CreateRuntime() => new BehaviorAttack(this);
}
