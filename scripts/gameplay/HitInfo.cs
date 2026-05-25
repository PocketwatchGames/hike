using Godot;

// Runtime payload passed to HurtBox.Hit. Senders (weapons, traps, damage
// zones, anything else that deals damage) build this from a DamageData
// template plus runtime context (attacker, hit direction). Receivers read
// the fields they care about — health damage routes through armor, status
// effects are appended to the receiver's _statusEffects, etc.
//
// Conditional behavior (crit when target is stunned, extra knockback on
// stun threshold cross, etc.) is layered via ApplyTrigger: the receiver
// calls it with the trigger that just became true and every matching
// DamageDataModifier from `modifiers` folds onto the live fields here.
public struct HitInfo
{
	public Node source;
	public float healthDamage;
	public float stun;
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
	public Godot.Collections.Array<StatusEffectData> statusEffects;
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
		// Roll pierce once up-front so the prediction and the apply see the
		// same outcome even though modifiers may shift `pierce` between them.
		pierceRoll = GD.Randf();
		if (template != null)
		{
			healthDamage = template.healthDamage;
			stun = template.stun;
			hitstun = template.hitstun;
			knockbackDistance = template.knockbackDistance;
			knockbackTime = template.knockbackTime;
			pierce = template.pierce;
			blunt = template.blunt;
			statusEffects = template.statusEffects;
			modifiers = template.modifiers;
			dot = template.dot;
		}
		else
		{
			healthDamage = 0f;
			stun = 0f;
			hitstun = 0f;
			knockbackDistance = 0f;
			knockbackTime = 0f;
			pierce = 0f;
			blunt = 0f;
			statusEffects = null;
			modifiers = null;
			dot = false;
		}
	}

	// True when the rolled chance landed inside the (possibly modifier-
	// boosted) pierce window. `pierceRoll` is sampled in [0,1), so a pierce
	// of 0 never fires and a pierce of 1 always fires.
	public bool Pierced => pierceRoll < pierce;

	// Fold every modifier whose trigger equals `trigger` onto the live
	// fields. Callers fire one trigger per condition crossing (OnCrit when
	// this hit crits, OnStun when this hit crossed stunThreshold, etc.); a
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
			if ((f & EDamageFields.Stun) != 0) { stun = mod.stun; }
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
