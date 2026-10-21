using System.Collections.Generic;
using System.IO;
using Godot;

// A sack and what is in it. The contents are written inside the sack's own
// subclass state, so every path that stores an item — dropped loot, the stash,
// a death sack, the save — keeps them without knowing sacks exist.
public class SackState : ItemState
{
	public override SackData data => _sackData;
	private readonly SackData _sackData;

	// Slot for slot, gaps included. Placement rules (what it accepts) are the
	// owner's to enforce — see Inventory.CarriedGrid.
	public readonly ItemGrid contents;

	public SackState(SackData d) : base(d)
	{
		_sackData = d;
		contents = new ItemGrid(d.capacity);
	}

	public bool Accepts(ItemData item) => _sackData.Accepts(item);

	public bool IsEmpty => contents.OccupiedCount() == 0;

	// True for a sack with anything in it — what a merchant won't take.
	public static bool IsFilled(ItemState item) => item is SackState sack && !sack.IsEmpty;

	public override void WriteSubclassState(BinaryWriter w)
	{
		contents.Serialize(w);
	}

	public override void ReadSubclassState(BinaryReader r)
	{
		// More saved slots than the sack now has (its capacity was cut): repack
		// into the slots that remain.
		List<ItemState> overflow = contents.Restore(r);
		foreach (ItemState item in overflow)
		{
			ItemState lost = contents.Add(item);
			if (lost != null)
			{
				GD.PushWarning($"SackState: '{_sackData.ResourcePath}' has no room for saved {lost.stackCount}x '{lost.data?.ResourcePath}' — it is lost.");
			}
		}
	}
}
