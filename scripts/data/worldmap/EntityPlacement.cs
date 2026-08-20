using Godot;

// One hand-placed entity: which spawn entry, where, and which way it faces.
//
// The entry is referenced DIRECTLY rather than by an index into the document's
// palette, so reordering the palette cannot silently turn every chest in the
// world into a goblin.
//
// A `SpawnEntryData` rather than a prop scene, because that is what the bake
// already knows how to place: the same entries the scatter layers use, spawned
// through the same `TrySpawn`. It also means one palette covers props, mobs,
// chests, loot and NPCs instead of one list per kind.
[GlobalClass]
public partial class EntityPlacement : Resource
{
    [Export] public SpawnEntryData entry;
    [Export] public Vector2I anchorXZ;

    // Quarter turns, sharing SubscenePlacement's enum. Entities can hold any
    // yaw, but the painter is a map: 90 degrees is what a author can aim at a
    // metre grid, and it is the same R/F the scene tool uses.
    [Export] public ESubsceneRotation rotation;
}
