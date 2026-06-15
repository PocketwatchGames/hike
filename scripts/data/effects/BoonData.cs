using Godot;

// A fairy "boon": one selectable reward offered by a fairy corpse on the
// upgrade screen. A thin wrapper so a boon can be more than a status effect —
// it pairs an optional StatusEffectData (applied to the picker) with an
// optional ItemData (a single unit dropped into the player's pack). Gold, which
// is not a status effect at all, is authored as a boon with only `grantedItem`
// set; the dash / flame-trail buffs as a boon with only `statusEffect` set; the
// Restore blessing inlines its instant StatusEffectData. Either field may be
// null, and the wrapped statusEffect may be declared inline in the boon's .tres
// (a sub-resource) or referenced — both apply identically.
//
// Display fields fall back to the wrapped status effect, so a buff boon needs
// no duplicate authoring while an item-only boon (gold) supplies its own.
[GlobalClass]
public partial class BoonData : Resource
{
	[Export] public StringName displayName;
	[Export(PropertyHint.MultilineText)] public string description = "";
	[Export] public Texture2D icon;

	// Status effect applied to the actor that picks this boon. Null for a boon
	// that only grants an item. May be inline or an external reference.
	[Export] public StatusEffectData statusEffect;

	// Single item unit granted to the picking PLAYER's inventory (no-op for an
	// actor without an inventory). Null for a boon that only applies an effect.
	[Export] public ItemData grantedItem;

	public StringName DisplayName =>
		!string.IsNullOrEmpty(displayName) ? displayName : (statusEffect?.displayName ?? new StringName());
	public string Description =>
		!string.IsNullOrEmpty(description) ? description : (statusEffect?.description ?? "");
	public Texture2D Icon => icon ?? statusEffect?.icon;
}
