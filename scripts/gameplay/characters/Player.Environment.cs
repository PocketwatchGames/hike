using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	// WarmthZone (campfires, etc.) calls these on body enter/exit. Counter,
	// not bool, so two campfires whose zones overlap don't release the player
	// from one when they leave the other. Entering accelerates the wetness
	// decay (PlayerData.wetnessWarmthDrySeconds) — a player walking up to
	// a fire dries off in seconds rather than minutes, and the wet status
	// releases naturally once wetness falls below the disarm threshold. The
	// zone's warmingTemperature is summed into _warmthBonus so
	// SampleEnvironmentTemperature can stack heat from multiple overlapping
	// fires.
	public void EnterWarmthZone(WarmthZone zone)
	{
		_warmthZoneCount++;
		if (zone != null)
		{
			_warmthBonus += zone.warmingTemperature;
		}
	}

	public void ExitWarmthZone(WarmthZone zone)
	{
		if (_warmthZoneCount > 0)
		{
			_warmthZoneCount--;
			if (zone != null)
			{
				_warmthBonus -= zone.warmingTemperature;
			}
		}
	}

	// True while the player stands inside any active safety zone (a starting
	// area, a lit campfire). Read by mob AI (TargetSafeCondition, the gated
	// AggroAcquiredCondition): aggressive mobs break off their attack to stare
	// and then wander away instead of engaging, and won't re-aggro until the
	// player steps back out. SafetyZone calls Enter/ExitSafetyZone on overlap.
	public bool IsSafe => _safeZoneCount > 0;

	public void EnterSafetyZone(SafetyZone zone)
	{
		_safeZoneCount++;
		if (CVars.safetyDebug.Value)
		{
			GD.Print($"[safety] player entered zone, count={_safeZoneCount} IsSafe={IsSafe}");
		}
	}

	public void ExitSafetyZone(SafetyZone zone)
	{
		if (_safeZoneCount > 0)
		{
			_safeZoneCount--;
		}
		if (CVars.safetyDebug.Value)
		{
			GD.Print($"[safety] player exited zone, count={_safeZoneCount} IsSafe={IsSafe}");
		}
	}

	// Per-physics-tick wet driver. Routes environmental wetness signals into
	// the player's Wet buildup meter (the controller arms / disarms via
	// armThreshold / disarmThreshold), AND ticks the same signals through
	// each equipped armor's own wetness meter (ArmorState.wetness). Equipped
	// armor wetness cascades back into the player meter each tick at
	// PlayerData.wetnessArmorCascadeRate scaled by the armor's current
	// wetness — so a wet shirt keeps soaking the wearer.
	//
	// Sources, in priority order:
	//   • In water     — every meter (player + each equipped armor) snaps
	//                    to 1 the moment the player enters water.
	//   • In soaking rain (light/heavy tier, sky-exposed, not in a warmth
	//                    zone) — every meter accumulates at
	//                    1 / wetnessRainSoakSeconds scaled by RainIntensity.
	//                    Drizzle is too fine to soak and is treated as clear.
	//   • Otherwise    — every meter drains at its OWN rate. The player's
	//                    drains at baseDryRate × ComposeStat(WetnessDryRate)
	//                    so equipped wet wool slows it; each armor drains
	//                    at baseDryRate × its own WetnessDryRate modifier.
	//                    Inside a warmth zone (campfires) the warmthRate
	//                    acts as a FLOOR — humid still days can never slow
	//                    drying below it, but a hot wind outdoors still can.
	private void TickWetEffect(float dt)
	{
		if (_wetEffectData == null || data == null)
		{
			return;
		}

		// Source classification — water beats rain beats nothing. Warmth
		// zones suppress rain accumulation entirely so the player dries off
		// at the fire even when it's raining around them; the fast warmth
		// dry rate then takes everyone down regardless of overhead
		// conditions. Water still wins over warmth — step into a stream
		// at a campfire and you're soaked.
		bool inWater = _waterState != EWaterState.None;
		bool inWarmth = _warmthZoneCount > 0;
		// Rain exposure in [0, 1]: 0 when sheltered (solid roof overhead or
		// dense enough canopy), up to 1 in fully open sky. A partial canopy
		// gives partial shelter, so rain soak scales by it.
		float rainExposure = (!inWater && !inWarmth) ? RainExposure01() : 0f;
		// Only light and heavy rain soak the player — drizzle is visible falling
		// rain too fine to register as wetness, so it neither wets nor blocks
		// drying (the player dries in a drizzle as if it were clear). Swimming
		// still soaks unconditionally via the inWater branch below.
		ERainTier rainTier = SkyController.Current?.Palette.RainTier ?? ERainTier.None;
		bool inSoakingRain = rainExposure > 0f && rainTier >= ERainTier.Light;

		float rainAccum = 0f;
		if (inSoakingRain && data.wetnessRainSoakSeconds > 0f)
		{
			float rainIntensity = Mathf.Clamp(SkyController.Current?.Palette.RainIntensity ?? 0f, 0f, 1f);
			rainAccum = (dt / data.wetnessRainSoakSeconds) * rainIntensity * rainExposure;
		}

		// Environmental drying scalar — shared across player and armor.
		// Each consumer scales its own WetnessDryRate modifier onto this
		// neutral rate. Skipped during water / rain (you're not drying when
		// being soaked).
		float baseDryRate = 0f;
		float warmthRate = 0f;
		if (!inWater && !inSoakingRain)
		{
			float windSpeed = _world?.SampleWindSpeed(GlobalPosition) ?? 0f;
			float airTemp = _world?.SampleAirTemperature(GlobalPosition) ?? data.dryRateReferenceTempF;
			float humidity = SkyController.Current?.Weather?.humidity ?? 0f;
			float windMul = 1f + windSpeed * data.dryRateWindBoostPerMps;
			float humidityMul = Mathf.Clamp(1f - humidity * data.dryRateHumidityDamping, 0f, 1f);
			float tempMul = Mathf.Max(0f, 1f + (airTemp - data.dryRateReferenceTempF) * data.dryRateTempBoostPerF);
			float envFactor = windMul * humidityMul * tempMul;
			baseDryRate = data.wetnessDrySeconds > 0f ? envFactor / data.wetnessDrySeconds : 0f;
			warmthRate = inWarmth && data.wetnessWarmthDrySeconds > 0f ? 1f / data.wetnessWarmthDrySeconds : 0f;
		}

		// Item-side wetness uses the modifier-less wet-clothes status so the
		// per-armor meter never accidentally double-applies cold/heat shifts
		// (those live on _wetEffectData and only fold once, on the player
		// when their own meter arms). Falls back to _wetEffectData if no
		// clothes-side resource is wired.
		StatusEffectData armorEffect = _wetClothesEffectData ?? _wetEffectData;

		// Tick every owned armor's own buildup first — equipped pieces AND
		// anything in the backpack — so wet wool stuffed in the pack still
		// dries on the same clock as worn wool. Done before the player
		// delta so the cascade contribution below reads the freshest
		// post-rain / post-dry armor wetness.
		if (_inventory != null)
		{
			foreach (ArmorState armor in _inventory.EnumerateAllArmor())
			{
				if (armor?.data == null) { continue; }
				float armorDelta;
				if (inWater)
				{
					armorDelta = 1f - armor.statusEffects.GetBuildup(armorEffect);
				}
				else
				{
					float armorDryMul = armor.data.modifiers != null
						? StatModifierUtil.Fold(EStat.WetnessDryRate, armor.data.ModifiersFlat, 1f)
						: 1f;
					float armorDryRate = Mathf.Max(baseDryRate * armorDryMul, warmthRate);
					armorDelta = rainAccum - armorDryRate * dt;
				}
				if (armorDelta != 0f)
				{
					armor.statusEffects.AddBuildup(armorEffect, armorDelta);
				}
			}
		}

		// Player meter delta. Cascade from EQUIPPED armor feeds in regardless
		// of in-water (water already pins the player to 1; cascade then is a
		// no-op via clamp) so the path is uniform. Armor in the backpack
		// doesn't cascade — it's not in contact with the wearer's skin.
		float playerDelta;
		if (inWater)
		{
			playerDelta = 1f - _statusEffects.GetBuildup(_wetEffectData);
		}
		else
		{
			float playerDryMul = ComposeStat(EStat.WetnessDryRate);
			float playerDryRate = Mathf.Max(baseDryRate * playerDryMul, warmthRate);
			playerDelta = rainAccum - playerDryRate * dt;
			if (_inventory != null && data.wetnessArmorCascadeRate > 0f)
			{
				foreach (ArmorState armor in _inventory.EnumerateEquippedArmor())
				{
					if (armor == null) { continue; }
					playerDelta += armor.statusEffects.GetBuildup(armorEffect) * data.wetnessArmorCascadeRate * dt;
				}
			}
		}

		if (playerDelta != 0f)
		{
			_statusEffects.AddBuildup(_wetEffectData, playerDelta);
		}
	}

	// Per-physics-tick dirty driver. Mirrors TickWetEffect's per-armor model:
	// each WORN piece of armor slowly accumulates grime (over
	// PlayerData.dirtyDaysToFull game-days of wear), and the player-side Dirty
	// effect — which carries the Scent penalty and the HUD icon — tracks the
	// dirtiest worn piece via its own ContinuousArm meter.
	//
	// Washing: while a piece's wet meter is armed its grime is pinned to zero.
	// Because the wet driver soaks EVERY owned piece (worn or packed), getting
	// the player wet cleans their whole wardrobe — a garment needn't be worn
	// to be washed. There is no passive decay; grime only resets by washing.
	private void TickDirtyEffect(float dt)
	{
		if (_dirtyEffectData == null || _inventory == null || data == null)
		{
			return;
		}

		// Item-side meters key off the modifier-less clothes status (fallback
		// to the player effect if unwired) so an armor piece never arms a
		// Scent-bearing instance of its own — the penalty folds once, on the
		// player, when their meter arms.
		StatusEffectData dirtyClothes = _dirtyClothesEffectData ?? _dirtyEffectData;
		StatusEffectData wetClothes = _wetClothesEffectData ?? _wetEffectData;

		// Grime accrues in GAME-time: dirtyDaysToFull day/night cycles of wear
		// fill the 0→1 meter. One game day is DayLengthSeconds real seconds at
		// time_scale 1, so the per-real-second rate tracks the same clock (and
		// CVar) that advances the sky.
		float dayLength = _world?.WorldState?.SimData?.dayLengthSeconds ?? 600f;
		float daysToFull = Mathf.Max(data.dirtyDaysToFull, 0.0001f);
		float dirtyDelta = dayLength > 0f ? dt * CVars.timeScale.Value / (daysToFull * dayLength) : 0f;

		foreach (ArmorState armor in _inventory.EnumerateAllArmor())
		{
			if (armor?.data == null) { continue; }
			// Wet wins: a soaked piece is being washed, so pin its grime to
			// zero (a fat negative contribution clamps to 0). Applies to packed
			// pieces too, so a swim or a downpour launders everything you own.
			if (wetClothes != null && armor.statusEffects.HasActive(wetClothes))
			{
				if (armor.statusEffects.GetBuildup(dirtyClothes) > 0f)
				{
					armor.statusEffects.AddBuildup(dirtyClothes, -1f);
				}
				continue;
			}
			// Only WORN pieces pick up grime; a packed piece holds its current
			// dirtiness until it's worn again.
			if (dirtyDelta > 0f && _inventory.IsEquipped(armor))
			{
				armor.statusEffects.AddBuildup(dirtyClothes, dirtyDelta);
			}
		}

		// Drive the player-side meter to the dirtiest worn piece. Its
		// ContinuousArm thresholds switch the Scent penalty + HUD icon on once
		// a worn piece is fully grimy and off when it's washed back below the
		// disarm threshold.
		float maxWornDirty = 0f;
		foreach (ArmorState armor in _inventory.EnumerateEquippedArmor())
		{
			if (armor == null) { continue; }
			maxWornDirty = Mathf.Max(maxWornDirty, armor.statusEffects.GetBuildup(dirtyClothes));
		}
		float playerDirtyDelta = maxWornDirty - _statusEffects.GetBuildup(_dirtyEffectData);
		if (playerDirtyDelta != 0f)
		{
			_statusEffects.AddBuildup(_dirtyEffectData, playerDirtyDelta);
		}
	}

	// Per-physics-tick muddy driver. The Muddy ContinuousArm meter fills while
	// the player walks on EGroundType.Mud ground (marsh, mud patches) and
	// drains slowly once they're back on dry footing. Stepping into water of
	// any depth rinses the mud right off — the meter snaps to zero, mirroring
	// the way water instantly soaks the Wet meter. Unlike Dirty there's no
	// per-armor model: mud cakes the player directly, not their wardrobe.
	private void TickMuddyEffect(float dt)
	{
		if (_muddyEffectData == null || data == null)
		{
			return;
		}

		float delta;
		if (_waterState != EWaterState.None)
		{
			// Water wins: rinse the mud off completely the moment any part of
			// the player is submerged. Snap exactly to empty.
			delta = -_statusEffects.GetBuildup(_muddyEffectData);
		}
		else
		{
			EGroundType ground = GroundTypeResolver.Resolve(_world?.WorldState, GlobalPosition);
			if (ground == EGroundType.Mud && data.muddySoakSeconds > 0f)
			{
				delta = dt / data.muddySoakSeconds;
			}
			else if (data.muddyDrySeconds > 0f)
			{
				delta = -dt / data.muddyDrySeconds;
			}
			else
			{
				delta = 0f;
			}
		}

		if (delta != 0f)
		{
			_statusEffects.AddBuildup(_muddyEffectData, delta);
		}
	}

	// Surface a continuous 0..1 progress value the HUD's status-effect
	// strip can render as a fill bar, for status effects whose intensity
	// is driven by a continuous player-side state rather than a timer.
	// Returns null for effects that don't have a custom mapping (the HUD
	// falls back to its timer-based progress).
	//
	// Currently returns null for everything — Wet was the only consumer and
	// its meter is now visualized via the controller's buildup bar (same
	// shape every other ContinuousArm / ThresholdCross effect uses). Kept
	// as a hook for future effects that need a custom non-timer mapping
	// distinct from their buildup meter (e.g. a hunger / thirst bar).
	public float? GetStatusEffectProgress(StatusEffectData effectData)
	{
		_ = effectData;
		return null;
	}

	// Slides _bodyTemperature toward the sampled environment + warmth bonus,
	// then arms / clears the cold and hot statuses based on the result.
	// Crossing a threshold IN applies the status with the timer paused (the
	// effect persists as long as the body is outside the safe band). Returning
	// to the safe band arms the authored 5s expiry — re-crossing pauses again
	// without re-stacking, mirroring the wet pattern.
	private void TickBodyTemperature(float dt)
	{
		if (data == null)
		{
			return;
		}
		if (_world == null)
		{
			return;
		}

		float envTemp = _world.SampleAirTemperature(GlobalPosition) + _warmthBonus;
		float speed = data.temperatureAcclimationSpeed;
		if (speed > 0f)
		{
			float diff = envTemp - _bodyTemperature;
			float step = speed * dt;
			if (Mathf.Abs(diff) <= step)
			{
				_bodyTemperature = envTemp;
			}
			else
			{
				_bodyTemperature += Mathf.Sign(diff) * step;
			}
		}
		else
		{
			_bodyTemperature = envTemp;
		}

		// Resistances from active status effects shift the trigger thresholds.
		// Positive coldResistance lowers the cold threshold (harder to chill);
		// positive heatResistance raises the hot threshold (harder to overheat).
		GetThermalResistances(out float coldResist, out float heatResist);
		// Wind chill. Multiplied by windTemperatureReduction (degrees F per
		// m/s) and shifted onto BOTH thresholds — the comfort band slides
		// upward in actual ambient, so cold triggers earlier and hot needs
		// hotter air to reach. SampleWindSpeed zeroes out under overhead
		// shelter so caves don't pretend to be windy.
		float windEffect = _world.SampleWindSpeed(GlobalPosition) * data.windTemperatureReduction;
		float coldThreshold = data.coldTemperature - coldResist + windEffect;
		float hotThreshold = data.hotTemperature + heatResist + windEffect;

		UpdateThermalStatus(ref _coldState, data.coldStatus, _bodyTemperature < coldThreshold);
		UpdateThermalStatus(ref _hotState, data.hotStatus, _bodyTemperature > hotThreshold);
	}

	// Shared apply / pause / arm logic for cold and hot statuses. `triggered`
	// is true while the body is outside the safe band — the status is held
	// with timer paused. Once the body re-enters the safe band, the authored
	// duration is armed and the existing TickStatusEffects pruning loop
	// removes the state when it expires.
	private void UpdateThermalStatus(ref StatusEffectState state, StatusEffectData effectData, bool triggered)
	{
		if (effectData == null)
		{
			return;
		}
		if (state != null && !_statusEffects.Contains(state))
		{
			state = null;
		}
		if (triggered)
		{
			if (state == null)
			{
				state = AddStatusEffect(effectData);
			}
			state?.PauseTimer();
			return;
		}
		if (state != null && !state.IsTimed)
		{
			state.ArmTimer(_world?.GameTimeMs ?? 0, _world?.DayNumber ?? 0, _world?.TimeOfDay01 ?? 0.0);
		}
	}
}
