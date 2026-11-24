using System.Collections.Generic;
using Godot;

// Sim — thin delegation to ChunkManager: world↔chunk coordinate conversion,
// spawn-chunk readiness, voxel lighting / light sources, fog toggles, and mesh
// rebuilds. Sim is the single public face callers reach; the actual work
// lives on ChunkManager. See Sim.cs for the file split.
public partial class Sim
{
    public static Vector3I WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector3I(
            Mathf.FloorToInt(worldPos.X / ChunkState.SIZE),
            Mathf.FloorToInt(worldPos.Y / ChunkState.SIZE),
            Mathf.FloorToInt(worldPos.Z / ChunkState.SIZE)
        );
    }

    // Voxel-space overload, kept integer so it stays exact at large world
    // coordinates where float division would round.
    public static Vector3I WorldToChunkCoord(Vector3I voxel)
    {
        return new Vector3I(
            FloorDiv(voxel.X, ChunkState.SIZE),
            FloorDiv(voxel.Y, ChunkState.SIZE),
            FloorDiv(voxel.Z, ChunkState.SIZE)
        );
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    public bool IsSpawnChunkReady(Vector3 spawnPosition)
    {
        return _chunkManager.IsSpawnChunkReady(spawnPosition);
    }

    // True when the chunk containing `worldPos` is currently streamed in (its mesh
    // + collision are resident). False once it has been evicted — so a caller that
    // relies on standing on chunk collision (e.g. a frozen corpse guarding against
    // falling through the world) can tell when its support has streamed out.
    public bool IsChunkLoadedAt(Vector3 worldPos)
    {
        return _chunkManager.IsChunkLoaded(WorldToChunkCoord(worldPos));
    }

    // What every runtime voxel write needs afterwards, whatever made it: a
    // relight of the moved cells, and dropping the walkability samples over them.
    // Re-meshing is separate (RemeshVoxels) because a geometry-less write — a
    // door's Barrier — needs no new mesh, but it does change what can be walked.
    public void OnVoxelsChanged(List<Vector3I> changedPositions)
    {
        _chunkManager.UpdateLighting(changedPositions);
        var chunks = new HashSet<Vector3I>();
        foreach (Vector3I cell in changedPositions)
        {
            if (chunks.Add(WorldToChunkCoord(cell)))
            {
                SharedWalkabilityCache.InvalidateChunk(WorldToChunkCoord(cell));
            }
        }
    }

    public void FlushLighting()
    {
        _chunkManager.FlushLighting();
    }

    public void AddLightSource(LightSource source)
    {
        _chunkManager.AddLightSource(source);
    }

    public void RemoveLightSource(LightSource source)
    {
        _chunkManager.RemoveLightSource(source);
    }

    public void SetLightAmplitude(LightSource source, float amplitude)
    {
        _chunkManager.SetLightAmplitude(source, amplitude);
    }

    public void SetFogDebugMode(int mode)
    {
        _chunkManager?.SetFogDebugMode(mode);
    }

    public void SetFogEnabled(bool enabled)
    {
        _chunkManager?.SetFogEnabled(enabled);
    }

    public void SetFogVolumetricEnabled(bool enabled)
    {
        _chunkManager?.SetFogVolumetricEnabled(enabled);
    }

    // Re-mesh every resident chunk whose mesh reads one of `cells`, which must
    // already hold their new voxels (ChunkMeshReach decides the set). Call after
    // OnVoxelsChanged for a write that changes geometry.
    public void RemeshVoxels(IEnumerable<Vector3I> cells)
    {
        _chunkManager.RebuildChunkMeshes(ChunkMeshReach.ChunksReading(_worldState, cells));
    }

    // Re-mesh every resident chunk whose mesh reads anything in `region` — for
    // an edit that moves a non-voxel mesh input there (a roof's SunOpaque).
    public void RemeshRegion(VoxelBox region)
    {
        _chunkManager.RebuildChunkMeshes(ChunkMeshReach.ChunksReading(region));
    }
}
