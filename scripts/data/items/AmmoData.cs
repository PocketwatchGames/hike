using Godot;

// One kind of round for the weapons of its ammoType. Carried in the grids like
// any stackable; the member's loaded kind per type (Inventory.LoadedAmmo) is what
// those weapons fire, drawn from every carried stack. A fired round lands as an
// ordinary pickup of this same item, unless it breaks.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class AmmoData : ItemData
{
	[Export] public EAmmoType ammoType = EAmmoType.None;

	// What a weapon falls back to when the loaded kind runs out. At most one per
	// ammoType (resource_check).
	[Export] public bool isStandard = false;

	// Chance a fired round is destroyed where it lands instead of leaving a pickup.
	[Export(PropertyHint.Range, "0,1,0.01")] public float breakChance = 0f;

	// Off = a hit on a living mob glances off and lands loose instead of embedding.
	[Export] public bool embedsInTargets = true;

	// Model shown while embedded in a live mob (ArrowStuck). Null falls back to
	// the worldSprite billboard.
	[Export] public PackedScene stuckModel;

	// Weapon mods this round carries into every shot that fires it (a fire arrow's
	// Burning, a dart's poison), folded in beside the weapon's own mods. Scope is
	// read as on the weapon's: AllAttacks, or one charge tier of whatever fires it.
	[Export] public Godot.Collections.Array<StatusEffectDescriptor> mods = new();

	// `mods` composed once onto an item-side controller the firing weapon folds in
	// (StatusEffectController.SetLoadedRoundSource). Null when there are none.
	// Shared by every stack of this kind: *Data is immutable after load.
	public StatusEffectController ModSource()
	{
		if (_modSourceBuilt)
		{
			return _modSource;
		}
		_modSourceBuilt = true;
		if (mods == null || mods.Count == 0)
		{
			return null;
		}
		_modSource = new StatusEffectController(null, null, null);
		foreach (StatusEffectDescriptor desc in mods)
		{
			if (desc?.effect != null)
			{
				_modSource.AddWeaponMod(desc.effect, desc.scope, desc.chargeIndex);
			}
		}
		return _modSource;
	}
	private StatusEffectController _modSource;
	private bool _modSourceBuilt;

	protected override EItemCategory ComputeCategory() => EItemCategory.Ammo;
}
