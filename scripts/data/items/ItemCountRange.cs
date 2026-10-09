using System.Collections.Generic;
using Godot;

// The one rolled-drop row: a (possibly modded) item, a count with a random
// spread, and the chance the row is present at all. Every loot list that ROLLS
// uses it — species death loot, a mob row's carried loot, chest contents, a
// breakable's own drops and a zone's breakable loot. A certain stack (an
// inventory, a mob's already-rolled carried loot) is an ItemCount instead.
// Rolled count is `count + rng.Next(0, countRange + 1)`, so the defaults produce
// a deterministic 1.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class ItemCountRange : Resource
{
    [Export] public ItemDescriptor item;
    [Export] public int count = 1;
    [Export] public int countRange = 0;

    // Rolled once, on its own, before the count. 1 never draws, so a list of
    // certain rows consumes no extra randomness.
    [Export(PropertyHint.Range, "0,1,0.01")] public float chance = 1f;

    public bool RollPresent(System.Random rng)
    {
        if (chance >= 1f)
        {
            return true;
        }
        return chance > 0f && rng.NextDouble() < chance;
    }

    // The count alone, ignoring `chance` — for a list that deals a fixed total
    // (ZoneGenData.distributedLoot), where presence has no meaning.
    public int RollCount(System.Random rng)
    {
        if (countRange <= 0)
        {
            return count;
        }
        return count + rng.Next(0, countRange + 1);
    }

    // Presence then count: 0 when the row didn't come up.
    public int RollUnits(System.Random rng)
    {
        return RollPresent(rng) ? RollCount(rng) : 0;
    }

    // Roll this row and append the items to `into`: one stack for a stackable
    // item, one state per unit otherwise — a non-stackable item carries its own
    // state (a weapon's ammo), so two of them are two items.
    public void Resolve(System.Random rng, List<ItemState> into)
    {
        AppendStates(item, RollUnits(rng), into);
    }

    public static void ResolveAll(IEnumerable<ItemCountRange> rows, System.Random rng, List<ItemState> into)
    {
        if (rows == null)
        {
            return;
        }
        foreach (ItemCountRange row in rows)
        {
            row?.Resolve(rng, into);
        }
    }

    public static void AppendStates(ItemDescriptor descriptor, int units, List<ItemState> into)
    {
        if (descriptor?.item == null || units <= 0)
        {
            return;
        }
        if (descriptor.item.IsStackable)
        {
            ItemState stack = descriptor.CreateState();
            stack.SetCount(units);
            into.Add(stack);
            return;
        }
        for (int i = 0; i < units; i++)
        {
            into.Add(descriptor.CreateState());
        }
    }
}
