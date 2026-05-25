using Godot;

// Per-hit (or per-tick) contribution to a status-effect buildup meter.
// Authored as an entry in DamageData.buildups / ContinuousDamageData.buildups;
// the receiver's StatusEffectController accumulates `amount` into the meter
// for `effect`, applies the effect when the meter crosses 1, and decays the
// remainder according to the effect's buildupRemovalDelay / buildupRemovalSpeed.
[GlobalClass]
public partial class StatusEffectBuildup : Resource
{
	// Status effect whose buildup meter this contribution feeds. Same instance
	// may be referenced by many DamageData templates — the meter lives on the
	// receiver, keyed by this resource reference.
	[Export] public StatusEffectData effect;

	// Buildup units added to the receiver's meter on hit. Crossing 1 applies
	// `effect` once and (per StatusEffectData.clearBuildupOnApply) either zeros
	// the meter or subtracts 1 for the next apply. Per-tick paths (continuous
	// damage zones, dot intervals) scale by delta before adding.
	[Export] public float amount = 0f;
}
