using System.Collections.Generic;
using Godot;

// Side-panel on the inventory screen that displays the highlighted item's
// name, icon, and description. Hidden outright when nothing is highlighted —
// InventoryScreen routes focus changes here via InventoryPanel.onFocusedItemChanged.
[GlobalClass]
public partial class ItemInfoPanel : PanelContainer
{
	[Export] private Label _nameLabel;
	[Export] private Label _descriptionLabel;
	[Export] private TextureRect _icon;
	[Export] private PackedScene _actionPanel;
	[Export] private Control _actionPanelContainer;
	[Export] private Control _statContainer;
	[Export] private PackedScene _statScene;
	[Export] private Control _statusContainer;
	[Export] private PackedScene _statusScene;
	// When false, action panels list only the attack names — the per-attack
	// stat rows (damage, range, cost) are suppressed. The party screen uses
	// this for a compact at-a-glance readout.
	[Export] private bool _showDetails = true;
	[Export] Control _reagentsRoot;
	[Export] Godot.Collections.Array<ItemSlotPanel> _reagentSlots;

	// forceIdentified overrides the world's identification gate so the item's
	// stats/description always show — the forge picker uses it so a freshly
	// minted piece reads in full before the player commits to it.
	//
	// reagents, when supplied, are the required ingredients shown in the "Required
	// Reagents" row — a recipe's `inputs` for a crafting preview. Left null, the row
	// falls back to the item's own use cost when it's an alchemy spell
	// (SpellData.reagents), and hides entirely for anything else.
	public void SetItem(ItemState item, bool forceIdentified = false, IReadOnlyList<RecipeInput> reagents = null)
	{
		ItemData data = item?.data;
		if (data == null)
		{
			Visible = false;
			return;
		}
		SimState worldSim = Sim.Current?.WorldState?.SimState;
		bool identified = forceIdentified || worldSim == null || worldSim.IsItemIdentified(data);
		if (_nameLabel != null)
		{
			_nameLabel.Text = worldSim != null
				? worldSim.GetItemDisplayName(item)
				: WeaponNameGenerator.Compose(data.displayName.ToString(), item);
		}
		if (_descriptionLabel != null)
		{
			// Hide flavor text while the item is unidentified — revealing
			// the recipe of a "?" potion via the info panel would defeat
			// the reveal-on-use design.
			string flavor = data.description ?? string.Empty;
			_descriptionLabel.Text = identified ? flavor : string.Empty;
		}
		if (_icon != null)
		{
			_icon.Texture = data.inventorySprite;
		}
		RebuildItemStats(item, identified);
		RebuildStatusEffects(item);
		RebuildActionPanels(item, identified);
		// An explicit list (a crafting recipe's inputs) wins; otherwise a spell shows
		// its own cast cost. Only spells carry reagents on the item, so the auto path
		// never leaks an unidentified consumable's recipe.
		RebuildReagents(reagents ?? (data as SpellData)?.reagents);
		Visible = true;
	}

	// Fill the "Required Reagents" slots — one authored ItemSlotPanel per entry at its
	// required count; extra slots are cleared. The whole row (_reagentsRoot) hides
	// when there are no reagents to show.
	private void RebuildReagents(IReadOnlyList<RecipeInput> reagents)
	{
		if (_reagentsRoot != null)
		{
			_reagentsRoot.Visible = reagents != null && reagents.Count > 0;
		}
		if (_reagentSlots == null)
		{
			return;
		}
		for (int i = 0; i < _reagentSlots.Count; i++)
		{
			ItemSlotPanel slot = _reagentSlots[i];
			if (slot == null)
			{
				continue;
			}
			ItemState reagent = null;
			if (reagents != null && i < reagents.Count)
			{
				RecipeInput ri = reagents[i];
				if (ri?.item != null && ri.count > 0)
				{
					reagent = ri.item.CreateState();
					reagent.stackCount = ri.count;
				}
			}
			slot.SetItem(reagent);
		}
	}

	// One StatusEffectInfoPanel per armed effect on the item. Reuses the
	// same panel PlayerStatsPanel uses for the player's status readout —
	// icon + name + bars (via embedded StatusEffectHud) + per-stat rows.
	// Buildup-only meters are skipped here, matching the slot view's policy:
	// this section shows what's *actually* affecting the item, not pre-arm
	// progress still climbing toward its threshold. Hidden / no-op if the
	// container or scene field isn't wired in the authoring .tscn.
	private void RebuildStatusEffects(ItemState item)
	{
		if (_statusContainer == null)
		{
			return;
		}
		foreach (Node child in _statusContainer.GetChildren())
		{
			if (child is StatusEffectInfoPanel existing)
			{
				existing.QueueFree();
			}
		}
		if (item == null || _statusScene == null)
		{
			return;
		}
		var effects = item.statusEffects.StatusEffects;
		for (int i = 0; i < effects.Count; i++)
		{
			StatusEffectState state = effects[i];
			StatusEffectData data = state?.data;
			if (data == null)
			{
				continue;
			}
			StatusEffectInfoPanel panel = _statusScene.Instantiate<StatusEffectInfoPanel>();
			_statusContainer.AddChild(panel);
			// ContinuousArm effects (wet) — the meter IS the progress; bar
			// fills 0..1 with the intensity. ThresholdCross effects on items
			// (future timed enchants) — feed the remaining-time fraction.
			float progress;
			bool hasTimer;
			float buildup;
			if (data.buildupBehavior == EBuildupBehavior.ContinuousArm)
			{
				progress = Mathf.Clamp(item.statusEffects.GetBuildup(data), 0f, 1f);
				hasTimer = true;
				buildup = 0f;
			}
			else if (state.IsTimed)
			{
				ulong now = Sim.Current?.GameTimeMs ?? 0;
				double nowTod = Sim.Current?.TimeOfDayAbsolute ?? 0.0;
				progress = state.RemainingProgress(now, nowTod);
				hasTimer = true;
				buildup = 0f;
			}
			else
			{
				progress = 0f;
				hasTimer = false;
				buildup = 0f;
			}
			panel.SetStatusEffect(data, 1, progress, hasTimer, buildup);
		}
	}

	// Item-level stats live above the per-action panels. StatList picks
	// the appropriate generator per item type; each generator suppresses
	// neutral-value rows so a plain item reads as just its name + icon.
	private void RebuildItemStats(ItemState item, bool identified)
	{
		ClearStats();
		if (!identified)
		{
			return;
		}
		switch (item)
		{
			case WeaponState weapon:
				AddStats(StatList.Ammo(weapon));
				break;
			case ArmorState armor:
				AddStats(StatList.ArmorStats(armor));
				break;
			case ConsumableState consumable:
				AddStats(StatList.ConsumableStats(consumable));
				break;
		}
	}

	private void ClearStats()
	{
		if (_statContainer == null)
		{
			return;
		}
		foreach (Node child in _statContainer.GetChildren())
		{
			if (child is StatPanel)
			{
				child.QueueFree();
			}
		}
	}

	private void AddStats(IEnumerable<(string name, string value)> entries)
	{
		if (_statContainer == null || _statScene == null)
		{
			return;
		}
		foreach (var (name, value) in entries)
		{
			StatPanel stat = _statScene.Instantiate<StatPanel>();
			_statContainer.AddChild(stat);
			stat.SetText(name, value);
		}
	}

	// One ItemActionPanel per charged action. Cleared and rebuilt each
	// time the highlighted item changes; hidden entirely when the item
	// is unidentified (same reveal-on-use rule as the description text).
	private void RebuildActionPanels(ItemState item, bool identified)
	{
		if (_actionPanelContainer == null)
		{
			return;
		}
		foreach (Node child in _actionPanelContainer.GetChildren())
		{
			if (child is ItemActionPanel existing)
			{
				existing.QueueFree();
			}
		}
		if (!identified || _actionPanel == null)
		{
			return;
		}
		if (item is not WeaponState weapon)
		{
			return;
		}
		WeaponData data = weapon.data;
		ItemActionProfile profile = data?.actionProfile;
		if (profile?.chargedActions == null)
		{
			return;
		}
		for (int i = 0; i < profile.chargedActions.Count; i++)
		{
			ItemAction action = profile.chargedActions[i];
			if (action == null)
			{
				continue;
			}
			ItemActionPanel panel = _actionPanel.Instantiate<ItemActionPanel>();
			_actionPanelContainer.AddChild(panel);
			panel.SetAction(action, data, i, _showDetails);
		}
	}

}
