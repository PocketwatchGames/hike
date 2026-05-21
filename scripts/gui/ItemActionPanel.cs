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

	public void SetAction(ItemAction action, WeaponData weapon, int index)
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
		if (action == null)
		{
			return;
		}

		// Damage stats lead the row — that's what the player optimizes for.
		// AoE actions show DPS / radius / duration instead of direct damage,
		// since they spawn an area effect rather than dealing a hit.
		ItemEvent areaEvent = FindAreaEffectEvent(action);
		DamageData damage;
		if (areaEvent != null)
		{
			damage = weapon?.GetDamage(areaEvent.damageProfileKey);
			AddStats(StatList.AreaEffect(areaEvent, damage));
			AddStats(StatList.TargetRange(action));
		}
		else
		{
			ItemEvent damageEvent = FindDamageEvent(action);
			damage = weapon?.GetDamage(damageEvent?.damageProfileKey ?? new StringName("primary"));
			AddStats(StatList.BaseDamage(damage));
			AddStats(StatList.Range(action, damageEvent));
		}
		AddStats(StatList.ActionCostsAndCooldown(action));

		// Conditional damage layers (Crit / Stun / Backstab) get their own
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
		if (_actionContextScene == null || _actionContextContainer == null)
		{
			return;
		}
		ItemActionContextPanel panel = _actionContextScene.Instantiate<ItemActionContextPanel>();
		_actionContextContainer.AddChild(panel);
		panel.SetHeader(GameClient.Current.damageTriggerLabels[mod.trigger]);
		panel.ClearStats();
		foreach (var (name, value) in StatList.DamageModifier(mod))
		{
			panel.AddStat(name, value);
		}
	}

	// Walks the action's events and returns the first one that actually
	// deals damage (Melee, Hitscan, or Projectile). That event's authored
	// damage override (if any) wins over the weapon-level default — same
	// resolution order the runtime uses in ItemEventHandlers.
	private static ItemEvent FindDamageEvent(ItemAction action)
	{
		if (action?.events == null)
		{
			return null;
		}
		const EItemEventType damageFlags = EItemEventType.Melee | EItemEventType.Hitscan | EItemEventType.Projectile;
		foreach (ItemEvent ev in action.events)
		{
			if (ev == null)
			{
				continue;
			}
			if ((ev.type & damageFlags) != 0)
			{
				return ev;
			}
		}
		return null;
	}

	// Walks the action's events (and any projectile impactEvent chained off
	// them) looking for a SpawnAreaEffect entry. Used to switch the action
	// stat row into AoE mode — Rain of Arrows reads its DPS / radius /
	// duration from the impactEvent on its arcing projectile.
	private static ItemEvent FindAreaEffectEvent(ItemAction action)
	{
		if (action?.events == null)
		{
			return null;
		}
		foreach (ItemEvent ev in action.events)
		{
			if (ev == null)
			{
				continue;
			}
			if ((ev.type & EItemEventType.SpawnAreaEffect) != 0)
			{
				return ev;
			}
			if (ev.impactEvent != null && (ev.impactEvent.type & EItemEventType.SpawnAreaEffect) != 0)
			{
				return ev.impactEvent;
			}
		}
		return null;
	}
}
