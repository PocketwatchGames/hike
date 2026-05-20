using System.Text;
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
	[Export] private ProgressBar _levelProgress;
	[Export] private Label _levelLabel;

	public void SetItem(ItemState item)
	{
		ItemData data = item?.data;
		if (data == null)
		{
			Visible = false;
			return;
		}
		WorldSimState worldSim = World.Current?.WorldState?.SimState;
		bool identified = worldSim == null || worldSim.IsItemIdentified(data);
		if (_nameLabel != null)
		{
			_nameLabel.Text = worldSim != null
				? worldSim.GetItemDisplayName(data)
				: data.displayName.ToString();
		}
		if (_descriptionLabel != null)
		{
			// Hide flavor text AND derived stat readouts while the item is
			// unidentified — revealing the recipe of a "?" potion or the
			// damage table of a "?" weapon via the info panel would defeat
			// the reveal-on-use design.
			_descriptionLabel.Text = identified ? BuildDescriptionText(item) : string.Empty;
		}
		if (_icon != null)
		{
			_icon.Texture = data.inventorySprite;
		}
		UpdateLevelDisplay(item);
		Visible = true;
	}

	private static string BuildDescriptionText(ItemState item)
	{
		ItemData data = item?.data;
		if (data == null)
		{
			return string.Empty;
		}
		var sb = new StringBuilder();
		string flavor = data.description ?? string.Empty;
		if (flavor.Length > 0)
		{
			sb.Append(flavor);
		}
		if (item is WeaponState weapon)
		{
			AppendWeaponActions(sb, weapon);
		}
		return sb.ToString();
	}

	private static void AppendWeaponActions(StringBuilder sb, WeaponState weapon)
	{
		WeaponData data = weapon.data;
		AppendWeaponStats(sb, weapon, data);
		ItemActionProfile profile = data?.actionProfile;
		if (profile?.chargedActions == null || profile.chargedActions.Count == 0)
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
			if (sb.Length > 0)
			{
				sb.Append("\n\n");
			}
			AppendActionBlock(sb, action, data, i);
		}
	}

	// Weapon-level stats that aren't per-action. Currently just the ammo
	// counter, labeled "Ammo" when shots are recoverable (arrowLootData wired,
	// like the bow's arrows) and "Charges" when they're consumed permanently
	// (wands, single-use throwables). maxAmmo == 0 means the weapon has no
	// ammo concept at all, so the line is suppressed.
	private static void AppendWeaponStats(StringBuilder sb, WeaponState weapon, WeaponData data)
	{
		if (data == null || data.maxAmmo <= 0)
		{
			return;
		}
		if (sb.Length > 0)
		{
			sb.Append("\n\n");
		}
		string label = data.arrowLootData != null ? "Ammo" : "Charges";
		sb.Append(label).Append(": ").Append(weapon.ammo).Append(" / ").Append(data.maxAmmo);
	}

	private static void AppendActionBlock(StringBuilder sb, ItemAction action, WeaponData weapon, int index)
	{
		string title = action.displayName.ToString();
		if (string.IsNullOrEmpty(title))
		{
			title = $"Action {index + 1}";
		}
		sb.Append(title);

		// Costs first — what the player pays to fire this. Blood reads as
		// a heavier sacrifice than stamina (HP vs regenerating gauge), so
		// it leads. Both suppressed when 0 so common free-to-fire actions
		// stay clean.
		AppendStat(sb, "Blood Cost", action.bloodCost);
		AppendStat(sb, "Stamina Cost", action.staminaCost);

		ItemEvent areaEvent = FindAreaEffectEvent(action);
		if (areaEvent != null)
		{
			AppendAreaStats(sb, areaEvent);
			AppendStatMeters(sb, "Target Range", action.positionalRange);
		}
		else
		{
			ItemEvent damageEvent = FindDamageEvent(action);
			DamageData damage = damageEvent?.damageData ?? weapon?.damageData;
			if (damage != null)
			{
				AppendStat(sb, "Damage", damage.healthDamage);
				AppendStatPercent(sb, "Pierce", damage.pierce);
				AppendStat(sb, "Stun", damage.stun);
				// Knockback < 1m reads as flinch / shrug — not worth a line
				// of inventory text. The threshold matches the design
				// distinction between hitstun (always applied) and "real"
				// knockback that actually relocates the target.
				if (damage.knockbackDistance > 1f)
				{
					AppendStat(sb, "Knockback", damage.knockbackDistance);
				}
				AppendStatusEffects(sb, damage.statusEffects);
				AppendDamageModifiers(sb, damage);
			}
			AppendRange(sb, action, damageEvent);
		}
		// Cooldown read by the player includes the Active phase — the
		// weapon is unusable for both windows back-to-back, so showing only
		// cooldownSeconds undercounts the felt time between presses.
		AppendStatSeconds(sb, "Cooldown", action.cooldownSeconds + action.activeDurationSeconds);
	}

	// Renders conditional damage layers as indented sub-blocks under the base
	// damage stats. Each modifier prints one header line per trigger ("On
	// Crit:" / "On Stun:") followed by the field overrides selected by its
	// `overrides` mask — same hide rules as the base block (no hitstun, no
	// knockback ≤ 1) so a modifier that only flips hitstun renders as a
	// header with no body, which we then suppress entirely.
	private static void AppendDamageModifiers(StringBuilder sb, DamageData damage)
	{
		if (damage?.modifiers == null)
		{
			return;
		}
		foreach (DamageDataModifier mod in damage.modifiers)
		{
			if (mod == null || mod.overrides == EDamageFields.None)
			{
				continue;
			}
			int before = sb.Length;
			sb.Append('\n').Append("  ").Append(TriggerLabel(mod.trigger)).Append(':');
			int headerEnd = sb.Length;
			if ((mod.overrides & EDamageFields.HealthDamage) != 0)
			{
				AppendModStat(sb, "Damage", mod.healthDamage);
			}
			if ((mod.overrides & EDamageFields.Pierce) != 0)
			{
				AppendModStatPercent(sb, "Pierce", mod.pierce);
			}
			if ((mod.overrides & EDamageFields.Stun) != 0)
			{
				AppendModStat(sb, "Stun", mod.stun);
			}
			if ((mod.overrides & EDamageFields.KnockbackDistance) != 0 && mod.knockbackDistance > 1f)
			{
				AppendModStat(sb, "Knockback", mod.knockbackDistance);
			}
			if ((mod.overrides & EDamageFields.AddStatusEffects) != 0)
			{
				AppendModStatusEffects(sb, mod.addStatusEffects);
			}
			// If only suppressed fields were authored (hitstun, knockback ≤ 1,
			// KnockbackTime), the header would dangle empty — roll back.
			if (sb.Length == headerEnd)
			{
				sb.Length = before;
			}
		}
	}

	private static string TriggerLabel(EDamageTrigger trigger)
	{
		return trigger switch
		{
			EDamageTrigger.OnCrit => "On Crit",
			EDamageTrigger.OnStun => "On Stun",
			EDamageTrigger.OnBackstab => "On Backstab",
			_ => trigger.ToString(),
		};
	}

	// Modifier sub-block uses 4-space indent so it nests under the action's
	// 2-space base stats. AppendStat-family helpers always use 2 spaces, so
	// the modifier stats need their own emitters.
	private static void AppendModStat(StringBuilder sb, string label, float value)
	{
		if (value <= 0f) { return; }
		sb.Append('\n').Append("    ").Append(label).Append(": ").Append(FormatNumber(value));
	}

	private static void AppendModStatPercent(StringBuilder sb, string label, float fraction)
	{
		if (fraction <= 0f) { return; }
		int pct = Mathf.Clamp(Mathf.RoundToInt(fraction * 100f), 0, 100);
		if (pct <= 0) { return; }
		sb.Append('\n').Append("    ").Append(label).Append(": ").Append(pct).Append('%');
	}

	private static void AppendModStatusEffects(StringBuilder sb, Godot.Collections.Array<StatusEffectData> effects)
	{
		if (effects == null || effects.Count == 0) { return; }
		sb.Append('\n').Append("    Also Inflicts: ");
		bool first = true;
		foreach (StatusEffectData effect in effects)
		{
			if (effect == null) { continue; }
			if (!first) { sb.Append(", "); }
			string name = effect.displayName.ToString();
			sb.Append(string.IsNullOrEmpty(name) ? effect.ResourceName : name);
			first = false;
		}
	}

	// AoE-specific stat block. DPS = healthDamage / tickInterval — the rate
	// at which a target standing in the zone takes damage. Total expected
	// damage for a full-duration stay = DPS * duration.
	private static void AppendAreaStats(StringBuilder sb, ItemEvent areaEvent)
	{
		DamageData damage = areaEvent.areaDamage;
		float tick = areaEvent.areaTickInterval;
		if (damage != null && tick > 0f && damage.healthDamage > 0f)
		{
			// Round to the nearest int — sub-decimal precision on a derived
			// "damage per second" reads as false precision (the underlying
			// damage is integer-valued and the tick cadence is authored to
			// one decimal place).
			float dps = Mathf.Round(damage.healthDamage / tick);
			AppendStat(sb, "DPS", dps);
		}
		else if (damage != null)
		{
			AppendStat(sb, "Damage", damage.healthDamage);
		}
		if (damage != null)
		{
			AppendStat(sb, "Stun", damage.stun);
			AppendStatusEffects(sb, damage.statusEffects);
		}
		AppendStatMeters(sb, "AoE Radius", areaEvent.areaRadius);
		AppendStatSeconds(sb, "AoE Time", areaEvent.areaDurationSeconds);
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
	// them) looking for a SpawnAreaEffect entry. Used to switch the action's
	// stat block into AoE mode — Rain of Arrows reads its DPS / radius /
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

	private static void AppendRange(StringBuilder sb, ItemAction action, ItemEvent damageEvent)
	{
		if (damageEvent == null)
		{
			return;
		}
		// Range scales with how long the tier was held — SampleRangeScale
		// lerps base → base*chargedRangeScale across the hold. Show as a
		// span ("8-16m") when the tier actually ramps, single value
		// otherwise. Melee's "Reach" doesn't ramp in the current model so
		// it stays a single value.
		float scale = Mathf.Max(1f, action.chargedRangeScale);
		if ((damageEvent.type & EItemEventType.Melee) != 0)
		{
			AppendStatMeters(sb, "Reach", damageEvent.meleeRange);
		}
		else if ((damageEvent.type & EItemEventType.Hitscan) != 0)
		{
			AppendStatMeterSpan(sb, "Range", damageEvent.hitScanRange, scale);
		}
		else if ((damageEvent.type & EItemEventType.Projectile) != 0)
		{
			float baseRange = damageEvent.projectileSpeed * damageEvent.projectileLifetimeSeconds;
			AppendStatMeterSpan(sb, "Range", baseRange, scale);
		}
	}

	// Renders "Label: 8m" when scale <= 1 and "Label: 8-16m" when the tier
	// ramps range during charge. Skips emission entirely when base <= 0.
	private static void AppendStatMeterSpan(StringBuilder sb, string label, float baseValue, float scale)
	{
		if (baseValue <= 0f)
		{
			return;
		}
		if (scale <= 1f)
		{
			AppendStatMeters(sb, label, baseValue);
			return;
		}
		sb.Append('\n').Append("  ").Append(label).Append(": ")
			.Append(FormatNumber(baseValue)).Append('-')
			.Append(FormatNumber(baseValue * scale)).Append('m');
	}

	private static void AppendStat(StringBuilder sb, string label, float value)
	{
		if (value <= 0f)
		{
			return;
		}
		sb.Append('\n').Append("  ").Append(label).Append(": ").Append(FormatNumber(value));
	}

	// Renders a 0..1 fraction as a rounded whole-percent ("50%"). Suppressed
	// at 0 so the common pierce-less case stays clean; clamped to 100 in case
	// authoring drifts above 1.0.
	private static void AppendStatPercent(StringBuilder sb, string label, float fraction)
	{
		if (fraction <= 0f)
		{
			return;
		}
		int pct = Mathf.Clamp(Mathf.RoundToInt(fraction * 100f), 0, 100);
		if (pct <= 0)
		{
			return;
		}
		sb.Append('\n').Append("  ").Append(label).Append(": ").Append(pct).Append('%');
	}

	private static void AppendStatSeconds(StringBuilder sb, string label, float seconds)
	{
		if (seconds <= 0f)
		{
			return;
		}
		sb.Append('\n').Append("  ").Append(label).Append(": ").Append(FormatNumber(seconds)).Append('s');
	}

	private static void AppendStatMeters(StringBuilder sb, string label, float meters)
	{
		if (meters <= 0f)
		{
			return;
		}
		sb.Append('\n').Append("  ").Append(label).Append(": ").Append(FormatNumber(meters)).Append('m');
	}

	private static void AppendStatusEffects(StringBuilder sb, Godot.Collections.Array<StatusEffectData> effects)
	{
		if (effects == null || effects.Count == 0)
		{
			return;
		}
		sb.Append('\n').Append("  Inflicts: ");
		bool first = true;
		foreach (StatusEffectData effect in effects)
		{
			if (effect == null)
			{
				continue;
			}
			if (!first)
			{
				sb.Append(", ");
			}
			string name = effect.displayName.ToString();
			sb.Append(string.IsNullOrEmpty(name) ? effect.ResourceName : name);
			first = false;
		}
	}

	private static string FormatNumber(float value)
	{
		// Drop the trailing ".0" when the number is integral so a 10-damage
		// hit reads "10" rather than "10.0", but keep one decimal place for
		// authored sub-second timings (0.25, 0.7, etc.).
		if (Mathf.Abs(value - Mathf.Round(value)) < 0.001f)
		{
			return Mathf.RoundToInt(value).ToString();
		}
		return value.ToString("0.##");
	}

	private void UpdateLevelDisplay(ItemState item)
	{
		int maxLevel = item.data?.maxLevel ?? 0;
		bool levels = maxLevel > 0;
		if (_levelProgress != null)
		{
			_levelProgress.Visible = levels;
		}
		if (_levelLabel != null)
		{
			_levelLabel.Visible = levels;
		}
		if (!levels)
		{
			return;
		}

		int level;
		int exp;
		switch (item)
		{
			case WeaponState w:
				level = w.level;
				exp = w.exp;
				break;
			case ArmorState a:
				level = a.level;
				exp = a.exp;
				break;
			default:
				level = 0;
				exp = 0;
				break;
		}

		if (_levelLabel != null)
		{
			_levelLabel.Text = (level + 1).ToString();
		}
		if (_levelProgress != null)
		{
			var thresholds = World.Current?.SimData?.ExpPerLevel;
			int cap = thresholds != null ? System.Math.Min(maxLevel, thresholds.Count) : 0;
			float ratio;
			if (thresholds == null || level >= cap)
			{
				ratio = 1f;
			}
			else
			{
				int prev = level > 0 ? thresholds[level - 1] : 0;
				int next = thresholds[level];
				int span = next - prev;
				ratio = span > 0 ? Mathf.Clamp((exp - prev) / (float)span, 0f, 1f) : 1f;
			}
			_levelProgress.MinValue = 0;
			_levelProgress.MaxValue = 1;
			_levelProgress.Value = ratio;
		}
	}
}
