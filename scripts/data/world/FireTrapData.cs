using Godot;

// Authored tuning for a fire-column trap. Drives the cycle Idle -> Warning ->
// Active -> Idle on a fixed cadence; per-instance random phase offsets
// (applied at spawn) keep neighbouring traps out of sync so a swamp full of
// them feels alive rather than synchronized.
[GlobalClass]
public partial class FireTrapData : Resource
{
    // Damage applied each tick to bodies inside the column while it's active.
    // The damage zone uses its own tickInterval (authored on the .tscn);
    // healthDamage here is the per-tick payload.
    [Export] public DamageData damageData;

    // Seconds between trigger and ignition. The warning sfx fires at the start
    // of this window so the player has a beat to step out of the column
    // footprint before the fire erupts.
    [Export] public float warningSeconds = 1.0f;

    // Seconds the fire column burns and the damage zone is active. 3.0 by
    // default per the design brief.
    [Export] public float activeSeconds = 3.0f;

    // Seconds between the end of one active phase and the start of the next
    // warning. activeSeconds + warningSeconds + cooldownSeconds = full cycle.
    [Export] public float cooldownSeconds = 4.0f;

    // Maximum random phase offset (in seconds) applied at spawn so adjacent
    // traps fire out of sync. The offset is rolled once per instance from the
    // sim state's stable seed so save/load preserves the rhythm.
    [Export] public float maxPhaseOffsetSeconds = 8.0f;

    // One-shot warning fx (audio + optional smoke puff) played at the start
    // of the Warning phase.
    [Export] public PackedScene warningEffect;

    // One-shot ignition fx (whoosh + initial flame burst) played at the
    // moment the column erupts.
    [Export] public PackedScene igniteEffect;

    // Looping fire-column fx (continuous flame + crackle) created on Active
    // enter and Stop()ped on Active exit so it winds down naturally.
    [Export] public PackedScene columnLoopEffect;
}
