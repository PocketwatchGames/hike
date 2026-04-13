using Godot;
using System;
using System.Collections.Generic;

[GlobalClass]
public partial class WorldEditor : Node3D
{
    [Export] public GameCamera camera;
    [Export] public EditorHud editorHud;
    [Export] public WorldGenData worldGenData;

    public Action onQuitToMenu;

    private const float MOVE_SPEED = 20f;
    private const float CLIP_START_OFFSET = 10f;

    private static readonly VoxelType[] PlaceableTypes =
    {
        VoxelType.Stone, VoxelType.Grass, VoxelType.Dirt, VoxelType.Sand,
        VoxelType.Wood, VoxelType.StoneSlabBottom, VoxelType.StoneSlabTop,
        VoxelType.WoodSlabBottom, VoxelType.WoodSlabTop, VoxelType.GrassSlabBottom,
        VoxelType.DirtSlabBottom, VoxelType.Barrier, VoxelType.Water,
    };

    private static readonly string[] EntityNames =
    {
        "PlayerSpawn", "Tree", "TallGrass", "Loot", "Chest", "Torch", "Door", "Goblin", "KunKun",
    };

    private World _world;
    private WorldState _worldState;
    private Vector3 _cursorPosition;
    private float _clipY = float.PositiveInfinity;
    private int _voxelTypeIndex = 0;
    private int _entityTypeIndex = 0;
    private bool _entityMode = false;
    private int _debugFrameCount = 0;

    public void Init(WorldState worldState)
    {
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

        _world.Initialize(worldState, _cursorPosition, camera, () => _cursorPosition);
        GD.Print("[Editor] World initialized");

        _world.EnableEditorMode();
        _world.UpdateEntityLoading(_cursorPosition);

        camera.Init(this);
        camera.ManualClipMode = true;
        camera.SetInitialPosition(_cursorPosition);
        camera.SetClip(_clipY, _cursorPosition);

        GD.Print($"[Editor] Camera pos={camera.GlobalPosition}, rot={camera.GlobalRotation}");
        GD.Print($"[Editor] Camera projection={camera.Projection}, size={camera.Size}");

        if (camera.WaterCapPlane.MaterialOverride is ShaderMaterial waterCapMat)
        {
            _world.SetLightMapUniforms(waterCapMat);
        }

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

        camera.UpdateCamera(deltaTime, _cursorPosition);
        camera.SetClip(_clipY, _cursorPosition);
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

        // Ctrl+S save
        if (e is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.S && keyEvent.CtrlPressed)
            {
                Save();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        // Left click: place/delete
        if (e is InputEventMouseButton mouseButton && mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.Left)
        {
            bool ctrlHeld = mouseButton.CtrlPressed;
            if (_entityMode)
            {
                HandleEntityClick(mouseButton.Position, ctrlHeld);
            }
            else
            {
                HandleVoxelClick(mouseButton.Position, ctrlHeld);
            }
            GetViewport().SetInputAsHandled();
        }
    }

    private void HandleVoxelClick(Vector2 screenPos, bool delete)
    {
        var result = Raycast(screenPos);
        if (result.Count == 0)
        {
            return;
        }

        Vector3 hitPos = (Vector3)result["position"];
        Vector3 hitNormal = (Vector3)result["normal"];

        if (delete)
        {
            // Delete the block that was hit
            Vector3I target = new Vector3I(
                Mathf.FloorToInt(hitPos.X - hitNormal.X * 0.5f),
                Mathf.FloorToInt(hitPos.Y - hitNormal.Y * 0.5f),
                Mathf.FloorToInt(hitPos.Z - hitNormal.Z * 0.5f)
            );
            if (target.Y >= Mathf.FloorToInt(_clipY))
            {
                return;
            }
            _worldState.SetVoxelWorld(target.X, target.Y, target.Z, VoxelType.Air);
            AfterVoxelEdit(target);
        }
        else
        {
            // Place a block adjacent to the hit face
            Vector3I target = new Vector3I(
                Mathf.FloorToInt(hitPos.X + hitNormal.X * 0.5f),
                Mathf.FloorToInt(hitPos.Y + hitNormal.Y * 0.5f),
                Mathf.FloorToInt(hitPos.Z + hitNormal.Z * 0.5f)
            );
            if (target.Y >= Mathf.FloorToInt(_clipY))
            {
                return;
            }
            _worldState.SetVoxelWorld(target.X, target.Y, target.Z, PlaceableTypes[_voxelTypeIndex]);
            AfterVoxelEdit(target);
        }
    }

    private void AfterVoxelEdit(Vector3I target)
    {
        var changed = new List<Vector3I> { target };
        _world.UpdateLighting(changed);
        _world.RebuildNearbyChunkMeshes(new Vector3(target.X, target.Y, target.Z), changed);
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
        switch (entityName)
        {
            case "Tree":
                return new PropSimState(PropType.Tree, position, worldGenData.TreeScene);
            case "TallGrass":
                return new PropSimState(PropType.TallGrass, position, worldGenData.TallGrassScene);
            case "Loot":
                return new PropSimState(PropType.Loot, position, worldGenData.LootScene);
            case "Chest":
                return new ChestSimState(position, worldGenData.ChestScene, 3, worldGenData.LootScene);
            case "Torch":
                return new TorchSimState(position, worldGenData.TorchScene);
            case "Door":
                return new DoorSimState(position, 0f, worldGenData.DoorScene);
            case "Goblin":
                return new MobSimState(position, 0f, worldGenData.GoblinScene, worldGenData.GoblinData);
            case "KunKun":
                return new MobSimState(position, 0f, worldGenData.KunKunScene, worldGenData.KunKunData);
            default:
                return null;
        }
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

    private Godot.Collections.Dictionary Raycast(Vector2 screenPos)
    {
        Vector3 rayOrigin = camera.ProjectRayOrigin(screenPos);
        Vector3 rayDir = camera.ProjectRayNormal(screenPos);
        Vector3 rayEnd = rayOrigin + rayDir * 200f;

        var spaceState = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd);
        query.CollisionMask = (uint)(ECollisionLayer.Environment | ECollisionLayer.Water);
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

    public static WorldState CreateEmptyWorld(WorldGenData genData)
    {
        var min = new Vector3I(-4, -1, -4);
        var max = new Vector3I(3, 1, 3);
        var ws = new WorldState(min, max, genData.SimData);

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
                        chunk.Voxels[x, 0, z] = VoxelType.Grass;
                    }
                }
            }
        }

        ws.Spawn = new Vector3(0, 1, 0);

        // Compute initial sunlight so the world isn't pitch black
        LightEngine.ComputeLighting(ws, new List<(Vector3 position, int level)>());

        return ws;
    }
}
