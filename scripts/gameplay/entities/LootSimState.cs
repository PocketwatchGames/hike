using Godot;

// Persistent state for drops on the ground. Covers both auto-pickup variants
// (walk over to collect — AutoLoot) and interactive variants (press Interact —
// Loot). The carried ItemData controls which scene-root class is instantiated
// (via AutoPickup) and what Scene to spawn. Item is the optional carried
// ItemState; world-spawned loot leaves it null (pickup creates a fresh
// ItemState from Data), player drops set it so pickup deposits the original
// stack. Not currently serialized — phase 1 doesn't persist inventory across
// save/load.
public class LootSimState : EntitySimState
{
    public readonly ItemData Data;
    public bool PickedUp;
    public ItemState Item;

    public LootSimState(Vector3 worldPosition, ItemData data)
        : base(worldPosition, data?.Scene)
    {
        Data = data;
    }

    // Legacy migration constructor. Pre-LootData saves carried the scene path
    // on disk but no ItemData reference (Tag.Prop with the retired AutoLoot /
    // Loot PropType bytes). EntitySerializer uses this overload so the
    // recovered Scene survives the upgrade even when Data is null.
    public LootSimState(Vector3 worldPosition, PackedScene scene, ItemData data)
        : base(worldPosition, scene)
    {
        Data = data;
    }

    public override Node3D CreateEntity(World world)
    {
        if (PickedUp)
        {
            return null;
        }
        // Null Data falls back to auto-pickup — historical behavior before
        // LootData existed. Authored callsites should always supply Data.
        bool autoPickup = Data == null || Data.AutoPickup;
        return autoPickup ? AutoLoot.Create(world, this) : Loot.Create(world, this);
    }
}
