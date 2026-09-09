using System;
using Godot;

[GlobalClass]
public partial class LootSpawnEntry : SpawnEntryData
{
    // The item plus any permanent mods composed onto it (e.g. a "Fragile" bomb).
    // This is the weapon-customization seam: author the permutation per-spawn on
    // the descriptor rather than baking a unique ItemData for every combination.
    [Export] public ItemDescriptor item;

    // The drops this entry offers. The group's own definition — what `item` MAY
    // be — so an author curates which of the game's hundreds of items are worth
    // standing on the ground, rather than a palette offering all of them.
    [Export] public ItemDescriptor[] variants = Array.Empty<ItemDescriptor>();

    public override StringName VariantProperty => PropertyName.item;

    public override string VariantName() => item?.item?.displayName;

    public override Texture2D PaletteIcon => item?.item?.inventorySprite;

    public override Resource[] ResourceCandidates(StringName property)
        => property == PropertyName.item && variants.Length > 0
            ? variants : base.ResourceCandidates(property);

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (item?.item == null)
        {
            return;
        }
        var simState = new LootSimState(position, item.item)
        {
            RotationY = FacingY(context),
        };
        // Eager-create the carried ItemState only when the descriptor has
        // per-instance data to compose (mods or ephemeral) — plain drops leave
        // Item null on the sim state and synthesize a fresh state at pickup
        // (cheaper, matches the world-loot default). Mirrors the fairy-loot
        // composition path in Sim.SpawnLoot.
        if (item.NeedsComposedState)
        {
            simState.Item = item.CreateState();
        }
        ws.AddEntity(simState);
    }
}
