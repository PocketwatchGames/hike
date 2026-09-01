using System;
using Godot;

// A floor trapdoor the player can toggle by interacting, and that a LeverSpawnEntry
// can throw from across the room when both carry the same link tag.
//
// WHICH lever opens WHICH trapdoor is a fact about one room, not about trapdoors
// in general, so the shared palette entry carries no tag: an author types the
// same word on the lever and on the trapdoor, and the painter forks this entry
// into each placement on first edit (EntityPlacement.EditableEntry).
//
// An empty tag is a legitimate authoring — a plain player-operated trapdoor —
// which is why this one places happily without an edit and the lever does not.
//
// Deliberately NOT RequireFlatTerrain: a trapdoor is a hand-placed floor leaf,
// often on a built deck or a tunnel floor at an authored floorY, and the flat
// gate measures the TERRAIN column under it. Requiring it would silently drop
// every trapdoor set into a floor over sloping ground.
[GlobalClass]
public partial class TrapdoorSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    // Shared key a lever pulls this trapdoor by. Empty = player-operated only.
    [Export] public string linkTag = "";

    public override string VariantName()
        => string.IsNullOrEmpty(linkTag) ? null : linkTag;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new TrapdoorSimState(position, context?.FacingY ?? 0f, scene)
        {
            LinkTag = linkTag ?? "",
        });
    }
}
