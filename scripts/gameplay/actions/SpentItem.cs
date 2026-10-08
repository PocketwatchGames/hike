using System.Collections.Generic;

// Units of one item an action spent from the actor's inventory — what the
// "used" feedback (GameClient.ShowItemsUsed) draws and logs.
public readonly record struct SpentItem(ItemData item, int count)
{
    // Record `count` units of `item`, folding into an entry for the same item.
    public static void Add(List<SpentItem> into, ItemData item, int count)
    {
        if (into == null || item == null || count <= 0)
        {
            return;
        }
        for (int i = 0; i < into.Count; i++)
        {
            if (into[i].item == item)
            {
                into[i] = new SpentItem(item, into[i].count + count);
                return;
            }
        }
        into.Add(new SpentItem(item, count));
    }
}
