// How a charged tier interprets player aim input. Authored per-ItemAction
// so a single profile can switch modes between tiers (a bow's snap shot is
// Directional, its charged rain-of-arrows is Positional; a melee axe is
// Directional, its charged throw-with-explosion is Positional). The player's
// aim cursor (Player.AimWorldPosition) is a single world-space ground point
// that both modes write to, so the reticle's ground circle is continuous
// when the active tier flips mode mid-charge.
public enum EAimType
{
	// Stick deflection (or virtual mouse cursor) drives the actor's facing
	// directly — the aim point is `player + ActorForward * weaponRange`,
	// clipped by the world raycast that the reticle already runs. Matches
	// the pre-existing aim behavior for bows / hitscan weapons.
	Directional,
	// Stick deflection (or virtual mouse cursor) is a RATE input that pushes
	// the aim point across the ground per frame. Movement speed scales with
	// the active tier's weapon range so a short-range and long-range
	// positional aim both sweep edge-to-edge in the same wall time. The
	// cursor is clamped to a disk of radius=weaponRange around the player.
	// On entry from a Directional tier the cursor seeds from the previous
	// forward-derived point so the ground circle doesn't jump.
	Positional,
}
