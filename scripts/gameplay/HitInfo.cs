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
	// armor-penetration-chance, armor chip, knockback magnitude — when applying.
	public EStat tags;
	public float healthDamage;
	// Multiplier converting healthDamage into aggro on the receiver (and, for a
	// player receiver, relayed to their companion). Sourced from the DamageData
	// template; the continuous-damage path defaults it to 1 so DoT zones still
	// build threat. See DamageData.aggroMultiplier / AggroTracker.
	public float aggroMultiplier;
	public float hitstun;
	// Knockback magnitude (m/s of velocity change) and lockout window;
	// sourced from the DamageData template. The receiver multiplies this
	// by `hitDirection.Normalized()` at apply time, so the sender's
	// hitDirection decides where the push goes and the data decides how
	// strong.
	public float knockbackDistance;
	public float knockbackTime;
	// Chance (0..1) that this hit bypasses the receiver's armor pool and
	// lands directly on health. See DamageData.armorPenetration.
	public float armorPenetration;
	// Anti-armor multiplier on the healthDamage chip — receivers compute
	// armor chip as `healthDamage * (1 + blunt)`. See DamageData.blunt.
	public float blunt;
	// Random sample in [0,1) drawn once at construction. Receivers compare
	// it against the final `armorPenetration` (after modifiers fold) via
	// `ArmorPenetrated` instead of re-rolling, so HurtBox.QueryHit and
	// HurtBox.Hit always agree on whether the same swing penetrated armor.
	public float armorPenetrationRoll;
	// Random sample in [0,1) for the crit decision. Same pattern as armorPenetrationRoll
	// — drawn once at construction so the attacker's QueryHit
	// prediction and the receiver's Hit-time ResolveTriggers agree on whether this
	// swing crits even though crit is now probabilistic (driven by the
	// receiver's `Vulnerable` score).
	public float critRoll;
	// Attack-side base crit chance carried from DamageData.critChance; the
	// receiver composes it with its Vulnerable score before comparing critRoll
	// (Mob.IsCritEligible). Continuous (per-frame DoT) hits leave it 0 — they
	// carry no modifiers, so a crit could never fold anything.
	public float critChance;
	// Fraction of healthDamage that bypasses armor and lands directly on
	// health, with the remainder routed through armor chip. Set by the
	// continuous-damage path (ContinuousDamageData.armorPenetration) — spreads
	// armor bypass across time instead of rolling per hit. 0 = use the
	// discrete `ArmorPenetrated` path; > 0 = continuous-style split. Discrete
	// hits leave this at 0.
	public float armorBypassFraction;
	// Per-hit effect contributions sourced from DamageData.buildups /
	// ContinuousDamageData.buildups (plus modifier / weapon-mod folds). Receivers
	// apply each entry immediately or fold it into the per-effect buildup meter
	// per StatusEffectBuildup.applyImmediately; the meter, decay timing, and apply
	// trigger live on the StatusEffectData itself. Continuous-damage hits pre-
	// scale `amount` by delta so the rate authored on ContinuousDamageData.buildups
	// is integrated correctly when applied as a one-frame chunk.
	public Godot.Collections.Array<StatusEffectBuildup> buildups;
	// Scalar applied to every buildup entry's `amount` at apply time. Discrete
	// hits leave this at 1; the continuous-damage constructor sets it to the
	// physics delta so a per-second buildup rate authored on
	// ContinuousDamageData.buildups integrates correctly without forcing the
	// receiver to learn about frame timing. The buildups array itself is a
	// direct reference to the authored resource list (never mutated), so the
	// multiplier is the only allocation-free way to convey scaling.
	public float buildupAmountMultiplier;
	// Magnitude scalar the receiver stamps onto any status effect this hit applies
	// (StatusEffectState.potency) — the attacker's OutgoingLevelScale, so a leveled
	// weapon/mob's poison/burn TICKS harder as ONE stack rather than piling more
	// stacks. Distinct from buildupAmountMultiplier: that governs how much METER a
	// hit deposits (proc rate, deliberately level-independent now); this governs how
	// hard the resulting DoT hits. 1 = base authored numbers.
	public float potency;
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
	// Whether this hit may damage hurtboxes allied with the attacker. Sourced
	// from DamageData.friendlyFire (overridden by hazard-level policy on a
	// DamageZone). Receivers read it off the payload via HurtBox.CanHit — true
	// bypasses the team filter so the hit lands on everyone. Continuous-damage
	// hits leave it false unless the sender overrides it.
	public bool friendlyFire;
	// Faction of whoever launched this hit. Set by the sender (melee/hitscan
	// from the actor's team, projectiles from the firer's, DamageZone from the
	// hazard's). Receivers consult it in their HurtBox.CanHit gate — the hit's
	// own carried context is what lets a receiver decide by team without the
	// attacker walking the receiver's tree. Defaults to Hostile when a sender
	// doesn't set it (only matters for senders that actually gate).
	public ETeam attackerTeam;
	// Crit / backstab modifiers that actually folded onto this hit via
	// ApplyTrigger. Populated receiver-side (Mob.ResolveTriggers) so a receiver
	// can classify its floating HUD number as CRIT! / BACKSTAB! without
	// recomputing the decision. Set only when a matching modifier was present and
	// applied — a hit that is geometrically a crit/backstab but carries no such
	// modifier leaves these clear, so the flag always reflects an applied result,
	// not mere eligibility. OnDizzy has no HUD-text counterpart so isn't recorded.
	public EDamageTriggerFlags triggers;
	// Copy-on-first-write guard for `buildups` — the first AddBuildups fold
	// clones the authored list before appending so the template is never mutated.
	private bool _buildupsOwned;

	public HitInfo(DamageData template, Node source, Vector3 hitDirection = default, ETeam attackerTeam = ETeam.Hostile)
	{
		this.source = source;
		this.hitDirection = hitDirection;
		this.attackerTeam = attackerTeam;
		_buildupsOwned = false;
		triggers = EDamageTriggerFlags.None;
		// Roll armor penetration + crit once up-front so the prediction and the
		// apply see the same outcome even though modifiers may shift the
		// underlying fields between them.
		armorPenetrationRoll = GD.Randf();
		critRoll = GD.Randf();
		armorBypassFraction = 0f;
		if (template != null)
		{
			tags = template.tags;
			// Roll the authored ±variance here, once per hit — downstream readers
			// (prediction, armor chip, aggro) all see the same varied value.
			healthDamage = template.healthDamage * (1f + template.damageVariance * (GD.Randf() * 2f - 1f));
			aggroMultiplier = template.aggroMultiplier;
			hitstun = template.hitstun;
			knockbackDistance = template.knockbackDistance;
			knockbackTime = template.knockbackTime;
			armorPenetration = template.armorPenetration;
			critChance = template.critChance;
			blunt = template.blunt;
			buildups = template.buildups;
			modifiers = template.modifiers;
			dot = template.dot;
			friendlyFire = template.friendlyFire;
		}
		else
		{
			tags = EStat.None;
			healthDamage = 0f;
			aggroMultiplier = 1f;
			hitstun = 0f;
			knockbackDistance = 0f;
			knockbackTime = 0f;
			armorPenetration = 0f;
			critChance = 0f;
			blunt = 0f;
			buildups = null;
			modifiers = null;
			dot = false;
			friendlyFire = false;
		}
		buildupAmountMultiplier = 1f;
		potency = 1f;
	}

	// Continuous-damage constructor. Pre-scales healthDamage by `delta` so the
	// receiver's discrete apply path lands a one-frame chunk; sets
	// `armorBypassFraction` from the template's fractional armorPenetration; flags
	// `dot = true` so the HUD rollup kicks in. Buildups pass through unscaled
	// — `buildupAmountMultiplier` is set to `delta` so the receiver scales
	// per-second rates correctly at apply time without mutating the authored
	// list. No modifiers or status effects — continuous damage authors its
	// status cadence through interval entries on the same DamageZone.
	public HitInfo(ContinuousDamageData template, Node source, float delta, Vector3 hitDirection = default, ETeam attackerTeam = ETeam.Hostile)
	{
		this.source = source;
		this.hitDirection = hitDirection;
		this.attackerTeam = attackerTeam;
		_buildupsOwned = false;
		triggers = EDamageTriggerFlags.None;
		armorPenetrationRoll = GD.Randf();
		critRoll = GD.Randf();
		if (template != null)
		{
			tags = template.tags;
			healthDamage = template.healthDamage * delta;
			blunt = template.blunt;
			armorBypassFraction = template.armorPenetration;
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
		// Continuous-damage hits build threat too — default the aggro multiplier
		// to 1 (aggro tracks the per-frame health chunk) since ContinuousDamageData
		// doesn't author its own. healthDamage is already delta-scaled above.
		aggroMultiplier = 1f;
		hitstun = 0f;
		knockbackDistance = 0f;
		knockbackTime = 0f;
		armorPenetration = 0f;
		critChance = 0f;
		modifiers = null;
		dot = true;
		friendlyFire = false;
		// Per-second buildup rates on ContinuousDamageData scale by delta so a
		// body that stays in the zone for one second accumulates `amount`
		// units regardless of frame rate.
		buildupAmountMultiplier = delta;
		// Continuous zones (gas, trap fields) default to base magnitude; a leveled
		// source (a zone-scaled trap) overrides this after construction.
		potency = 1f;
	}

	// True when the rolled chance landed inside the (possibly modifier-
	// boosted) armor-penetration window. `armorPenetrationRoll` is sampled in
	// [0,1), so an armorPenetration of 0 never fires and an armorPenetration
	// of 1 always fires.
	public bool ArmorPenetrated => armorPenetrationRoll < armorPenetration;

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
			// A modifier for this trigger actually folded — record the flag from
			// the real result so a receiver's HUD text / impact overlay reflects an
			// applied crit/backstab rather than mere geometric eligibility.
			if (trigger == EDamageTrigger.OnCrit) { triggers |= EDamageTriggerFlags.Crit; }
			else if (trigger == EDamageTrigger.OnBackstab) { triggers |= EDamageTriggerFlags.Backstab; }
			EDamageFields f = mod.overrides;
			if ((f & EDamageFields.HealthDamage) != 0) { healthDamage = mod.healthDamage; }
			if ((f & EDamageFields.DamageMultiplier) != 0) { healthDamage *= mod.damageMultiplier; }
			if ((f & EDamageFields.Hitstun) != 0) { hitstun = mod.hitstun; }
			if ((f & EDamageFields.KnockbackDistance) != 0) { knockbackDistance = mod.knockbackDistance; }
			if ((f & EDamageFields.KnockbackTime) != 0) { knockbackTime = mod.knockbackTime; }
			if ((f & EDamageFields.ArmorPenetration) != 0) { armorPenetration = mod.armorPenetration; }
			if ((f & EDamageFields.Blunt) != 0) { blunt = mod.blunt; }
			if ((f & EDamageFields.AddBuildups) != 0)
			{
				AddBuildups(mod.addBuildups);
			}
		}
	}

	// Floating-HUD classification for this hit's damage number. Backstab takes
	// precedence over Crit (an unaware-mob backstab folds both, but BACKSTAB! is
	// the rarer, more meaningful callout); a plain hit reads as DamageLight.
	// Shared by the mob and player damage-received paths so both label crits and
	// backstabs identically.
	public EHudTextType HudDamageType()
	{
		if ((triggers & EDamageTriggerFlags.Backstab) != 0) { return EHudTextType.Backstab; }
		if ((triggers & EDamageTriggerFlags.Crit) != 0) { return EHudTextType.Crit; }
		return EHudTextType.DamageLight;
	}

	// Append effect contributions to this hit (conditional-modifier adds like a
	// backstab's extra dizzy buildup, weapon-mod on-hit enchants like a Flaming
	// weapon's Burning). Copies the source template's array on first write so the
	// authored DamageData.buildups list is never mutated; subsequent appends reuse
	// the owned copy. Folds fire before the receiver's ApplyHitBuildups pass
	// (OnBackstab/OnCrit in Mob.Hit), so appended entries land on the same swing.
	public void AddBuildups(Godot.Collections.Array<StatusEffectBuildup> extra)
	{
		if (extra == null || extra.Count == 0)
		{
			return;
		}
		if (!_buildupsOwned)
		{
			var copy = new Godot.Collections.Array<StatusEffectBuildup>();
			if (buildups != null)
			{
				for (int j = 0; j < buildups.Count; j++)
				{
					copy.Add(buildups[j]);
				}
			}
			buildups = copy;
			_buildupsOwned = true;
		}
		for (int j = 0; j < extra.Count; j++)
		{
			buildups.Add(extra[j]);
		}
	}
}
