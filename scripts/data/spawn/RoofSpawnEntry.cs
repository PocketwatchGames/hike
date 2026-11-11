using System;
using Godot;

// A sloped roof: a style plus a footprint and a pitch, which RoofMeshBuilder
// turns into geometry at spawn. Every roof is its own shape, so no palette file
// offers one — the editor's roof tool authors the whole entry per placement
// (AuthoredPlacement with no `source`).
//
// `position` is the CENTRE of the footprint at eave level, as RoofSimState
// documents; the footprint turns with the facing like any other entity's yaw.
[GlobalClass]
public partial class RoofSpawnEntry : SpawnEntryData
{
    [Export] public RoofStyleData style;

    // Footprint in metres, before the style's eave overhang.
    [Export(PropertyHint.Range, "1,64,1,or_greater")] public float sizeX = 4f;
    [Export(PropertyHint.Range, "1,64,1,or_greater")] public float sizeZ = 4f;
    [Export] public ERoofSeamAxis seamAxis;
    [Export] public ERoofForm form;
    [Export(PropertyHint.Range, "1,75,1")] public float slopeDegrees = 35f;

    // How far gone this roof is, 0..1 (see RoofSimState.Broken).
    [Export(PropertyHint.Range, "0,1,0.01")] public float broken;

    public override StringName VariantProperty => PropertyName.style;

    public override string VariantName()
    {
        if (style == null)
        {
            return null;
        }
        return string.IsNullOrEmpty(style.displayName)
            ? style.ResourcePath.GetFile().GetBaseName()
            : style.displayName;
    }

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (style == null)
        {
            GD.PushWarning($"RoofSpawnEntry at {position}: no style, so there is no surface to build it from.");
            return;
        }
        ws.AddEntity(new RoofSimState(position, style, sizeX, sizeZ, seamAxis, form, slopeDegrees, broken)
        {
            RotationY = FacingY(context),
        });
    }
}
