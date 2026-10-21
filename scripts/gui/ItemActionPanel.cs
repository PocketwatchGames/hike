using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class ItemActionPanel : PanelContainer
{
	[Export] private Label _nameLabel;
	// Cost chips on the title line, each hidden when the action doesn't cost it.
	[Export] private Control _attackTimeCost;
	[Export] private Label _attackTimeLabel;
	[Export] private Control _staminaCost;
	[Export] private Label _staminaLabel;
	[Export] private Control _oilCost;
	[Export] private Label _oilLabel;
	[Export] private Label _descriptionLabel;
	// Holds the stat rows and, after them, the conditional-damage context panels.
	[Export] private Control _statContainer;
	[Export] private PackedScene _actionContextScene;
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
		bool details = action != null && showDetails;
		SetCost(_attackTimeCost, _attackTimeLabel,
			details ? StatList.AttackTime(weapon?.actionProfile, index, action) : 0f, StatFormat.Seconds);
		SetCost(_staminaCost, _staminaLabel, details ? action.staminaCost : 0f, StatFormat.Number);
		SetCost(_oilCost, _oilLabel, details ? action.oilCost : 0f, StatFormat.Number);
		if (!details)
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
			AddStats(StatList.BaseDamage(damage, StatList.CountHits(action, damageEvent?.damageProfileKey)));
			AddStats(StatList.Range(action, damageEvent));
			AddStats(StatList.Lunge(action));
		}
		AddStats(StatList.BloodCost(action));

		// A weapon's own extras on a trigger (the knife's backstab) are titled
		// panels in the same flow — their stats only apply when it fires. The
		// standard crit / backstab bonus every weapon gets has no panel of its own.
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
				BuildModifierContext(mod, damage);
			}
		}
	}

	private static void SetCost(Control chip, Label label, float value, System.Func<float, string> format)
	{
		if (chip == null)
		{
			return;
		}
		chip.Visible = value > 0f;
		if (label != null && value > 0f)
		{
			label.Text = format(value);
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
			if (child is StatPanel || child is ItemActionContextPanel)
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

	private void BuildModifierContext(DamageDataModifier mod, DamageData damage)
	{
		ItemActionContextPanel.Populate(_actionContextScene, _statContainer,
			GameClient.Current.damageTriggerLabels[mod.trigger],
			StatList.DamageModifier(mod, StatList.StandardMultiplier(mod.trigger, damage)));
	}
}
