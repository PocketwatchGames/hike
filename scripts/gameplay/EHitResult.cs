// Outcome of a HurtBox.Hit call, returned to the attacker so the weapon can
// pick the right impact effect. None means the hit was filtered out (target
// dead, no damage data, self-hit). Object covers props with hurtboxes that
// don't have a health/armor model (doors, chests, loot). Health/Armor cover
// damageable creatures and report which pool absorbed the hit; Lethal is the
// killing blow on a creature (overrides Health for impact-effect picking).
public enum EHitResult
{
    None,
    Object,
    Armor,
    Health,
    Lethal,
}
