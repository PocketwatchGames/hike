using Godot;

// A carried vessel of one liquid. It is never used directly: carrying it
// enlarges a pool the player already has. A water flask adds its hours to the
// thirst meter (drunk from before the body goes thirsty); an oil flask adds to
// the lantern's oil (burned once the lantern runs dry). A well refills every
// water flask, an oil fountain every oil flask. Non-stackable — each one holds
// its own amount (FlaskState.amount), and a new flask comes full.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class FlaskData : ItemData
{
	[Export] public ELiquid liquid = ELiquid.Water;

	// What a full flask holds, in its liquid's unit: hours of drinking for
	// water (the thirst meter's own unit), oil units for oil (a lantern holds 100).
	[Export(PropertyHint.Range, "0.5,1000,0.5,or_greater")] public float capacity = 6f;

	protected override EItemCategory ComputeCategory() => EItemCategory.Container;

	public override ItemState CreateState()
	{
		return new FlaskState(this);
	}
}
