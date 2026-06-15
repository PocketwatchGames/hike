using System.Collections.Generic;
using Godot;

// Stat-entry generators for inventory / tooltip / HUD readouts. Each
// method walks the relevant data shape and yields (name, value) tuples;
// the consumer decides what to render (StatPanel widget, tooltip line,
// debug log). Display names come from GameClient.statNames; per-value
// formatting goes through StatFormat. Centralizing here means a new UI
// surface only needs to iterate — it doesn't reimplement the rules for
// which fields to suppress, what unit suffix to use, etc.
public static class StatList
{
	// Direct-hit damage block: Damage / ArmorPenetration / Knockback, plus one entry
	// per inflicted status effect and one per buildup contribution.
	public static IEnumerable<(string name, string value)> BaseDamage(DamageData damage)
	{
		if (damage == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		if (damage.healthDamage > 0f)
		{
			yield return (names[EStatName.Damage], StatFormat.Number(damage.healthDamage));
		}
		if (damage.armorPenetration > 0f)
		{
			yield return (names[EStatName.ArmorPenetration], StatFormat.Percent(damage.armorPenetration));
		}
		if (damage.blunt > 0f)
		{
			yield return (names[EStatName.Blunt], StatFormat.Percent(damage.blunt));
		}
		// Knockback < 1m reads as flinch / shrug — not worth surfacing. The
		// threshold matches the design distinction between hitstun (always
		// applied) and "real" knockback that actually relocates the target.
		if (damage.knockbackDistance > 1f)
		{
			yield return (names[EStatName.Knockback], StatFormat.Number(damage.knockbackDistance));
		}
		foreach (var entry in StatusEffects(damage.statusEffects))
		{
			yield return entry;
		}
		foreach (var entry in Buildups(damage.buildups))
		{
			yield return entry;
		}
	}

	// AoE block. Sums continuous DPS + per-interval DPS (each entry's
	// healthDamage / tickInterval) into one rolled-up Dps row, then lists
	// per-entry status effects / buildups, then radius / duration. Continuous
	// damage already reads as DPS in its authoring (healthDamage per
	// second); interval damage is per-tick and gets divided.
	public static IEnumerable<(string name, string value)> AreaEffect(ItemEvent areaEvent, WeaponData weapon)
	{
		if (areaEvent == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		float totalDps = 0f;
		ContinuousDamageData continuous = weapon?.GetContinuousDamage(areaEvent.areaContinuousKey);
		if (continuous != null && continuous.healthDamage > 0f)
		{
			totalDps += continuous.healthDamage;
		}
		if (areaEvent.areaIntervals != null)
		{
			for (int i = 0; i < areaEvent.areaIntervals.Count; i++)
			{
				AreaIntervalSpec spec = areaEvent.areaIntervals[i];
				if (spec == null || spec.tickInterval <= 0f)
				{
					continue;
				}
				DamageData d = weapon?.GetDamage(spec.damageProfileKey);
				if (d == null || d.healthDamage <= 0f)
				{
					continue;
				}
				totalDps += d.healthDamage / spec.tickInterval;
			}
		}
		if (totalDps > 0f)
		{
			// Round to the nearest int — sub-decimal precision on a derived
			// "damage per second" reads as false precision.
			yield return (names[EStatName.Dps], StatFormat.Number(Mathf.Round(totalDps)));
		}
		// Per-interval status effects + buildups. Per-second buildup rates on
		// the continuous source read as a meter the player can't time, so
		// they're omitted — interval entries are the player-legible CC channel.
		if (areaEvent.areaIntervals != null)
		{
			for (int i = 0; i < areaEvent.areaIntervals.Count; i++)
			{
				AreaIntervalSpec spec = areaEvent.areaIntervals[i];
				if (spec == null)
				{
					continue;
				}
				DamageData d = weapon?.GetDamage(spec.damageProfileKey);
				if (d == null)
				{
					continue;
				}
				foreach (var entry in StatusEffects(d.statusEffects))
				{
					yield return entry;
				}
				foreach (var entry in Buildups(d.buildups))
				{
					yield return entry;
				}
			}
		}
		if (areaEvent.areaRadius > 0f)
		{
			yield return (names[EStatName.Radius], StatFormat.Meters(areaEvent.areaRadius));
		}
		if (areaEvent.areaDurationSeconds > 0f)
		{
			yield return (names[EStatName.Duration], StatFormat.Seconds(areaEvent.areaDurationSeconds));
		}
	}

	// Action-level costs + cooldown. Cooldown read by the player includes
	// the Active phase — the weapon is unusable for both windows back-to-
	// back, so showing only cooldownSeconds undercounts felt time between
	// presses.
	public static IEnumerable<(string name, string value)> ActionCostsAndCooldown(ItemAction action)
	{
		if (action == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		if (action.bloodCost > 0f)
		{
			yield return (names[EStatName.BloodCost], StatFormat.Number(action.bloodCost));
		}
		if (action.staminaCost > 0f)
		{
			yield return (names[EStatName.StaminaCost], StatFormat.Number(action.staminaCost));
		}
		float cooldown = action.cooldownSeconds + action.activeDurationSeconds;
		if (cooldown > 0f)
		{
			yield return (names[EStatName.Cooldown], StatFormat.Seconds(cooldown));
		}
	}

	// Reach / Range for the damage event. Range scales with how long the
	// tier was held — SampleRangeScale lerps base → base*chargedRangeScale
	// across the hold. Renders as a span ("8-16m") when the tier actually
	// ramps; melee Reach doesn't ramp in the current model.
	public static IEnumerable<(string name, string value)> Range(ItemAction action, ItemEvent damageEvent)
	{
		if (action == null || damageEvent == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		float scale = Mathf.Max(1f, action.chargedRangeScale);
		if ((damageEvent.type & EItemEventType.Melee) != 0)
		{
			if (damageEvent.range > 0f)
			{
				yield return (names[EStatName.Reach], StatFormat.Meters(damageEvent.range));
			}
		}
		else if ((damageEvent.type & EItemEventType.Hitscan) != 0)
		{
			if (damageEvent.hitScanRange > 0f)
			{
				yield return (names[EStatName.Range], StatFormat.MeterSpan(damageEvent.hitScanRange, scale));
			}
		}
		else if ((damageEvent.type & EItemEventType.Projectile) != 0)
		{
			float baseRange = damageEvent.projectileSpeed * damageEvent.projectileLifetimeSeconds;
			if (baseRange > 0f)
			{
				yield return (names[EStatName.Range], StatFormat.MeterSpan(baseRange, scale));
			}
		}
	}

	// AoE "where can you place it?" range. Distinct from damage-event
	// range (which is how far the hit reaches) — Target Range is how far
	// the player can pick the AoE's center.
	public static IEnumerable<(string name, string value)> TargetRange(ItemAction action)
	{
		if (action == null || action.positionalRange <= 0f)
		{
			yield break;
		}
		yield return (GameClient.Current.statNames[EStatName.TargetRange], StatFormat.Meters(action.positionalRange));
	}

	// One entry per buildup contribution — name = "<effect>",
	// value = amount as a fraction of the 1.0 apply threshold. Null entries
	// and zero/negative amounts are skipped.
	public static IEnumerable<(string name, string value)> Buildups(Godot.Collections.Array<StatusEffectBuildup> buildups)
	{
		if (buildups == null)
		{
			yield break;
		}
		foreach (StatusEffectBuildup entry in buildups)
		{
			if (entry == null || entry.effect == null || entry.amount <= 0f)
			{
				continue;
			}
			string effectName = entry.effect.displayName.ToString();
			if (string.IsNullOrEmpty(effectName))
			{
				effectName = entry.effect.ResourceName;
			}
			yield return (effectName, StatFormat.Number(entry.amount));
		}
	}

	// One entry per status effect — name = effect display name, value =
	// authored duration (or empty if 0, so the StatPanel value side hides).
	public static IEnumerable<(string name, string value)> StatusEffects(Godot.Collections.Array<StatusEffectData> effects)
	{
		if (effects == null)
		{
			yield break;
		}
		foreach (StatusEffectData effect in effects)
		{
			if (effect == null)
			{
				continue;
			}
			string name = effect.displayName.ToString();
			if (string.IsNullOrEmpty(name))
			{
				name = effect.ResourceName;
			}
			string value = effect.duration > 0f ? StatFormat.Seconds(effect.duration) : string.Empty;
			yield return (name, value);
		}
	}

	// Conditional damage layer (On Crit / On Dizzy / On Backstab). Only the
	// fields the modifier's `overrides` mask selects are emitted; the rest
	// flow through from the base damage at runtime.
	public static IEnumerable<(string name, string value)> DamageModifier(DamageDataModifier mod)
	{
		if (mod == null || mod.overrides == EDamageFields.None)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		if ((mod.overrides & EDamageFields.HealthDamage) != 0 && mod.healthDamage > 0f)
		{
			yield return (names[EStatName.Damage], StatFormat.Number(mod.healthDamage));
		}
		if ((mod.overrides & EDamageFields.ArmorPenetration) != 0 && mod.armorPenetration > 0f)
		{
			yield return (names[EStatName.ArmorPenetration], StatFormat.Percent(mod.armorPenetration));
		}
		if ((mod.overrides & EDamageFields.Blunt) != 0 && mod.blunt > 0f)
		{
			yield return (names[EStatName.Blunt], StatFormat.Percent(mod.blunt));
		}
		if ((mod.overrides & EDamageFields.KnockbackDistance) != 0 && mod.knockbackDistance > 1f)
		{
			yield return (names[EStatName.Knockback], StatFormat.Number(mod.knockbackDistance));
		}
		if ((mod.overrides & EDamageFields.AddStatusEffects) != 0)
		{
			foreach (var entry in StatusEffects(mod.addStatusEffects))
			{
				yield return entry;
			}
		}
	}

	// Full readout for one StatusEffectData — the dials it actually moves.
	// Lifecycle / payload fields (duration, dps) render with bespoke
	// formatting; everything else (move speed, sense modifiers, temperature
	// thresholds, type-tag damage scales) lives on `modifiers` and folds
	// through the generalized renderer below.
	public static IEnumerable<(string name, string value)> StatusEffectInfo(StatusEffectData effect)
	{
		if (effect == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		if (effect.duration > 0f)
		{
			yield return (names[EStatName.Duration], StatFormat.Seconds(effect.duration));
		}
		float dps = effect.dot?.damagePerSecond ?? 0f;
		if (dps > 0f)
		{
			yield return (names[EStatName.Dps], StatFormat.Number(dps));
		}
		else if (dps < 0f)
		{
			// Healing is authored as negative damagePerSecond — flip the sign
			// so the player sees a positive heal-rate number under the Healing
			// label rather than a "Damage: -3".
			yield return (names[EStatName.Heal], StatFormat.Number(-dps));
		}
		foreach (var entry in Modifiers(effect.modifiers))
		{
			yield return entry;
		}
	}

	// Map an EStat (modifier-target / hit-tag value) onto the matching UI
	// label key. Values that don't have a UI label return EStatName.Damage
	// as a fallback — currently every EStat value has a mirrored EStatName
	// since EStatName was extended in lockstep, so the fallback is dead
	// code, but the explicit default keeps the switch exhaustive-safe
	// against future EStat additions before their labels land.
	private static EStatName ToStatName(EStat stat)
	{
		return stat switch
		{
			EStat.Damage => EStatName.Damage,
			EStat.Fire => EStatName.Fire,
			EStat.Blunt => EStatName.Blunt,
			EStat.Dizzy => EStatName.Dizzy,
			EStat.ArmorPenetration => EStatName.ArmorPenetration,
			EStat.Electrical => EStatName.Electrical,
			EStat.Ranged => EStatName.Ranged,
			EStat.Melee => EStatName.Melee,
			EStat.Poison => EStatName.Poison,
			EStat.Magical => EStatName.Magical,
			EStat.Knockback => EStatName.Knockback,
			EStat.OutgoingDamage => EStatName.OutgoingDamage,
			EStat.MoveSpeed => EStatName.MoveSpeed,
			EStat.AnimSpeed => EStatName.AnimSpeed,
			EStat.Vision => EStatName.Vision,
			EStat.NightVision => EStatName.NightVision,
			EStat.Hearing => EStatName.Hearing,
			EStat.Noise => EStatName.Noise,
			EStat.Scent => EStatName.Scent,
			EStat.FootprintAlpha => EStatName.FootprintAlpha,
			EStat.FootprintDuration => EStatName.FootprintDuration,
			EStat.Camouflage => EStatName.Camouflage,
			EStat.MaxStamina => EStatName.MaxStamina,
			EStat.MaxArmor => EStatName.Armor,
			EStat.MaxHealth => EStatName.Health,
			EStat.ColdResist => EStatName.ColdResist,
			EStat.HeatResist => EStatName.HeatResist,
			_ => EStatName.Damage,
		};
	}

	// Walk a StatModifier list and yield (label, formatted-value) tuples for
	// every entry off its stat's neutral identity. Multiplicative stats
	// render as scale deltas (e.g. "MoveSpeed: -25%"); additive stats render
	// as signed offsets (e.g. "Camouflage: +5"). Used by StatusEffectInfo
	// and ArmorStats so both shapes get a uniform readout.
	public static IEnumerable<(string name, string value)> Modifiers(Godot.Collections.Array<StatModifier> modifiers)
	{
		if (modifiers == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		for (int i = 0; i < modifiers.Count; i++)
		{
			StatModifier m = modifiers[i];
			if (m == null || m.stat == EStat.None)
			{
				continue;
			}
			bool additive = StatModifierUtil.IsAdditive(m.stat);
			if (additive)
			{
				if (m.value == 0f)
				{
					continue;
				}
				yield return (names[ToStatName(m.stat)], StatFormat.SignedNumber(m.value));
			}
			else
			{
				if (Mathf.IsEqualApprox(m.value, 1f))
				{
					continue;
				}
				yield return (names[ToStatName(m.stat)], StatFormat.ScaleDelta(m.value));
			}
		}
	}

	// Composite player readout for the inventory's stats panel. Core dials
	// (Health / Armor / Stamina / Speed) always render so the player can
	// confirm their character sheet at a glance. Sense stats render only
	// when armor or an active status effect moves them off the PlayerData
	// base. Resistances render only when their summed total is non-zero.
	public static IEnumerable<(string name, string value)> PlayerStats(Player player)
	{
		if (player == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;

		yield return (names[EStatName.Health], StatFormat.Number(player.MaxHealth));
		yield return (names[EStatName.MaxStamina], StatFormat.Number(player.MaxStamina));
		yield return (names[EStatName.Armor], StatFormat.Number(player.MaxArmor));
		float speed = player.SpeedMultiplier;
		if (!Mathf.IsEqualApprox(speed, 1f))
		{
			yield return (names[EStatName.MoveSpeed], StatFormat.ScaleDelta(speed));
		}

		player.GetSenseStats(out float camouflage, out float visionMultiplier, out float hearingMultiplier, out float noiseMultiplier, out float scentMultiplier);
		if (camouflage != 0f)
		{
			yield return (names[EStatName.Camouflage], StatFormat.SignedNumber(camouflage));
		}
		// Sense modifiers render as signed deltas off neutral 1.0 — the
		// player reads "-25%" as "I'm 25% quieter" without needing to know
		// the authored base value. Rows are suppressed at neutral so a
		// player with no modifier-bearing gear sees no sense rows at all.
		if (!Mathf.IsEqualApprox(visionMultiplier, 1f))
		{
			yield return (names[EStatName.Vision], StatFormat.ScaleDelta(visionMultiplier));
		}
		if (!Mathf.IsEqualApprox(hearingMultiplier, 1f))
		{
			yield return (names[EStatName.Hearing], StatFormat.ScaleDelta(hearingMultiplier));
		}
		if (!Mathf.IsEqualApprox(noiseMultiplier, 1f))
		{
			yield return (names[EStatName.Noise], StatFormat.ScaleDelta(noiseMultiplier));
		}
		if (!Mathf.IsEqualApprox(scentMultiplier, 1f))
		{
			yield return (names[EStatName.Scent], StatFormat.ScaleDelta(scentMultiplier));
		}

		player.GetThermalResistances(out float cold, out float heat);
		if (cold != 0f)
		{
			yield return (names[EStatName.ColdResist], StatFormat.SignedNumber(cold));
		}
		if (heat != 0f)
		{
			yield return (names[EStatName.HeatResist], StatFormat.SignedNumber(heat));
		}
	}

	// Per-armor readout: max armor capacity + every modifier the piece
	// authors. Resistances and sense modifiers are suppressed at their
	// neutral values (0 additive, 1 multiplicative) so a plain piece of
	// armor reads as just "Armor: N". Vision / hearing / noise / scent are
	// shown as percent-of-base scalars rather than effective absolute
	// values — the player can't know the base without looking at the panel
	// stats screen, so "Noise: 50%" reads cleaner as "this piece halves my
	// noise" than a raw decibels number.
	public static IEnumerable<(string name, string value)> ArmorStats(ArmorState armor)
	{
		ArmorData data = armor?.data;
		if (data == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		if (data.maxArmor > 0f)
		{
			yield return (names[EStatName.Armor], StatFormat.Number(data.maxArmor));
		}
		foreach (var entry in Modifiers(data.modifiers))
		{
			yield return entry;
		}
	}

	// Per-consumable readout: walks the consumable's action profile, finds
	// every ApplyStatusEffect event, and yields what the use will do —
	// heals (HealEffect → "Healing: N"), inflicted status effects (one
	// StatusEffectInfoPanel-style row per StatusEffectData with the
	// authored duration as the value). Unknown ItemEffect subclasses are
	// silently skipped; the panel surfaces meaningful outcomes only.
	public static IEnumerable<(string name, string value)> ConsumableStats(ConsumableState consumable)
	{
		ItemActionProfile profile = consumable?.data?.actionProfile;
		if (profile?.chargedActions == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		foreach (ItemAction action in profile.chargedActions)
		{
			if (action?.events == null)
			{
				continue;
			}
			foreach (ItemEvent ev in action.events)
			{
				if (ev?.effects == null)
				{
					continue;
				}
				foreach (ItemEffect effect in ev.effects)
				{
					if (effect is HealEffect heal && heal.amount > 0f)
					{
						yield return (names[EStatName.Heal], StatFormat.Number(heal.amount));
					}
					else if (effect is ApplyStatusEffect apply && apply.statusEffect != null)
					{
						StatusEffectData sed = apply.statusEffect;
						string effectName = sed.displayName.ToString();
						if (string.IsNullOrEmpty(effectName))
						{
							effectName = sed.ResourceName;
						}
						string value = sed.duration > 0f ? StatFormat.Seconds(sed.duration) : string.Empty;
						yield return (effectName, value);
					}
				}
			}
		}
	}

	// Item-level ammo readout. Labeled "Ammo" when shots are recoverable
	// (arrowLootData wired, like the bow's arrows) and "Charges" when
	// they're consumed permanently (wands, single-use throwables).
	public static IEnumerable<(string name, string value)> Ammo(WeaponState weapon)
	{
		WeaponData data = weapon?.data;
		if (data == null || data.maxAmmo <= 0)
		{
			yield break;
		}
		EStatName key = data.arrowLootData != null ? EStatName.Ammo : EStatName.Charges;
		yield return (GameClient.Current.statNames[key], weapon.ammo + " / " + data.maxAmmo);
	}
}
