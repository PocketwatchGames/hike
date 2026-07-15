using Godot;

// Persistent state for drops on the ground. All loot uses one scene (Loot) and
// one runtime class — the auto-pickup vs interact-pickup decision happens at
// run time inside Loot based on the player's inventory state. Item is the
// optional carried ItemState; world-spawned loot leaves it null (pickup
// synthesizes a fresh ItemState from Data), player drops set it so pickup
// deposits the original stack. RequireInteract latches the loot into "press to
// pick up" mode regardless of inventory state — set on player-initiated drops
// so freshly dropped piles don't immediately re-enter the inventory.
// RequireInteract is in-memory only (inventory is not yet serialized).
public class LootSimState : EntitySimState
{
    public readonly ItemData Data;
    public bool PickedUp;
    public ItemState Item;
    public bool RequireInteract;
    // True for loot that dropped at runtime (mob kills, dig yields, player drops)
    // rather than authored worldgen ground loot. A full spawn-state reset
    // (World.ResetSpawns) sweeps these so a revived encounter doesn't leave
    // the last life's spoils lying around, while authored loot (Dropped == false)
    // stays put. In-memory only, like RequireInteract.
    public bool Dropped;

    public LootSimState(Vector3 worldPosition, ItemData data)
        : base(worldPosition, scene: null)
    {
        Data = data;
    }

    public override Node3D CreateEntity(World world)
    {
        if (PickedUp)
        {
            return null;
        }
        GameClient gc = GameClient.Current;
        PackedScene scene = gc?.lootScene;
        if (scene == null)
        {
            return null;
        }
        return Loot.Create(world, this, scene);
    }

    // Overrides for the per-loot-kind pickup contract — base implementation
    // is the default "any player can pick up and the item goes into the
    // backpack" behavior. ArrowLootSimState overrides these to gate pickup
    // on the source weapon being equipped and to route the picked-up arrow
    // back to the weapon's ammo pool instead of the inventory.

    // Player-side gate consulted alongside the inventory-fit checks in
    // Loot.CanActorInteract / TryDepositItem. Return false to refuse
    // pickup outright.
    public virtual bool CanPickup(Player player) => true;

    // When false, FinalizePickup skips the inventory-deposit step and just
    // runs the despawn animation + OnRemovedFromWorld hook. Use for loot
    // whose pickup semantics aren't "add to backpack" (e.g. arrows that
    // return ammo to the source weapon).
    public virtual bool ShouldDepositToInventory() => true;

    // Called from Loot after the world-removal commit (PickedUp set true)
    // for any removal cause — player pickup, LootData.removeTimeMs timeout,
    // future causes. Not invoked by deserialization; load-time PickedUp
    // values from disk skip this hook.
    public virtual void OnRemovedFromWorld() { }
}
