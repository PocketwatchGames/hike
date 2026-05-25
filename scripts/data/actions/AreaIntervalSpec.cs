using Godot;

// Weapon-side specification of one interval entry on a spawned damage
// zone. Keys into the firing entity's `damageProfiles` dict so the actual
// DamageData lives at the root of the owning resource (easier to author,
// easier to compare, easier to surface in the HUD). The ItemEvent's
// `areaIntervals` array holds zero or more of these; at spawn time, the
// area-effect handler resolves each key against the firing weapon's /
// mob's damage profiles and configures the DamageZone accordingly.
[GlobalClass]
public partial class AreaIntervalSpec : Resource
{
	// Lookup key into the firing entity's damageProfiles. Resolution
	// failure (missing key) skips this interval silently — the rest of
	// the zone still spawns.
	[Export] public StringName damageProfileKey = new("primary");

	// Seconds between ticks while a body is inside the zone.
	[Export] public float tickInterval = 1f;

	// True = first tick fires the moment a HurtBox enters. False = wait
	// tickInterval before the first hit.
	[Export] public bool tickOnEnter = true;
}
