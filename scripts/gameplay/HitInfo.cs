using Godot;

// Runtime payload passed to HurtBox.Hit. Senders (weapons, traps, damage
// zones, anything else that deals damage) build this from a DamageData
// template plus runtime context (attacker, hit direction). Receivers read
// the fields they care about — health damage routes through armor, status
// effects are appended to the receiver's _statusEffects, etc.
//
// Conditional behavior (crit when target is dizzy, extra knockback on the
// hit that lands dizzy, etc.) is layered via ApplyTrigger: the receiver
// calls it with the trigger that just became true and every matching
// DamageDataModifier from `modifiers` folds onto the live fields here.
// OnDizzy specifically fires when a buildup contribution for a
// StatusEffectData with applyTrigger=OnDizzy (i.e. Dizzy) crosses its
// threshold.
public struct HitInfo
{
	public Node source;
	// Type tags carried from the source template (DamageData.tags /
	// ContinuousDamageData.tags). Receivers fold their per-tag StatModifier
	// entries against this mask at multiple gameplay sites — damage scale,
	// pierce-chance, armor chip, knockback magnitude — when applying.
	public EStat tags;
	public float healthDamage;
	public float hitstun;
	// Knockback magnitude (m/s of velocity change) and lockout window;
	// sourced from the DamageData template. The receiver multiplies this
	// by `hitDirection.Normalized()` at apply time, so the sender's
	// hitDirection decides where the push goes and the data decides how
	// strong.
	public float knockbackDistance;
	public float knockbackTime;
	// Chance (0..1) that this hit bypasses the receiver's armor pool and
	// lands directly on health. See DamageData.pierce.
	public float pierce;
	// Anti-armor multiplier on the healthDamage chip — receivers compute
	// armor chip as `healthDamage * (1 + blunt)`. See DamageData.blunt.
	public float blunt;
	// Random sample in [0,1) drawn once at construction. Receivers compare
	// it against the final `pierce` (after modifiers fold) via `Pierced`
	// instead of re-rolling, so HurtBox.QueryHitType and HurtBox.Hit always
	// agree on whether the same swing pierced.
	public float pierceRoll;
	// Random sample in [0,1) for the crit decision. Same pattern as pierceRoll
	// — drawn once at construction so the attacker's QueryHitTriggers
	// prediction and the receiver's Hit-time ApplyCrit agree on whether this
	// swing crits even though crit is now probabilistic (driven by the
	// receiver's `Vulnerable` score).
	public float critRoll;
	// Fraction of healthDamage that bypasses armor and lands directly on
	// health, with the remainder routed through armor chip. Set by the
	// continuous-damage path (ContinuousDamageData.pierce) — spreads
	// armor bypass across time instead of rolling per hit. 0 = use the
	// discrete `Pierced` path; > 0 = continuous-style split. Discrete
	// hits leave this at 0.
	public float armorBypassFraction;
	public Godot.Collections.Array<StatusEffectData> statusEffects;
	// Per-hit buildup contributions sourced from DamageData.buildups /
	// ContinuousDamageData.buildups. Receivers fold each entry into the per-
	// effect buildup meter; the meter, decay timing, and apply trigger live on
	// the StatusEffectData itself. Continuous-damage hits pre-scale `amount`
	// by delta so the rate authored on ContinuousDamageData.buildups is
	// integrated correctly when applied as a one-frame chunk.
	public Godot.Collections.Array<StatusEffectBuildup> buildups;
	// Scalar applied to every buildup entry's `amount` at apply time. Discrete
	// hits leave this at 1; the continuous-damage constructor sets it to the
	// physics delta so a per-second buildup rate authored on
	// ContinuousDamageData.buildups integrates correctly without forcing the
	// receiver to learn about frame timing. The buildups array itself is a
	// direct reference to the authored resource list (never mutated), so the
	// multiplier is the only allocation-free way to convey scaling.
	public float buildupAmountMultiplier;
	// Direction the receiver should be pushed along on a non-zero knockback
	// hit. Set by the sender at hit time — melee/hitscan use the attacker's
	// forward, traps use whatever direction the trap implies (spike → up),
	// static damage zones leave it zero so no knockback applies. Receivers
	// normalize and strip Y before scaling.
	public Vector3 hitDirection;
	// Optional conditional override layers carried from the DamageData
	// template. Receivers fold matching entries onto the fields above via
	// ApplyTrigger when they detect the corresponding condition. Null on
	// templates that don't author any modifiers.
	public Godot.Collections.Array<DamageDataModifier> modifiers;
	// Marks this hit as a per-frame damage tick (DamageZone with a fast
	// tickInterval, future per-frame burn etc.). Receivers route DoT hits
	// into a per-second accumulator instead of spawning a floating HUD
	// number every frame.
	public bool dot;
	// Tracks whether `statusEffects` has been cloned away from the source
	// template's array. The first AddStatusEffects fold allocates a fresh
	// list so we don't mutate the authored DamageData; subsequent folds
	// reuse the owned copy.
	private bool _statusEffectsOwned;

	public HitInfo(DamageData template, Node source, Vector3 hitDirection = default)
	{
		this.source = source;
		this.hitDirection = hitDirection;
		_statusEffectsOwned = false;
		// Roll pierce + crit once up-front so the prediction and the apply see
		// the same outcome even though modifiers may shift the underlying
		// fields between them.
		pierceRoll = GD.Randf();
		critRoll = GD.Randf();
		armorBypassFraction = 0f;
		if (template != null)
		{
			tags = template.tags;
			healthDamage = template.healthDamage;
			hitstun = template.hitstun;
			knockbackDistance = template.knockbackDistance;
			knockbackTime = template.knockbackTime;
			pierce = template.pierce;
			blunt = template.blunt;
			statusEffects = template.statusEffects;
			buildups = template.buildups;
			modifiers = template.modifiers;
			dot = template.dot;
		}
		else
		{
			tags = EStat.None;
			healthDamage = 0f;
			hitstun = 0f;
			knockbackDistance = 0f;
			knockbackTime = 0f;
			pierce = 0f;
			blunt = 0f;
			statusEffects = null;
			buildups = null;
			modifiers = null;
			dot = false;
		}
		buildupAmountMultiplier = 1f;
	}

	// Continuous-damage constructor. Pre-scales healthDamage by `delta` so the
	// receiver's discrete apply path lands a one-frame chunk; sets
	// `armorBypassFraction` from the template's fractional pierce; flags
	// `dot = true` so the HUD rollup kicks in. Buildups pass through unscaled
	// — `buildupAmountMultiplier` is set to `delta` so the receiver scales
	// per-second rates correctly at apply time without mutating the authored
	// list. No modifiers or status effects — continuous damage authors its
	// status cadence through interval entries on the same DamageZone.
	public HitInfo(ContinuousDamageData template, Node source, float delta, Vector3 hitDirection = default)
	{
		this.source = source;
		this.hitDirection = hitDirection;
		_statusEffectsOwned = false;
		pierceRoll = GD.Randf();
		critRoll = GD.Randf();
		if (template != null)
		{
			tags = template.tags;
			healthDamage = template.healthDamage * delta;
			blunt = template.blunt;
			armorBypassFraction = template.pierce;
			buildups = template.buildups;
		}
		else
		{
			tags = EStat.None;
			healthDamage = 0f;
			blunt = 0f;
			armorBypassFraction = 0f;
			buildups = null;
		}
		hitstun = 0f;
		knockbackDistance = 0f;
		knockbackTime = 0f;
		pierce = 0f;
		statusEffects = null;
		modifiers = null;
		dot = true;
		// Per-second buildup rates on ContinuousDamageData scale by delta so a
		// body that stays in the zone for one second accumulates `amount`
		// units regardless of frame rate.
		buildupAmountMultiplier = delta;
	}

	// True when the rolled chance landed inside the (possibly modifier-
	// boosted) pierce window. `pierceRoll` is sampled in [0,1), so a pierce
	// of 0 never fires and a pierce of 1 always fires.
	public bool Pierced => pierceRoll < pierce;

	// Fold every modifier whose trigger equals `trigger` onto the live
	// fields. Callers fire one trigger per condition crossing (OnCrit when
	// this hit crits, OnDizzy when this hit landed dizzy, etc.); a
	// modifier authored for a different trigger is skipped.
	public void ApplyTrigger(EDamageTrigger trigger)
	{
		if (modifiers == null) { return; }
		for (int i = 0; i < modifiers.Count; i++)
		{
			DamageDataModifier mod = modifiers[i];
			if (mod == null) { continue; }
			if (mod.trigger != trigger) { continue; }
			EDamageFields f = mod.overrides;
			if ((f & EDamageFields.HealthDamage) != 0) { healthDamage = mod.healthDamage; }
			if ((f & EDamageFields.Hitstun) != 0) { hitstun = mod.hitstun; }
			if ((f & EDamageFields.KnockbackDistance) != 0) { knockbackDistance = mod.knockbackDistance; }
			if ((f & EDamageFields.KnockbackTime) != 0) { knockbackTime = mod.knockbackTime; }
			if ((f & EDamageFields.Pierce) != 0) { pierce = mod.pierce; }
			if ((f & EDamageFields.Blunt) != 0) { blunt = mod.blunt; }
			if ((f & EDamageFields.AddStatusEffects) != 0 && mod.addStatusEffects != null && mod.addStatusEffects.Count > 0)
			{
				if (!_statusEffectsOwned)
				{
					var copy = new Godot.Collections.Array<StatusEffectData>();
					if (statusEffects != null)
					{
						for (int j = 0; j < statusEffects.Count; j++)
						{
							copy.Add(statusEffects[j]);
						}
					}
					statusEffects = copy;
					_statusEffectsOwned = true;
				}
				for (int j = 0; j < mod.addStatusEffects.Count; j++)
				{
					statusEffects.Add(mod.addStatusEffects[j]);
				}
			}
		}
	}
}
