using Godot;
using Godot.Collections;
using System.Collections.Generic;

// The spell list for the alchemy campfire screen (Spells tab). One button per
// KNOWN spell (SimData.spells filtered by SimState.IsSpellKnown), each
// labeled with the spell name and how many casts the party reagent pool currently
// affords (Cooking.CountAffordable). Selecting a button attunes that spell to the
// player's single consumable slot; SpellSelectionScreen owns the player/campfire
// and performs the attune. Pure view + selection plumbing.
//
// Unlike CookingPanel there are no editable input slots or cook job — just the
// button list, an info panel, and a read-only reagent-cost readout for the focused
// spell. Every known spell is attunable regardless of current reagents — the
// "(xN)" count is informational (a 0 means the attunement is set but can't be cast
// until reagents are gathered).
[GlobalClass]
public partial class SpellSelectionPanel : MarginContainer
{
	[Export] private Control _spellButtonContainer;
	[Export] private Label _noSpellsLabel;
	[Export] private PackedScene _spellButtonScene;
	[Export] private ItemInfoPanel _itemInfoPanel;

	// A spell button was clicked — the screen attunes it.
	public System.Action<SpellData> onSpellSelected;

	// Button cache keyed by spell so a focused button survives a reagent-count
	// refresh (diff-based rebuild, mirroring CookingPanel.RefreshRecipes).
	readonly System.Collections.Generic.Dictionary<SpellData, Button> _spellButtons = new();

	public override void _Ready()
	{
		ShowSpellDetail(null);
	}

	// Rebuild the list: one button per known spell, labeled with its live castable
	// count from `pool`; the attuned spell is marked. Buttons are never disabled —
	// any known spell can be attuned even with no reagents on hand.
	public void RefreshSpells(Array<SpellData> spells, SimState worldSim, IEnumerable<ItemState> pool, SpellData attuned)
	{
		if (_spellButtonContainer == null)
		{
			return;
		}
		// Materialize the pool once so CountAffordable can re-scan it per spell.
		var poolList = new List<ItemState>();
		if (pool != null)
		{
			foreach (ItemState s in pool)
			{
				poolList.Add(s);
			}
		}

		var desired = new HashSet<SpellData>();
		if (worldSim != null && spells != null)
		{
			for (int i = 0; i < spells.Count; i++)
			{
				SpellData spell = spells[i];
				if (spell != null && worldSim.IsSpellKnown(spell))
				{
					desired.Add(spell);
				}
			}
		}

		// Drop buttons for spells no longer in the known set.
		var stale = new List<SpellData>();
		foreach (var key in _spellButtons.Keys)
		{
			if (!desired.Contains(key))
			{
				stale.Add(key);
			}
		}
		for (int i = 0; i < stale.Count; i++)
		{
			_spellButtons[stale[i]]?.QueueFree();
			_spellButtons.Remove(stale[i]);
		}

		foreach (SpellData spell in desired)
		{
			if (!_spellButtons.TryGetValue(spell, out Button button))
			{
				button = CreateSpellButton(spell);
				if (button != null)
				{
					_spellButtons[spell] = button;
				}
			}
			if (button == null)
			{
				continue;
			}
			int affordable = Cooking.CountAffordable(spell.reagents, poolList);
			string name = worldSim != null ? worldSim.GetItemDisplayName(spell) : spell.displayName.ToString();
			// Leading marker on the currently-attuned spell so the active choice reads
			// at a glance.
			string prefix = spell == attuned ? "▶ " : string.Empty;
			button.Text = $"{prefix}{name} (x{affordable})";
		}

		if (_noSpellsLabel != null)
		{
			_noSpellsLabel.Visible = _spellButtons.Count == 0;
		}
	}

	Button CreateSpellButton(SpellData spell)
	{
		if (_spellButtonScene == null || _spellButtonContainer == null)
		{
			return null;
		}
		Button button = _spellButtonScene.Instantiate<Button>();
		if (button == null)
		{
			return null;
		}
		button.Icon = spell.inventorySprite;
		SpellData captured = spell;
		button.FocusEntered += () => ShowSpellDetail(captured);
		// Mouse hover grabs focus so the info panel tracks the cursor like D-pad nav.
		button.MouseEntered += button.GrabFocus;
		button.Pressed += () => onSpellSelected?.Invoke(captured);
		_spellButtonContainer.AddChild(button);
		return button;
	}

	// Bind the info panel (spell preview) and the reagent-cost slots to a spell.
	// Null clears everything.
	void ShowSpellDetail(SpellData spell)
	{
		if (_itemInfoPanel != null)
		{
			if (spell != null)
			{
				ItemState state = spell.CreateState();
				state.SetCount(1);
				_itemInfoPanel.SetItem(state);
			}
			else
			{
				_itemInfoPanel.SetItem(null);
			}
		}

	}

	// Focus the button for `spell` if it exists and is visible; returns false when
	// it isn't in the list (caller falls back to the first available button).
	public bool GrabFocusFor(SpellData spell)
	{
		if (spell != null && _spellButtons.TryGetValue(spell, out Button button) && button != null && button.Visible)
		{
			button.GrabFocus();
			return true;
		}
		return false;
	}

	// Focus the first spell button so gamepad / keyboard has a starting point.
	// Returns false when the list is empty.
	public bool GrabFirstAvailableFocus()
	{
		if (_spellButtonContainer == null)
		{
			return false;
		}
		foreach (Node child in _spellButtonContainer.GetChildren())
		{
			if (child is Button button && button.Visible)
			{
				button.GrabFocus();
				return true;
			}
		}
		return false;
	}
}
