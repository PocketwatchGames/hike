// Capability marker for item data that drives a press/hold action through an
// ItemActionProfile — the attuned spell and the lantern. Deliberately an
// interface, not a base class: SpellData and LanternData are otherwise
// unrelated (one is a reagent-cast spell, the other an equipped fuel-burning
// light), so they share only this one capability. Lets the UI ask "can this be
// used?" without a common ancestor. WeaponData runs its own attack path and
// intentionally does not implement this.
public interface IUsableItem
{
	ItemActionProfile ActionProfile { get; }
}
