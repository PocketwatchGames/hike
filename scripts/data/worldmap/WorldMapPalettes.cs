using Godot;

// The painted document's palette slot ledgers, in their own `.tres` beside the
// layer images and the placements.
//
// Its own file for exactly the reason WorldMapPlacements has one: the painter
// WRITES this at runtime — every session appends whatever new resources have
// appeared on disk — while WorldMapData holds the bake settings an author edits
// in the inspector. Keeping them in one file would mean opening the painter
// rewrites the resource the Godot editor may have open, which is how genData got
// stripped twice.
//
// A document with no ledger yet is not an error: every palette starts empty and
// the first discovery fills it. What must never happen is a ledger being
// REORDERED, so nothing here removes or sorts.
[GlobalClass]
public partial class WorldMapPalettes : Resource
{
    [Export] public WorldMapPaletteLedger[] ledgers = System.Array.Empty<WorldMapPaletteLedger>();

    // This palette's ledger, created empty if the document has never carried
    // one. Never returns null: an absent ledger and an empty one mean the same
    // thing, and making the caller tell them apart buys nothing.
    public WorldMapPaletteLedger For(string palette)
    {
        foreach (WorldMapPaletteLedger ledger in ledgers)
        {
            if (ledger != null && ledger.palette == palette)
            {
                return ledger;
            }
        }
        var made = new WorldMapPaletteLedger { palette = palette };
        var list = new System.Collections.Generic.List<WorldMapPaletteLedger>(ledgers) { made };
        ledgers = list.ToArray();
        return made;
    }
}
