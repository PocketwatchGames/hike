using System;

// Condition selector on DamageDataModifier — when a hit is being applied,
// the receiver calls HitInfo.ApplyTrigger with the trigger that matches
// the current state (crit-eligible target, this hit crossed the stun
// threshold, etc.), and every modifier whose `trigger` equals it folds
// onto the live hit payload.
//
// Wire values are stable — append new entries, never reassign existing
// numbers, so existing .tres files keep loading.
public enum EDamageTrigger
{
	OnCrit = 0,
	OnStun = 1,
	OnBackstab = 2,
}

// Bitset of trigger conditions that fire on a single hit — populated by the
// receiver (HurtBox.QueryHitTriggers) so the attacker can layer per-tier
// impact overlays (ItemAction.impactCritEffect / impactBackstabEffect) on
// top of the base impact fx. OnStun isn't represented because it depends on
// the cumulative stun meter, which is mutated by the hit itself — predicting
// it from outside the receiver would require duplicating the meter math.
[Flags]
public enum EDamageTriggerFlags
{
	None = 0,
	Crit = 1 << 0,
	Backstab = 1 << 1,
}
