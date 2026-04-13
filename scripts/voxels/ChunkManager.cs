using System;
using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class ChunkManager : Node3D
{
    // Always-loaded sphere around the player. Sized to cover the camera's reach so
    // world-space entity spawning (World.ENTITY_LOAD_RADIUS) can rely on chunks
    // being present regardless of camera angle — rotating the camera must not be
    // able to reveal un-spawned mobs/props.
    private const int NEARBY_RADIUS = 6;
    private const int NEARBY_RADIUS_SQ = NEARBY_RADIUS * NEARBY_RADIUS;
    private const int MAX_LOAD_DISTANCE = 10;
    private const int MAX_REBUILDS_PER_FRAME = 3;

    public event Action<Vector3I> onChunkLoaded;
    public event Action<Vector3I> onChunkUnloaded;

    private readonly Dictionary<Vector3I, ChunkMesh> _loadedChunks = new();
    private readonly Queue<Vector3I> _meshRebuildQueue = new();
    private Vector3I _lastPlayerChunkCoord;
    private Func<Vector3> _getPlayerPosition;
    private WorldState _worldData;
    private LightMap _lightMap;
    private Camera3D _camera;

    public void Initialize(WorldState worldData, Vector3 spawnPosition, Camera3D camera, Func<Vector3> getPlayerPosition)
    {
        _worldData = worldData;
        _lightMap = new LightMap(worldData);
        _camera = camera;
        _getPlayerPosition = getPlayerPosition;
        _lastPlayerChunkCoord = World.WorldToChunkCoord(spawnPosition);
        UpdateLoadedChunks();
    }

    public bool IsChunkLoaded(Vector3I coord)
    {
        return _loadedChunks.ContainsKey(coord);
    }

    public bool IsSpawnChunkReady(Vector3 spawnPosition)
    {
        Vector3I coord = World.WorldToChunkCoord(spawnPosition);
        return _loadedChunks.TryGetValue(coord, out ChunkMesh chunk) && chunk.CollisionReady;
    }

    public override void _Process(double delta)
    {
        ProcessMeshRebuildQueue();

        _lastPlayerChunkCoord = World.WorldToChunkCoord(_getPlayerPosition());
        UpdateLoadedChunks();
    }

    public void UpdateLighting(List<Vector3I> changedPositions)
    {
        _worldData.UpdateLightingAt(changedPositions);
        _lightMap.Update(_worldData);
    }

    public void PropagateLighting(List<Vector3I> sourcePositions)
    {
        _worldData.PropagateLightingAt(sourcePositions);
        _lightMap.Update(_worldData);
    }

    public void RebuildNearbyChunkMeshes(Vector3 worldPos, List<Vector3I> changedPositions)
    {
        UpdateLighting(changedPositions);

        Vector3I center = World.WorldToChunkCoord(worldPos);
        foreach (Vector3I coord in _loadedChunks.Keys)
        {
            if (Math.Abs(coord.X - center.X) <= 1 && Math.Abs(coord.Y - center.Y) <= 1 && Math.Abs(coord.Z - center.Z) <= 1)
            {
                _meshRebuildQueue.Enqueue(coord);
            }
        }
    }

    public void SetLightMapUniforms(Node3D node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is Sprite3D sprite && sprite.MaterialOverride is ShaderMaterial mat)
            {
                mat.SetShaderParameter("light_map", _lightMap.Texture);
                mat.SetShaderParameter("light_map_origin", _lightMap.Origin);
                mat.SetShaderParameter("light_map_inv_size", Vector3.One / _lightMap.Size);
            }
        }
    }

    private void UpdateLoadedChunks()
    {
        var desired = new HashSet<Vector3I>();

        // Always load a sphere of chunks around the player for collision, gameplay,
        // and entity spawning. Spherical (not cubic) so the load boundary is at the
        // same world-space distance in every direction.
        for (int x = -NEARBY_RADIUS; x <= NEARBY_RADIUS; x++)
        {
            for (int y = -NEARBY_RADIUS; y <= NEARBY_RADIUS; y++)
            {
                for (int z = -NEARBY_RADIUS; z <= NEARBY_RADIUS; z++)
                {
                    if (x * x + y * y + z * z > NEARBY_RADIUS_SQ)
                    {
                        continue;
                    }
                    desired.Add(_lastPlayerChunkCoord + new Vector3I(x, y, z));
                }
            }
        }

        // Load frustum-visible chunks up to max distance
        if (_camera != null && _camera.IsInsideTree())
        {
            Godot.Collections.Array<Plane> frustumPlanes = _camera.GetFrustum();
            int maxDistSq = MAX_LOAD_DISTANCE * MAX_LOAD_DISTANCE;
            for (int x = -MAX_LOAD_DISTANCE; x <= MAX_LOAD_DISTANCE; x++)
            {
                for (int y = -MAX_LOAD_DISTANCE; y <= MAX_LOAD_DISTANCE; y++)
                {
                    for (int z = -MAX_LOAD_DISTANCE; z <= MAX_LOAD_DISTANCE; z++)
                    {
                        if (x * x + y * y + z * z > maxDistSq)
                        {
                            continue;
                        }

                        Vector3I coord = _lastPlayerChunkCoord + new Vector3I(x, y, z);

                        if (desired.Contains(coord))
                        {
                            continue;
                        }

                        Aabb chunkAabb = new Aabb(
                            new Vector3(
                                coord.X * ChunkState.SIZE,
                                coord.Y * ChunkState.SIZE,
                                coord.Z * ChunkState.SIZE),
                            new Vector3(ChunkState.SIZE, ChunkState.SIZE, ChunkState.SIZE)
                        );

                        if (IsAabbInFrustum(chunkAabb, frustumPlanes))
                        {
                            desired.Add(coord);
                        }
                    }
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
            onChunkUnloaded?.Invoke(coord);
            _loadedChunks[coord].QueueFree();
            _loadedChunks.Remove(coord);
        }

        // Load new chunks from world data
        foreach (Vector3I coord in desired)
        {
            if (!_loadedChunks.ContainsKey(coord))
            {
                ChunkState data = _worldData.GetChunk(coord);
                if (data == null)
                {
                    continue;
                }
                ChunkMesh mesh = ChunkMesh.Create(data, _worldData.GetVoxelWorld, _lightMap);
                AddChild(mesh);
                _loadedChunks[coord] = mesh;
                onChunkLoaded?.Invoke(coord);
            }
        }
    }

    private void ProcessMeshRebuildQueue()
    {
        int rebuilt = 0;
        while (_meshRebuildQueue.Count > 0 && rebuilt < MAX_REBUILDS_PER_FRAME)
        {
            Vector3I coord = _meshRebuildQueue.Dequeue();
            if (!_loadedChunks.TryGetValue(coord, out ChunkMesh oldMesh))
            {
                continue;
            }

            ChunkState data = _worldData.GetChunk(coord);
            if (data == null)
            {
                continue;
            }

            oldMesh.QueueFree();
            ChunkMesh mesh = ChunkMesh.Create(data, _worldData.GetVoxelWorld, _lightMap);
            AddChild(mesh);
            _loadedChunks[coord] = mesh;
            rebuilt++;
        }
    }

    private static bool IsAabbInFrustum(Aabb aabb, Godot.Collections.Array<Plane> planes)
    {
        foreach (Plane plane in planes)
        {
            Vector3 nearVertex = new Vector3(
                plane.Normal.X > 0 ? aabb.Position.X : aabb.End.X,
                plane.Normal.Y > 0 ? aabb.Position.Y : aabb.End.Y,
                plane.Normal.Z > 0 ? aabb.Position.Z : aabb.End.Z
            );

            if (plane.DistanceTo(nearVertex) > 0)
            {
                return false;
            }
        }
        return true;
    }
}
