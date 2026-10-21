using Godot;

// A carried bag of slots that holds only the categories it lists — an
// ingredient sack takes Material and nothing else. While it sits in a member's
// belt or backpack its slots are part of what they carry (Inventory.Sacks), and
// a pickup it accepts goes into it before the backpack. Its contents travel
// with it: dropped, stashed or left in a death sack, it keeps them
// (SackState.contents). Never holds another sack, whatever `accepts` lists.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class SackData : ItemData
{
	[Export(PropertyHint.Range, "1,32,1")] public int capacity = 8;

	// The item categories this sack holds.
	[Export] public Godot.Collections.Array<EItemCategory> accepts = new();

	// `accepts` as a managed lookup indexed by category — CanFullyAdd asks per
	// frame for every loot in pickup range. Built once; *Data is immutable after load.
	private bool[] _acceptsFlat;

	protected override EItemCategory ComputeCategory() => EItemCategory.Sack;

	public bool Accepts(ItemData data)
	{
		if (data == null || data is SackData || !data.IsCarriable)
		{
			return false;
		}
		_acceptsFlat ??= BuildAcceptsFlat();
		int category = (int)data.Category;
		return category >= 0 && category < _acceptsFlat.Length && _acceptsFlat[category];
	}

	private bool[] BuildAcceptsFlat()
	{
		int max = 0;
		foreach (EItemCategory category in accepts)
		{
			max = Mathf.Max(max, (int)category);
		}
		var flat = new bool[max + 1];
		foreach (EItemCategory category in accepts)
		{
			flat[(int)category] = true;
		}
		return flat;
	}

	public override ItemState CreateState()
	{
		return new SackState(this);
	}
}
