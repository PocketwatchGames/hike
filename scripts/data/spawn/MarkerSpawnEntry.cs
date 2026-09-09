using System;
using Godot;

// A tagged position with no body — a spot in an authored scene that whoever
// STAMPS the scene decides the content of (SubsceneVariant fills the pool by
// this tag). Editor-only art: worldgen consumes markers at stamp time, so the
// pin never reaches a running game.
//
// The pool is the per-placement value, and `variants` is the list of pools this
// world uses — advisory, so a marker can still be tagged with a pool nobody has
// written down yet. An untagged marker joins no pool and so is never filled,
// which is a placement doing nothing rather than a placement doing harm, so it
// warns instead of refusing.
[GlobalClass]
public partial class MarkerSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    // Which pool a marker placed from this entry joins.
    [Export] public string poolTag = "";

    // The pool names an author may pick from. What `poolTag` MAY be, not what
    // it is — see SpawnEntryData.NameCandidates: an answer here is a list the
    // editor offers, never a set the value is checked against.
    [Export] public string[] variants = Array.Empty<string>();

    public override StringName VariantProperty => PropertyName.poolTag;

    public override string VariantName() => string.IsNullOrEmpty(poolTag) ? null : poolTag;

    public override string[] NameCandidates(StringName property)
        => property == PropertyName.poolTag && variants.Length > 0
            ? variants : base.NameCandidates(property);

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        if (string.IsNullOrEmpty(poolTag))
        {
            GD.PushWarning($"MarkerSpawnEntry at {position}: no pool tag, so nothing will ever be "
                + "placed on this marker. Set one on the placement.");
        }
        ws.AddEntity(new MarkerSimState(position, poolTag ?? "", scene)
        {
            RotationY = FacingY(context),
        });
    }
}
