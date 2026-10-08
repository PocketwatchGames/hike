using System.Collections.Generic;
using Godot;

// Authored count + random spread for a (possibly modded) item — the authored
// twin of ItemCount. Rolled at spawn into concrete ItemStates, so a chest that
// rolled "4 mushrooms" at worldgen always holds 4. Rolled count is
// `count + rng.Next(0, countRange + 1)`, so the defaults produce a
// deterministic 1.
[GlobalClass]
public partial class ItemCountRange : Resource
{
    [Export] public ItemDescriptor item;
    [Export] public int count = 1;
    [Export] public int countRange = 0;

    public int RollCount(System.Random rng)
    {
        return count + rng.Next(0, Mathf.Max(0, countRange) + 1);
    }

    // Roll this range and append the items to `into`: one stack for a stackable
    // item, one state per unit otherwise — a non-stackable item carries its own
    // state (a lantern's fuel), so two of them are two items.
    public void Resolve(System.Random rng, List<ItemState> into)
    {
        AppendStates(item, RollCount(rng), into);
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
