using Godot;

// Per-instance state for an arrow dropped at a shot's impact point. Carries
// a runtime reference back to the WeaponState that fired the arrow so the
// weapon can recover ammo when the arrow is removed (player pickup or
// LootData.removeTimeMs timeout). The reference is runtime-only — save/load
// flattens this back to a plain LootSimState (the weapon binding is not
// part of the persisted shape, mirroring how WeaponState itself isn't
// world-serialized). After a reload the arrow becomes a generic pickup.
//
// CanPickup / ShouldDepositToInventory / OnRemovedFromWorld implement the
// arrow-specific contract on top of the shared Loot scene:
//   - pickup is gated to the player who still holds the source weapon
//   - pickup does not deposit into inventory (the arrow returns to the
//     weapon's ammo pool, not the backpack)
//   - removal-for-any-reason routes back to WeaponState.OnArrowRemoved so
//     the ammo bump is uniform across pickup, timeout, or future causes
//     (explosion damage, etc.).
public class ArrowLootSimState : LootSimState, IWeaponArrow
{
    // Source weapon — the bow that fired this arrow. Runtime-only; not
    // serialized. Null after a save/load round-trip; the arrow then
    // degrades into a normal Loot pickup.
    public WeaponState SourceWeapon;

    public ArrowLootSimState(Vector3 worldPosition, ArrowLootData data, WeaponState sourceWeapon)
        : base(worldPosition, data)
    {
        SourceWeapon = sourceWeapon;
    }

    public override bool CanPickup(Player player)
    {
        // No source weapon = no binding (post-load fallback) — treat as a
        // normal pickup. Otherwise the player must still own that specific
        // WeaponState anywhere in the inventory — equipped OR holstered in the
        // backpack. Recovered ammo lands on the bound WeaponState regardless
        // of equip state, so a stashed bow still tops up. Only fully dropping
        // the weapon out of the inventory locks recovery (its central recharge
        // timer also pauses then); re-acquiring it resumes both.
        if (SourceWeapon == null)
        {
            return base.CanPickup(player);
        }
        return player?.Inventory != null && player.Inventory.Contains(SourceWeapon);
    }

    public override bool ShouldDepositToInventory() => SourceWeapon == null;

    public override void OnRemovedFromWorld()
    {
        SourceWeapon?.OnArrowRemoved(this);
    }

    public void Recover()
    {
        // Live in the world — run the normal despawn path (pickup outro +
        // OnRemovedFromWorld → ammo bump). Mirrors a hand pickup / old timeout.
        if (RuntimeNode is Loot loot && GodotObject.IsInstanceValid(loot))
        {
            loot.RecoverArrow();
            return;
        }
        // Not currently spawned (its chunk is unloaded). Bump ammo and latch
        // PickedUp directly so the arrow doesn't respawn when the chunk
        // re-streams; there's no node to play an outro on.
        PickedUp = true;
        OnRemovedFromWorld();
    }
}
