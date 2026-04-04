using System;
using System.Collections.Generic;
using Godot;

public partial class VoxelWorld : Node3D
{
    private const int NEARBY_RADIUS = 1;
    private const int MAX_LOAD_DISTANCE = 5;

    private readonly Dictionary<Vector3I, ChunkMesh> _loadedChunks = new();
    private readonly Dictionary<Vector3I, List<Node3D>> _loadedProps = new();
    private readonly Dictionary<Vector3I, List<Node3D>> _loadedInteractives = new();
    private readonly Queue<Vector3I> _meshRebuildQueue = new();
    private Vector3I _lastPlayerChunkCoord;
    private Func<Vector3> _getPlayerPosition;
    private WorldState _worldData;
    private LightMap _lightMap;
    private Camera3D _camera;
    private float _spriteYScale = 1.0f;

    public void Initialize(WorldState worldData, Vector3 spawnPosition)
    {
        _worldData = worldData;
        _lightMap = new LightMap(worldData);
        _lastPlayerChunkCoord = WorldToChunkCoord(spawnPosition);
        CreateWorldBoundary();
        UpdateLoadedChunks();
    }

    public void SetCamera(GameCamera camera)
    {
        _camera = camera;
        _spriteYScale = camera.SpriteYScale;
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
        ProcessMeshRebuildQueue();

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
            Mathf.FloorToInt(worldPos.X / ChunkState.SIZE),
            Mathf.FloorToInt(worldPos.Y / ChunkState.SIZE),
            Mathf.FloorToInt(worldPos.Z / ChunkState.SIZE)
        );
    }

    private void CreateWorldBoundary()
    {
        Vector3 minWorld = new Vector3(
            _worldData.Min.X * ChunkState.SIZE,
            _worldData.Min.Y * ChunkState.SIZE,
            _worldData.Min.Z * ChunkState.SIZE
        );
        Vector3 maxWorld = new Vector3(
            (_worldData.Max.X + 1) * ChunkState.SIZE,
            (_worldData.Max.Y + 1) * ChunkState.SIZE,
            (_worldData.Max.Z + 1) * ChunkState.SIZE
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
            _loadedChunks[coord].QueueFree();
            _loadedChunks.Remove(coord);

            if (_loadedProps.TryGetValue(coord, out List<Node3D> props))
            {
                foreach (Node3D prop in props)
                {
                    prop.QueueFree();
                }
                _loadedProps.Remove(coord);
            }

            if (_loadedInteractives.TryGetValue(coord, out List<Node3D> interactives))
            {
                foreach (Node3D interactive in interactives)
                {
                    interactive.QueueFree();
                }
                _loadedInteractives.Remove(coord);
            }
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

                List<PropSpawnState> propDataList = _worldData.GetProps(coord);
                if (propDataList != null)
                {
                    var propInstances = new List<Node3D>();
                    foreach (PropSpawnState propData in propDataList)
                    {
                        if (propData.PickedUp)
                        {
                            continue;
                        }

                        Node3D prop = propData.Type switch
                        {
                            PropType.TallGrass => TallGrass.Create(propData, _spriteYScale),
                            PropType.Loot => Loot.Create(propData, _spriteYScale, OnLootPickedUp),
                            _ => PropInstance.Create(propData, _spriteYScale),
                        };
                        AddChild(prop);
                        SetLightMapUniforms(prop);
                        propInstances.Add(prop);
                    }
                    _loadedProps[coord] = propInstances;
                }

                List<InteractiveSpawnState> interactiveDataList = _worldData.GetInteractives(coord);
                if (interactiveDataList != null)
                {
                    var interactiveInstances = new List<Node3D>();
                    foreach (InteractiveSpawnState interactiveData in interactiveDataList)
                    {
                        Node3D interactive = interactiveData.Type switch
                        {
                            InteractiveType.Door => Door.Create(interactiveData, _worldData, this, _spriteYScale),
                            InteractiveType.Torch => Torch.Create(interactiveData, _worldData, this, _spriteYScale),
                            _ => null,
                        };
                        if (interactive != null)
                        {
                            AddChild(interactive);
                            SetLightMapUniforms(interactive);
                            interactiveInstances.Add(interactive);
                        }
                    }
                    _loadedInteractives[coord] = interactiveInstances;

                    // Restore state must happen after AddChild so _Ready has run
                    foreach (Node3D interactive in interactiveInstances)
                    {
                        if (interactive is Door door)
                        {
                            door.RestoreState();
                        }
                        else if (interactive is Torch torch)
                        {
                            torch.RestoreState();
                        }
                    }
                }
            }
        }
    }

    private const int MAX_REBUILDS_PER_FRAME = 3;

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

        // Queue nearby chunks for rebuild across multiple frames
        Vector3I center = WorldToChunkCoord(worldPos);
        foreach (Vector3I coord in _loadedChunks.Keys)
        {
            if (Math.Abs(coord.X - center.X) <= 1 && Math.Abs(coord.Y - center.Y) <= 1 && Math.Abs(coord.Z - center.Z) <= 1)
            {
                _meshRebuildQueue.Enqueue(coord);
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

    public void CullProps(float cameraClip)
    {
        foreach (List<Node3D> props in _loadedProps.Values)
        {
            foreach (Node3D prop in props)
            {
                prop.Visible = prop.GlobalPosition.Y < cameraClip;
            }
        }
    }

    private void OnLootPickedUp(Loot loot)
    {
        foreach (List<Node3D> props in _loadedProps.Values)
        {
            if (props.Remove(loot))
            {
                break;
            }
        }
    }

    private void SetLightMapUniforms(Node3D node)
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
