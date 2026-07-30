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

    // Chunks holding changed voxels, grown by one in every direction: a chunk's
    // mesh culls its faces and shades its corners against its neighbours'
    // voxels, so a change on a seam restains the chunk next door.
    private HashSet<Vector3I> ChunksToRemesh()
    {
        var touched = new HashSet<Vector3I>();
        foreach (Vector3I cell in _voxels)
        {
            touched.Add(Sim.WorldToChunkCoord(cell));
        }

        var grown = new HashSet<Vector3I>();
        foreach (Vector3I coord in touched)
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
