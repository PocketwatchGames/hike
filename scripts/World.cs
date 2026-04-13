using System;
using System.Collections.Generic;
using Godot;

public partial class World : Node3D
{
    // Reference to the active world, used by static contexts (CVars, etc.)
    // that need to reach into the running game without a node-tree lookup.
    // Set in Initialize, cleared on tree exit. Only one game world is active
    // at a time so a single static slot is sufficient.
    public static World Current { get; private set; }

    public SimData SimData => _worldState.SimData;
    public WorldState WorldState => _worldState;
    public ulong GameTimeMs => _worldState.GameTimeMs;

    // Spherical radius (in chunks) for spawning entities around the player. Must be
    // <= ChunkManager.NEARBY_RADIUS so chunk collision is guaranteed to exist when
    // an entity spawns. Kept symmetric in world space (not frustum-culled) so
    // rotating the camera never reveals un-spawned entities.
    private const int ENTITY_LOAD_RADIUS = 5;
    private const int ENTITY_LOAD_RADIUS_SQ = ENTITY_LOAD_RADIUS * ENTITY_LOAD_RADIUS;

    public IReadOnlyDictionary<Vector3I, List<Node3D>> ActiveEntities => _activeEntities;

    public Action<Mob> onMobSpawned;
    public Action<Mob> onMobRemoved;

    private readonly Dictionary<Vector3I, List<Node3D>> _activeEntities = new();
    private readonly HashSet<Vector3I> _desiredEntityChunks = new();
    private WorldState _worldState;
    private ChunkManager _chunkManager;
    private Player _player;
    private Vector3I _lastEntityChunkCoord;

    public Player player => _player;

    public void Initialize(WorldState worldState, Vector3 spawnPosition, GameCamera camera, Func<Vector3> getPlayerPosition)
    {
        _worldState = worldState;
        _lastEntityChunkCoord = WorldToChunkCoord(spawnPosition);

        _chunkManager = new ChunkManager();
        AddChild(_chunkManager);
        _chunkManager.onChunkLoaded += OnChunkLoaded;
        _chunkManager.onChunkUnloaded += OnChunkUnloaded;
        _chunkManager.Initialize(worldState, spawnPosition, camera, getPlayerPosition);

        CreateWorldBoundary();

        Current = this;
    }

    public override void _ExitTree()
    {
        if (Current == this)
        {
            Current = null;
        }
    }

    public void SetPlayer(Player player)
    {
        _player = player;
        Vector3I center = WorldToChunkCoord(_player.GlobalPosition);
        _lastEntityChunkCoord = center;
        RebuildDesiredEntityChunks(center);
        SyncEntitiesToDesired();
    }

    // Advances simulation time. Called by GameClient each unpaused frame so the
    // sim clock freezes when the game is paused. Persistent storage lives in
    // WorldState so the clock survives save/load.
    public void Tick(double delta)
    {
        _worldState.GameTimeMs += (ulong)(delta * 1000.0);
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

        RebuildDesiredEntityChunks(currentCoord);
        SyncEntitiesToDesired();
    }

    private void RebuildDesiredEntityChunks(Vector3I center)
    {
        _desiredEntityChunks.Clear();
        for (int x = -ENTITY_LOAD_RADIUS; x <= ENTITY_LOAD_RADIUS; x++)
        {
            for (int y = -ENTITY_LOAD_RADIUS; y <= ENTITY_LOAD_RADIUS; y++)
            {
                for (int z = -ENTITY_LOAD_RADIUS; z <= ENTITY_LOAD_RADIUS; z++)
                {
                    if (x * x + y * y + z * z > ENTITY_LOAD_RADIUS_SQ)
                    {
                        continue;
                    }
                    _desiredEntityChunks.Add(center + new Vector3I(x, y, z));
                }
            }
        }
    }

    private void SyncEntitiesToDesired()
    {
        // Despawn entities in chunks that left range
        UnloadEntitiesOutsideSet(_desiredEntityChunks, _activeEntities);

        // Spawn entities in chunks that are in range and already have their mesh loaded.
        // Chunks whose mesh hasn't loaded yet will get picked up by OnChunkLoaded.
        foreach (Vector3I coord in _desiredEntityChunks)
        {
            if (_activeEntities.ContainsKey(coord))
            {
                continue;
            }
            if (!_chunkManager.IsChunkLoaded(coord))
            {
                continue;
            }
            LoadEntitiesForChunk(coord);
        }
    }

    private void OnChunkLoaded(Vector3I coord)
    {
        if (_player == null)
        {
            return;
        }
        if (!_desiredEntityChunks.Contains(coord))
        {
            return;
        }
        if (_activeEntities.ContainsKey(coord))
        {
            return;
        }
        LoadEntitiesForChunk(coord);
    }

    private void OnChunkUnloaded(Vector3I coord)
    {
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            return;
        }
        foreach (Node3D node in entities)
        {
            node.QueueFree();
        }
        _activeEntities.Remove(coord);
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

    public void SetLightMapUniforms(Node3D node)
    {
        _chunkManager.SetLightMapUniforms(node);
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
        var simState = new PropSimState(PropType.Loot, position, scene);
        _worldState.AddEntity(simState);
        Loot loot = Loot.Create(this, simState, impulse);

        Vector3I coord = WorldToChunkCoord(position);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            entities = new List<Node3D>();
            _activeEntities[coord] = entities;
        }
        RegisterEntity(loot, entities);

        return loot;
    }

    // Single iteration primitive for "all loaded entities of type T". Call sites
    // should use this rather than walking _activeEntities directly, so a future
    // typed cache (e.g. List<Mob>) can be swapped in here without touching them.
    public IEnumerable<T> GetEntities<T>() where T : Node3D
    {
        foreach (List<Node3D> entities in _activeEntities.Values)
        {
            foreach (Node3D entity in entities)
            {
                if (entity is T t)
                {
                    yield return t;
                }
            }
        }
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
        List<EntitySimState> states = _worldState.GetEntities(coord);
        if (states != null)
        {
            foreach (EntitySimState state in states)
            {
                Node3D entity = state.CreateEntity(this);
                if (entity != null)
                {
                    RegisterEntity(entity, entities);
                }
            }
        }
        _activeEntities[coord] = entities;
    }

    private void RegisterEntity(Node3D entity, List<Node3D> entities)
    {
        if (entity is IWorldEntity worldEntity)
        {
            worldEntity.OnSpawned(this);
        }
        entities.Add(entity);
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
