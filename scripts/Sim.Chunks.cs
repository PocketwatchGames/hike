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

    public void UpdateLighting(List<Vector3I> changedPositions)
    {
        _chunkManager.UpdateLighting(changedPositions);
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

    // Requeue specific chunks. For edits whose extent is known up front — an
    // editor fill or its undo names its own chunks rather than deriving them
    // from a voxel list.
    public void RebuildChunkMeshes(IEnumerable<Vector3I> chunkCoords)
    {
        _chunkManager.RebuildChunkMeshes(chunkCoords);
    }
}
