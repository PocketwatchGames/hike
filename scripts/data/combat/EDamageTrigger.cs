using System;

// Condition selector on DamageDataModifier — when a hit is being applied,
// the receiver calls HitInfo.ApplyTrigger with the trigger that matches
// the current state (crit-eligible target, this hit crossed the dizzy
// buildup threshold, etc.), and every modifier whose `trigger` equals it
// folds onto the live hit payload.
//
// Wire values are stable — append new entries, never reassign existing
// numbers, so existing .tres files keep loading.
public enum EDamageTrigger
{
	OnCrit = 0,
	// Fires on the hit whose buildup contribution crossed the threshold of a
	// StatusEffectData whose applyTrigger == OnDizzy (i.e. Dizzy itself today).
	// Named "OnDizzy" rather than "OnApply" so the trigger's meaning ties to
	// the effect everyone authors against; future effects with their own
	// triggers would append new entries here.
	OnDizzy = 1,
	OnBackstab = 2,
	// Sentinel "no trigger" used by StatusEffectData.applyTrigger to mark
	// buildup-apply effects that don't fold any conditional modifiers when
	// they cross the threshold. Appended (not inserted) so existing .tres
	// trigger values keep their wire meanings.
	None = 3,
}

// Bitset of trigger conditions that fire on a single hit — populated by the
// receiver (HurtBox.QueryHit) so the attacker can layer per-tier
// impact overlays (ItemAction.impactCritEffect / impactBackstabEffect) on
// top of the base impact fx. OnDizzy isn't represented because it depends on
// the cumulative buildup meter, which is mutated by the hit itself —
// predicting it from outside the receiver would require duplicating the
// meter math.
[Flags]
public enum EDamageTriggerFlags
{
	None = 0,
	Crit = 1 << 0,
	Backstab = 1 << 1,
}
