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

    public override PackedScene PaletteScene => scene;

    // Shared key a lever pulls this trapdoor by. Empty = player-operated only.
    [Export] public string linkTag = "";

    // The tags an author may pick from. Advisory, like the lever's — and the
    // blank an unlinked trapdoor keeps is not in it, because that is the field's
    // default rather than one of the choices.
    [Export] public string[] variants = Array.Empty<string>();

    public override StringName VariantProperty => PropertyName.linkTag;

    public override string[] NameCandidates(StringName property)
        => property == PropertyName.linkTag && variants.Length > 0
            ? variants : base.NameCandidates(property);

    public override string VariantName()
        => string.IsNullOrEmpty(linkTag) ? null : linkTag;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        // The facing is the hinge side.
        ws.AddEntity(new TrapdoorSimState(position, FacingY(context), scene)
        {
            LinkTag = linkTag ?? "",
        });
    }
}
