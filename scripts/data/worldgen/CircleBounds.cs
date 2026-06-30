using Godot;

// Circular inset in the chunk grid, centered on CenterChunk with RadiusChunks
// reach. The village uses this. Fixtures anchor at the center.
[GlobalClass]
public partial class CircleBounds : ZoneBounds
{
    [Export] public Vector2I centerChunk;
    [Export] public float radiusChunks = 2f;

    // Border wobble: the radius is pushed in/out by up to this many chunks,
    // sampled from the context's smooth edge noise, so the zone melts into its
    // neighbor with an organic edge instead of a clean circle. 0 = clean circle.
    [Export] public float edgeNoiseChunks;

    public override bool Contains(int chunkX, int chunkZ, in ZoneBoundsContext ctx)
    {
        float dx = chunkX - centerChunk.X;
        float dz = chunkZ - centerChunk.Y;
        float dist = Mathf.Sqrt(dx * dx + dz * dz);
        float radius = radiusChunks + edgeNoiseChunks * ctx.SampleEdgeNoise(chunkX, chunkZ);
        return dist <= radius;
    }

    public override bool TryGetAnchorChunk(in ZoneBoundsContext ctx, out Vector2I chunk)
    {
        chunk = centerChunk;
        return true;
    }
}
