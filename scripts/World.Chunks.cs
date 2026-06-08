using System.Collections.Generic;
using Godot;

// World — thin delegation to ChunkManager: world↔chunk coordinate conversion,
// spawn-chunk readiness, voxel lighting / light sources, fog toggles, and mesh
// rebuilds. World is the single public face callers reach; the actual work
// lives on ChunkManager. See World.cs for the file split.
public partial class World
{
    public static Vector3I WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector3I(
            Mathf.FloorToInt(worldPos.X / ChunkState.SIZE),
            Mathf.FloorToInt(worldPos.Y / ChunkState.SIZE),
            Mathf.FloorToInt(worldPos.Z / ChunkState.SIZE)
        );
    }

    public bool IsSpawnChunkReady(Vector3 spawnPosition)
    {
        return _chunkManager.IsSpawnChunkReady(spawnPosition);
    }

    public void UpdateLighting(List<Vector3I> changedPositions)
    {
        _chunkManager.UpdateLighting(changedPositions);
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

    public void RebuildNearbyChunkMeshes(Vector3 worldPos, List<Vector3I> changedPositions)
    {
        _chunkManager.RebuildNearbyChunkMeshes(worldPos, changedPositions);
    }
}
