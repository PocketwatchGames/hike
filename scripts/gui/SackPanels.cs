using Godot;
using System.Collections.Generic;

// One slot grid per sack a member carries (Inventory.Sacks), stacked in this
// container and kept in step by Refresh — instanced on demand, and hidden
// rather than freed when there are fewer sacks. Like BackpackPanel it owns no
// verbs: it forwards each slot's raw events with the sack grid and slot index,
// resolved from the last Refresh, for the screen to act on.
[GlobalClass]
public partial class SackPanels : VBoxContainer
{
	[Export] private PackedScene _panelScene;

	public System.Action<Inventory.CarriedGrid, int, ItemSlotPanel> onSlotFocused;
	public System.Action<Inventory.CarriedGrid, int, ItemSlotPanel> onSlotButtonDown;
	public System.Action<Inventory.CarriedGrid, int, ItemSlotPanel> onSlotButtonUp;

	// Every panel ever instanced; the first `_grids.Count` are showing, panel i
	// over _grids[i].
	readonly List<BackpackPanel> _panels = new();
	readonly List<Inventory.CarriedGrid> _grids = new();

	// Show one grid per sack `inventory` carries, in its order.
	public void Refresh(Inventory inventory)
	{
		_grids.Clear();
		if (inventory != null)
		{
			_grids.AddRange(inventory.Sacks);
		}
		if (_grids.Count > _panels.Count && _panelScene == null)
		{
			GD.PushError($"SackPanels '{Name}': {_grids.Count} sacks to show but no panel scene to build them from.");
			_grids.RemoveRange(_panels.Count, _grids.Count - _panels.Count);
		}
		for (int i = _panels.Count; i < _grids.Count; i++)
		{
			BackpackPanel panel = _panelScene.Instantiate<BackpackPanel>();
			AddChild(panel);
			_panels.Add(panel);
			Wire(panel, i);
		}
		for (int i = 0; i < _panels.Count; i++)
		{
			bool shown = i < _grids.Count;
			_panels[i].Visible = shown;
			_panels[i].Refresh(shown ? _grids[i].Slots : null);
			if (shown)
			{
				_panels[i].SetTitle(SackName(_grids[i].sack));
			}
		}
	}

	// The name the item panel would show for the sack itself.
	static string SackName(SackState sack)
	{
		SimState worldSim = Sim.Current?.WorldState?.SimState;
		return worldSim != null ? worldSim.GetItemDisplayName(sack) : sack.data.displayName.ToString();
	}

	void Wire(BackpackPanel panel, int sack)
	{
		panel.onSlotFocused += (index, slot) => Raise(onSlotFocused, sack, index, slot);
		panel.onSlotButtonDown += (index, slot) => Raise(onSlotButtonDown, sack, index, slot);
		panel.onSlotButtonUp += (index, slot) => Raise(onSlotButtonUp, sack, index, slot);
	}

	void Raise(System.Action<Inventory.CarriedGrid, int, ItemSlotPanel> handler, int sack, int index, ItemSlotPanel slot)
	{
		if (sack < _grids.Count)
		{
			handler?.Invoke(_grids[sack], index, slot);
		}
	}

	// The showing panels, each with the sack grid it shows.
	public IEnumerable<(Inventory.CarriedGrid grid, BackpackPanel panel)> EnumeratePanels()
	{
		for (int i = 0; i < _grids.Count; i++)
		{
			yield return (_grids[i], _panels[i]);
		}
	}

	// The sack grid and slot index behind `slot`; false when it isn't one of ours.
	public bool TryGetSlot(ItemSlotPanel slot, out Inventory.CarriedGrid grid, out int index)
	{
		for (int i = 0; i < _grids.Count; i++)
		{
			index = _panels[i].IndexOf(slot);
			if (index >= 0)
			{
				grid = _grids[i];
				return true;
			}
		}
		grid = null;
		index = -1;
		return false;
	}

	// The first slot holding an item across the showing sacks, or null.
	public ItemSlotPanel FirstOccupied()
	{
		for (int i = 0; i < _grids.Count; i++)
		{
			ItemSlotPanel slot = _panels[i].FirstOccupied();
			if (slot != null)
			{
				return slot;
			}
		}
		return null;
	}

	public void SetFocusable(bool focusable)
	{
		foreach (BackpackPanel panel in _panels)
		{
			panel.SetFocusable(focusable);
		}
	}
}
