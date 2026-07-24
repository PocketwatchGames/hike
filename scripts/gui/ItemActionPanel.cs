using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class ItemActionPanel : PanelContainer
{
	[Export] private Label _nameLabel;
	[Export] private Label _descriptionLabel;
	[Export] private Control _actionContextContainer;
	[Export] private PackedScene _actionContextScene;
	[Export] private Control _statContainer;
	[Export] private PackedScene _statScene;

	// showDetails false lists just the attack name — the per-attack stat rows
	// and conditional-damage context panels are skipped for a compact readout.
	public void SetAction(ItemAction action, WeaponData weapon, int index, bool showDetails = true)
	{
		string title = action?.displayName.ToString();
		if (string.IsNullOrEmpty(title))
		{
			title = $"Action {index + 1}";
		}
		if (_nameLabel != null)
		{
			_nameLabel.Text = title;
		}
		if (_descriptionLabel != null)
		{
			_descriptionLabel.Text = string.Empty;
			_descriptionLabel.Visible = false;
		}

		ClearStats();
		ClearContextPanels();
		if (action == null || !showDetails)
		{
			return;
		}

		// Damage stats lead the row — that's what the player optimizes for.
		// AoE actions show DPS / radius / duration instead of direct damage,
		// since they spawn an area effect rather than dealing a hit.
		ItemEvent areaEvent = StatList.FindAreaEffectEvent(action);
		DamageData damage = null;
		if (areaEvent != null)
		{
			AddStats(StatList.AreaEffect(areaEvent, weapon));
			AddStats(StatList.TargetRange(action));
		}
		else
		{
			ItemEvent damageEvent = StatList.FindDamageEvent(action);
			damage = weapon?.GetDamage(damageEvent?.damageProfileKey ?? new StringName("primary"));
			AddStats(StatList.BaseDamage(damage));
			AddStats(StatList.Range(action, damageEvent));
		}
		AddStats(StatList.ActionCostsAndCooldown(action));

		// Conditional damage layers (Crit / Dizzy / Backstab) get their own
		// panels because their stats only apply when the trigger fires —
		// they can't share the row with the unconditional base.
		if (damage?.modifiers != null)
		{
			foreach (DamageDataModifier mod in damage.modifiers)
			{
				if (mod == null || mod.overrides == EDamageFields.None)
				{
					continue;
				}
				if (mod.trigger == EDamageTrigger.OnDizzy)
				{
					continue;
				}
				BuildModifierContext(mod);
			}
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

	private void ClearContextPanels()
	{
		if (_actionContextContainer == null)
		{
			return;
		}
		foreach (Node child in _actionContextContainer.GetChildren())
		{
			if (child is ItemActionContextPanel existing)
			{
				existing.QueueFree();
			}
		}
	}

	private void BuildModifierContext(DamageDataModifier mod)
	{
		ItemActionContextPanel.Populate(_actionContextScene, _actionContextContainer,
			GameClient.Current.damageTriggerLabels[mod.trigger], StatList.DamageModifier(mod));
	}
}
