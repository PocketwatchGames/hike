// Named ceiling on the actor's movement speed while a charge tier is held.
// Authored per-ItemAction as `maxSpeedCharging` (whole Charging phase) and/or
// `chargedSpeedMax` (engages only once the tier is fully charged); the player
// clamps its computed move speed down to the matching gait speed
// (Player.moveSpeed table) while Charging — a cap, not a multiplier, so it
// never speeds the player up.
// Only the player consumes the speed value; mobs read it only through
// ActionRunner.LocksMovement (Stationary == rooted).
public enum EChargeSpeedCap
{
	// Fully rooted while charging (maps to 0). Counts as LocksMovement.
	Stationary,
	// Capped at PlayerData.sneakSpeed — a heavy windup / ranged draw crawl.
	Sneak,
	// Capped at PlayerData.moveSpeed — can jog but not sprint while charging.
	Run,
	// Capped at PlayerData.sprintSpeed — effectively unrestricted (the player
	// can't exceed sprint speed anyway). The default for un-authored tiers.
	Sprint,
}
