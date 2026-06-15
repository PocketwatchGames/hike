using Godot;

// A PackedScene paired with a relative selection weight, for authoring
// weighted-random scene palettes (e.g. a TerrainKitData's tree palette).
// Higher Weight = drawn more often, measured against the other entries in the
// same list — the values are relative, not probabilities, so a list of
// {2, 1, 1} picks the first scene half the time. Feed a list of these into a
// WeightedList to draw one.
[GlobalClass]
public partial class WeightedScene : Resource
{
    [Export] public PackedScene Scene;
    [Export] public float Weight = 1f;

    // Refill `list` with the non-null scenes in `entries`, keyed by their
    // Weight. Reuses the caller's list (cleared first) so hot scatter loops
    // don't allocate.
    public static void Fill(WeightedList<PackedScene> list, WeightedScene[] entries)
    {
        list.Clear();
        if (entries != null)
        {
            foreach (WeightedScene entry in entries)
            {
                if (entry?.Scene != null)
                {
                    list.Add(entry.Scene, entry.Weight);
                }
            }
        }
    }

    // Convenience for one-shot callers (e.g. the editor's single placement):
    // allocates a fresh WeightedList and fills it. Hot loops should reuse a
    // list via Fill instead.
    public static WeightedList<PackedScene> BuildList(WeightedScene[] entries)
    {
        var list = new WeightedList<PackedScene>();
        Fill(list, entries);
        return list;
    }
}
