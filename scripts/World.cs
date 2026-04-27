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
    private bool _editorMode;
    private Vector3I _lastEntityChunkCoord;

    public Player player => _player;

    public void Initialize(WorldState worldState, Vector3 spawnPosition, GameCamera camera, ShaderMaterial fogMaterial, Func<Vector3> getPlayerPosition)
    {
        _worldState = worldState;
        _lastEntityChunkCoord = WorldToChunkCoord(spawnPosition);

        _chunkManager = new ChunkManager();
        AddChild(_chunkManager);
        _chunkManager.onChunkLoaded += OnChunkLoaded;
        _chunkManager.onChunkUnloaded += OnChunkUnloaded;
        _chunkManager.Initialize(worldState, spawnPosition, camera, fogMaterial, getPlayerPosition);

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

        // Advance normalized time-of-day. time_scale lets the player
        // fast-forward the cycle without disturbing GameTimeMs (which
        // drives cooldowns and AI timers that should stay at real speed).
        float dayLength = _worldState.SimData?.DayLengthSeconds ?? 600f;
        if (dayLength > 0f)
        {
            double todDelta = delta * CVars.timeScale.Value / dayLength;
            _worldState.TimeOfDayAbsolute += todDelta;
            double tod = _worldState.TimeOfDay01 + todDelta;
            tod -= System.Math.Floor(tod);
            _worldState.TimeOfDay01 = tod;
        }
    }

    public override void _Process(double delta)
    {
        if (_player == null)
        {
            return;
        }

        UpdateEntityLoading(_player.GlobalPosition);
    }

    public void UpdateEntityLoading(Vector3 center)
    {
        Vector3I currentCoord = WorldToChunkCoord(center);
        if (currentCoord == _lastEntityChunkCoord)
        {
            return;
        }
        _lastEntityChunkCoord = currentCoord;

        RebuildDesiredEntityChunks(currentCoord);
        SyncEntitiesToDesired();
    }

    public void EnableEditorMode()
    {
        _editorMode = true;
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
        if (!_editorMode && _player == null)
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

    // Constants for the sun-reach raycast. Origin is offset off the surface so
    // a query point sitting on a face doesn't self-hit. Distance only needs to
    // clear nearby occluders (cliffs, tree trunks, cave roofs) — the sun is
    // infinitely far but a few dozen voxels of clearance is enough to know
    // whether we're in the open.
    private const float SUN_RAY_ORIGIN_OFFSET = 0.05f;
    private const float SUN_RAY_DISTANCE = 64f;

    // True if a ray cast from `pos` toward the sun reaches open sky without
    // hitting environment geometry. Mirrors the directional-shadow term used
    // by the shaders; gameplay code should call this per-actor (not per-voxel)
    // because each call costs one Jolt query.
    public bool IsPointInDirectionalSun(Vector3 pos)
    {
        Vector3 toSun = -_worldState.ShadowLightDirection;
        Vector3 from = pos + toSun * SUN_RAY_ORIGIN_OFFSET;
        Vector3 to = from + toSun * SUN_RAY_DISTANCE;
        var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Environment);
        var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        return result.Count == 0;
    }

    // Convenience: perceived brightness at `pos` matching the shader model,
    // including the directional-shadow term. Skip the raycast with
    // `checkDirectionalShadow = false` for cheap callers (e.g. plant growth)
    // that don't care whether direct sun is geometrically blocked.
    public float GetPerceivedLight(Vector3 pos, bool checkDirectionalShadow = true)
    {
        bool inSun = !checkDirectionalShadow || IsPointInDirectionalSun(pos);
        return _worldState.GetPerceivedLightWorld(pos, inSun);
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

    public void UnloadChunkEntities(Vector3I coord)
    {
        OnChunkUnloaded(coord);
    }

    public void LoadChunkEntities(Vector3I coord)
    {
        if (!_chunkManager.IsChunkLoaded(coord))
        {
            return;
        }
        if (_activeEntities.ContainsKey(coord))
        {
            return;
        }
        LoadEntitiesForChunk(coord);
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
