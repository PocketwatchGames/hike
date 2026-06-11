// Marker interface for any arrow the WeaponState is tracking as "out in
// the world." Implementations today: ArrowLootSimState (ground pickup) and
// ArrowStuck (visual stuck on a mob). The weapon's outstandingArrows list
// is the single source of truth for ammo accounting — each entry returns
// 1 ammo via WeaponState.OnArrowRemoved when it leaves play, regardless
// of which form it was in. Transition between forms (stuck → loose on
// mob death) goes through DetachArrow so the count stays balanced.
public interface IWeaponArrow
{
	// Force this arrow out of the world right now and return its 1 ammo to
	// the source weapon (via WeaponState.OnArrowRemoved), exactly as a player
	// pickup would. Called by the weapon's central ammo-recharge timer
	// (Player.TickAmmoRecharge → WeaponState.RecoverOldestArrow) when the
	// timer elapses, so the oldest outstanding arrow is auto-recovered. Both
	// forms route through their normal removal path so the despawn outro and
	// ammo accounting stay uniform with a hand pickup.
	void Recover();
}
