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
}
