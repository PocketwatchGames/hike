using Godot;
using System;
using System.Collections.Generic;
using System.IO;

[GlobalClass]
public partial class WorldEditor : Node3D
{
    [Export] public GameCamera camera;
    [Export] public EditorHud editorHud;
    [Export] public WorldGenData worldGenData;

    // Live editor instance, exposed for console-driven subscene commands
    // (subscene_corner / subscene_save / subscene_stamp). Mirrors the
    // World.Current pattern used by world_export and friends. Cleared in
    // _ExitTree so a CVar fired after the editor closes no-ops gracefully.
    public static WorldEditor Current;

    public Action onQuitToMenu;

    private const float MOVE_SPEED = 20f;
    private const float CLIP_START_OFFSET = 10f;
    private const float CLIP_VISUAL_BIAS = 0.05f;

    private static readonly VoxelType[] PlaceableTypes =
    {
        VoxelType.Stone, VoxelType.Barrier, VoxelType.Water,
    };

    private static readonly string[] EntityNames =
    {
        "PlayerSpawn", "Tree", "TallGrass", "Loot", "Chest", "Torch", "Door", "SpikeTrap", "Goblin", "KunKun", "ClimbableTree",
    };

    private World _world;
    private WorldState _worldState;
    private Vector3 _cursorPosition;
    private float _clipY = float.PositiveInfinity;
    private int _voxelTypeIndex = 0;
    private int _entityTypeIndex = 0;
    private bool _entityMode = false;
    private int _paintHeight = 1;
    private bool _dragActive = false;
    private bool _dragDeleting = false;
    private bool _dragReplacing = false;
    private int _dragBaseY = 0;
    private readonly HashSet<Vector3I> _lastPaintedBlocks = new HashSet<Vector3I>();
    private int _debugFrameCount = 0;

    // Two-corner bbox selection for subscene save. Each is the floored
    // voxel coordinate of the editor cursor at the time the corner was
    // marked. Null until set; both required before save. Marking a third
    // corner overwrites A and clears B.
    private Vector3I? _subsceneCornerA;
    private Vector3I? _subsceneCornerB;

    public void Init(WorldState worldState)
    {
        Current = this;
        _worldState = worldState;
        _cursorPosition = worldState.Spawn;
        _clipY = _cursorPosition.Y + CLIP_START_OFFSET;

        GD.Print($"[Editor] Init: spawn={_cursorPosition}, clipY={_clipY}");
        GD.Print($"[Editor] WorldState: Min={worldState.Min}, Max={worldState.Max}, chunks={worldState._chunks.Count}");

        // Count non-empty chunks
        int nonEmptyChunks = 0;
        int totalVoxels = 0;
        foreach (var kvp in worldState._chunks)
        {
            bool hasVoxels = false;
            for (int x = 0; x < ChunkState.SIZE && !hasVoxels; x++)
            {
                for (int y = 0; y < ChunkState.SIZE && !hasVoxels; y++)
                {
                    for (int z = 0; z < ChunkState.SIZE && !hasVoxels; z++)
                    {
                        if (kvp.Value.Voxels[x, y, z] != VoxelType.Air)
                        {
                            hasVoxels = true;
                            totalVoxels++;
                        }
                    }
                }
            }
            if (hasVoxels)
            {
                nonEmptyChunks++;
            }
        }
        GD.Print($"[Editor] Non-empty chunks: {nonEmptyChunks}, has voxels: {totalVoxels > 0}");

        _world = new World();
        AddChild(_world);
        GD.Print("[Editor] World added to tree");

        _world.Initialize(worldState, _cursorPosition, camera, null, () => _cursorPosition);
        GD.Print("[Editor] World initialized");

        _world.EnableEditorMode();
        _world.UpdateEntityLoading(_cursorPosition);

        camera.Init(this);
        camera.ManualClipMode = true;
        camera.SetInitialPosition(_cursorPosition);
        camera.SetClip(_clipY - CLIP_VISUAL_BIAS, _cursorPosition);

        GD.Print($"[Editor] Camera pos={camera.GlobalPosition}, rot={camera.GlobalRotation}");
        GD.Print($"[Editor] Camera projection={camera.Projection}, size={camera.Size}");

        UpdateHud();
        GD.Print("[Editor] Init complete");
    }

    public override void _Process(double deltaTime)
    {
        _debugFrameCount++;
        if (_debugFrameCount <= 5 || _debugFrameCount % 300 == 0)
        {
            int childCount = _world.GetChildCount();
            GD.Print($"[Editor] Frame {_debugFrameCount}: cursor={_cursorPosition}, cam={camera.GlobalPosition}, worldChildren={childCount}, clipY={_clipY}");
        }

        if (ConsoleUI.IsOpen)
        {
            return;
        }

        float dt = (float)deltaTime;

        // WASD movement on XZ plane relative to camera yaw
        Vector2 input = Input.GetVector("MoveLeft", "MoveRight", "MoveUp", "MoveDown");
        if (input.LengthSquared() > 0f)
        {
            float yaw = camera.Yaw;
            Vector3 forward = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
            Vector3 right = new Vector3(forward.Z, 0, -forward.X);
            _cursorPosition += (forward * input.Y + right * input.X) * MOVE_SPEED * dt;
        }

        camera.UpdateCamera(deltaTime, _cursorPosition, 0f);
        camera.SetClip(_clipY - CLIP_VISUAL_BIAS, _cursorPosition);
        CullProps(camera.Clip);
        _world.UpdateEntityLoading(_cursorPosition);

        editorHud.UpdatePosition(_cursorPosition);
        editorHud.UpdateClip(_clipY);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (ConsoleUI.IsOpen)
        {
            return;
        }

        if (e.IsActionPressed("TogglePause"))
        {
            onQuitToMenu?.Invoke();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (e.IsActionPressed("CameraLeft"))
        {
            camera.RotateLeft();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (e.IsActionPressed("CameraRight"))
        {
            camera.RotateRight();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (e.IsActionPressed("EditorUp"))
        {
            _cursorPosition.Y += 1f;
            _clipY += 1f;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (e.IsActionPressed("EditorDown"))
        {
            _cursorPosition.Y -= 1f;
            _clipY -= 1f;
            GetViewport().SetInputAsHandled();
            return;
        }

        // Q/E cycle types
        if (e.IsActionPressed("UseItem"))
        {
            CyclePrevious();
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (e.IsActionPressed("Interact"))
        {
            CycleNext();
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Tab toggles mode
        if (e.IsActionPressed("Inventory"))
        {
            _entityMode = !_entityMode;
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Ctrl+S save, number keys for paint height
        if (e is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.S && keyEvent.CtrlPressed)
            {
                Save();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (keyEvent.Keycode >= Key.Key0 && keyEvent.Keycode <= Key.Key9 && !keyEvent.CtrlPressed)
            {
                int digit = (int)(keyEvent.Keycode - Key.Key0);
                _paintHeight = digit == 0 ? 10 : digit;
                UpdateHud();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        // Left click: place/delete/replace (with drag support in voxel mode)
        if (e is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                bool ctrlHeld = mouseButton.CtrlPressed;
                bool altHeld = mouseButton.AltPressed;
                if (_entityMode)
                {
                    HandleEntityClick(mouseButton.Position, ctrlHeld);
                }
                else
                {
                    bool overwrite = ctrlHeld || altHeld;
                    if (ComputeVoxelTarget(mouseButton.Position, overwrite, out Vector3I hitBlock, out Vector3I baseTarget))
                    {
                        _lastPaintedBlocks.Clear();
                        PaintColumnAt(baseTarget, ctrlHeld);
                        _dragActive = true;
                        _dragDeleting = ctrlHeld;
                        _dragReplacing = altHeld && !ctrlHeld;
                        _dragBaseY = baseTarget.Y;
                    }
                }
                GetViewport().SetInputAsHandled();
            }
            else
            {
                _dragActive = false;
                _dragDeleting = false;
                _dragReplacing = false;
                _lastPaintedBlocks.Clear();
            }
        }

        if (e is InputEventMouseMotion mouseMotion && _dragActive)
        {
            bool delete = _dragDeleting;
            bool overwrite = _dragDeleting || _dragReplacing;
            if (ComputeVoxelTarget(mouseMotion.Position, overwrite, out Vector3I hitBlock, out Vector3I baseTarget))
            {
                // Skip if the ray hits a block we just painted (for place) or if the
                // base target is one we already modified.
                if (baseTarget.Y == _dragBaseY
                    && !_lastPaintedBlocks.Contains(baseTarget)
                    && !_lastPaintedBlocks.Contains(hitBlock))
                {
                    PaintColumnAt(baseTarget, delete);
                }
            }
        }
    }

    private bool ComputeVoxelTarget(Vector2 screenPos, bool overwriteHitBlock, out Vector3I hitBlock, out Vector3I baseTarget)
    {
        hitBlock = default;
        baseTarget = default;

        Vector3 hitPos;
        Vector3 hitNormal;

        // If a clip is active, first check whether the ray starts inside a solid
        // block at the clip plane. Trimesh colliders are single-sided, so a ray
        // originating inside geometry would pass through without any hit. Detect
        // this case by sampling the voxel at the ray/clip-plane intersection and,
        // if it's solid, synthesize a hit on the top of that block.
        if (_clipY < float.PositiveInfinity)
        {
            Vector3 rayOrigin = camera.ProjectRayOrigin(screenPos);
            Vector3 rayDir = camera.ProjectRayNormal(screenPos);
            if (rayDir.Y < 0f)
            {
                float clipPlaneY = _clipY - CLIP_VISUAL_BIAS;
                float t = (clipPlaneY - rayOrigin.Y) / rayDir.Y;
                Vector3 planeHit = rayOrigin + rayDir * t;
                int vx = Mathf.FloorToInt(planeHit.X);
                int vz = Mathf.FloorToInt(planeHit.Z);
                int vy = Mathf.FloorToInt(_clipY) - 1;
                VoxelType voxel = _worldState.GetVoxelWorld(vx, vy, vz);
                if (voxel != VoxelType.Air && voxel != VoxelType.Water)
                {
                    hitPos = new Vector3(planeHit.X, clipPlaneY, planeHit.Z);
                    hitNormal = Vector3.Up;
                    FinalizeTarget(hitPos, hitNormal, overwriteHitBlock, out hitBlock, out baseTarget);
                    return true;
                }
            }
        }

        var result = Raycast(screenPos);
        if (result.Count == 0)
        {
            return false;
        }

        hitPos = (Vector3)result["position"];
        hitNormal = (Vector3)result["normal"];

        FinalizeTarget(hitPos, hitNormal, overwriteHitBlock, out hitBlock, out baseTarget);
        return true;
    }

    private static void FinalizeTarget(Vector3 hitPos, Vector3 hitNormal, bool overwriteHitBlock, out Vector3I hitBlock, out Vector3I baseTarget)
    {
        hitBlock = new Vector3I(
            Mathf.FloorToInt(hitPos.X - hitNormal.X * 0.5f),
            Mathf.FloorToInt(hitPos.Y - hitNormal.Y * 0.5f),
            Mathf.FloorToInt(hitPos.Z - hitNormal.Z * 0.5f));

        if (overwriteHitBlock)
        {
            baseTarget = hitBlock;
        }
        else
        {
            baseTarget = new Vector3I(
                Mathf.FloorToInt(hitPos.X + hitNormal.X * 0.5f),
                Mathf.FloorToInt(hitPos.Y + hitNormal.Y * 0.5f),
                Mathf.FloorToInt(hitPos.Z + hitNormal.Z * 0.5f));
        }
    }

    private void PaintColumnAt(Vector3I baseTarget, bool delete)
    {
        int clipFloor = Mathf.FloorToInt(_clipY);
        VoxelType type = delete ? VoxelType.Air : PlaceableTypes[_voxelTypeIndex];
        var changed = new List<Vector3I>();

        for (int i = 0; i < _paintHeight; i++)
        {
            Vector3I target = new Vector3I(baseTarget.X, baseTarget.Y + i, baseTarget.Z);
            if (target.Y >= clipFloor)
            {
                break;
            }
            _worldState.SetVoxelWorld(target.X, target.Y, target.Z, type);
            changed.Add(target);
            _lastPaintedBlocks.Add(target);
        }

        if (changed.Count > 0)
        {
            _world.UpdateLighting(changed);
            _world.RebuildNearbyChunkMeshes(new Vector3(baseTarget.X, baseTarget.Y, baseTarget.Z), changed);
        }
    }

    private void HandleEntityClick(Vector2 screenPos, bool delete)
    {
        var result = Raycast(screenPos);
        if (result.Count == 0)
        {
            return;
        }

        Vector3 hitPos = (Vector3)result["position"];
        Vector3 hitNormal = (Vector3)result["normal"];

        // Place on the surface
        Vector3 placePos = hitPos + hitNormal * 0.5f;

        if (delete)
        {
            DeleteNearestEntity(placePos);
        }
        else
        {
            PlaceEntity(placePos);
        }
    }

    private void PlaceEntity(Vector3 position)
    {
        string entityName = EntityNames[_entityTypeIndex];

        if (entityName == "PlayerSpawn")
        {
            _worldState.Spawn = position;
            GD.Print($"Player spawn set to {position}");
            return;
        }

        EntitySimState simState = CreateEntitySimState(entityName, position);
        if (simState == null)
        {
            return;
        }

        _worldState.AddEntity(simState);

        // Spawn the visual node immediately
        Node3D node = simState.CreateEntity(_world);
        if (node != null)
        {
            Vector3I coord = World.WorldToChunkCoord(position);
            if (!_world.ActiveEntities.ContainsKey(coord))
            {
                // The chunk may not have an active entity list yet; force one via
                // UpdateEntityLoading which will pick it up. For now, trigger a
                // reload by invalidating and re-running entity loading.
            }
            // Register the entity node with the world
            RegisterEditorEntity(node, coord);
        }
    }

    private EntitySimState CreateEntitySimState(string entityName, Vector3 position)
    {
        // Tree/TallGrass scene palettes live on the terrain kit at the cursor's
        // surface column, so the editor's spawn dropdown reads from whichever
        // kit was stamped at that voxel (matches what WorldGen would pick at
        // run time). Loot/Chest/Torch palettes are pulled out of the first
        // zone's SpawnListData entries — the editor picks the first matching
        // subclass entry as its representative scene. Door/SpikeTrap stay on
        // WorldGenData (they aren't placed by the per-zone scan). Goblin /
        // KunKun load their MobData directly so the editor doesn't depend on
        // them being authored into a spawn list.
        TerrainKitData cursorKit = ResolveKitAtCursor(position);
        ZoneGenData firstZone = worldGenData.Zones != null && worldGenData.Zones.Length > 0 ? worldGenData.Zones[0] : null;
        switch (entityName)
        {
            case "Tree":
            {
                WeightedList<PackedScene> treeChances = WeightedScene.BuildList(cursorKit?.TreeScenes);
                return treeChances.Count > 0
                    ? new PropSimState(PropType.Tree, position, treeChances.Choose(GD.Randf() * treeChances.TotalWeight))
                    : null;
            }
            case "TallGrass":
            {
                WeightedList<PackedScene> grassChances = WeightedScene.BuildList(cursorKit?.TallGrassScenes);
                return grassChances.Count > 0
                    ? new PropSimState(PropType.Foliage, position, grassChances.Choose(GD.Randf() * grassChances.TotalWeight))
                    : null;
            }
            case "Loot":
            {
                LootSpawnEntry loot = FindFirstSurfaceEntry<LootSpawnEntry>(firstZone);
                if (loot?.Item?.item == null) { return null; }
                var lootSim = new LootSimState(position, loot.Item.item);
                if (loot.Item.HasStatusEffects)
                {
                    lootSim.Item = loot.Item.CreateState();
                }
                return lootSim;
            }
            case "Chest":
            {
                ChestSpawnEntry chest = FindFirstCaveEntry<ChestSpawnEntry>(firstZone);
                return chest?.Scene != null
                    ? new ChestSimState(position, chest.Scene) { LootItems = ChestSpawnEntry.Resolve(chest.LootItems, new Random()) }
                    : null;
            }
            case "Torch":
            {
                TorchSpawnEntry torch = FindFirstCaveEntry<TorchSpawnEntry>(firstZone);
                return torch?.Scene != null ? new TorchSimState(position, torch.Scene) : null;
            }
            case "Door":
                return new DoorSimState(position, 0f, worldGenData.DoorScene);
            case "ClimbableTree":
                return worldGenData.ClimbableTreeScene != null
                    ? new ClimbableTreeSimState(position, worldGenData.ClimbableTreeScene)
                    : null;
            case "SpikeTrap":
                return worldGenData.SpikeTrapScene != null
                    ? new TrapSimState(position, worldGenData.SpikeTrapScene)
                    : null;
            case "Goblin":
            {
                MobData data = GD.Load<MobData>("res://resources/data/characters/goblin.tres");
                return data?.MobScene != null ? new MobSimState(position, 0f, data.MobScene, data) : null;
            }
            case "KunKun":
            {
                MobData data = GD.Load<MobData>("res://resources/data/characters/kun_kun.tres");
                return data?.MobScene != null ? new MobSimState(position, 0f, data.MobScene, data) : null;
            }
            default:
                return null;
        }
    }

    private static T FindFirstSurfaceEntry<T>(ZoneGenData zone) where T : SpawnEntryData
    {
        return FindFirstEntryIn<T>(zone?.SurfaceEntities);
    }

    private static T FindFirstCaveEntry<T>(ZoneGenData zone) where T : SpawnEntryData
    {
        return FindFirstEntryIn<T>(zone?.CaveEntities);
    }

    private static T FindFirstEntryIn<T>(SpawnListData list) where T : SpawnEntryData
    {
        if (list?.Entries == null) { return null; }
        foreach (SpawnEntryData entry in list.Entries)
        {
            if (entry is T match) { return match; }
        }
        return null;
    }

    private void RegisterEditorEntity(Node3D node, Vector3I coord)
    {
        if (node is IWorldEntity worldEntity)
        {
            worldEntity.OnSpawned(_world);
        }

        // Access internal entity list — World.ActiveEntities is readonly dict,
        // but we need to add to it. Use the same approach as World.SpawnLoot:
        // if the chunk list doesn't exist, the entity will be picked up when
        // the chunk's entities are loaded. For now, call QueueFree on duplicate
        // and let the next entity loading pass pick it up.
        // Actually, we need direct access. Let's add to World via the public API.
        // World already has SpawnLoot which does this. We'll add a general method.

        // For now, add the node to the scene tree directly and let chunk entity
        // loading manage it on the next pass. The entity is already in WorldState.
        // Force a reload of this chunk's entities.
        ReloadChunkEntities(coord);
    }

    private void ReloadChunkEntities(Vector3I coord)
    {
        // Unload then reload entities for this chunk so the newly added one appears
        _world.UnloadChunkEntities(coord);
        _world.LoadChunkEntities(coord);
    }

    private void DeleteNearestEntity(Vector3 position)
    {
        Vector3I centerChunk = World.WorldToChunkCoord(position);
        float bestDist = 4f; // max search radius squared = 2^2
        EntitySimState bestSim = null;
        Vector3I bestChunk = default;

        // Search the center chunk and neighbors
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    Vector3I chunkCoord = centerChunk + new Vector3I(dx, dy, dz);
                    List<EntitySimState> simStates = _worldState.GetEntities(chunkCoord);
                    if (simStates == null)
                    {
                        continue;
                    }
                    foreach (EntitySimState sim in simStates)
                    {
                        float dist = sim.WorldPosition.DistanceSquaredTo(position);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestSim = sim;
                            bestChunk = chunkCoord;
                        }
                    }
                }
            }
        }

        if (bestSim == null)
        {
            return;
        }

        // Remove from world state
        _worldState.RemoveEntity(bestSim);

        // Find and remove the active node
        if (_world.ActiveEntities.TryGetValue(bestChunk, out List<Node3D> activeNodes))
        {
            // Find the node closest to the sim state's position
            Node3D toRemove = null;
            float nodeBestDist = float.MaxValue;
            foreach (Node3D node in activeNodes)
            {
                float dist = node.GlobalPosition.DistanceSquaredTo(bestSim.WorldPosition);
                if (dist < nodeBestDist)
                {
                    nodeBestDist = dist;
                    toRemove = node;
                }
            }
            if (toRemove != null)
            {
                _world.RemoveEntity(toRemove);
                toRemove.QueueFree();
            }
        }
    }

    // Resolve the surface terrain kit under the editor cursor. Walks the
    // column at the cursor's XZ down up to SEARCH voxels looking for the
    // first solid / water voxel; that voxel's terrain id is what scene
    // palettes (TreeScenes, TallGrassScenes) read off the active kit
    // palette. Returns null if the column has no content within reach.
    private TerrainKitData ResolveKitAtCursor(Vector3 position)
    {
        int ox = (int)Math.Floor(position.X);
        int oy = (int)Math.Floor(position.Y);
        int oz = (int)Math.Floor(position.Z);
        const int SEARCH = 4;
        for (int dy = 0; dy <= SEARCH; dy++)
        {
            int sy = oy - dy;
            VoxelType v = _worldState.GetVoxelWorld(ox, sy, oz);
            if (v != VoxelType.Air && v != VoxelType.Water)
            {
                int terrainId = _worldState.GetTerrainIdWorld(ox, sy, oz);
                TerrainKitData[] palette = WorldGen.ActiveKitPalette;
                if (palette == null || terrainId < 0 || terrainId >= palette.Length) { return null; }
                return palette[terrainId];
            }
        }
        return null;
    }

    private Godot.Collections.Dictionary Raycast(Vector2 screenPos)
    {
        Vector3 rayOrigin = camera.ProjectRayOrigin(screenPos);
        Vector3 rayDir = camera.ProjectRayNormal(screenPos);

        // If a clip is active, start the ray just below the clip plane so we
        // don't hit collision geometry above the clip that was culled visually.
        if (_clipY < float.PositiveInfinity && rayDir.Y < 0f)
        {
            const float CLIP_RAY_EXTRA = 0.01f;
            float targetY = _clipY - CLIP_VISUAL_BIAS - CLIP_RAY_EXTRA;
            if (rayOrigin.Y > targetY)
            {
                float t = (targetY - rayOrigin.Y) / rayDir.Y;
                rayOrigin += rayDir * t;
            }
        }

        Vector3 rayEnd = rayOrigin + rayDir * 200f;

        var spaceState = GetWorld3D().DirectSpaceState;
        using var query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd);
        query.CollisionMask = (uint)(ECollisionLayer.Solid | ECollisionLayer.Water);
        return spaceState.IntersectRay(query);
    }

    private void CycleNext()
    {
        if (_entityMode)
        {
            _entityTypeIndex = (_entityTypeIndex + 1) % EntityNames.Length;
        }
        else
        {
            _voxelTypeIndex = (_voxelTypeIndex + 1) % PlaceableTypes.Length;
        }
    }

    private void CyclePrevious()
    {
        if (_entityMode)
        {
            _entityTypeIndex = (_entityTypeIndex - 1 + EntityNames.Length) % EntityNames.Length;
        }
        else
        {
            _voxelTypeIndex = (_voxelTypeIndex - 1 + PlaceableTypes.Length) % PlaceableTypes.Length;
        }
    }

    private void UpdateHud()
    {
        if (_entityMode)
        {
            editorHud.UpdateEntityMode(EntityNames[_entityTypeIndex], _entityTypeIndex, EntityNames.Length);
        }
        else
        {
            editorHud.UpdateVoxelMode(PlaceableTypes[_voxelTypeIndex].ToString(), _voxelTypeIndex, PlaceableTypes.Length);
        }
        editorHud.UpdatePaintHeight(_paintHeight);
    }

    private void CullProps(float cameraClip)
    {
        foreach (List<Node3D> entities in _world.ActiveEntities.Values)
        {
            foreach (Node3D entity in entities)
            {
                entity.Visible = entity.GlobalPosition.Y < cameraClip;
            }
        }
    }

    private void Save()
    {
        string path = CVars.worldFile.Value;
        try
        {
            WorldFile.Write(path, _worldState);
            GD.Print($"World saved to {path}");
        }
        catch (Exception e)
        {
            GD.PrintErr($"Save failed: {e.Message}");
        }
    }

    public override void _ExitTree()
    {
        if (Current == this)
        {
            Current = null;
        }
    }

    // Mark the editor cursor's current voxel as the next subscene corner.
    // Toggle order: empty → A; A → B; A+B → reset and start over with A.
    public void MarkSubsceneCorner()
    {
        var voxel = new Vector3I(
            Mathf.FloorToInt(_cursorPosition.X),
            Mathf.FloorToInt(_cursorPosition.Y),
            Mathf.FloorToInt(_cursorPosition.Z));
        if (_subsceneCornerA == null)
        {
            _subsceneCornerA = voxel;
            GD.Print($"subscene_corner: A = {voxel}");
        }
        else if (_subsceneCornerB == null)
        {
            _subsceneCornerB = voxel;
            Vector3I min = ComponentMin(_subsceneCornerA.Value, voxel);
            Vector3I max = ComponentMax(_subsceneCornerA.Value, voxel);
            Vector3I size = max - min + new Vector3I(1, 1, 1);
            GD.Print($"subscene_corner: B = {voxel}; bbox min={min} max={max} size={size}");
        }
        else
        {
            _subsceneCornerA = voxel;
            _subsceneCornerB = null;
            GD.Print($"subscene_corner: reset, A = {voxel}");
        }
    }

    public void ClearSubsceneCorners()
    {
        _subsceneCornerA = null;
        _subsceneCornerB = null;
        GD.Print("subscene_corner: cleared");
    }

    // Save the bounded selection as a subscene file. All voxels inside the
    // bbox are marked present (= they will overwrite destination voxels on
    // stamp) — keep the selection tight if you don't want to nuke
    // surrounding terrain. Entities whose WorldPosition falls inside the
    // bbox are deep-cloned (via the EntitySerializer round-trip) and added
    // with subscene-local coordinates. Anchor defaults to (0,0,0) — bbox
    // min corner.
    //
    // includeEnv=true also bakes Wind/EnvTag overrides from the source
    // chunks' subgrids — use this for castles/dungeons that need to
    // override the destination's default-baked ambience.
    public void SaveSubscene(string path, bool includeEnv)
    {
        if (_subsceneCornerA == null || _subsceneCornerB == null)
        {
            GD.PrintErr("subscene_save: need two corners (subscene_corner twice).");
            return;
        }

        Vector3I min = ComponentMin(_subsceneCornerA.Value, _subsceneCornerB.Value);
        Vector3I max = ComponentMax(_subsceneCornerA.Value, _subsceneCornerB.Value);
        Vector3I size = max - min + new Vector3I(1, 1, 1);

        var sub = new SubsceneState(size);
        for (int dx = 0; dx < size.X; dx++)
        {
            for (int dy = 0; dy < size.Y; dy++)
            {
                for (int dz = 0; dz < size.Z; dz++)
                {
                    int wx = min.X + dx;
                    int wy = min.Y + dy;
                    int wz = min.Z + dz;
                    sub.Voxels[dx, dy, dz] = _worldState.GetVoxelWorld(wx, wy, wz);
                    sub.Shape[dx, dy, dz] = (byte)_worldState.GetShapeWorld(wx, wy, wz);
                    sub.TerrainId[dx, dy, dz] = (byte)_worldState.GetTerrainIdWorld(wx, wy, wz);
                    sub.OverlayId[dx, dy, dz] = (byte)_worldState.GetOverlayIdWorld(wx, wy, wz);
                    sub.DetailGroup[dx, dy, dz] = (byte)_worldState.GetDetailGroupWorld(wx, wy, wz);
                    sub.DetailStrength[dx, dy, dz] = (byte)_worldState.GetDetailStrengthWorld(wx, wy, wz);
                    sub.PresenceMask[dx, dy, dz] = true;
                }
            }
        }

        if (includeEnv)
        {
            sub.EnsureWindFactor();
            sub.EnsureEnvTag();
            BakeEnvFromWorld(sub, min);
        }

        sub.Entities = CollectEntitiesInBox(min, max, size);
        sub.Anchor = Vector3.Zero;

        try
        {
            SubsceneFile.Write(path, sub);
            GD.Print($"subscene_save: wrote {path} (size={size}, env={(includeEnv ? "yes" : "no")}, entities={sub.Entities.Count})");
        }
        catch (Exception e)
        {
            GD.PrintErr($"subscene_save failed: {e.Message}");
        }
    }

    // Stamp a subscene file at the editor cursor and rebuild meshes /
    // entity loading for affected chunks so the result is visible
    // immediately. Runtime stamping — runs StampAll, which writes both
    // voxels and (if authored) env overrides since the default bakes
    // ran at world creation and won't clobber anything now.
    public void StampSubscene(string path)
    {
        SubsceneState sub;
        try
        {
            sub = SubsceneFile.Read(path);
        }
        catch (Exception e)
        {
            GD.PrintErr($"subscene_stamp: read failed: {e.Message}");
            return;
        }

        Vector3 anchor = _cursorPosition;
        Vector3I worldOriginI = new Vector3I(
            Mathf.FloorToInt(anchor.X - sub.Anchor.X),
            Mathf.FloorToInt(anchor.Y - sub.Anchor.Y),
            Mathf.FloorToInt(anchor.Z - sub.Anchor.Z));
        Vector3I size = sub.Size;

        // Build the changed list so UpdateLighting / RebuildNearbyChunkMeshes
        // know which voxels to recompute around. Cheaper than enumerating
        // every voxel: list the cells we will write (presence mask).
        var changed = new List<Vector3I>(size.X * size.Y * size.Z);
        for (int dx = 0; dx < size.X; dx++)
        {
            for (int dy = 0; dy < size.Y; dy++)
            {
                for (int dz = 0; dz < size.Z; dz++)
                {
                    if (sub.PresenceMask[dx, dy, dz])
                    {
                        changed.Add(new Vector3I(worldOriginI.X + dx, worldOriginI.Y + dy, worldOriginI.Z + dz));
                    }
                }
            }
        }

        // Track the chunks that gained entities so we can refresh their
        // active entity nodes after stamping.
        var entityChunks = new HashSet<Vector3I>();
        if (sub.Entities != null)
        {
            foreach (EntitySimState e in sub.Entities)
            {
                Vector3 worldPos = e.WorldPosition + new Vector3(
                    anchor.X - sub.Anchor.X,
                    anchor.Y - sub.Anchor.Y,
                    anchor.Z - sub.Anchor.Z);
                entityChunks.Add(World.WorldToChunkCoord(worldPos));
            }
        }

        SubsceneStamper.StampAll(_worldState, sub, anchor);

        if (changed.Count > 0)
        {
            _world.UpdateLighting(changed);
            _world.RebuildNearbyChunkMeshes(anchor, changed);
        }
        foreach (Vector3I cc in entityChunks)
        {
            _world.UnloadChunkEntities(cc);
            _world.LoadChunkEntities(cc);
        }
        GD.Print($"subscene_stamp: stamped {Path.GetFileName(path)} at {anchor} (voxels={changed.Count}, entityChunks={entityChunks.Count})");
    }

    private static Vector3I ComponentMin(Vector3I a, Vector3I b)
    {
        return new Vector3I(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    }

    private static Vector3I ComponentMax(Vector3I a, Vector3I b)
    {
        return new Vector3I(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    }

    private void BakeEnvFromWorld(SubsceneState sub, Vector3I worldOrigin)
    {
        const int S = ChunkState.ENV_VOXELS_PER_CELL;
        Vector3I envSize = sub.EnvSize;
        for (int lcx = 0; lcx < envSize.X; lcx++)
        {
            for (int lcy = 0; lcy < envSize.Y; lcy++)
            {
                for (int lcz = 0; lcz < envSize.Z; lcz++)
                {
                    // Subscene env-cell center → world voxel center → world env-cell.
                    int vcx = worldOrigin.X + lcx * S + S / 2;
                    int vcy = worldOrigin.Y + lcy * S + S / 2;
                    int vcz = worldOrigin.Z + lcz * S + S / 2;
                    int cwx = (int)Math.Floor((double)vcx / S);
                    int cwy = (int)Math.Floor((double)vcy / S);
                    int cwz = (int)Math.Floor((double)vcz / S);
                    int chunkX = (int)Math.Floor((double)cwx / ChunkState.ENV_SUBGRID_SIZE);
                    int chunkY = (int)Math.Floor((double)cwy / ChunkState.ENV_SUBGRID_SIZE);
                    int chunkZ = (int)Math.Floor((double)cwz / ChunkState.ENV_SUBGRID_SIZE);
                    ChunkState chunk = _worldState.GetChunk(new Vector3I(chunkX, chunkY, chunkZ));
                    if (chunk == null)
                    {
                        continue;
                    }
                    int sx = ((cwx % ChunkState.ENV_SUBGRID_SIZE) + ChunkState.ENV_SUBGRID_SIZE) % ChunkState.ENV_SUBGRID_SIZE;
                    int sy = ((cwy % ChunkState.ENV_SUBGRID_SIZE) + ChunkState.ENV_SUBGRID_SIZE) % ChunkState.ENV_SUBGRID_SIZE;
                    int sz = ((cwz % ChunkState.ENV_SUBGRID_SIZE) + ChunkState.ENV_SUBGRID_SIZE) % ChunkState.ENV_SUBGRID_SIZE;
                    sub.WindFactor[lcx, lcy, lcz] = chunk.WindFactor[sx, sy, sz];
                    sub.EnvTag[lcx, lcy, lcz] = chunk.EnvTag[sx, sy, sz];
                }
            }
        }
    }

    // Walk every chunk overlapping the bbox, collect EntitySimStates inside
    // it, and return deep-cloned copies with subscene-local positions. The
    // serializer round-trip is the cheapest deep-clone path that keeps every
    // entity field aligned with the EntitySerializer's wire format — adding
    // fields to an entity automatically propagates through here.
    private List<EntitySimState> CollectEntitiesInBox(Vector3I min, Vector3I max, Vector3I size)
    {
        var inside = new List<EntitySimState>();
        Vector3I cMin = new Vector3I(
            (int)Math.Floor((double)min.X / ChunkState.SIZE),
            (int)Math.Floor((double)min.Y / ChunkState.SIZE),
            (int)Math.Floor((double)min.Z / ChunkState.SIZE));
        Vector3I cMax = new Vector3I(
            (int)Math.Floor((double)max.X / ChunkState.SIZE),
            (int)Math.Floor((double)max.Y / ChunkState.SIZE),
            (int)Math.Floor((double)max.Z / ChunkState.SIZE));
        for (int cx = cMin.X; cx <= cMax.X; cx++)
        {
            for (int cy = cMin.Y; cy <= cMax.Y; cy++)
            {
                for (int cz = cMin.Z; cz <= cMax.Z; cz++)
                {
                    List<EntitySimState> chunkEntities = _worldState.GetEntities(new Vector3I(cx, cy, cz));
                    if (chunkEntities == null)
                    {
                        continue;
                    }
                    foreach (EntitySimState e in chunkEntities)
                    {
                        Vector3 p = e.WorldPosition;
                        if (p.X >= min.X && p.X < min.X + size.X
                            && p.Y >= min.Y && p.Y < min.Y + size.Y
                            && p.Z >= min.Z && p.Z < min.Z + size.Z)
                        {
                            inside.Add(e);
                        }
                    }
                }
            }
        }

        if (inside.Count == 0)
        {
            return new List<EntitySimState>();
        }

        // Deep-clone via serializer round-trip, then translate into
        // subscene-local space. This avoids mutating editor-owned
        // entities and keeps clone semantics aligned with disk format.
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            EntitySerializer.WriteList(bw, inside);
        }
        ms.Position = 0;
        using var br = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: false);
        List<EntitySimState> clones = EntitySerializer.ReadList(br);

        Vector3 offset = new Vector3(-min.X, -min.Y, -min.Z);
        foreach (EntitySimState clone in clones)
        {
            clone.WorldPosition += offset;
            if (clone is MobSimState mob)
            {
                mob.SpawnPosition += offset;
            }
        }
        return clones;
    }

    public static WorldState CreateEmptyWorld(WorldGenData genData)
    {
        var min = new Vector3I(-4, -1, -4);
        var max = new Vector3I(3, 1, 3);
        var ws = new WorldState(min, max, genData.SimData);

        // Mirror WorldGen's zone setup so the sky preview has something
        // to blend in the editor. ZoneIndex stays 0 across all chunks
        // here (the editor's empty stub is a single uniform area); the
        // full editor will paint indices when authoring.
        ws.Zones = new ZoneState[genData.Zones.Length];
        for (int i = 0; i < genData.Zones.Length; i++)
        {
            ws.Zones[i] = new ZoneState
            {
                Data = genData.Zones[i]?.Zone,
                WindDirection = new Vector3(0.7f, 0f, 0.7f),
                Elevation = 0f,
            };
        }

        // Initialize all chunks
        for (int cx = min.X; cx <= max.X; cx++)
        {
            for (int cy = min.Y; cy <= max.Y; cy++)
            {
                for (int cz = min.Z; cz <= max.Z; cz++)
                {
                    var coord = new Vector3I(cx, cy, cz);
                    ws._chunks[coord] = new ChunkState(coord);
                }
            }
        }

        // Fill world y=0 layer with grass (chunk y=0, local y=0)
        for (int cx = min.X; cx <= max.X; cx++)
        {
            for (int cz = min.Z; cz <= max.Z; cz++)
            {
                var chunk = ws._chunks[new Vector3I(cx, 0, cz)];
                for (int x = 0; x < ChunkState.SIZE; x++)
                {
                    for (int z = 0; z < ChunkState.SIZE; z++)
                    {
                        chunk.Voxels[x, 0, z] = VoxelType.Terrain;
                        chunk.Shape[x, 0, z] = (byte)VoxelTypeInfo.SharpAxes.Y;
                    }
                }
            }
        }

        ws.Spawn = new Vector3(0, 1, 0);

        // Compute initial sunlight so the world isn't pitch black
        LightEngine.ComputeSunlight(ws);

        return ws;
    }
}
