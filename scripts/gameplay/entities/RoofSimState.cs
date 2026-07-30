using Godot;

// A drag-authored sloped roof. Unlike every other entity this carries no
// PackedScene: there is nothing authored to instantiate, only a footprint and a
// pitch that RoofMeshBuilder turns into geometry at spawn. That keeps a roof
// cheap to store (a handful of numbers plus a style reference) and lets an
// author resize one without needing a new prop scene per size.
//
// WorldPosition is the CENTRE of the footprint at EAVE level — the elevation
// the author dragged at. Roof.cs seats the mesh's own origin up at the ridge;
// see there for why the clip anchor and the placement anchor differ.
public class RoofSimState : EntitySimState
{
    public readonly RoofStyleData Style;
    // Footprint in meters (voxels are 1m), before the style's eave overhang.
    public readonly float SizeX;
    public readonly float SizeZ;
    public readonly ERoofSeamAxis SeamAxis;
    public readonly float SlopeDegrees;

    public RoofSimState(Vector3 worldPosition, RoofStyleData style, float sizeX, float sizeZ, ERoofSeamAxis seamAxis, float slopeDegrees)
        : base(worldPosition, scene: null)
    {
        Style = style;
        SizeX = sizeX;
        SizeZ = sizeZ;
        SeamAxis = seamAxis;
        SlopeDegrees = slopeDegrees;
    }

    // A style that failed to load leaves nothing to build a surface from, so
    // the roof stays unspawned rather than materializing untextured.
    public override bool ShouldSpawn(Sim sim) => Style != null;

    public override Node3D CreateEntity(Sim sim)
    {
        return Roof.Create(sim, this);
    }
}
