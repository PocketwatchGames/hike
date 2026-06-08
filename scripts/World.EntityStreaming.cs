using System;
using System.Collections.Generic;
using Godot;

// World — chunk-driven entity streaming: the desired-chunk set, the spawn
// queue + per-frame drain budget, chunk load/unload reactions, and the shared
// per-cell path-blocker grid. See World.cs for the file split.
public partial class World
{
    // Spherical radius (in chunks) for spawning entities around the player. Must be
    // <= ChunkManager.NEARBY_RADIUS so chunk collision is guaranteed to exist when
    // an entity spawns. Kept symmetric in world space (not frustum-culled) so
    // rotating the camera never reveals un-spawned entities.
    private const int ENTITY_LOAD_RADIUS = 5;
    private const int ENTITY_LOAD_RADIUS_SQ = ENTITY_LOAD_RADIUS * ENTITY_LOAD_RADIUS;
    // Reduced radius used for the initial spawn-time fill. The loading screen is
    // opaque so the player can't see past the inner sphere; the outer shell
    // streams in over the next few seconds after the fade via the normal
    // per-frame drain. Sphere counts: r=3 → ~110 chunks vs r=5 → ~520, so the
    // loading wait drains ~5x fewer entities before handing control back.
    private const int INITIAL_ENTITY_LOAD_RADIUS = 3;
    private const int INITIAL_ENTITY_LOAD_RADIUS_SQ = INITIAL_ENTITY_LOAD_RADIUS * INITIAL_ENTITY_LOAD_RADIUS;

    public IReadOnlyDictionary<Vector3I, List<Node3D>> ActiveEntities => _activeEntities;

    public Action<Mob> onMobSpawned;
    public Action<Mob> onMobRemoved;
    public Action<Discoverable> onDiscoverableSpawned;
    public Action<Discoverable> onDiscoverableRemoved;
    // Fires after LoadEntitiesForChunk has spawned the chunk's entity nodes.
    // Used by the minimap to stamp prop foliage once the trees / props are
    // actually in the scene (the chunk-mesh-loaded event fires earlier, when
    // entities don't exist yet).
    public Action<Vector3I> onChunkEntitiesLoaded;

    private readonly Dictionary<Vector3I, List<Node3D>> _activeEntities = new();
    private readonly HashSet<Vector3I> _desiredEntityChunks = new();

    // Per-frame budget for entity instantiation. The hitch detector caught
    // 26ms+ C# spikes from chunks containing 8 mobs (goblins) + their movinglight
    // torches all instantiating on the same frame, plus another ~40ms of
    // post-_Process gap from Jolt broadphase insertion and GpuParticles
    // first-render setup. Spreading at 8/frame, typical chunks (5-30 entities)
    // spawn in 1-4 frames — visually a brief pop-in of mobs/props, dramatically
    // better than a 130ms freeze.
    public const int DEFAULT_MAX_ENTITIES_PER_FRAME = 8;
    // Settable so the loading sequence can burst the drain rate while the
    // overlay is opaque (no visible frame cost). Reset to default before the
    // fade so in-game streaming keeps its hitch-free 8/frame cadence.
    public int MaxEntitiesPerFrame { get; set; } = DEFAULT_MAX_ENTITIES_PER_FRAME;
    // Cleared by ExpandToFullEntityRadius once the loading screen is ready to
    // fade. While true, RebuildDesiredEntityChunks uses INITIAL_ENTITY_LOAD_RADIUS
    // so SetPlayer's initial sync only enqueues the inner sphere.
    private bool _useInitialEntityRadius = true;

    private readonly struct PendingSpawn
    {
        public readonly Vector3I ChunkCoord;
        public readonly EntitySimState State;
        public PendingSpawn(Vector3I chunkCoord, EntitySimState state)
        {
            ChunkCoord = chunkCoord;
            State = state;
        }
    }

    private readonly Queue<PendingSpawn> _spawnQueue = new();
    // Per-chunk pending-entity count. Decremented as DrainSpawnQueue creates
    // each entity; on hitting zero we fire onChunkEntitiesLoaded so the
    // minimap (and any future consumer of "chunk's entities are fully in
    // the tree") sees the right edge. Cleaned up when chunks unload
    // mid-spawn — the corresponding queue entries get dropped at dequeue
    // by the _activeEntities-presence check.
    private readonly Dictionary<Vector3I, int> _spawningRemaining = new();

    // Per-cell refcount of pathfinding blockers contributed by spawned
    // entities (trees, chests, etc.). Refcounted so multiple entities sharing
    // a cell — or lifetime overlap during respawn — don't drop the block
    // prematurely. Queried by WalkabilityGrid.SampleColumn so mobs route
    // around props the voxel grid alone can't see.
    private readonly PathBlockerGrid _pathBlockers = new();

    private bool _editorMode;

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
        int radius = _useInitialEntityRadius ? INITIAL_ENTITY_LOAD_RADIUS : ENTITY_LOAD_RADIUS;
        int radiusSq = _useInitialEntityRadius ? INITIAL_ENTITY_LOAD_RADIUS_SQ : ENTITY_LOAD_RADIUS_SQ;
        _desiredEntityChunks.Clear();
        for (int x = -radius; x <= radius; x++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                for (int z = -radius; z <= radius; z++)
                {
                    if (x * x + y * y + z * z > radiusSq)
                    {
                        continue;
                    }
                    _desiredEntityChunks.Add(center + new Vector3I(x, y, z));
                }
            }
        }
    }

    // Switch from the initial (small) entity-load radius to the full radius
    // and enqueue spawns for the newly-desired outer-shell chunks. Called by
    // GameClient once the inner sphere has drained and the loading screen is
    // about to fade — the outer shell pops in over the next few seconds via
    // the normal DrainSpawnQueue budget.
    public void ExpandToFullEntityRadius()
    {
        if (!_useInitialEntityRadius)
        {
            return;
        }
        _useInitialEntityRadius = false;
        if (_player == null)
        {
            return;
        }
        RebuildDesiredEntityChunks(WorldToChunkCoord(_player.GlobalPosition));
        SyncEntitiesToDesired();
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
        // Drop any pending-spawn bookkeeping first so DrainSpawnQueue's
        // _activeEntities-presence check skips any of this chunk's still-in-
        // queue entities.
        _spawningRemaining.Remove(coord);
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

    // True once every entity-eligible chunk around the player has finished
    // streaming its entities out of _spawnQueue. ChunkManager's initial-load
    // pass fills the full mesh sphere (NEARBY_RADIUS = 6) synchronously before
    // IsSpawnChunkReady flips, so by the time SetPlayer runs every chunk
    // inside the active entity radius has its mesh and gets LoadEntitiesForChunk
    // called. GameClient holds the spawn-fade opaque until this returns true so
    // tallgrass / props / knowledge stones don't pop in over the reveal —
    // during the initial load the active radius is INITIAL_ENTITY_LOAD_RADIUS,
    // so this becomes true once only the inner sphere has drained; the outer
    // shell is enqueued by ExpandToFullEntityRadius right before the fade.
    public bool AreEntitySpawnsDrained()
    {
        return _spawnQueue.Count == 0 && _spawningRemaining.Count == 0;
    }

    // Drives the loading screen's entity-spawn phase progress bar. Sampled
    // once after SetPlayer (peak) and each frame during the drain (current);
    // (peak - current) / peak is the fraction complete.
    public int PendingEntitySpawnCount => _spawnQueue.Count;

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
        using var _prof = Profiler.Sample("World.LoadChunkEntities");
        // Register the chunk in _activeEntities immediately, even though
        // entities will trickle in over the next several frames via
        // DrainSpawnQueue. Keeps OnChunkLoaded / SyncEntitiesToDesired
        // idempotent — second call for the same coord sees the entry and
        // skips. Consumers that walk _activeEntities (GetEntities<T>,
        // RemoveEntity) see entities as they appear; only difference is
        // that onChunkEntitiesLoaded (minimap stamp) fires when the queue
        // finishes the chunk, not at enqueue time.
        var entities = new List<Node3D>();
        _activeEntities[coord] = entities;
        List<EntitySimState> states = _worldState.GetEntities(coord);
        if (states == null || states.Count == 0)
        {
            onChunkEntitiesLoaded?.Invoke(coord);
            return;
        }
        _spawningRemaining[coord] = states.Count;
        foreach (EntitySimState state in states)
        {
            _spawnQueue.Enqueue(new PendingSpawn(coord, state));
        }
    }

    private void DrainSpawnQueue()
    {
        using var _prof = Profiler.Sample("World.DrainSpawnQueue");
        int spawned = 0;
        int budget = MaxEntitiesPerFrame;
        while (spawned < budget && _spawnQueue.Count > 0)
        {
            PendingSpawn pending = _spawnQueue.Dequeue();
            // Chunk could have been unloaded between enqueue and now; drop
            // the entity silently. _activeEntities is the single source of
            // truth for "is this chunk still alive."
            if (!_activeEntities.TryGetValue(pending.ChunkCoord, out List<Node3D> entities))
            {
                _spawningRemaining.Remove(pending.ChunkCoord);
                continue;
            }
            Node3D entity = pending.State.CreateEntity(this);
            if (entity != null)
            {
                // Per-type spawn counter — surfaces under engine monitors so
                // a hitch dump shows e.g. "spawn.Mob 8" right at the boundary.
                Profiler.IncrementCounter("spawn." + entity.GetType().Name);
                RegisterEntity(entity, entities, pending.State);
            }
            spawned++;
            // Decrement chunk's pending count; fire onChunkEntitiesLoaded on
            // the last entity so the minimap stamp pass sees the full set.
            if (_spawningRemaining.TryGetValue(pending.ChunkCoord, out int remaining))
            {
                remaining--;
                if (remaining <= 0)
                {
                    _spawningRemaining.Remove(pending.ChunkCoord);
                    onChunkEntitiesLoaded?.Invoke(pending.ChunkCoord);
                }
                else
                {
                    _spawningRemaining[pending.ChunkCoord] = remaining;
                }
            }
        }
    }

    private void RegisterEntity(Node3D entity, List<Node3D> entities, EntitySimState state = null)
    {
        if (entity is IWorldEntity worldEntity)
        {
            worldEntity.OnSpawned(this);
        }
        // Porous props / interactives: move their default-layer colliders onto
        // Porous so smell, sound, perched vision, and flight pass through while
        // movement and grounded sight still block. One shared concept (IPorous)
        // and one application site for props and interactives alike.
        if (entity is IPorous porous && porous.Porous)
        {
            PorousColliders.Apply(entity);
        }
        if (state != null)
        {
            state.RuntimeNode = entity;
            // Clear the back-reference whenever the node leaves the tree
            // (chunk eviction, day/night despawn, mob death). RefreshTimeOfDayEntities
            // uses RuntimeNode to detect which states currently have a live
            // node — without this, a freed but still-referenced node would
            // make the state look "already spawned" forever.
            entity.TreeExiting += () =>
            {
                if (state.RuntimeNode == entity)
                {
                    state.RuntimeNode = null;
                }
            };
        }
        if (state != null)
        {
            // Refcounted: each blocker entity adds 1 to every cell it
            // occupies, and removes 1 on TreeExiting. Overlapping props (a
            // chest tucked next to a tree, two adjacent trees sharing a cell)
            // keep the cell blocked until the last owner leaves.
            List<Vector3I> blockerCells = new();
            state.GetPathBlockerCells(entity, blockerCells);
            if (blockerCells.Count > 0)
            {
                for (int i = 0; i < blockerCells.Count; i++)
                {
                    AddPathBlocker(blockerCells[i]);
                }
                // Capture so removal is automatic regardless of why the node
                // leaves the tree (chunk eviction, editor delete, scene
                // teardown). World outlives its child entities, so the
                // closure's implicit `this` is safe.
                entity.TreeExiting += () =>
                {
                    for (int i = 0; i < blockerCells.Count; i++)
                    {
                        RemovePathBlocker(blockerCells[i]);
                    }
                };
            }
        }
        entities.Add(entity);
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

    private void UnloadEntitiesOutsideSet(HashSet<Vector3I> desired, Dictionary<Vector3I, List<Node3D>> loaded)
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
            // Pending-spawn bookkeeping shares the chunk-coord key; drop
            // it so DrainSpawnQueue ignores any queue entries still
            // pointing at this chunk.
            _spawningRemaining.Remove(coord);
        }
    }

    // Path-blocker grid forwards — refcounted per-cell, keyed by world voxel.
    // Navigation (WalkabilityGrid, NavigationGoals) queries IsPathBlocked;
    // RegisterEntity adds/removes as blocker entities spawn and leave the tree.
    public void AddPathBlocker(Vector3I cell)
    {
        _pathBlockers.Add(cell);
    }

    public void RemovePathBlocker(Vector3I cell)
    {
        _pathBlockers.Remove(cell);
    }

    public bool IsPathBlocked(int wx, int wy, int wz)
    {
        return _pathBlockers.IsBlocked(wx, wy, wz);
    }
}
