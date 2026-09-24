using System;
using Godot;

// A tagged position a ROAD should reach — a front door, the gap in a square's
// wall. Editor-only art like the marker pin: worldgen turns hints into points of
// interest at stamp time and the pin never reaches a running game.
//
// The tag is the per-placement value and does two jobs: a RoadConnection
// addresses "<placement>.<tag>", and it picks the tread an auto-linked spur gets
// (WorldGenData.pathHintProfiles). So "door" and "gate" are genuinely different
// hints, not one hint with a setting.
//
// A hint nothing addresses is inert rather than wrong — a scene may carry more
// doors than a road pass chooses to reach — so an empty tag warns and places.
[GlobalClass]
public partial class PathHintSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    [Export] public string hintTag = "";

    // What `hintTag` MAY be. Advisory, so a profile added to WorldGenData later
    // is reachable before anyone remembers to list it here.
    [Export] public string[] variants = Array.Empty<string>();

    public override StringName VariantProperty => PropertyName.hintTag;

    public override string VariantName() => string.IsNullOrEmpty(hintTag) ? null : hintTag;

    public override string[] NameCandidates(StringName property)
        => property == PropertyName.hintTag && variants.Length > 0
            ? variants : base.NameCandidates(property);

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        if (string.IsNullOrEmpty(hintTag))
        {
            GD.PushWarning($"PathHintSpawnEntry at {position}: no hint name, so no road can "
                + "address it. Set one on the placement.");
        }
        ws.AddEntity(new PathHintSimState(position, hintTag ?? "", scene)
        {
            RotationY = FacingY(context),
        });
    }
}
