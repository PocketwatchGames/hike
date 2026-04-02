using System;
using System.Collections.Generic;
using Godot;

public partial class VoxelWorld : Node3D
{
    private const int NEARBY_RADIUS = 1;
    private const int MAX_LOAD_DISTANCE = 5;

    private readonly Dictionary<Vector3I, ChunkMesh> _loadedChunks = new();
    private Vector3I _lastPlayerChunkCoord;
    private Func<Vector3> _getPlayerPosition;
    private WorldData _worldData;
    private Camera3D _camera;

    public void Initialize(WorldData worldData, Vector3 spawnPosition)
    {
        _worldData = worldData;
        _lastPlayerChunkCoord = WorldToChunkCoord(spawnPosition);
        CreateWorldBoundary();
        UpdateLoadedChunks();
    }

    public void SetCamera(Camera3D camera)
    {
        _camera = camera;
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

    public override void _Process(double delta)
    {
        if (_getPlayerPosition == null || _camera == null)
        {
            return;
        }

        _lastPlayerChunkCoord = WorldToChunkCoord(_getPlayerPosition());
        UpdateLoadedChunks();
    }

    public static Vector3I WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector3I(
            Mathf.FloorToInt(worldPos.X / ChunkData.SIZE),
            Mathf.FloorToInt(worldPos.Y / ChunkData.SIZE),
            Mathf.FloorToInt(worldPos.Z / ChunkData.SIZE)
        );
    }

    private void CreateWorldBoundary()
    {
        Vector3 minWorld = new Vector3(
            _worldData.Min.X * ChunkData.SIZE,
            _worldData.Min.Y * ChunkData.SIZE,
            _worldData.Min.Z * ChunkData.SIZE
        );
        Vector3 maxWorld = new Vector3(
            (_worldData.Max.X + 1) * ChunkData.SIZE,
            (_worldData.Max.Y + 1) * ChunkData.SIZE,
            (_worldData.Max.Z + 1) * ChunkData.SIZE
        );
        Vector3 center = (minWorld + maxWorld) / 2f;
        Vector3 size = maxWorld - minWorld;

        const float WALL_THICKNESS = 1f;
        float wallHeight = size.Y;

        // North wall (+Z)
        AddBoundaryWall(new Vector3(center.X, center.Y, maxWorld.Z + WALL_THICKNESS / 2f),
            new Vector3(size.X + WALL_THICKNESS * 2f, wallHeight, WALL_THICKNESS));

        // South wall (-Z)
        AddBoundaryWall(new Vector3(center.X, center.Y, minWorld.Z - WALL_THICKNESS / 2f),
            new Vector3(size.X + WALL_THICKNESS * 2f, wallHeight, WALL_THICKNESS));

        // East wall (+X)
        AddBoundaryWall(new Vector3(maxWorld.X + WALL_THICKNESS / 2f, center.Y, center.Z),
            new Vector3(WALL_THICKNESS, wallHeight, size.Z + WALL_THICKNESS * 2f));

        // West wall (-X)
        AddBoundaryWall(new Vector3(minWorld.X - WALL_THICKNESS / 2f, center.Y, center.Z),
            new Vector3(WALL_THICKNESS, wallHeight, size.Z + WALL_THICKNESS * 2f));

        // Floor (-Y)
        AddBoundaryWall(new Vector3(center.X, minWorld.Y - WALL_THICKNESS / 2f, center.Z),
            new Vector3(size.X + WALL_THICKNESS * 2f, WALL_THICKNESS, size.Z + WALL_THICKNESS * 2f));
    }

    private void AddBoundaryWall(Vector3 position, Vector3 size)
    {
        var body = new StaticBody3D();
        body.Position = position;

        var shape = new BoxShape3D();
        shape.Size = size;

        var collisionShape = new CollisionShape3D();
        collisionShape.Shape = shape;

        body.AddChild(collisionShape);
        AddChild(body);
    }

    private void UpdateLoadedChunks()
    {
        var desired = new HashSet<Vector3I>();

        // Always load immediate surroundings for collision/gameplay
        for (int x = -NEARBY_RADIUS; x <= NEARBY_RADIUS; x++)
        {
            for (int y = -NEARBY_RADIUS; y <= NEARBY_RADIUS; y++)
            {
                for (int z = -NEARBY_RADIUS; z <= NEARBY_RADIUS; z++)
                {
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
                                coord.X * ChunkData.SIZE,
                                coord.Y * ChunkData.SIZE,
                                coord.Z * ChunkData.SIZE),
                            new Vector3(ChunkData.SIZE, ChunkData.SIZE, ChunkData.SIZE)
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
            _loadedChunks[coord].QueueFree();
            _loadedChunks.Remove(coord);
        }

        // Load new chunks from world data
        foreach (Vector3I coord in desired)
        {
            if (!_loadedChunks.ContainsKey(coord))
            {
                ChunkData data = _worldData.GetChunk(coord);
                if (data == null)
                {
                    continue;
                }
                ChunkMesh mesh = ChunkMesh.Create(data);
                AddChild(mesh);
                _loadedChunks[coord] = mesh;
            }
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
