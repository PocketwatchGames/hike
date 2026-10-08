using System;
using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class LootSpawnEntry : SpawnEntryData
{
    // The item plus any permanent mods composed onto it (e.g. a "Fragile" bomb).
    // This is the weapon-customization seam: author the permutation per-spawn on
    // the descriptor rather than baking a unique ItemData for every combination.
    //
    // The palette's `loot` row holds none: like a chest's contents, which item
    // lies here is picked per placement, from every ItemData the world offers.
    [Export] public ItemDescriptor item;

    // One pile holding the whole stack for a stackable item; a non-stackable one
    // carries its own state per unit, so it lies here as `count` piles.
    [Export(PropertyHint.Range, "1,99,1,or_greater")] public int count = 1;

    public override string VariantName() => item?.item?.displayName;

    public override Texture2D PaletteIcon => item?.item?.inventorySprite;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (item?.item == null)
        {
            GD.PushError($"LootSpawnEntry at {position}: no item — pick one (on the placement, in the "
                + "painter's property panel). Not placed.");
            return;
        }
        int units = Math.Max(1, count);
        // A plain single drop leaves Item null and synthesizes a fresh state at
        // pickup (cheaper, matches the world-loot default). Anything with a count
        // or per-instance data to compose carries its states.
        if (units == 1 && !item.NeedsComposedState)
        {
            ws.AddEntity(new LootSimState(position, item.item)
            {
                RotationY = FacingY(context),
            });
            return;
        }
        var states = new List<ItemState>();
        ItemCountRange.AppendStates(item, units, states);
        foreach (ItemState state in states)
        {
            ws.AddEntity(new LootSimState(position, item.item)
            {
                Item = state,
                RotationY = FacingY(context),
            });
        }
    }
}
