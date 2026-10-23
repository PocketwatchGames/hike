using System.Collections.Generic;
using Godot;

// A cookpot meal's readout: name, icon, description, how filling it is and the
// status effects it imparts. The CookpotScreen shows it over the Eat button; the
// CookingScreen shows it, with the recipe's ingredients, over a meal recipe.
[GlobalClass]
public partial class MealInfoPanel : PanelContainer
{
	[Export] private Label _nameLabel;
	[Export] private Label _descriptionLabel;
	[Export] private TextureRect _icon;
	[Export] private Label _satiationLabel;
	[Export] private Control _statusContainer;
	[Export] private PackedScene _statusScene;
	// The ingredients row; hidden when no reagents are given.
	[Export] private Control _reagentsRoot;
	[Export] private Control _reagentSlotContainer;
	[Export] private PackedScene _reagentSlotScene;

	// Null hides the panel.
	public void SetMeal(MealData meal, IReadOnlyList<MaterialCount> reagents = null)
	{
		Visible = meal != null;
		if (meal == null)
		{
			return;
		}
		if (_nameLabel != null)
		{
			_nameLabel.Text = meal.displayName.ToString();
		}
		if (_descriptionLabel != null)
		{
			_descriptionLabel.Text = meal.description ?? string.Empty;
			_descriptionLabel.Visible = !string.IsNullOrEmpty(meal.description);
		}
		if (_icon != null)
		{
			_icon.Texture = meal.icon;
		}
		if (_satiationLabel != null)
		{
			_satiationLabel.Text = Loc.Get(Loc.Keys.meal_fills);
		}
		RebuildStatusEffects(meal);
		RebuildReagents(reagents);
	}

	void RebuildStatusEffects(MealData meal)
	{
		if (_statusContainer == null)
		{
			return;
		}
		foreach (Node child in _statusContainer.GetChildren())
		{
			child.QueueFree();
		}
		if (_statusScene == null || meal.effects == null)
		{
			return;
		}
		int count = meal.effects.Count;
		for (int i = 0; i < count; i++)
		{
			if (meal.effects[i] is not ApplyStatusEffect { statusEffect: StatusEffectData effect })
			{
				continue;
			}
			StatusEffectInfoPanel panel = _statusScene.Instantiate<StatusEffectInfoPanel>();
			_statusContainer.AddChild(panel);
			panel.SetStatusEffect(effect);
		}
	}

	void RebuildReagents(IReadOnlyList<MaterialCount> reagents)
	{
		bool any = reagents != null && reagents.Count > 0;
		if (_reagentsRoot != null)
		{
			_reagentsRoot.Visible = any;
		}
		if (_reagentSlotContainer == null)
		{
			return;
		}
		foreach (Node child in _reagentSlotContainer.GetChildren())
		{
			child.QueueFree();
		}
		if (!any || _reagentSlotScene == null)
		{
			return;
		}
		for (int i = 0; i < reagents.Count; i++)
		{
			MaterialCount row = reagents[i];
			if (row?.item == null || row.count <= 0)
			{
				continue;
			}
			ItemState state = row.item.CreateState();
			state.SetCount(row.count);
			ItemSlotPanel slot = _reagentSlotScene.Instantiate<ItemSlotPanel>();
			_reagentSlotContainer.AddChild(slot);
			// A readout, not a slot the cursor should land on.
			slot.SetFocusable(false);
			slot.SetItem(state);
		}
	}
}
