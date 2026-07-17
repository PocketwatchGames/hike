using Godot;
using System;
using System.Collections.Generic;

// Spells tab of the camp screen — the alchemy campfire. Left: the KNOWN spells
// (SimData.spells) with a live castable-count from the party reagent pool
// (SpellSelectionPanel). Right: the reagent materials themselves (BackpackPanel
// over the party material stash), for reference. Selecting a spell attunes it to
// the player's single consumable slot (Inventory.AttuneSpell); its ammo is then
// however many casts the pooled reagents afford (Player.GetSpellAmmo).
//
// Attuning stays on the screen (so the marker/counts update and the player can
// change their mind); the player leaves via ui_cancel, which CampScreen owns.
// CampScreen still calls this with the old Cook-tab signature (onCooked/onContinue)
// — attuning isn't cooking, so onCooked is ignored and onContinue is the leave
// callback.
[GlobalClass]
public partial class SpellSelectionScreen : Control
{
	[Export] private SpellSelectionPanel _spellPanel;
	[Export] private BackpackPanel _backpackPanel;

	Action _onClose;
	Player _player;
	Campfire _campfire;

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

	public void Open(Player player, Campfire campfire = null, Action onCooked = null, Action onContinue = null)
	{
		if (player != null)
		{
			_player = player;
		}
		_campfire = campfire;
		_onClose = onContinue;
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
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
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
	}

	void Refresh()
	{
		if (_player == null)
		{
			return;
		}
		SimData simData = _player.World?.SimData;
		WorldSimState worldSim = _player.World?.WorldState?.SimState;
		var pool = new List<ItemState>(_player.CombinedMaterialPool());
		_spellPanel?.RefreshSpells(simData?.spells, worldSim, pool, _player.Inventory?.AttunedSpell);
		_backpackPanel?.Refresh(MaterialStash);
	}

	List<ItemState> MaterialStash => _player?.World?.WorldState?.SimState?.PartyMaterialStash;

	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		_spellPanel?.GrabFirstAvailableFocus();
	}
}
