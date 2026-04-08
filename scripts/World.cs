using System;
using System.Collections.Generic;
using Godot;

public partial class World : Node3D
{
    public SimData SimData => _worldState.SimData;
    public WorldState WorldState => _worldState;

    private const int ENTITY_LOAD_RADIUS = 2;

    public IReadOnlyDictionary<Vector3I, List<Node3D>> ActiveEntities => _activeEntities;

    public Action<Mob> onMobSpawned;

    private readonly Dictionary<Vector3I, List<Node3D>> _activeEntities = new();
    private WorldState _worldState;
    private ChunkManager _chunkManager;
    private Player _player;
    private Vector3I _lastEntityChunkCoord;

    public void Initialize(WorldState worldState, Vector3 spawnPosition, GameCamera camera, Func<Vector3> getPlayerPosition)
    {
        _worldState = worldState;
        _lastEntityChunkCoord = WorldToChunkCoord(spawnPosition);

        _chunkManager = new ChunkManager();
        AddChild(_chunkManager);
        _chunkManager.Initialize(worldState, spawnPosition, camera, getPlayerPosition);

        CreateWorldBoundary();
    }

    public void SetPlayer(Player player)
    {
        _player = player;
        LoadEntitiesInRadius(WorldToChunkCoord(_player.GlobalPosition));
    }

    public override void _Process(double delta)
    {
        if (_player == null)
        {
            return;
        }

        Vector3I currentCoord = WorldToChunkCoord(_player.GlobalPosition);
        if (currentCoord == _lastEntityChunkCoord)
        {
            return;
        }
        _lastEntityChunkCoord = currentCoord;

        LoadEntitiesInRadius(currentCoord);
    }

    private void LoadEntitiesInRadius(Vector3I center)
    {
        var desired = new HashSet<Vector3I>();
        for (int x = -ENTITY_LOAD_RADIUS; x <= ENTITY_LOAD_RADIUS; x++)
        {
            for (int y = -ENTITY_LOAD_RADIUS; y <= ENTITY_LOAD_RADIUS; y++)
            {
                for (int z = -ENTITY_LOAD_RADIUS; z <= ENTITY_LOAD_RADIUS; z++)
                {
                    desired.Add(center + new Vector3I(x, y, z));
                }
            }
        }

        // Unload entities in chunks that left range
        UnloadEntitiesOutsideSet(desired, _activeEntities);

        // Load entities in newly in-range chunks (only if voxels are loaded)
        foreach (Vector3I coord in desired)
        {
            if (!_activeEntities.ContainsKey(coord) && _chunkManager.IsChunkLoaded(coord))
            {
                LoadEntitiesForChunk(coord);
            }
        }
    }

    public bool IsSpawnChunkReady(Vector3 spawnPosition)
    {
        return _chunkManager.IsSpawnChunkReady(spawnPosition);
    }

    public void UpdateLighting(List<Vector3I> changedPositions)
    {
        _chunkManager.UpdateLighting(changedPositions);
    }

    public void PropagateLighting(List<Vector3I> sourcePositions)
    {
        _chunkManager.PropagateLighting(sourcePositions);
    }

    public void RebuildNearbyChunkMeshes(Vector3 worldPos, List<Vector3I> changedPositions)
    {
        _chunkManager.RebuildNearbyChunkMeshes(worldPos, changedPositions);
    }

    public static Vector3I WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector3I(
            Mathf.FloorToInt(worldPos.X / ChunkState.SIZE),
            Mathf.FloorToInt(worldPos.Y / ChunkState.SIZE),
            Mathf.FloorToInt(worldPos.Z / ChunkState.SIZE)
        );
    }

    public Loot SpawnLoot(PackedScene scene, Vector3 position, Vector3 impulse)
    {
        var spawnState = new PropSpawnState(PropType.Loot, position, scene);
        _worldState.AddProp(spawnState);
        Loot loot = Loot.Create(this, spawnState, impulse);
        _chunkManager.SetLightMapUniforms(loot);

        Vector3I coord = WorldToChunkCoord(position);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            entities = new List<Node3D>();
            _activeEntities[coord] = entities;
        }
        entities.Add(loot);

        return loot;
    }

    public void RemoveEntity(Node3D entity)
    {
        foreach (List<Node3D> entities in _activeEntities.Values)
        {
            if (entities.Remove(entity))
            {
                break;
            }
        }
    }

    private void LoadEntitiesForChunk(Vector3I coord)
    {
        var entities = new List<Node3D>();

        List<PropSpawnState> propDataList = _worldState.GetProps(coord);
        if (propDataList != null)
        {
            foreach (PropSpawnState propData in propDataList)
            {
                if (propData.PickedUp)
                {
                    continue;
                }

                Node3D prop = propData.Type switch
                {
                    PropType.TallGrass => TallGrass.Create(this, propData),
                    PropType.Loot => Loot.Create(this, propData),
                    _ => PropInstance.Create(this, propData),
                };
                _chunkManager.SetLightMapUniforms(prop);
                entities.Add(prop);
            }
        }

        List<MobSpawnState> mobDataList = _worldState.GetMobs(coord);
        if (mobDataList != null)
        {
            foreach (MobSpawnState mobData in mobDataList)
            {
                if (!mobData.Alive)
                {
                    continue;
                }

                Mob mob = Mob.Create(this, mobData);
                entities.Add(mob);
                onMobSpawned?.Invoke(mob);
            }
        }

        List<InteractiveSpawnState> interactiveDataList = _worldState.GetInteractives(coord);
        if (interactiveDataList != null)
        {
            foreach (InteractiveSpawnState interactiveData in interactiveDataList)
            {
                Node3D interactive = interactiveData switch
                {
                    DoorSpawnState door => Door.Create(this, door),
                    TorchSpawnState torch => Torch.Create(this, torch),
                    ChestSpawnState chest => Chest.Create(this, chest),
                    _ => null,
                };
                if (interactive != null)
                {
                    _chunkManager.SetLightMapUniforms(interactive);
                    entities.Add(interactive);
                }
            }
        }

        _activeEntities[coord] = entities;
    }

    private static void UnloadEntitiesOutsideSet(HashSet<Vector3I> desired, Dictionary<Vector3I, List<Node3D>> loaded)
    {
        var toRemove = new List<Vector3I>();
        foreach (Vector3I coord in loaded.Keys)
        {
            if (!desired.Contains(coord))
            {
                toRemove.Add(coord);
            }
        }
        foreach (Vector3I coord in toRemove)
        {
            foreach (Node3D node in loaded[coord])
            {
                node.QueueFree();
            }
            loaded.Remove(coord);
        }
    }

    private void CreateWorldBoundary()
    {
        Vector3 minWorld = new Vector3(
            _worldState.Min.X * ChunkState.SIZE,
            _worldState.Min.Y * ChunkState.SIZE,
            _worldState.Min.Z * ChunkState.SIZE
        );
        Vector3 maxWorld = new Vector3(
            (_worldState.Max.X + 1) * ChunkState.SIZE,
            (_worldState.Max.Y + 1) * ChunkState.SIZE,
            (_worldState.Max.Z + 1) * ChunkState.SIZE
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
}
