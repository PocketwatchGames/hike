// Marker interface for any arrow the WeaponState is tracking as "out in
// the world." Implementations today: ArrowLootSimState (ground pickup) and
// ArrowStuck (visual stuck on a mob). The weapon's outstandingArrows list
// is the single source of truth for ammo accounting — each entry returns
// 1 ammo via WeaponState.OnArrowRemoved when it leaves play, regardless
// of which form it was in. Transition between forms (stuck → loose on
// mob death) goes through DetachArrow so the count stays balanced.
public interface IWeaponArrow
{
	// Fraction in [0, 1] of the way through the arrow's removeTimeMs
	// timeout — 0 just after spawn, 1 about to time out and return ammo
	// to the source weapon. The HUD uses the max across outstandingArrows
	// to surface the next arrow that will replenish ammo. Returns 0 when
	// the arrow has no timeout authored or the runtime age isn't
	// currently trackable (e.g. loose loot whose chunk is unloaded).
	float GetReplenishProgress();
}
