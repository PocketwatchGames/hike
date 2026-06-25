using Godot;

// Axis-aligned Chebyshev-square inset in the chunk grid, centered on CenterChunk
// with HalfExtentChunks reach on each axis. Reproduces the legacy hub carve
// (with EdgeNoiseChunks = 0 it's an exact square). Fixtures anchor at the center.
[GlobalClass]
public partial class BoxBounds : ZoneBounds
{
    [Export] public Vector2I CenterChunk;
    [Export] public Vector2I HalfExtentChunks = new(1, 1);

    // Border wobble: the half-extent is pushed in/out by up to this many chunks
    // per edge, sampled from the context's smooth edge noise. 0 = clean square.
    [Export] public float EdgeNoiseChunks;

    public override bool Contains(int chunkX, int chunkZ, in ZoneBoundsContext ctx)
    {
        float wobble = EdgeNoiseChunks * ctx.SampleEdgeNoise(chunkX, chunkZ);
        float halfX = HalfExtentChunks.X + wobble;
        float halfZ = HalfExtentChunks.Y + wobble;
        return Mathf.Abs(chunkX - CenterChunk.X) <= halfX
            && Mathf.Abs(chunkZ - CenterChunk.Y) <= halfZ;
    }

    public override bool TryGetAnchorChunk(in ZoneBoundsContext ctx, out Vector2I chunk)
    {
        chunk = CenterChunk;
        return true;
    }
}
