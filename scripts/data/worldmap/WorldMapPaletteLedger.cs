using Godot;

// One palette's SLOT LEDGER: slot N of this palette is the resource at
// `slots[N]`.
//
// It exists because the painted rasters store an INDEX into a palette —
// zone.png, region.png, ground.png, scatter.png, mobs.png, paving.png and
// water_type.png all do — which makes the slot a file occupies a wire format.
// A slot that moves silently re-zones or re-textures every world already baked
// against it, with the stored bytes still perfectly valid. Same rule and same
// reason as KitPaletteData: APPEND ONLY, and the painter is the only thing that
// writes it.
//
// PATHS, not typed [Export] references. A path survives its file being deleted:
// the slot resolves to null, keeps its index, and is named in the warning, which
// is the only useful behaviour when columns painted with it are still out there.
// A typed reference would come back as a broken ext_resource with every slot
// after it shifted — the exact corruption the ledger is here to prevent.
[GlobalClass]
public partial class WorldMapPaletteLedger : Resource
{
    // Which palette this is, by the stable id in WorldMapPaletteSource.Table. A
    // string rather than the enum-ish id as an int, because a persisted key must
    // not move when C# is edited.
    [Export] public string palette = "";

    [Export] public string[] slots = System.Array.Empty<string>();
}
