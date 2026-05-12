using Godot;

// Persistent state for drops on the ground. All loot uses one scene (Loot) and
// one runtime class — the auto-pickup vs interact-pickup decision happens at
// run time inside Loot based on the player's inventory state. Item is the
// optional carried ItemState; world-spawned loot leaves it null (pickup
// synthesizes a fresh ItemState from Data), player drops set it so pickup
// deposits the original stack. RequireInteract latches the loot into "press to
// pick up" mode regardless of inventory state — set on player-initiated drops
// so freshly dropped piles don't immediately re-enter the inventory. Not
// currently serialized — phase 1 doesn't persist inventory across save/load,
// so RequireInteract is in-memory only.
public class LootSimState : EntitySimState
{
    public readonly ItemData Data;
    public bool PickedUp;
    public ItemState Item;
    public bool RequireInteract;

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
}
