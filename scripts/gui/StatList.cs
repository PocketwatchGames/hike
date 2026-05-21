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
	// Direct-hit damage block: Damage / Pierce / Stun / Knockback, plus
	// one entry per inflicted status effect.
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
		if (damage.pierce > 0f)
		{
			yield return (names[EStatName.Pierce], StatFormat.Percent(damage.pierce));
		}
		if (damage.stun > 0f)
		{
			yield return (names[EStatName.Stun], StatFormat.Number(damage.stun));
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
	}

	// AoE block. DPS = healthDamage / tickInterval — the rate a target
	// standing in the zone takes damage. Total expected damage for a
	// full-duration stay = DPS * duration.
	public static IEnumerable<(string name, string value)> AreaEffect(ItemEvent areaEvent, DamageData damage)
	{
		if (areaEvent == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		float tick = areaEvent.areaTickInterval;
		if (damage != null && tick > 0f && damage.healthDamage > 0f)
		{
			// Round to the nearest int — sub-decimal precision on a derived
			// "damage per second" reads as false precision (the underlying
			// damage is integer-valued and tick cadence is authored to one
			// decimal place).
			float dps = Mathf.Round(damage.healthDamage / tick);
			yield return (names[EStatName.Dps], StatFormat.Number(dps));
		}
		else if (damage != null && damage.healthDamage > 0f)
		{
			yield return (names[EStatName.Damage], StatFormat.Number(damage.healthDamage));
		}
		if (damage != null && damage.stun > 0f)
		{
			yield return (names[EStatName.Stun], StatFormat.Number(damage.stun));
		}
		if (damage != null)
		{
			foreach (var entry in StatusEffects(damage.statusEffects))
			{
				yield return entry;
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
			if (damageEvent.meleeRange > 0f)
			{
				yield return (names[EStatName.Reach], StatFormat.Meters(damageEvent.meleeRange));
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

	// Conditional damage layer (On Crit / On Stun / On Backstab). Only the
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
		if ((mod.overrides & EDamageFields.Pierce) != 0 && mod.pierce > 0f)
		{
			yield return (names[EStatName.Pierce], StatFormat.Percent(mod.pierce));
		}
		if ((mod.overrides & EDamageFields.Stun) != 0 && mod.stun > 0f)
		{
			yield return (names[EStatName.Stun], StatFormat.Number(mod.stun));
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
	// Fields at their neutral values (multiplier == 1, bonus == 0) are
	// suppressed so each effect surfaces only its meaningful stats.
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
		if (effect.damagePerSecond > 0f)
		{
			yield return (names[EStatName.Dps], StatFormat.Number(effect.damagePerSecond));
		}
		else if (effect.damagePerSecond < 0f)
		{
			// Healing is authored as negative damagePerSecond — flip the sign
			// so the player sees a positive heal-rate number under the Healing
			// label rather than a "Damage: -3".
			yield return (names[EStatName.Heal], StatFormat.Number(-effect.damagePerSecond));
		}
		if (!Mathf.IsEqualApprox(effect.movementMultiplier, 1f))
		{
			yield return (names[EStatName.MoveSpeed], StatFormat.Scale(effect.movementMultiplier));
		}
		if (effect.maxStaminaBonus != 0f)
		{
			yield return (names[EStatName.MaxStamina], StatFormat.Number(effect.maxStaminaBonus));
		}
		if (effect.coldResistance != 0f)
		{
			yield return (names[EStatName.ColdResist], StatFormat.Number(effect.coldResistance));
		}
		if (effect.heatResistance != 0f)
		{
			yield return (names[EStatName.HeatResist], StatFormat.Number(effect.heatResistance));
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
