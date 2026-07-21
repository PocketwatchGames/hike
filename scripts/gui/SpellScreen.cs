using Godot;
using Godot.Collections;

// Spell tab rendered inside AlmanacScreen. Lists every spell the player has
// learned this run (SimState.IsSpellKnown → Knowledge.KnownSpells) — one row
// per known spell. Focusing a row populates the item info panel with the spell
// and the reagent slots with the spell's per-cast reagent cost.
//
// View only — spells are learned out in the world (scrolls / teaching) and
// attuned at the alchemy campfire, not from this screen. The Almanac wrapper
// owns InputSuppressed / hud-visibility / ui_cancel handling; this screen just
// rebuilds when its tab is shown.
[GlobalClass]
public partial class SpellScreen : Control
{
	GameClient _gameClient;
	[Export] PackedScene _spellButtonScene;
	[Export] Control _spellListContainer;
	[Export] ItemInfoPanel _itemInfoPanel;
	[Export] Label _noSpellsLabel;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		ShowSpellDetail(null);
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			Rebuild();
		}
	}

	// Walk SimData.spells, keep only the ones the player has learned, and stamp
	// out one button per spell. The container also owns the "No Spells Known!"
	// label as a sibling child — we only free Button-typed children so the label
	// survives.
	void Rebuild()
	{
		if (_spellListContainer == null)
		{
			return;
		}
		foreach (Node child in _spellListContainer.GetChildren())
		{
			if (child is Button)
			{
				child.QueueFree();
			}
		}

		SimData simData = _gameClient?.Sim?.SimData;
		SimState worldSim = _gameClient?.Sim?.WorldState?.SimState;
		Button firstButton = null;
		SpellData firstSpell = null;
		if (simData != null && worldSim != null && simData.spells != null)
		{
			for (int i = 0; i < simData.spells.Count; i++)
			{
				SpellData spell = simData.spells[i];
				if (spell == null || !worldSim.IsSpellKnown(spell))
				{
					continue;
				}
				Button b = CreateSpellButton(spell);
				if (b != null && firstButton == null) { firstButton = b; firstSpell = spell; }
			}
		}

		bool any = firstButton != null;
		if (_noSpellsLabel != null)
		{
			_noSpellsLabel.Visible = !any;
		}
		if (any)
		{
			// Populate the right-hand detail synchronously so the panel shows
			// the first spell immediately. The deferred GrabFocus below moves
			// keyboard focus onto the button at end-of-frame; relying on its
			// FocusEntered signal to fill the panel would leave a blank state
			// in the meantime (and didn't reliably fire at all when the screen
			// was opened straight onto this tab).
			ShowSpellDetail(firstSpell);
			firstButton.CallDeferred(Control.MethodName.GrabFocus);
		}
		else
		{
			ShowSpellDetail(null);
		}
	}

	Button CreateSpellButton(SpellData spell)
	{
		if (_spellButtonScene == null || _spellListContainer == null)
		{
			return null;
		}
		Button button = _spellButtonScene.Instantiate<Button>();
		if (button == null)
		{
			return null;
		}
		SimState worldSim = _gameClient?.Sim?.WorldState?.SimState;
		button.Text = worldSim != null
			? worldSim.GetItemDisplayName(spell)
			: spell.displayName.ToString();
		button.Icon = spell.inventorySprite;
		SpellData captured = spell;
		button.FocusEntered += () => ShowSpellDetail(captured);
		// Mouse hover grabs focus so the right-hand info / reagent view tracks
		// the cursor the same way D-pad navigation does.
		button.MouseEntered += button.GrabFocus;
		_spellListContainer.AddChild(button);
		return button;
	}

	// Bind the right-hand info panel and reagent slots to a single spell row.
	// spell = null clears everything (used at construction and when no spells
	// are known).
	void ShowSpellDetail(SpellData spell)
	{
		if (spell != null)
		{
			ItemState state = spell.CreateState();
			state.stackCount = 1;
			_itemInfoPanel?.SetItem(state);
		}
		else
		{
			_itemInfoPanel?.SetItem(null);
		}

	}
}
