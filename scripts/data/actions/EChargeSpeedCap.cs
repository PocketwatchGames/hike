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
	// Capped at PlayerData.moveSpeed — the player's full running gait.
	Run,
	// Uncapped: moveSpeed is the fastest the player travels under their own
	// input, so nothing is clamped. The default for un-authored tiers.
	Unrestricted,
}
