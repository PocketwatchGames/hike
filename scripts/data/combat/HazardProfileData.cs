using Godot;

// Marks a hit as environmental-hazard damage (spike trap, fire column, poison
// cloud, lightning) and describes how threatening it is RELATIVE TO WHOEVER IT
// CATCHES, so one authored trap reads the same wherever it's placed and stays a
// usable tool against any mob. Hazards carry no level of their own; the target's
// toughness is the only axis.
//
// `strength` is the single dial. Against a receiver with max health H it resolves
// to an outmatch factor
//
//   t = strength / (strength + hazardDamageHalfPointPercent * H)
//
// which slides from ~1 (target far weaker than the hazard) toward 0 (target far
// tougher). Each channel below is a band that t positions between: a frail target
// takes the ceiling, a tough one converges on the floor but never below it. Since
// every band is a FRACTION OF MAX HEALTH, absolute damage still rises with the
// pool — a tough mob loses a smaller slice of a much bigger bar.
[Tool]
[GlobalClass]
public partial class HazardProfileData : Resource
{
	// How dangerous this hazard is, in damage units — the only severity dial.
	// Never dealt directly; it positions every band below against the receiver's
	// health pool. A hazard that deals no direct damage at all (poison gas) still
	// needs one: it's what makes the gas nastier to a weakling than to a boss.
	[Export] public float strength = 100f;

	// Direct damage as a fraction of max health — one-shot for a discrete hit,
	// per second for a continuous zone. Floor is the unavoidable bite; keep the
	// ceiling below 1 so a hazard alone never kills from full. Leave both 0 for a
	// hazard that only applies status (a gas cloud).
	[Export(PropertyHint.Range, "0,1,0.01")] public float damageFloorPercent = 0f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float damageCeilingPercent = 0f;

	// Multiplier on the buildup every StatusEffectBuildup this hazard carries
	// deposits, so a tough target takes proportionally longer to catch fire or
	// succumb instead of proccing on the same schedule as a weak one. Bands the
	// authored `amount` rather than replacing it, so a hazard applying several
	// effects keeps their relative rates.
	[Export(PropertyHint.Range, "0,1,0.01")] public float procFloorPercent = 0.25f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float procCeilingPercent = 1f;

	// Per-second fraction of max health ticked by any status effect this hazard
	// applies, replacing that effect's flat damagePerSecond for hazard-applied
	// instances only — the same status landed by a weapon still ticks its authored
	// flat number. Both 0 = no band, and hazard-applied instances tick normally.
	[Export(PropertyHint.Range, "0,1,0.01")] public float dotFloorPercent = 0f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float dotCeilingPercent = 0f;

	public bool HasDotBand => dotCeilingPercent > 0f || dotFloorPercent > 0f;

	// The outmatch factor t (see class comment). 1 = the receiver is negligible
	// against this hazard, 0 = far too tough for it to do more than its floors.
	// `halfPointPercent` is SimData.hazardDamageHalfPointPercent.
	public float Outmatch(float maxHealth, float halfPointPercent)
	{
		if (strength <= 0f || maxHealth <= 0f)
		{
			return 0f;
		}
		float pivot = halfPointPercent * maxHealth;
		return pivot > 0f ? strength / (strength + pivot) : 1f;
	}

	public float DamageFraction(float t) => damageFloorPercent + (damageCeilingPercent - damageFloorPercent) * t;
	public float ProcScale(float t) => procFloorPercent + (procCeilingPercent - procFloorPercent) * t;
	public float DotFraction(float t) => dotFloorPercent + (dotCeilingPercent - dotFloorPercent) * t;
}
