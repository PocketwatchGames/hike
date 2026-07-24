using System;

// Static, type-level classification of what a behavior expresses about the
// actor's stance — authored as a resting default on BehaviorData and flowed each
// tick through AIOutput.behaviorFlags, where a behavior MAY compose additional
// bits on top (see BehaviorAttack setting Attacking mid-swing). Mob caches the
// composed value on MobSimState.CurrentBehaviorFlags for out-of-tick queries
// (danger checks, despawn eligibility).
//
// A [Flags] bitmask rather than a single enum so the set is extensible (add a
// bit, not a new virtual/field) and one behavior can carry several. Engaging and
// Disengaging are mutually exclusive on the combat-stance axis — a behavior just
// doesn't set both. Append new bits, never reassign existing ones, so authored
// .tres values keep loading.
[Flags]
public enum EBehaviorFlags
{
    None = 0,
    // Actively pursuing / closing on a target: Attack, Investigate (searching a
    // last-known position), aerial attack, companion wary/attack. Drives the
    // "a hostile is coming for you" half of the interactive danger gate.
    Engaging = 1 << 0,
    // Breaking off from a target: Flee, Retreat (disengage-to-safety), fairy /
    // flyer escape. Never counts as danger — a fleeing mob is leaving.
    Disengaging = 1 << 1,
    // Composed dynamically by combat behaviors while actually mid-swing (the old
    // AIOutput.combatBehavior). Narrower than Engaging: a mob repositioning for
    // an attack is Engaging but not Attacking. Drives the player-facing
    // CombatTracker via Mob.ReportPlayerCombat.
    Attacking = 1 << 2,
}
