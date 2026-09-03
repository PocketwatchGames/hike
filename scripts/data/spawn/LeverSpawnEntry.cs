using System;
using Godot;

// A pull-lever that throws every loaded trapdoor sharing its target link tag.
// The other half of TrapdoorSpawnEntry — type the same word on both.
//
// A lever wired to nothing does nothing, so an unedited placement is refused
// loudly rather than shipping a handle that moves and opens no floor. Nothing
// else reports it: the runtime simply finds no trapdoor to trigger.
[GlobalClass]
public partial class LeverSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    // The linkTag of the trapdoor(s) this lever throws.
    [Export] public string targetLinkTag = "";

    public override string VariantName()
        => string.IsNullOrEmpty(targetLinkTag) ? null : targetLinkTag;

    // Aimable: a lever is mounted facing the room it is pulled from.
    public override bool UsesFacing => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        if (string.IsNullOrEmpty(targetLinkTag))
        {
            GD.PushError($"LeverSpawnEntry at {position}: no target link tag — which trapdoor a "
                + "lever throws is authored on the PLACEMENT (edit it in the painter and give the "
                + "trapdoor the same tag), not on the shared palette entry. Not placed.");
            return;
        }
        ws.AddEntity(new LeverSimState(position, context?.FacingY ?? 0f, scene)
        {
            TargetLinkTag = targetLinkTag,
        });
    }
}
