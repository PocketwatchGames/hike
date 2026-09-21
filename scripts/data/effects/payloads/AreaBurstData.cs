using Godot;

// An instant area blast: damage that lands once, in the frame it fires, on
// everything in range, plus its visual. Fired by AreaBurst.Fire — a status
// effect's attackImpact / dashBurst, an ItemEvent's AreaBurst (a projectile
// bursting on impact), an exploding barrel. Knockback pushes away from the
// center. A hazard that should LINGER is a DamageZone, not one of these.
// [Tool] so the editor can bind it under its [Tool] parents (StatusEffectData,
// ItemEvent).
[Tool]
[GlobalClass]
public partial class AreaBurstData : Resource
{
	// Damage dealt in range. May carry zero healthDamage — knockback and
	// StatusEffectBuildup payloads still apply. Null damage + null fx = no burst.
	[Export] public DamageData damage;

	// Burst radius in meters.
	[Export(PropertyHint.Range, "0.5,10,0.5,or_greater")] public float radius = 2f;

	// 0 = a sphere. Otherwise a column that reaches `radius` below the center and
	// this many meters above it, so a ground blast catches airborne targets.
	[Export(PropertyHint.Range, "0,20,0.5,or_greater")] public float height = 0f;

	// One-shot visual + sound (an Fx scene), world-parented at the burst origin.
	[Export] public PackedScene fx;
}
