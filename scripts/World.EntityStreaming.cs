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
    // Map markers self-register here on spawn / deregister on removal (mirrors
    // the Discoverable events). The Minimap subscribes to keep the live marker
    // set it scans for reveal-driven discovery at its reveal cadence.
    public Action<MapMarker> onMapMarkerSpawned;
    public Action<MapMarker> onMapMarkerRemoved;
    // Live map markers (NPCs, fallen party members) drawn at their current
    // position, always visible. Entities register on spawn / unregister on
    // removal; the map overlays iterate this each redraw. Unlike MapMarker these
    // are NOT recorded into Knowledge — they track the live entity, not a
    // discovered landmark.
    private readonly List<ILiveMapMarker> _liveMapMarkers = new();
    public IReadOnlyList<ILiveMapMarker> LiveMapMarkers => _liveMapMarkers;

    public void RegisterLiveMapMarker(ILiveMapMarker marker)
    {
        if (marker != null && !_liveMapMarkers.Contains(marker))
        {
            _liveMapMarkers.Add(marker);
        }
    }

    public void UnregisterLiveMapMarker(ILiveMapMarker marker)
    {
        _liveMapMarkers.Remove(marker);
    }
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

    // Breadcrumb trail of recent player positions, sampled once per
    // SimData.CompanionRescueSampleSeconds (oldest at index 0). When a chunk
    // unloads with the live companion filed under it, RescueCompanion relocates
    // the pet onto the oldest still-loaded crumb instead of destroying it — the
    // oldest is furthest behind the player, so the pet re-appears off-screen.
    private readonly List<Vector3> _playerPositionHistory = new();
    private float _companionHistoryAccumulator;
    // Seconds the following companion has been beyond CompanionRescueMaxDistance.
    // Drives the distance backstop in TickCompanionLeash; reset whenever the pet
    // is back inside the gap or after a rescue.
    private float _companionFarSeconds;

    // Live nodes for persistent (non-chunked) entities — the player's
    // companion(s). Spawned once by SpawnPersistentEntities and never filed in
    // _activeEntities, so chunk eviction can't free them. Tracked here so
    // GetEntities<T> still surfaces them to perception / queries that walk all
    // mobs, and so a runtime-tamed mob can be moved in via PromoteCompanionToPersistent.
    private readonly List<Node3D> _persistentEntityNodes = new();

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

    // Per-cell refcount of damaging-prop danger zones (fire traps, campfires,
    // spike traps). Same refcounted-cell structure as _pathBlockers, but
    // queried separately: a hazard cell is still walkable (a mob chasing the
    // player can be lured across it) — only wander/normal pathing routes
    // around it (WalkabilityGrid tags the cell, LocalPathfinder gates on its
    // avoidHazards flag).
    private readonly PathBlockerGrid _hazardCells = new();

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
        DespawnChunkEntities(coord, entities);
        _activeEntities.Remove(coord);
    }

    // Frees every entity node in an unloading chunk. The persistent companion is
    // never filed in _activeEntities (it lives outside chunk streaming — see
    // SpawnPersistentEntities / PromoteCompanionToPersistent), so it normally
    // isn't here at all; the skip is belt-and-suspenders against a stray filing
    // so chunk eviction can never destroy the pet. Shared by both despawn paths:
    // the entity-radius shrink (UnloadEntitiesOutsideSet) and the chunk-mesh
    // unload (OnChunkUnloaded).
    private void DespawnChunkEntities(Vector3I coord, List<Node3D> nodes)
    {
        foreach (Node3D node in nodes)
        {
            if (node is Mob mob && mob == _companion)
            {
                continue;
            }
            node.QueueFree();
        }
    }

    // Accumulates delta and records the player's position into the companion
    // breadcrumb trail once per CompanionRescueSampleSeconds. Driven from
    // World.Tick (so it freezes while paused). The trail is capped at
    // CompanionRescueHistoryCount, dropping the oldest crumb past the cap.
    // TickCompanionLeash relocates a stranded following pet onto the oldest crumb.
    public void TickCompanionRescueHistory(float delta)
    {
        if (_player == null)
        {
            return;
        }
        SimData simData = _worldState?.SimData;
        float interval = simData?.companionRescueSampleSeconds ?? 1f;
        _companionHistoryAccumulator += delta;
        if (_companionHistoryAccumulator < interval)
        {
            return;
        }
        _companionHistoryAccumulator = 0f;
        _playerPositionHistory.Add(_player.GlobalPosition);
        int cap = Mathf.Max(1, simData?.companionRescueHistoryCount ?? 16);
        while (_playerPositionHistory.Count > cap)
        {
            _playerPositionHistory.RemoveAt(0);
        }
    }

    // Per-frame leash that keeps the persistent companion inside the loaded
    // world. The pet is never destroyed (it's not chunk-owned), but if the player
    // outruns it the pet can end up in a chunk whose collision has unloaded (the
    // entity radius is smaller than the chunk-mesh radius, so this only happens
    // once it's beyond the mesh radius) and fall through the world. When the pet
    // is off any resident chunk, snap it onto the oldest off-screen breadcrumb so
    // it re-appears behind the player and resumes following. This applies whether
    // following or on "stay" — a stay pet only ends up here once the player has
    // abandoned it well past the loaded region, at which point catching up beats
    // falling out of the world (refining stay-abandonment is a separate design).
    public void TickCompanionLeash(float delta)
    {
        if (_companion == null || _player == null)
        {
            return;
        }
        Vector3I chunk = WorldToChunkCoord(_companion.GlobalPosition);
        bool resident = _activeEntities.ContainsKey(chunk) && _chunkManager.IsChunkLoaded(chunk);

        // Distance backstop: a FOLLOWING pet (a stay-commanded one is meant to
        // hang back, so it's excluded — it only gets the residency rescue
        // below) that's been beyond CompanionRescueMaxDistance for the grace
        // window gets snapped forward even while still resident. Catches a dog
        // that fell behind or wedged on geometry before it reaches the world
        // edge. Grace debounces a brief lag (rounding a corner, a short stall).
        bool farTriggered = false;
        if (!_companion.StayCommanded)
        {
            SimData simData = _worldState?.SimData;
            float maxDist = simData?.companionRescueMaxDistance ?? 30f;
            Vector3 gap = _companion.GlobalPosition - _player.GlobalPosition;
            gap.Y = 0f;
            if (gap.LengthSquared() > maxDist * maxDist)
            {
                _companionFarSeconds += delta;
                farTriggered = _companionFarSeconds >= (simData?.companionRescueMaxDistanceGraceSeconds ?? 1.5f);
            }
            else
            {
                _companionFarSeconds = 0f;
            }
        }
        else
        {
            _companionFarSeconds = 0f;
        }

        if (resident && !farTriggered)
        {
            return;
        }
        string reason = !resident ? "non-resident" : "too-far";
        if (TryFindCompanionRescuePosition(chunk, out Vector3 target))
        {
            _companionFarSeconds = 0f;
            if (CVars.companionDebug.Value)
            {
                float dist = _companion.GlobalPosition.DistanceTo(_player.GlobalPosition);
                GD.Print($"[companion] leash RESCUE ({reason}): pet at chunk {chunk} (dist {dist:F1}m) -> teleport to {target} (crumbs={_playerPositionHistory.Count})");
            }
            _companion.Teleport(target, fadeIn: true);
        }
        else if (CVars.companionDebug.Value)
        {
            float dist = _companion.GlobalPosition.DistanceTo(_player.GlobalPosition);
            GD.Print($"[companion] leash STRANDED ({reason}): pet at chunk {chunk} (dist {dist:F1}m) but NO valid rescue crumb found (crumbs={_playerPositionHistory.Count}) — pet stays put");
        }
    }

    // Picks the relocation target for a stranded/lost following companion.
    // First choice: the YOUNGEST (most recent) usable crumb that is currently
    // OFF-SCREEN — closest to the player we can put the dog without it popping
    // in within view, so a pet the player outran reappears just behind the
    // camera and trots back into frame on its own. Falls back to the OLDEST
    // usable crumb when none is off-screen (e.g. the player spun around so the
    // whole recent trail is in view), then to the player's own position. A
    // "usable" crumb sits in a chunk that's loaded, active, and desired — and
    // not the avoided chunk. The fade-in on Teleport covers the fallback cases
    // where the chosen point is unavoidably on-screen.
    private bool TryFindCompanionRescuePosition(Vector3I avoidChunk, out Vector3 position)
    {
        Vector3 playerPos = _player?.GlobalPosition ?? Vector3.Zero;

        // Newest -> oldest: first usable crumb that's off the screen.
        for (int i = _playerPositionHistory.Count - 1; i >= 0; i--)
        {
            Vector3 candidate = _playerPositionHistory[i];
            if (!IsCrumbUsable(candidate, avoidChunk) || !IsOffScreen16x9(candidate))
            {
                continue;
            }
            position = candidate;
            return true;
        }

        // Fallback: oldest usable crumb (furthest behind, most likely off-screen).
        for (int i = 0; i < _playerPositionHistory.Count; i++)
        {
            Vector3 candidate = _playerPositionHistory[i];
            if (!IsCrumbUsable(candidate, avoidChunk))
            {
                continue;
            }
            position = candidate;
            return true;
        }

        if (_player != null)
        {
            Vector3I chunk = WorldToChunkCoord(playerPos);
            if (chunk != avoidChunk && _activeEntities.ContainsKey(chunk))
            {
                position = playerPos;
                return true;
            }
        }
        position = default;
        return false;
    }

    // A crumb is usable as a rescue target when its chunk is loaded, active, and
    // currently desired — and isn't the chunk the pet is being rescued out of.
    private bool IsCrumbUsable(Vector3 candidate, Vector3I avoidChunk)
    {
        Vector3I chunk = WorldToChunkCoord(candidate);
        if (chunk == avoidChunk || !_desiredEntityChunks.Contains(chunk))
        {
            return false;
        }
        return _activeEntities.ContainsKey(chunk) && _chunkManager.IsChunkLoaded(chunk);
    }

    // Aspect ratio the on-screen test is fixed to, so the rescue's "off-screen"
    // choice plays identically on any window (ultrawide, 4:3, …) rather than
    // tracking the actual viewport.
    private const float RescueViewAspect = 16f / 9f;

    // True if a world point is outside a fixed 16:9 view of the main camera.
    // The camera keeps a constant vertical extent (Godot's default KeepHeight),
    // so we take that as the frame height and derive width = height * 16/9
    // instead of using the real viewport aspect — making the result resolution-
    // independent. Tested ~1m up (≈ the dog's body center) so a point at the
    // bottom edge of frame still reads on-screen. No camera (headless) → off.
    private static bool IsOffScreen16x9(Vector3 worldPos)
    {
        GameCamera cam = GameCamera.Current;
        if (cam == null)
        {
            return true;
        }
        // Camera-local space: camera looks down -Z, so a point in front of it
        // has depth = -local.Z > 0; local.X is right, local.Y is up.
        Vector3 local = cam.GlobalTransform.AffineInverse() * (worldPos + Vector3.Up);
        float depth = -local.Z;
        if (depth <= cam.Near || depth >= cam.Far)
        {
            return true;
        }
        float halfH = cam.Projection == Camera3D.ProjectionType.Orthogonal
            ? cam.Size * 0.5f
            : Mathf.Tan(Mathf.DegToRad(cam.Fov) * 0.5f) * depth;
        float halfW = halfH * RescueViewAspect;
        return Mathf.Abs(local.X) > halfW || Mathf.Abs(local.Y) > halfH;
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

            // Hazard danger zone — refcounted the same way as blocker cells,
            // but into the separate hazard grid (walkable, only wander/normal
            // pathing avoids it). Authored radius lives on the sim state.
            if (state.HazardRadius > 0f)
            {
                List<Vector3I> hazardCells = new();
                PathBlockerRasterizer.RasterizeDisc(state.WorldPosition, state.HazardRadius, hazardCells);
                if (hazardCells.Count > 0)
                {
                    for (int i = 0; i < hazardCells.Count; i++)
                    {
                        AddHazard(hazardCells[i]);
                    }
                    entity.TreeExiting += () =>
                    {
                        for (int i = 0; i < hazardCells.Count; i++)
                        {
                            RemoveHazard(hazardCells[i]);
                        }
                    };
                }
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
        // Persistent entities (the companion) aren't in _activeEntities but must
        // still surface to perception, threat scans, and any all-mobs query.
        foreach (Node3D entity in _persistentEntityNodes)
        {
            if (entity is T t)
            {
                yield return t;
            }
        }
    }

    // True when any loaded mob is an active threat to the player — dangerous,
    // hostile, and either triggered or currently visible (see
    // Mob.IsThreateningPlayer). Drives NoDangerRequirement so "safe" interactives
    // like cooking at a campfire refuse to start while danger is around. Cheap
    // enough to call on demand at action-press time (mobs are few and loaded).
    public bool IsDangerPresent()
    {
        foreach (Mob mob in GetEntities<Mob>())
        {
            if (mob.IsThreateningPlayer)
            {
                return true;
            }
        }
        return false;
    }

    // Emit a discrete noise impulse at `position`, attributed to `source` — the
    // actor that made the noise. Distinct from the continuous movement-noise
    // hearing each mob samples per perception tick: this is the one-shot "loud
    // event" channel for weapon impacts, breaking objects, barks, and the like.
    // `decibels` scales each listener's audible radius exactly as movement
    // decibels do (audible distance = decibels * hearingRange, wind/fog
    // adjusted); the bump falls off with distance via the listener's authored
    // hearing curve. `audience` selects who reacts: Mobs raise their perception
    // of the source (alert enemies), Player raises its awareness of the source
    // mob (a barking dog draws the eye without tipping off enemies). A null
    // `source` is a no-op — no actor to attribute the noise to.
    public void CreateNoiseEvent(Vector3 position, float decibels, Node3D source = null,
        ENoiseAudience audience = ENoiseAudience.Mobs)
    {
        if (decibels <= 0f || source == null)
        {
            return;
        }
        if ((audience & ENoiseAudience.Mobs) != 0)
        {
            foreach (Mob mob in GetEntities<Mob>())
            {
                mob.HearNoise(position, decibels, source);
            }
        }
        // Player branch: raise the player's awareness of the SOURCE mob itself —
        // the player-side mirror of Mob.HearNoise. Like all hearing it primes the
        // perception meter but never latches Detected/Discovered on its own (that
        // needs line of sight — see PlayerPerception.Tick).
        if ((audience & ENoiseAudience.Player) != 0 && source is Mob sourceMob && player != null)
        {
            PlayerData pd = player.data;
            if (pd != null && pd.hearingRange > 0f && sourceMob.SimState != null)
            {
                float maxAudibleDistance = decibels * pd.hearingRange
                    * PlayerPerception.HearingRangeMultiplier(this, player.GlobalPosition);
                float distSq = (player.GlobalPosition - position).LengthSquared();
                if (maxAudibleDistance > 0f && distSq < maxAudibleDistance * maxAudibleDistance)
                {
                    float falloff = Mathf.Pow(1f - Mathf.Sqrt(distSq) / maxAudibleDistance, pd.hearingRangePower);
                    sourceMob.SimState.PlayerPerception = Mathf.Clamp(
                        sourceMob.SimState.PlayerPerception + falloff * pd.hearingStrength, 0f, 1f);
                }
            }
        }
    }

    // Spawns the live nodes for all persistent (non-chunked) entity states once,
    // outside the chunk-streaming queue. Called from SetPlayer after the initial
    // chunk-mesh sphere is ready (so the companion's spawn chunk has collision)
    // and before the loading fade, so the pet is present on reveal rather than
    // popping in. Idempotent: skips a state whose node is already live.
    public void SpawnPersistentEntities()
    {
        if (_worldState == null)
        {
            return;
        }
        foreach (EntitySimState state in _worldState.PersistentEntities)
        {
            if (state.RuntimeNode != null)
            {
                continue;
            }
            Node3D entity = state.CreateEntity(this);
            if (entity != null)
            {
                RegisterPersistentEntity(entity, state);
            }
        }
    }

    // RegisterEntity's analog for a persistent (non-chunked) entity: runs the
    // OnSpawned hook and the RuntimeNode back-reference bookkeeping, but files the
    // node in _persistentEntityNodes instead of a per-chunk _activeEntities list,
    // so chunk eviction never frees it. Persistent entities are mobs (the
    // companion), which contribute no path blockers, so that refcount path is
    // intentionally omitted.
    private void RegisterPersistentEntity(Node3D entity, EntitySimState state)
    {
        if (entity is IWorldEntity worldEntity)
        {
            worldEntity.OnSpawned(this);
        }
        if (state != null)
        {
            state.RuntimeNode = entity;
        }
        // Drop our tracking (and the RuntimeNode back-ref) whenever the node
        // leaves the tree — companion death, or world teardown — so GetEntities
        // never walks a freed object and a dead pet's state stops looking spawned.
        entity.TreeExiting += () =>
        {
            _persistentEntityNodes.Remove(entity);
            if (state != null && state.RuntimeNode == entity)
            {
                state.RuntimeNode = null;
            }
        };
        _persistentEntityNodes.Add(entity);
    }

    // Runtime-taming migration: lift an already-live, chunk-streamed mob out of
    // chunk ownership and into the persistent store, so the chunk it spawned in
    // can evict without destroying the now-companion. Moves both the live node
    // (out of _activeEntities into _persistentEntityNodes) and its sim state
    // (WorldState.PromoteToPersistent). The node itself stays in the tree.
    public void PromoteCompanionToPersistent(Mob companion)
    {
        if (companion == null)
        {
            return;
        }
        RemoveEntity(companion);
        if (!_persistentEntityNodes.Contains(companion))
        {
            _persistentEntityNodes.Add(companion);
            // This node was spawned via the chunk path (RegisterEntity), which
            // added a RuntimeNode-clear TreeExiting but not a persistent-list
            // cleanup — attach one now so a later death doesn't leave a freed
            // reference for GetEntities to walk.
            companion.TreeExiting += () => _persistentEntityNodes.Remove(companion);
        }
        _worldState?.PromoteToPersistent(companion.SimState);
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
            DespawnChunkEntities(coord, loaded[coord]);
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

    // Hazard grid forwards — refcounted per-cell, keyed by world voxel. Same
    // shape as the path-blocker forwards above, but a hazard cell stays
    // walkable; WalkabilityGrid.SampleColumn tags it CellFlags.Hazard and only
    // wander/normal pathfinding routes around it (attack pathing walks in).
    public void AddHazard(Vector3I cell)
    {
        _hazardCells.Add(cell);
    }

    public void RemoveHazard(Vector3I cell)
    {
        _hazardCells.Remove(cell);
    }

    public bool IsHazard(int wx, int wy, int wz)
    {
        return _hazardCells.IsBlocked(wx, wy, wz);
    }
}
