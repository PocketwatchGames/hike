using System;
using Godot;

// One cascade's curtain. Like a roof this carries no PackedScene — there is
// nothing authored to instantiate, only the lip worldgen measured the water
// pouring over, which WaterfallMeshBuilder sweeps into a sheet at spawn.
//
// The drop itself is AIR and stays air (see HeightMap.Waterfalls): this entity
// is purely audio-visual, with no collision and no path blocking, so the player
// falls through a waterfall exactly as they fall through the gap it draws in.
//
// WorldPosition is the centre of the sheet at the lip — where the water leaves
// the ground — so the entity files into a chunk near the top of the fall.
public class WaterfallSimState : EntitySimState
{
    // The metre-wide steps of the edge the water pours over, in world columns.
    public readonly WaterfallLip[] Lips;
    // World Y of the water SURFACE at the lip and at the landing — the pool
    // above and the pool (or bed) below, not the voxels under them.
    public readonly float TopY;
    public readonly float BottomY;

    public WaterfallSimState(Vector3 worldPosition, float topY, float bottomY, WaterfallLip[] lips)
        : base(worldPosition, scene: null)
    {
        Lips = lips ?? Array.Empty<WaterfallLip>();
        TopY = topY;
        BottomY = bottomY;
    }

    public float FallHeight => Math.Max(0f, TopY - BottomY);

    // A fall with no lip was measured as a cascade but has no edge to pour over,
    // and one whose tier the author hasn't defined is below the size worth
    // drawing.
    public override bool ShouldSpawn(Sim sim)
    {
        WaterfallData data = sim?.SimData?.waterfalls;
        return Lips.Length > 0 && data?.sheetMaterial != null && data.TierFor(FallHeight) != null;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return Waterfall.Create(sim, this);
    }
}
