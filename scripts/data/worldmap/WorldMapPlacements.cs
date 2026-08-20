using Godot;

// The painted document's subscene stamps, in their own `.tres` beside the layer
// images.
//
// Not a raster and not a field on WorldMapData. Not a raster because a stamp is
// "this scene, here, facing that way" — a per-column byte cannot hold an
// identity, an orientation and a footprint that overlaps its neighbours. Not a
// field on the document because the painter WRITES this list at runtime, and
// WorldMapData holds the bake settings and the palettes the author edits in the
// inspector; keeping them in one file would mean every placement drag rewrites
// the resource the Godot editor may have open, which is how genData got stripped
// twice. Saved by the painter's own Save, alongside the images it also owns.
[GlobalClass]
public partial class WorldMapPlacements : Resource
{
    [Export] public SubscenePlacement[] placements = System.Array.Empty<SubscenePlacement>();

    // Hand-placed individual entities. Same file as the subscene stamps because
    // they are the same kind of thing — a point, an identity and a facing — and
    // the painter writes both at runtime.
    [Export] public EntityPlacement[] entities = System.Array.Empty<EntityPlacement>();

    // Where the player starts, in world XZ. Unset means the world origin, which
    // is what the bake used before this could be authored.
    [Export] public bool hasSpawn;
    [Export] public Vector2I spawnXZ;
}
