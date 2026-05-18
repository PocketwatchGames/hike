// Marker interface for any arrow the WeaponState is tracking as "out in
// the world." Implementations today: ArrowLootSimState (ground pickup) and
// ArrowStuck (visual stuck on a mob). The weapon's outstandingArrows list
// is the single source of truth for ammo accounting — each entry returns
// 1 ammo via WeaponState.OnArrowRemoved when it leaves play, regardless
// of which form it was in. Transition between forms (stuck → loose on
// mob death) goes through DetachArrow so the count stays balanced.
public interface IWeaponArrow
{
}
