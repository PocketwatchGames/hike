using Godot;

// Persistent state for drops on the ground. Covers both auto-pickup variants
// (walk over to collect — AutoLoot) and interactive variants (press Interact —
// Loot). The carried LootData controls which scene-root class is instantiated
// and what Scene to spawn. Item is the optional carried ItemState; world-
// spawned loot leaves it null, player drops set it so pickup can deposit the
// item back into the inventory. Not currently serialized — phase 1 doesn't
// persist inventory across save/load.
public class LootSimState : EntitySimState
{
    public readonly LootData Data;
    public bool PickedUp;
    public ItemState Item;

    public LootSimState(Vector3 worldPosition, LootData data)
        : base(worldPosition, data?.Scene)
    {
        Data = data;
    }

    // Legacy migration constructor. Pre-LootData saves carried the scene path
    // on disk but no LootData reference (Tag.Prop with the retired AutoLoot /
    // Loot PropType bytes). EntitySerializer uses this overload so the
    // recovered Scene survives the upgrade even when Data is null.
    public LootSimState(Vector3 worldPosition, PackedScene scene, LootData data)
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
