using System.Collections.Generic;
using System.Linq;
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
	// Knockback below this reads as a flinch, not a shove that relocates the
	// target, so it gets no stat row.
	const float MinShownKnockback = 3f;
	// A tier's forward motion below this is a step into the swing, not a lunge.
	const float MinShownLunge = 3f;

	// Direct-hit damage block: Damage / ArmorPenetration / Knockback, plus one entry
	// per inflicted status effect and one per buildup contribution.
	// `hits` is how many times one use of the attack lands this profile
	// (CountHits, at least 1): damage and buildups read as the attack's total; chances and
	// per-hit effects (crit, armor pen, knockback) stay per hit.
	// `action` is the tier that lands it: a charge ramp on damage or armor pen
	// renders as a span ("3-6", "50-100%") the way Range does.
	public static IEnumerable<(string name, string value)> BaseDamage(DamageData damage, int hits = 1, ItemAction action = null)
	{
		if (damage == null)
		{
			yield break;
		}
		hits = Mathf.Max(1, hits);
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		if (damage.healthDamage > 0f)
		{
			string value = DamageValue(damage, hits);
			float damageScale = ItemAction.SampleDamageScale(action, 1f);
			if (damageScale > 1f)
			{
				value += "-" + Mathf.CeilToInt(damage.healthDamage * hits * damageScale);
			}
			yield return (names[EStatName.Damage], value);
		}
		if (damage.critChance > 0f)
		{
			yield return (names[EStatName.CritChance], StatFormat.Percent(damage.critChance));
		}
		float maxArmorPenetration = damage.armorPenetration + ItemAction.SampleArmorPenetrationBonus(action, 1f);
		if (maxArmorPenetration > 0f)
		{
			yield return (names[EStatName.ArmorPenetration], StatFormat.PercentSpan(damage.armorPenetration, maxArmorPenetration));
		}
		if (damage.blunt > 0f)
		{
			yield return (names[EStatName.ArmorChip], StatFormat.ScaleDelta(1f + damage.blunt));
		}
		if (damage.knockbackDistance >= MinShownKnockback)
		{
			yield return (names[EStatName.Knockback], StatFormat.Number(damage.knockbackDistance));
		}
		foreach (var entry in Buildups(damage.buildups, hits))
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
		// An instant blast reads as a plain hit: its damage, then its reach.
		if ((areaEvent.type & EItemEventType.AreaBurst) != 0 && areaEvent.areaBurst != null)
		{
			foreach (var entry in BaseDamage(areaEvent.areaBurst.damage))
			{
				yield return entry;
			}
			yield return (names[EStatName.Radius], StatFormat.Meters(areaEvent.areaBurst.radius));
			yield break;
		}
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

	// A blood cost is the one action cost shown as a stat row; attack time,
	// stamina and oil sit on the action's title line (ItemActionPanel).
	public static IEnumerable<(string name, string value)> BloodCost(ItemAction action)
	{
		if (action != null && action.bloodCost > 0f)
		{
			yield return (GameClient.Current.statNames[EStatName.BloodCost], StatFormat.Number(action.bloodCost));
		}
	}

	// Press until the weapon can fire again: the hold to reach this tier, the
	// swing, and the recovery tail.
	public static float AttackTime(ItemActionProfile profile, int tierIndex, ItemAction action)
	{
		return ItemActionProfile.GetTierFireTime(profile, tierIndex)
			+ action.activeDurationSeconds + action.cooldownSeconds;
	}

	// How far the tier carries the wielder forward, when it's far enough to count.
	public static IEnumerable<(string name, string value)> Lunge(ItemAction action)
	{
		float distance = action?.ComputeLungeDistance() ?? 0f;
		if (distance >= MinShownLunge)
		{
			yield return (GameClient.Current.statNames[EStatName.Lunge], StatFormat.Meters(distance));
		}
	}

	// Range for a ranged damage event (melee reach isn't shown). Range scales
	// with how long the tier was held — SampleRangeScale lerps base →
	// base*chargedRangeScale across the hold. Renders as a span ("8-16m") when
	// the tier actually ramps.
	public static IEnumerable<(string name, string value)> Range(ItemAction action, ItemEvent damageEvent)
	{
		if (action == null || damageEvent == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		float scale = Mathf.Max(1f, action.chargedRangeScale);
		if ((damageEvent.type & EItemEventType.Hitscan) != 0)
		{
			if (damageEvent.hitScanRange > 0f)
			{
				yield return (names[EStatName.Range], StatFormat.MeterSpan(damageEvent.hitScanRange, scale));
			}
		}
		else if ((damageEvent.type & EItemEventType.Projectile) != 0)
		{
			// Arced lobs author reach directly (projectileMaxRange, speed ignored);
			// flat flight derives it as speed × lifetime.
			float baseRange = damageEvent.projectileArcing
				? damageEvent.projectileMaxRange
				: damageEvent.projectileSpeed * damageEvent.projectileLifetimeSeconds;
			if (baseRange > 0f)
			{
				yield return (names[EStatName.Range], StatFormat.MeterSpan(baseRange, scale));
			}
		}
	}

	// AoE "where can you place it?" range. Distinct from damage-event
	// range (which is how far the hit reaches) — Target Range is how far
	// the player can pick the AoE's center. Positional tiers only: Arced
	// throws have no placeable target (reach rides the Range stat) and
	// Directional tiers ignore positionalRange entirely.
	public static IEnumerable<(string name, string value)> TargetRange(ItemAction action)
	{
		if (action == null || action.aimType != EAimType.Positional || action.positionalRange <= 0f)
		{
			yield break;
		}
		yield return (GameClient.Current.statNames[EStatName.TargetRange], StatFormat.Meters(action.positionalRange));
	}

	// One entry per on-hit effect contribution — name = "<effect>". An
	// immediate-apply entry reads as the effect's authored duration (like a flat
	// status effect); a meter entry reads as the percent of the meter the attack
	// fills, uncapped — past 100% a stacking effect applies again, and a
	// clear-on-apply one (Stun) starts refilling between hits. Null entries and zero-amount meter entries are skipped.
	public static IEnumerable<(string name, string value)> Buildups(Godot.Collections.Array<StatusEffectBuildup> buildups, int hits = 1)
	{
		if (buildups == null)
		{
			yield break;
		}
		foreach (StatusEffectBuildup entry in buildups)
		{
			if (entry == null || entry.effect == null)
			{
				continue;
			}
			string effectName = entry.effect.displayName.ToString();
			if (string.IsNullOrEmpty(effectName))
			{
				effectName = entry.effect.ResourceName;
			}
			if (entry.applyImmediately)
			{
				yield return (effectName, StatFormat.Duration(entry.effect));
			}
			else if (entry.amount > 0f)
			{
				yield return (effectName, StatFormat.UncappedPercent(entry.amount * hits));
			}
		}
	}

	// The centre of the ±damageVariance roll, which is the authored damage itself,
	// summed over `hits` and rounded up.
	static string DamageValue(DamageData damage, int hits)
	{
		return Mathf.CeilToInt(damage.healthDamage * hits).ToString();
	}

	// Conditional damage layer (On Crit / On Dizzy / On Backstab). Only what the
	// modifier changes beyond the standard bonus gets a row.
	// `standardMultiplier` is that bonus (StandardMultiplier), folded into an
	// authored Damage multiplier so the row reads the total.
	public static IEnumerable<(string name, string value)> DamageModifier(DamageDataModifier mod, float standardMultiplier = 1f)
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
		if ((mod.overrides & EDamageFields.DamageMultiplier) != 0 && mod.damageMultiplier != 1f)
		{
			yield return (names[EStatName.Damage], StatFormat.Multiplier(standardMultiplier * mod.damageMultiplier));
		}
		if ((mod.overrides & EDamageFields.ArmorPenetration) != 0 && mod.armorPenetration > 0f)
		{
			yield return (names[EStatName.ArmorPenetration], StatFormat.Percent(mod.armorPenetration));
		}
		if ((mod.overrides & EDamageFields.Blunt) != 0 && mod.blunt > 0f)
		{
			yield return (names[EStatName.ArmorChip], StatFormat.ScaleDelta(1f + mod.blunt));
		}
		if ((mod.overrides & EDamageFields.KnockbackDistance) != 0 && mod.knockbackDistance >= MinShownKnockback)
		{
			yield return (names[EStatName.Knockback], StatFormat.Number(mod.knockbackDistance));
		}
		if ((mod.overrides & EDamageFields.AddBuildups) != 0)
		{
			foreach (var entry in Buildups(mod.addBuildups))
			{
				yield return entry;
			}
		}
	}

	// The standard bonus a hit of `damage` gets on `trigger` (HitInfo.ApplyCrit /
	// ApplyBackstab): SimData's crit multiplier, its backstab multiplier for a
	// melee hit, else 1.
	public static float StandardMultiplier(EDamageTrigger trigger, DamageData damage)
	{
		SimData simData = Sim.Current?.SimData;
		if (simData == null)
		{
			return 1f;
		}
		return trigger switch
		{
			EDamageTrigger.OnCrit => simData.critMultiplier,
			EDamageTrigger.OnBackstab when damage != null && (damage.tags & EHitTag.Melee) != 0 => simData.backstabMultiplier,
			_ => 1f,
		};
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
		string duration = StatFormat.Duration(effect);
		if (!string.IsNullOrEmpty(duration))
		{
			yield return (names[EStatName.Duration], duration);
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

	// Level-derived combat scaling for a forge upgrade of tier `level` applied to
	// the concrete `slot`. Offense slots (Melee/Ranged) surface the outgoing
	// damage+buildup multiplier; the Armor slot surfaces incoming damage reduction.
	// Both come from the shared SimData curve (see LevelOutgoingScale /
	// LevelIncomingResist). Level 0 (neutral ×1) and non-upgrade slots yield nothing.
	public static IEnumerable<(string name, string value)> UpgradeLevelInfo(int level, EUpgradeSlot slot)
	{
		SimData simData = Sim.Current?.SimData;
		if (level <= 0 || simData == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		if (slot == EUpgradeSlot.Armor)
		{
			yield return (names[EStatName.DamageReduction], StatFormat.Percent(1f - simData.LevelIncomingResist(level)));
		}
		else if (slot == EUpgradeSlot.Melee || slot == EUpgradeSlot.Ranged)
		{
			yield return (names[EStatName.DamageScale], StatFormat.Multiplier(simData.LevelOutgoingScale(level)));
		}
	}

	// Map a modifier target onto its UI label key. A value with no label falls
	// back to EStatName.Damage — every value has one today, so the fallback
	// only guards a future addition landing before its label.
	private static EStatName ToStatName(EStat stat)
	{
		return stat switch
		{
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
			EStat.FortitudeResistance => EStatName.Fortitude,
			EStat.OilCost => EStatName.OilCost,
			EStat.DashDistance => EStatName.DashDistance,
			EStat.DurabilityWear => EStatName.DurabilityWear,
			EStat.HungerDrainRate => EStatName.HungerDrain,
			EStat.ThirstDrainRate => EStatName.ThirstDrain,
			EStat.FatigueDrainRate => EStatName.FatigueDrain,
			_ => EStatName.Damage,
		};
	}

	private static EStatName ToStatName(EHitTag tag)
	{
		return tag switch
		{
			EHitTag.Damage => EStatName.Damage,
			EHitTag.Fire => EStatName.Fire,
			EHitTag.Blunt => EStatName.Blunt,
			EHitTag.Dizzy => EStatName.Dizzy,
			EHitTag.ArmorPenetration => EStatName.ArmorPenetration,
			EHitTag.Electrical => EStatName.Electrical,
			EHitTag.Ranged => EStatName.Ranged,
			EHitTag.Melee => EStatName.Melee,
			EHitTag.Poison => EStatName.Poison,
			EHitTag.Magical => EStatName.Magical,
			EHitTag.Knockback => EStatName.Knockback,
			EHitTag.Physical => EStatName.Physical,
			EHitTag.Burning => EStatName.Burning,
			EHitTag.Poisoned => EStatName.Poisoned,
			EHitTag.Shocked => EStatName.Shocked,
			EHitTag.Sunlight => EStatName.Sunlight,
			EHitTag.Bleeding => EStatName.Bleeding,
			_ => EStatName.Damage,
		};
	}

	// Walk a modifier list and yield (label, formatted-value) tuples for every
	// entry off its neutral identity. Multiplicative entries render as scale
	// deltas ("MoveSpeed: -25%"); additive stats as signed offsets
	// ("Camouflage: +5"). Used by StatusEffectInfo, ArmorStats and the bestiary.
	public static IEnumerable<(string name, string value)> Modifiers(Godot.Collections.Array<Modifier> modifiers)
	{
		if (modifiers == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;
		int count = modifiers.Count;
		for (int i = 0; i < count; i++)
		{
			switch (modifiers[i])
			{
				case StatModifier m when m.stat != EStat.None:
					if (StatModifierUtil.IsAdditive(m.stat))
					{
						if (m.value != 0f)
						{
							yield return (names[ToStatName(m.stat)], StatFormat.SignedNumber(m.value));
						}
					}
					else if (!Mathf.IsEqualApprox(m.value, 1f))
					{
						yield return (names[ToStatName(m.stat)], StatFormat.ScaleDelta(m.value));
					}
					break;
				case TagModifier t when t.tag != EHitTag.None && !Mathf.IsEqualApprox(t.value, 1f):
					yield return (names[ToStatName(t.tag)], StatFormat.ScaleDelta(t.value));
					break;
			}
		}
	}

	// Composite player readout for the inventory's stats panel. Core dials
	// (Health / Armor / Stamina / Speed) always render so the player can
	// confirm their character sheet at a glance. Sense stats render only
	// when armor or an active status effect moves them off the PlayerData
	// base. Resistances render only when their summed total is non-zero.
	// `includePools` false drops the Health / Stamina / Armor rows for a screen
	// that already shows them as bars (PlayerVitalsHud).
	public static IEnumerable<(string name, string value)> PlayerStats(Player player, bool includePools = true)
	{
		if (player == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;

		if (includePools)
		{
			yield return (names[EStatName.Health], StatFormat.Number(player.MaxHealth));
			yield return (names[EStatName.MaxStamina], StatFormat.Number(player.MaxStamina));
			yield return (names[EStatName.Armor], StatFormat.Number(player.MaxArmor));
		}

		// Per-character party sheet (PlayerState) — the authored attributes that
		// distinguish one member from another on the camp Select-Character panel.
		// Display-only for now (not yet folded into gameplay), read straight off
		// the hosted member. Always shown so members are comparable.
		if (player.Member is PlayerState member)
		{
			yield return (names[EStatName.Strength], StatFormat.Number(member.strength));
			yield return (names[EStatName.Perception], StatFormat.Number(member.perception));
			yield return (names[EStatName.Stealth], StatFormat.Number(member.stealth));
			yield return (names[EStatName.Fortitude], StatFormat.Number(member.fortitude));
		}

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

	// Composite base-species readout for the bestiary detail page. Mirrors
	// PlayerStats — the core character-sheet dials a player would compare
	// across creatures: health, armor (only when the species has any), move
	// speed, and sight range. Weapon and loot loadouts are spawn-time
	// composition (SpeciesData), not base traits, so they're
	// intentionally excluded.
	public static IEnumerable<(string name, string value)> MobStats(MobData mob)
	{
		if (mob == null)
		{
			yield break;
		}
		Dictionary<EStatName, string> names = GameClient.Current.statNames;

		yield return (names[EStatName.Health], StatFormat.Number(mob.maxHealth));
		if (mob.maxArmor > 0f)
		{
			yield return (names[EStatName.Armor], StatFormat.Number(mob.maxArmor));
		}
		yield return (names[EStatName.MoveSpeed], StatFormat.Number(mob.maxSpeed));
		yield return (names[EStatName.Vision], StatFormat.Meters(mob.visionRange));
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
	public static IEnumerable<(string name, string value)> ConsumableStats(ItemActionProfile profile)
	{
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
						string value = StatFormat.Duration(sed);
						yield return (effectName, value);
					}
				}
			}
		}
	}

	// How much of the hunger meter a food restores.
	public static IEnumerable<(string name, string value)> Food(ItemData item)
	{
		if (item is ConsumableData food && food.satiation > 0f)
		{
			yield return (GameClient.Current.statNames[EStatName.Satiation], StatFormat.Percent(food.satiation));
		}
	}

	// How full a flask or lantern is: water in hours, oil in units.
	public static IEnumerable<(string name, string value)> Contents(ItemState item)
	{
		string label = GameClient.Current.statNames[EStatName.Contents];
		if (item is FlaskState flask)
		{
			string held = $"{StatFormat.Number(flask.amount)} / {StatFormat.Number(flask.data.capacity)}";
			yield return (label, flask.data.liquid == ELiquid.Water ? held + " h" : held);
		}
		else if (item is LanternState lantern)
		{
			yield return (label, $"{StatFormat.Number(lantern.oil)} / {StatFormat.Number(lantern.data.oilCapacity)}");
		}
	}

	// The kind of round a ranged weapon fires, or a round's own type.
	public static IEnumerable<(string name, string value)> Ammo(ItemData item)
	{
		EAmmoType type = item switch
		{
			WeaponData weapon => weapon.ammoType,
			AmmoData ammo => ammo.ammoType,
			_ => EAmmoType.None,
		};
		if (GameClient.Current.ammoTypeNames.TryGetValue(type, out string typeName))
		{
			yield return (GameClient.Current.statNames[EStatName.Ammo], typeName);
		}
	}

	// A round's odds of breaking where it lands. 0 = no row.
	public static IEnumerable<(string name, string value)> BreakChance(ItemData item)
	{
		float chance = item switch
		{
			AmmoData ammo => ammo.breakChance,
			WeaponData { thrown: true } thrown => thrown.thrownBreakChance,
			_ => 0f,
		};
		if (chance > 0f)
		{
			yield return (GameClient.Current.statNames[EStatName.BreakChance], StatFormat.Percent(chance));
		}
	}

	// Equipped gear's Weight row: the stamina units it takes off the wearer's
	// max. 0 = no row.
	public static IEnumerable<(string name, string value)> Weight(ItemData item)
	{
		float weight = (item as GearData)?.weight ?? 0f;
		if (weight != 0f)
		{
			yield return (GameClient.Current.statNames[EStatName.Weight], StatFormat.Number(weight));
		}
	}

	// A shield's Block row: the guard pool capacity. 0 = no row. Parry is NOT
	// here — its counter-strike is a triggered effect shown as a "Parry" context
	// panel (see ParryCounter), the same way Crit / Backstab render as contexts.
	public static IEnumerable<(string name, string value)> ShieldStats(ShieldData shield)
	{
		if (shield != null && shield.guardArmor > 0f)
		{
			yield return (GameClient.Current.statNames[EStatName.Block], StatFormat.Number(shield.guardArmor));
		}
	}

	// The counter-strike a parry deals back, or null when the shield can't parry
	// or its counter shows nothing. Feed it through BaseDamage to render the
	// "Parry" context — damage plus every rider (armor penetration, knockback,
	// Dizzy); a damage-free counter (a tower shield's shove) still renders.
	public static DamageData ParryCounter(ShieldData shield)
	{
		if (shield == null || !shield.CanParry)
		{
			return null;
		}
		return Displayable(shield.parryCounter);
	}

	// The thorns a block deals back (ShieldData.blockCounter), or null when the
	// shield can't block or its counter shows nothing. Rendered like ParryCounter.
	public static DamageData BlockCounter(ShieldData shield)
	{
		if (shield == null || shield.guardArmor <= 0f)
		{
			return null;
		}
		return Displayable(shield.blockCounter);
	}

	private static DamageData Displayable(DamageData counter)
	{
		return counter != null && BaseDamage(counter).Any() ? counter : null;
	}

	// Compact combat readout for one mob weapon (bestiary). Summarizes the
	// first action that actually attacks: an AoE action's DPS / radius /
	// duration + target range, or a direct hit's damage / range / on-hit
	// effects. A non-attacking weapon (a pure buff like a battle cry) yields
	// nothing, so the caller can skip it.
	public static IEnumerable<(string name, string value)> WeaponSummary(WeaponData weapon)
	{
		if (weapon?.actionProfile?.chargedActions == null)
		{
			yield break;
		}
		foreach (ItemAction action in weapon.actionProfile.chargedActions)
		{
			if (action == null)
			{
				continue;
			}
			ItemEvent areaEvent = FindAreaEffectEvent(action);
			if (areaEvent != null)
			{
				foreach (var entry in AreaEffect(areaEvent, weapon))
				{
					yield return entry;
				}
				foreach (var entry in TargetRange(action))
				{
					yield return entry;
				}
				yield break;
			}
			ItemEvent damageEvent = FindDamageEvent(action);
			if (damageEvent == null)
			{
				continue;
			}
			DamageData damage = weapon.GetDamage(damageEvent.damageProfileKey ?? new StringName("primary"));
			foreach (var entry in BaseDamage(damage, CountHits(action, damageEvent.damageProfileKey), action))
			{
				yield return entry;
			}
			foreach (var entry in Range(action, damageEvent))
			{
				yield return entry;
			}
			yield break;
		}
	}

	private const EItemEventType DamageEventFlags = EItemEventType.Melee | EItemEventType.Hitscan | EItemEventType.Projectile;

	// Walks an action's events and returns the first one that actually deals
	// damage (Melee, Hitscan, or Projectile). That event's authored damage
	// override (if any) wins over the weapon-level default — same resolution
	// order the runtime uses in ItemEventHandlers.
	// How many of the action's damage events resolve `profileKey` — each sweeps its
	// own target set (ActionRunner.RentSwingHitSet), so one target takes every hit.
	public static int CountHits(ItemAction action, StringName profileKey)
	{
		if (action?.events == null)
		{
			return 0;
		}
		int hits = 0;
		foreach (ItemEvent ev in action.events)
		{
			if (ev != null && (ev.type & DamageEventFlags) != 0 && ev.damageProfileKey == profileKey)
			{
				hits++;
			}
		}
		return hits;
	}

	public static ItemEvent FindDamageEvent(ItemAction action)
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
			if ((ev.type & DamageEventFlags) != 0)
			{
				return ev;
			}
		}
		return null;
	}

	// Walks an action's events (and any projectile impactEvent chained off
	// them) looking for a SpawnAreaEffect or AreaBurst entry. Used to switch a stat row
	// into AoE mode — Rain of Arrows reads its DPS / radius / duration from
	// the impactEvent on its arcing projectile.
	private const EItemEventType AreaEventFlags = EItemEventType.SpawnAreaEffect | EItemEventType.AreaBurst;

	public static ItemEvent FindAreaEffectEvent(ItemAction action)
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
			if ((ev.type & AreaEventFlags) != 0)
			{
				return ev;
			}
			if (ev.impactEvent != null && (ev.impactEvent.type & AreaEventFlags) != 0)
			{
				return ev.impactEvent;
			}
		}
		return null;
	}
}
