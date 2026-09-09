using System.Collections.Generic;
using Godot;

// The scene-side follow-up a batch of WorldState writes needs: relight and
// re-mesh the chunks whose voxels moved, respawn the entity nodes of the
// chunks whose entity buckets changed.
//
// Every editor mutation funnels through here — brush strokes, subscene stamps,
// undo and redo all need exactly the same follow-up, and batching it means a
// 10k-cell fill pays for one pass rather than one per cell.
public sealed class EditorRefresh
{
    private readonly List<Vector3I> _voxels = new List<Vector3I>();
    private readonly HashSet<Vector3I> _entityChunks = new HashSet<Vector3I>();

    public void AddVoxel(Vector3I cell)
    {
        _voxels.Add(cell);
    }

    public void AddVoxels(List<Vector3I> cells)
    {
        _voxels.AddRange(cells);
    }

    public void AddEntityChunk(Vector3I chunkCoord)
    {
        _entityChunks.Add(chunkCoord);
    }

    public void Apply(Sim sim)
    {
        if (sim != null)
        {
            // Mantleable ledges are baked, not derived at mesh time, so an edit
            // that changes the rock — or moves the props standing on it — has to
            // redo them BEFORE the re-mesh below reads them back.
            VoxelBox touched = TouchedBox();
            if (!touched.IsEmpty)
            {
                ClimbLedgeStamper.RestampRegion(sim.WorldState, touched);
            }
            if (_voxels.Count > 0)
            {
                sim.UpdateLighting(_voxels);
                sim.RebuildChunkMeshes(ChunksToRemesh());
            }
            foreach (Vector3I coord in _entityChunks)
            {
                // Round-trip through the streaming path rather than spawning
                // nodes directly: only it files a node in Sim.ActiveEntities and
                // sets the state's RuntimeNode back-reference, which the
                // editor's entity picking and culling both read.
                sim.UnloadChunkEntities(coord);
                sim.LoadChunkEntities(coord);
            }
        }
        _voxels.Clear();
        _entityChunks.Clear();
    }

    // Everything this batch disturbed, as one box: the voxels written, plus the
    // full extent of any chunk whose entities changed — a prop dropped on a
    // ledge takes the affordance away without touching a voxel.
    private VoxelBox TouchedBox()
    {
        var min = new Vector3I(int.MaxValue, int.MaxValue, int.MaxValue);
        var max = new Vector3I(int.MinValue, int.MinValue, int.MinValue);
        foreach (Vector3I cell in _voxels)
        {
            min = min.Min(cell);
            max = max.Max(cell);
        }
        foreach (Vector3I coord in _entityChunks)
        {
            Vector3I lo = coord * ChunkState.SIZE;
            min = min.Min(lo);
            max = max.Max(lo + Vector3I.One * (ChunkState.SIZE - 1));
        }
        return max.X < min.X ? VoxelBox.Empty : new VoxelBox(min, max);
    }

    private HashSet<Vector3I> ChunksToRemesh()
    {
        var touched = new HashSet<Vector3I>();
        foreach (Vector3I cell in _voxels)
        {
            touched.Add(Sim.WorldToChunkCoord(cell));
        }
        return GrowByOne(touched);
    }

    // Chunks grown by one in every direction: a chunk's mesh culls its faces and
    // shades its corners against its neighbours' voxels and sunlight, so a
    // change on a seam restains the chunk next door.
    public static HashSet<Vector3I> GrowByOne(IEnumerable<Vector3I> chunkCoords)
    {
        var grown = new HashSet<Vector3I>();
        foreach (Vector3I coord in chunkCoords)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        grown.Add(coord + new Vector3I(dx, dy, dz));
                    }
                }
            }
        }
        return grown;
    }
}
