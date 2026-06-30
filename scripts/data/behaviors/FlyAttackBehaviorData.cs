using Godot;

// Aerial variant of AttackBehaviorData: the same encircle / approach / weapon
// geometry, plus the vertical tiers a flying combatant moves between. The mob
// stays airborne for the whole engagement; which height it holds is chosen per
// tick from the weapon it's bringing to bear (see BehaviorFlyAttack).
[GlobalClass]
public partial class FlyAttackBehaviorData : AttackBehaviorData
{
    // A weapon whose desiredAttackRange is at least this (meters) is treated as
    // the mob's "ranged" attack: while bringing it to bear the mob rises to
    // rangedAltitude. Shorter-range weapons are "melee" — the mob descends to
    // the target's height to dart in. Sits between the melee and ranged weapons'
    // desiredAttackRange values.
    [Export] public float rangedTierRange = 5f;

    // Height above the terrain (voxels) the mob climbs to for its ranged tier —
    // the "rise to N meters and fire" altitude. Terrain-relative, so it clears
    // hills the way ordinary cruising does.
    [Export] public float rangedAltitude = 3f;

    // Offset (voxels) added to the target's world Y for the encircle / melee
    // tier. 0 hovers exactly at the target's height; the physics layer floors
    // the result ~1m above the ground regardless, so a small positive value
    // makes the mob ride slightly above the player to swoop down on a dart.
    [Export] public float engageHeightOffset = 0f;

    public override BehaviorBase CreateRuntime() => new BehaviorFlyAttack(this);
}
