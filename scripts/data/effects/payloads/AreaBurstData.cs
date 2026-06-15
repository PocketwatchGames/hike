using Godot;

// A one-shot AoE burst a status effect fires off the carrying actor — used for both
// the on-attack-impact burst (StatusEffectData.attackImpact) and the on-dash burst
// (dashBurst, which the controller applies with radial knockback).
[GlobalClass]
public partial class AreaBurstData : Resource
{
	// Damage dealt in range. May carry zero healthDamage — knockback and
	// StatusEffectBuildup payloads still apply. Null damage + null fx = no burst.
	[Export] public DamageData damage;

	// Burst radius in meters.
	[Export(PropertyHint.Range, "0.5,10,0.5,or_greater")] public float radius = 2f;

	// One-shot visual + sound, world-parented at the burst origin.
	[Export] public PackedScene fx;
}
