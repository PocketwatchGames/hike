using Godot;

// Per-second health change a status effect drips while active, ticked in 1s chunks
// by StatusEffectController.Tick. Null on StatusEffectData.dot = none.
// [Tool] so the editor can bind it under its [Tool] parent StatusEffectData.
[Tool]
[GlobalClass]
public partial class DamageOverTimeData : Resource
{
	// Per-second HP delta: positive damages (poison), negative heals. Applied in whole
	// 1-second chunks, so fractional rates average out over time.
	[Export] public float damagePerSecond;

	// Per-second damage as a FRACTION of the actor's current max health (0.05 =
	// 5%/s), on top of damagePerSecond. Unlike the flat term it is NOT reduced by
	// tag resistance or defensive level, so the time-to-kill is the same regardless
	// of the health pool — a level-scaled mob with 16x health melts in the same
	// seconds as a base one. Only actors that wire a max-health accessor (the Mob
	// controller) apply it; inert elsewhere (the player, items). Used by sunburn.
	[Export(PropertyHint.Range, "0,1,0.005")] public float fractionMaxHealthPerSecond = 0f;

	// Fraction of each damage chunk that bypasses armor (1 = armor never soaks it).
	// Ignored for heals.
	[Export(PropertyHint.Range, "0,1,0.01")] public float armorPenetration = 1f;

	// Per-second MAX-health decay (withering): shrinks max health, clamps current down,
	// kills at 0. Deals no hit and shows no damage number. Only the Mob controller wires
	// this — inert on actors that don't (the player today).
	[Export] public float maxHealthDrainPerSecond = 0f;
}
