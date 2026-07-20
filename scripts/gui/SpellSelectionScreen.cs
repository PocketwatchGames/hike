using Godot;
using System;
using System.Collections.Generic;

// Select-Spell screen of the camp hub — the alchemy campfire. Left: the KNOWN
// spells (SimData.spells) with a live castable-count from the party reagent pool
// (SpellSelectionPanel). Right: the reagent materials themselves (BackpackPanel
// over the party material stash), for reference. Selecting a spell attunes it to
// the player's single consumable slot (Inventory.AttuneSpell); its ammo is then
// however many casts the pooled reagents afford (Player.GetSpellAmmo).
//
// Selecting a spell attunes it and immediately hands back to CampScreen (via
// onSelected), which returns to the hub. CampScreen owns navigation — ui_cancel
// there backs out to the hub without a pick.
[GlobalClass]
public partial class SpellSelectionScreen : Control
{
	[Export] private SpellSelectionPanel _spellPanel;
	[Export] private BackpackPanel _backpackPanel;

	Action _onSelected;
	Player _player;
	Campfire _campfire;
	// Spell to pre-focus on open (the previous pick), so re-attuning is one press.
	SpellData _preferred;

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (_spellPanel != null)
		{
			_spellPanel.onSpellSelected += OnSpellSelected;
		}
	}

	public override void _ExitTree()
	{
		if (_spellPanel != null)
		{
			_spellPanel.onSpellSelected -= OnSpellSelected;
		}
	}

	// onSelected fires after a spell is attuned; preferred is the button to start
	// focused on (the player's previous pick).
	public void Open(Player player, Campfire campfire, Action onSelected, SpellData preferred = null)
	{
		if (player != null)
		{
			_player = player;
		}
		_campfire = campfire;
		_onSelected = onSelected;
		_preferred = preferred;
		Visible = true;
		// Deferred so the just-shown buttons are visible-in-tree before GrabFocus.
		Callable.From(ApplyInitialFocus).CallDeferred();
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		Visible = false;
		_onSelected = null;
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			Refresh();
		}
	}

	void OnSpellSelected(SpellData spell)
	{
		_player?.Inventory?.AttuneSpell(spell);
		Refresh();
		_onSelected?.Invoke();
	}

	void Refresh()
	{
		if (_player == null)
		{
			return;
		}
		SimData simData = _player.Sim?.SimData;
		SimState worldSim = _player.Sim?.WorldState?.SimState;
		var pool = new List<ItemState>(_player.CombinedMaterialPool());
		_spellPanel?.RefreshSpells(simData?.spells, worldSim, pool, _player.Inventory?.AttunedSpell);
		_backpackPanel?.Refresh(MaterialStash);
	}

	List<ItemState> MaterialStash => _player?.Sim?.WorldState?.SimState?.PartyMaterialStash;

	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		// Start on the previous pick if it's still known; else the first spell.
		if (_preferred == null || !(_spellPanel?.GrabFocusFor(_preferred) ?? false))
		{
			_spellPanel?.GrabFirstAvailableFocus();
		}
	}
}
