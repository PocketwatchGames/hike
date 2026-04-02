using System;
using System.Collections.Generic;
using Godot;

public partial class VoxelWorld : Node3D
{
    private const int LOAD_RADIUS = 1;

    private readonly Dictionary<Vector3I, ChunkMesh> _loadedChunks = new();
    private Vector3I _lastPlayerChunkCoord;
    private Func<Vector3> _getPlayerPosition;

    public void Initialize(Vector3 spawnPosition)
    {
        _lastPlayerChunkCoord = WorldToChunkCoord(spawnPosition);
        UpdateLoadedChunks();
    }

    public void SetPlayerPositionSource(Func<Vector3> getter)
    {
        _getPlayerPosition = getter;
    }

    public bool IsSpawnChunkReady(Vector3 spawnPosition)
    {
        Vector3I coord = WorldToChunkCoord(spawnPosition);
        return _loadedChunks.TryGetValue(coord, out ChunkMesh chunk) && chunk.CollisionReady;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_getPlayerPosition == null)
        {
            return;
        }

        Vector3I currentCoord = WorldToChunkCoord(_getPlayerPosition());
        if (currentCoord != _lastPlayerChunkCoord)
        {
            _lastPlayerChunkCoord = currentCoord;
            UpdateLoadedChunks();
        }
    }

    public static Vector3I WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector3I(
            Mathf.FloorToInt(worldPos.X / ChunkData.SIZE),
            Mathf.FloorToInt(worldPos.Y / ChunkData.SIZE),
            Mathf.FloorToInt(worldPos.Z / ChunkData.SIZE)
        );
    }

    private void UpdateLoadedChunks()
    {
        var desired = new HashSet<Vector3I>();

        for (int x = -LOAD_RADIUS; x <= LOAD_RADIUS; x++)
        {
            for (int y = -LOAD_RADIUS; y <= LOAD_RADIUS; y++)
            {
                for (int z = -LOAD_RADIUS; z <= LOAD_RADIUS; z++)
                {
                    desired.Add(_lastPlayerChunkCoord + new Vector3I(x, y, z));
                }
            }
        }

        // Unload chunks no longer needed
        var toRemove = new List<Vector3I>();
        foreach (Vector3I coord in _loadedChunks.Keys)
        {
            if (!desired.Contains(coord))
            {
                toRemove.Add(coord);
            }
        }
        foreach (Vector3I coord in toRemove)
        {
            _loadedChunks[coord].QueueFree();
            _loadedChunks.Remove(coord);
        }

        // Load new chunks
        foreach (Vector3I coord in desired)
        {
            if (!_loadedChunks.ContainsKey(coord))
            {
                var data = new ChunkData(coord);
                ChunkMesh mesh = ChunkMesh.Create(data);
                AddChild(mesh);
                _loadedChunks[coord] = mesh;
            }
        }
    }
}
