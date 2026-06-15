using Godot;

// Per-second health change a status effect drips while active, ticked in 1s chunks
// by StatusEffectController.Tick. Null on StatusEffectData.dot = none.
[GlobalClass]
public partial class DamageOverTimeData : Resource
{
	// Per-second HP delta: positive damages (poison), negative heals. Applied in whole
	// 1-second chunks, so fractional rates average out over time.
	[Export] public float damagePerSecond;

	// Fraction of each damage chunk that bypasses armor (1 = armor never soaks it).
	// Ignored for heals.
	[Export(PropertyHint.Range, "0,1,0.01")] public float armorPenetration = 1f;

	// Per-second MAX-health decay (withering): shrinks max health, clamps current down,
	// kills at 0. Deals no hit and shows no damage number. Only the Mob controller wires
	// this — inert on actors that don't (the player today).
	[Export] public float maxHealthDrainPerSecond = 0f;
}
