using Godot;
using System;
using System.Collections.Generic;
using System.IO;

// What an entity brush places. Prop carries a PropLibraryEntry — one brush per
// library entry, so placing is a choice rather than a roll off a kit's weighted
// palette. Every other kind stamps a fixed prefab off the EditorBrushPalette.
public enum EEditorEntityKind
{
    PlayerSpawn,
    Prop,
    Loot,
    Chest,
    Torch,
    Door,
    SpikeTrap,
    ClimbableTree,
    Goblin,
    KunKun,
}

// One entry in the editor's entity palette.
public readonly struct EntityBrush
{
    public readonly string Name;
    public readonly EEditorEntityKind Kind;
    // Set only for Prop. Carries the scene AND the behavior (PropType) the
    // placed entity gets, so the editor never has to infer one from the other.
    public readonly PropLibraryEntry Prop;

    public EntityBrush(string name, EEditorEntityKind kind, PropLibraryEntry prop = null)
    {
        Name = name;
        Kind = kind;
        Prop = prop;
    }
}

// What the voxel brush does to the cells it covers. Paint targets the empty
// cell the ray came from; Erase and Replace both target the cell that was hit.
// Ctrl and Alt momentarily force Erase / Replace over the selected operation.
public enum EEditorBrushOperation
{
    Paint,
    Erase,
    Replace,
}

// How the voxel brush turns a click into cells.
//   Voxel  — the single targeted cell.
//   Floor  — drag-fill an XZ rectangle, one cell thick, flat at the anchor's Y.
//   Wall   — drag-fill a vertical slab along whichever of X / Z the drag ran
//            further, also based at the anchor's Y.
//   Fill   — Floor's footprint extruded to a full band's height.
//   Window — one cell a fixed height above the column's floor.
//   Door   — a short column starting at the column's floor.
// Window / Door are Y-relative to the floor rather than to the click so they
// land consistently when stamped (or erased) onto a wall face.
public enum EEditorBrushShape
{
    Voxel,
    Floor,
    Wall,
    Fill,
    Window,
    Door,
}

[GlobalClass]
public partial class WorldEditor : Node3D
{
    [Export] public GameCamera camera;
    [Export] public EditorHud editorHud;
    [Export] public EditorBrushPalette brushPalette;
    // Filename prompt shown the first time a world is saved (no world_file, or
    // a path the menu minted for a new world that isn't on disk yet).
    [Export] public ConfirmationDialog saveDialog;
    [Export] public LineEdit saveNameEdit;
    // Where a newly named world is written when world_file carries no directory.
    [Export] public string defaultSaveDir = "user://";

    [ExportGroup("Brush Shapes")]
    [Export(PropertyHint.Range, "1,16,1")] public int wallHeight = 4;
    [Export(PropertyHint.Range, "1,16,1")] public int fillHeight = 4;
    [Export(PropertyHint.Range, "1,8,1")] public int doorHeight = 2;
    // Cells between the column's floor and the bottom of a window.
    [Export(PropertyHint.Range, "0,8,1")] public int windowFloorOffset = 1;
    // How far down a column FindFloorY looks for ground before giving up.
    [Export(PropertyHint.Range, "1,128,1")] public int floorSearchDepth = 64;
    [Export] public Color regionPreviewColor = new Color(0.3f, 1f, 0.5f);

    [ExportGroup("Entity Picking")]
    // Floor on an entity's click box per axis, so a flat prop (tall grass) or a
    // mesh-less marker still presents something to hit.
    [Export] public Vector3 minimumPickExtents = new Vector3(0.35f, 0.35f, 0.35f);
    [Export] public Color entityHoverColor = new Color(1f, 0.4f, 0.3f);

    // Live editor instance, exposed for console-driven subscene commands
    // (subscene_corner / subscene_save / subscene_stamp). Mirrors the
    // Sim.Current pattern used by world_export and friends. Cleared in
    // _ExitTree so a CVar fired after the editor closes no-ops gracefully.
    public static WorldEditor Current;

    public Action onQuitToMenu;

    private const float MOVE_SPEED = 20f;
    private const float CLIP_START_OFFSET = 10f;
    private const float CLIP_VISUAL_BIAS = 0.05f;
    private const string WORLD_FILE_EXTENSION = "hike";

    private static readonly VoxelType[] PlaceableTypes =
    {
        VoxelType.Terrain, VoxelType.Stone, VoxelType.Desert, VoxelType.Marsh,
        VoxelType.Barrier, VoxelType.Water,
    };

    // Fixed-prefab entity brushes, in palette order. The scene-palette brushes
    // (trees, tall grass) aren't here — they're expanded from the world's kits
    // at Init, one brush per scene, and appended after these.
    private static readonly EEditorEntityKind[] FixedEntityKinds =
    {
        EEditorEntityKind.PlayerSpawn, EEditorEntityKind.Loot, EEditorEntityKind.Chest,
        EEditorEntityKind.Torch, EEditorEntityKind.Door, EEditorEntityKind.SpikeTrap,
        EEditorEntityKind.ClimbableTree, EEditorEntityKind.Goblin, EEditorEntityKind.KunKun,
    };

    // Palette-index-aligned with the buttons in the Entities tab.
    private readonly List<EntityBrush> _entityBrushes = new List<EntityBrush>();

    private Sim _world;
    private WorldState _worldState;
    private Vector3 _cursorPosition;
    private float _clipY = float.PositiveInfinity;
    private int _voxelTypeIndex = 0;
    private int _entityTypeIndex = 0;
    private bool _entityMode = false;
    private EEditorBrushOperation _operation = EEditorBrushOperation.Paint;
    private EEditorBrushShape _brushShape = EEditorBrushShape.Voxel;
    private bool _plateauSnap = true;
    private bool _dragActive = false;
    private EEditorBrushOperation _dragOperation = EEditorBrushOperation.Paint;
    private int _dragBaseY = 0;
    private readonly HashSet<Vector3I> _lastPaintedBlocks = new HashSet<Vector3I>();

    // Drag-fill state for the region shapes (Floor / Wall). The anchor is the
    // press cell, _regionCurrent tracks the cursor, and the region is committed
    // on release — nothing is written until then, so the wireframe preview is
    // the only feedback mid-drag.
    private Vector3I? _regionAnchor;
    private Vector3I _regionCurrent;
    private EEditorBrushOperation _regionOperation;

    // Two-corner bbox selection for subscene save. Each is the floored
    // voxel coordinate of the editor cursor at the time the corner was
    // marked. Null until set; both required before save. Marking a third
    // corner overwrites A and clears B.
    private Vector3I? _subsceneCornerA;
    private Vector3I? _subsceneCornerB;

    // Palette slot of brushPalette.terrainBrushKit, resolved once in Init (the
    // kit palette is bound before the editor scene loads and never changes for
    // the session). Stamped on every VoxelType.Terrain voxel the brush paints.
    private byte _terrainBrushId;

    public void Init(WorldState worldState)
    {
        Current = this;
        MusicManager.Instance?.SetEditor(true);
        _worldState = worldState;
        if (!WorldGen.TryGetTerrainId(brushPalette?.terrainBrushKit, out _terrainBrushId))
        {
            GD.PushWarning($"WorldEditor: terrain brush kit '{brushPalette?.terrainBrushKit?.ResourcePath}' is not in this world's kit palette; painting terrain slot 0.");
        }
        _cursorPosition = worldState.Spawn;
        _clipY = _cursorPosition.Y + CLIP_START_OFFSET;

        _world = new Sim();
        AddChild(_world);

        _world.Initialize(worldState, _cursorPosition, camera, null, () => _cursorPosition);

        _world.EnableEditorMode(_cursorPosition);
        _world.UpdateEntityLoading(_cursorPosition);

        camera.Init(this);
        camera.ManualClipMode = true;
        camera.SetInitialPosition(_cursorPosition);
        camera.SetClip(_clipY - CLIP_VISUAL_BIAS, _cursorPosition);

        // Enter in the name field confirms the dialog.
        if (saveDialog != null && saveNameEdit != null)
        {
            saveDialog.RegisterTextEnter(saveNameEdit);
        }

        BuildToolPalette();
        editorHud.onVoxelBrushSelected += index => { _voxelTypeIndex = index; UpdateHud(); };
        editorHud.onEntityBrushSelected += index => { _entityTypeIndex = index; UpdateHud(); };
        editorHud.onEntityModeChanged += entityMode => { _entityMode = entityMode; UpdateHud(); };
        editorHud.onOperationSelected += operation => _operation = operation;
        editorHud.onShapeSelected += shape => _brushShape = shape;
        editorHud.onPlateauSnapChanged += snap => _plateauSnap = snap;
        editorHud.SetShape(_brushShape);
        _plateauSnap = editorHud.PlateauSnapChecked;
        UpdateHud();
    }

    // The atlas manifest is loaded here and nowhere else — see EditorBrushIcons
    // for why it must stay off the game's normal load path.
    private void BuildToolPalette()
    {
        VoxelAtlasManifest manifest = EditorBrushIcons.LoadManifest(brushPalette?.atlasManifestPath);

        var voxels = new EditorBrushEntry[PlaceableTypes.Length];
        for (int i = 0; i < PlaceableTypes.Length; i++)
        {
            VoxelType type = PlaceableTypes[i];
            voxels[i] = new EditorBrushEntry(
                type.ToString(),
                EditorBrushIcons.ForVoxelType(type, brushPalette?.terrainBrushKit, manifest));
        }

        BuildEntityBrushes();
        var entities = new EditorBrushEntry[_entityBrushes.Count];
        for (int i = 0; i < _entityBrushes.Count; i++)
        {
            entities[i] = new EditorBrushEntry(_entityBrushes[i].Name, _entityBrushes[i].Prop?.icon);
        }

        editorHud.BuildToolButtons(voxels, entities);
    }

    // Fixed prefabs first, then one brush per distinct tree scene and one per
    // distinct tall-grass scene across every kit in the world's palette.
    private void BuildEntityBrushes()
    {
        _entityBrushes.Clear();
        foreach (EEditorEntityKind kind in FixedEntityKinds)
        {
            _entityBrushes.Add(new EntityBrush(kind.ToString(), kind));
        }
        AddPropBrushes();
    }

    // Props come from the authored library, grouped by category so the palette
    // reads Trees, then Rocks, then Foliage rather than library order.
    private void AddPropBrushes()
    {
        PropLibraryEntry[] entries = brushPalette?.propLibrary?.entries;
        if (entries == null)
        {
            return;
        }
        foreach (EPropCategory category in Enum.GetValues<EPropCategory>())
        {
            foreach (PropLibraryEntry entry in entries)
            {
                if (entry?.scene == null || entry.category != category)
                {
                    continue;
                }
                _entityBrushes.Add(new EntityBrush(
                    string.IsNullOrEmpty(entry.displayName) ? entry.scene.ResourcePath.GetFile().GetBaseName() : entry.displayName,
                    EEditorEntityKind.Prop,
                    entry));
            }
        }
    }

    public override void _Process(double deltaTime)
    {
        // Movement polls Input directly, which ignores focus — without this the
        // camera flies around while the save-name field is being typed into.
        if (ConsoleUI.IsOpen || IsSaveDialogOpen)
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
        // The editor holds the cutaway permanently engaged (ManualClipMode), so
        // the cap plane draws every frame and the cap mask must be kept in sync
        // — unsynced, the mask stays at its white "draw the cap here" clear and
        // the fullscreen cap plane paints over the entire world. The editor
        // renders straight into the window, so that viewport IS the inner size.
        camera.SyncCapMaskCamera((Vector2I)GetViewport().GetVisibleRect().Size);
        CullProps(camera.Clip);
        _world.UpdateEntityLoading(_cursorPosition);

        editorHud.UpdatePosition(_cursorPosition);
        editorHud.UpdateClip(_clipY);
        // Ctrl / Alt momentarily override the selected operation; poll them so
        // the button row tracks the modifier even with no click event in flight.
        editorHud.SetHeldOverride(ModifierOverride(Input.IsKeyPressed(Key.Ctrl), Input.IsKeyPressed(Key.Alt)));
        DrawRegionPreview();
        DrawEntityHoverBox();
    }

    // Ctrl previews exactly what an erase click would remove, using the same
    // pick the click does — so if no box appears, the click would miss too.
    // Entity mode only: in voxel mode Ctrl erases voxels and boxing props would
    // just be noise.
    private void DrawEntityHoverBox()
    {
        bool debug = CVars.editorPickDebug.Value;
        // Keycode vs physical matters here: a remapped layout can report Ctrl on
        // only one of them, which would make the whole feature look dead.
        bool ctrl = Input.IsKeyPressed(Key.Ctrl) || Input.IsPhysicalKeyPressed(Key.Ctrl);
        Vector2 mouse = GetViewport().GetMousePosition();

        if (debug)
        {
            // Unconditional reference box at the cursor's ground position. If this
            // is invisible too, the problem is DebugDraw in the editor, not the pick.
            Vector3 c = _cursorPosition;
            DebugDraw.Box(c - Vector3.One, c + Vector3.One, Colors.Yellow);
        }

        Node3D hovered = null;
        Aabb bounds = default;
        if (_entityMode && ctrl)
        {
            hovered = PickEntityAt(mouse, out bounds);
            if (hovered != null)
            {
                DebugDraw.Box(bounds.Position, bounds.End, entityHoverColor);
            }
        }

        if (debug)
        {
            LogPickDebug(ctrl, mouse, hovered, bounds);
        }
    }

    // Printed only when the summary changes, so holding Ctrl doesn't flood the
    // console at frame rate.
    private string _lastPickDebug;

    private void LogPickDebug(bool ctrl, Vector2 mouse, Node3D hovered, Aabb bounds)
    {
        int chunks = 0;
        int total = 0;
        int visible = 0;
        int withBounds = 0;
        int hits = 0;
        Vector3 rayOrigin = camera.ProjectRayOrigin(mouse);
        Vector3 rayDir = camera.ProjectRayNormal(mouse);
        foreach (List<Node3D> entities in _world.ActiveEntities.Values)
        {
            chunks++;
            foreach (Node3D entity in entities)
            {
                total++;
                if (!IsInstanceValid(entity) || !entity.Visible)
                {
                    continue;
                }
                visible++;
                Aabb b = WorldBoundsOf(entity);
                if (b.Size.LengthSquared() > 0f)
                {
                    withBounds++;
                }
                if (RayHitsAabb(rayOrigin, rayDir, b, out _))
                {
                    hits++;
                }
            }
        }

        int segments = DebugDrawRenderer.Instance?.SegmentCount ?? -1;
        string summary = $"[EditorPick] entityMode={_entityMode} ctrl={ctrl} mouse={mouse} ray={rayDir} "
            + $"chunks={chunks} entities={total} visible={visible} withBounds={withBounds} rayHits={hits} "
            + $"picked={hovered?.Name.ToString() ?? "<none>"} bounds={bounds.Size} debugSegments={segments}";
        if (summary != _lastPickDebug)
        {
            _lastPickDebug = summary;
            GD.Print(summary);
        }
    }

    // Wireframe box over the pending Floor / Wall fill. Re-emitted every frame
    // (DebugDraw is immediate-mode) for as long as the drag is held.
    private void DrawRegionPreview()
    {
        if (!_regionAnchor.HasValue)
        {
            return;
        }
        BuildRegionCells(_regionAnchor.Value, _regionCurrent, out Vector3I min, out Vector3I max);
        DebugDraw.Box(min, max + Vector3I.One, regionPreviewColor);
    }

    private bool IsSaveDialogOpen => saveDialog != null && saveDialog.Visible;

    // Ctrl wins over Alt when both are down. Null means no modifier is held, so
    // the operation selected on the panel applies.
    private static EEditorBrushOperation? ModifierOverride(bool ctrl, bool alt)
    {
        if (ctrl)
        {
            return EEditorBrushOperation.Erase;
        }
        if (alt)
        {
            return EEditorBrushOperation.Replace;
        }
        return null;
    }

    private EEditorBrushOperation OperationFor(bool ctrl, bool alt)
    {
        return ModifierOverride(ctrl, alt) ?? _operation;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (ConsoleUI.IsOpen || IsSaveDialogOpen)
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

        if (e is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.S && keyEvent.CtrlPressed)
            {
                Save();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        // Left click: paint/erase/replace (with drag support in voxel mode)
        if (e is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                EEditorBrushOperation operation = OperationFor(mouseButton.CtrlPressed, mouseButton.AltPressed);
                if (_entityMode)
                {
                    HandleEntityClick(mouseButton.Position, operation == EEditorBrushOperation.Erase);
                }
                else if (ComputeVoxelTarget(mouseButton.Position, Overwrites(operation), out Vector3I hitBlock, out Vector3I baseTarget, out Vector3I airTarget))
                {
                    _dragActive = true;
                    _dragOperation = operation;
                    if (IsRegionShape(_brushShape))
                    {
                        // Snap at press, not at fill time, so the drag plane and
                        // the preview box sit on the same elevation the region
                        // will actually be written at.
                        if (_plateauSnap && SupportsPlateauSnap(_brushShape))
                        {
                            baseTarget.Y = SnapToPlateau(baseTarget.Y);
                        }
                        _regionAnchor = baseTarget;
                        _regionCurrent = baseTarget;
                        _regionOperation = operation;
                    }
                    else
                    {
                        _lastPaintedBlocks.Clear();
                        StampAt(baseTarget, hitBlock, airTarget, operation);
                        _dragBaseY = baseTarget.Y;
                    }
                }
                GetViewport().SetInputAsHandled();
            }
            else
            {
                if (_regionAnchor.HasValue)
                {
                    FillRegion(_regionAnchor.Value, _regionCurrent, _regionOperation);
                    _regionAnchor = null;
                }
                _dragActive = false;
                _dragOperation = EEditorBrushOperation.Paint;
                _lastPaintedBlocks.Clear();
            }
        }

        if (e is InputEventMouseMotion mouseMotion && _dragActive)
        {
            // A region drag resolves against the flat plane through its anchor,
            // not against geometry — the press picks the elevation and the rest
            // of the drag stays on it, so sweeping across a hill or an existing
            // wall doesn't drag the far corner up onto whatever the ray hits.
            if (_regionAnchor.HasValue)
            {
                if (ResolvePlaneTarget(mouseMotion.Position, _regionAnchor.Value.Y, out Vector3I planeTarget))
                {
                    _regionCurrent = planeTarget;
                }
                return;
            }

            if (ComputeVoxelTarget(mouseMotion.Position, Overwrites(_dragOperation), out Vector3I hitBlock, out Vector3I baseTarget, out Vector3I airTarget))
            {
                // Skip if the ray hits a block we just painted (for place) or if the
                // base target is one we already modified.
                if (baseTarget.Y == _dragBaseY
                    && !_lastPaintedBlocks.Contains(baseTarget)
                    && !_lastPaintedBlocks.Contains(hitBlock))
                {
                    StampAt(baseTarget, hitBlock, airTarget, _dragOperation);
                }
            }
        }
    }

    // Erase and Replace both act on the cell the ray hit; only Paint targets the
    // empty cell in front of it.
    private static bool Overwrites(EEditorBrushOperation operation)
    {
        return operation != EEditorBrushOperation.Paint;
    }

    // Intersects the mouse ray with the horizontal plane through the middle of
    // layer `planeY` and returns the cell it lands in. Geometry-independent, so
    // the result stays at one elevation no matter what the cursor sweeps over.
    // False when the ray runs parallel to the plane or points away from it.
    private bool ResolvePlaneTarget(Vector2 screenPos, int planeY, out Vector3I target)
    {
        target = default;
        Vector3 rayOrigin = camera.ProjectRayOrigin(screenPos);
        Vector3 rayDir = camera.ProjectRayNormal(screenPos);
        if (Mathf.IsZeroApprox(rayDir.Y))
        {
            return false;
        }
        // Mid-cell, so the plane sits inside the layer rather than on the seam
        // between it and its neighbour.
        const float CELL_MIDPOINT = 0.5f;
        float t = (planeY + CELL_MIDPOINT - rayOrigin.Y) / rayDir.Y;
        if (t < 0f)
        {
            return false;
        }
        Vector3 planeHit = rayOrigin + rayDir * t;
        target = new Vector3I(Mathf.FloorToInt(planeHit.X), planeY, Mathf.FloorToInt(planeHit.Z));
        return true;
    }

    // airTarget is the empty cell the ray came from — the placement cell,
    // regardless of `overwriteHitBlock`. Window / Door measure the column's
    // floor from it so erasing into a wall (where baseTarget IS the wall) still
    // finds ground rather than the wall voxel directly under the click.
    private bool ComputeVoxelTarget(Vector2 screenPos, bool overwriteHitBlock, out Vector3I hitBlock, out Vector3I baseTarget, out Vector3I airTarget)
    {
        hitBlock = default;
        baseTarget = default;
        airTarget = default;

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
                    FinalizeTarget(hitPos, hitNormal, overwriteHitBlock, out hitBlock, out baseTarget, out airTarget);
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

        FinalizeTarget(hitPos, hitNormal, overwriteHitBlock, out hitBlock, out baseTarget, out airTarget);
        return true;
    }

    private static void FinalizeTarget(Vector3 hitPos, Vector3 hitNormal, bool overwriteHitBlock, out Vector3I hitBlock, out Vector3I baseTarget, out Vector3I airTarget)
    {
        hitBlock = new Vector3I(
            Mathf.FloorToInt(hitPos.X - hitNormal.X * 0.5f),
            Mathf.FloorToInt(hitPos.Y - hitNormal.Y * 0.5f),
            Mathf.FloorToInt(hitPos.Z - hitNormal.Z * 0.5f));

        airTarget = new Vector3I(
            Mathf.FloorToInt(hitPos.X + hitNormal.X * 0.5f),
            Mathf.FloorToInt(hitPos.Y + hitNormal.Y * 0.5f),
            Mathf.FloorToInt(hitPos.Z + hitNormal.Z * 0.5f));

        baseTarget = overwriteHitBlock ? hitBlock : airTarget;
    }

    private static bool IsRegionShape(EEditorBrushShape shape)
    {
        return shape == EEditorBrushShape.Floor
            || shape == EEditorBrushShape.Wall
            || shape == EEditorBrushShape.Fill;
    }

    // Only the drag-fill shapes have a base elevation to snap; the stamp shapes
    // take their height from the column's floor, so the toggle is meaningless
    // for them and the panel greys it out.
    public static bool SupportsPlateauSnap(EEditorBrushShape shape)
    {
        return IsRegionShape(shape);
    }

    // Rounds a base elevation down onto the camera's cutaway band grid, so a
    // wall or fill occupies exactly one band instead of straddling two.
    private static int SnapToPlateau(int y)
    {
        int step = Mathf.RoundToInt(GameCamera.PLATEAU_STEP);
        if (step <= 0)
        {
            return y;
        }
        return Mathf.FloorToInt(y / (float)step) * step;
    }

    // The stamp shapes (Voxel / Window / Door), applied at a single click or
    // along a free drag.
    //
    // Window / Door cut through the surface they're aimed at, so they use the
    // HIT cell's column for both Paint and Erase — unlike Voxel, they must not
    // follow baseTarget out to the air cell in front when painting, or filling
    // an opening back in would drop the block in mid-air beside the wall
    // instead of into the gap. Height still comes from airTarget's column: the
    // hit column is solid all the way down, so measuring the floor there would
    // just return the clicked cell.
    private void StampAt(Vector3I baseTarget, Vector3I hitBlock, Vector3I airTarget, EEditorBrushOperation operation)
    {
        var cells = new List<Vector3I>();
        switch (_brushShape)
        {
            case EEditorBrushShape.Window:
            {
                int floorY = FindFloorY(airTarget) + windowFloorOffset;
                cells.Add(new Vector3I(hitBlock.X, floorY, hitBlock.Z));
                break;
            }
            case EEditorBrushShape.Door:
            {
                int floorY = FindFloorY(airTarget);
                for (int i = 0; i < doorHeight; i++)
                {
                    cells.Add(new Vector3I(hitBlock.X, floorY + i, hitBlock.Z));
                }
                break;
            }
            default:
                cells.Add(baseTarget);
                break;
        }
        PaintCells(cells, operation, baseTarget);
        // Window / Door write at the column's floor, not at baseTarget, so the
        // drag's "already did this one" guard needs the target logged too.
        _lastPaintedBlocks.Add(baseTarget);
    }

    // Commits a Floor / Wall drag.
    private void FillRegion(Vector3I anchor, Vector3I current, EEditorBrushOperation operation)
    {
        BuildRegionCells(anchor, current, out Vector3I min, out Vector3I max);
        var cells = new List<Vector3I>();
        for (int x = min.X; x <= max.X; x++)
        {
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                {
                    cells.Add(new Vector3I(x, y, z));
                }
            }
        }
        PaintCells(cells, operation, anchor);
    }

    // Inclusive voxel bounds of a region drag. Both shapes sit at the anchor's
    // elevation — the drag never tilts them to follow terrain — so a wall built
    // across a slope stays a flat-topped plateau.
    private void BuildRegionCells(Vector3I anchor, Vector3I current, out Vector3I min, out Vector3I max)
    {
        if (_brushShape == EEditorBrushShape.Wall)
        {
            // The drag's longer horizontal axis picks the plane: run along X
            // (an XY slab) or along Z (a YZ slab), one cell thick either way.
            bool alongX = Math.Abs(current.X - anchor.X) >= Math.Abs(current.Z - anchor.Z);
            int lo = alongX ? Math.Min(anchor.X, current.X) : Math.Min(anchor.Z, current.Z);
            int hi = alongX ? Math.Max(anchor.X, current.X) : Math.Max(anchor.Z, current.Z);
            min = alongX ? new Vector3I(lo, anchor.Y, anchor.Z) : new Vector3I(anchor.X, anchor.Y, lo);
            max = alongX
                ? new Vector3I(hi, anchor.Y + wallHeight - 1, anchor.Z)
                : new Vector3I(anchor.X, anchor.Y + wallHeight - 1, hi);
            return;
        }

        // Floor and Fill share one XZ footprint; only the extrusion differs.
        int height = _brushShape == EEditorBrushShape.Fill ? fillHeight : 1;
        min = new Vector3I(Math.Min(anchor.X, current.X), anchor.Y, Math.Min(anchor.Z, current.Z));
        max = new Vector3I(Math.Max(anchor.X, current.X), anchor.Y + height - 1, Math.Max(anchor.Z, current.Z));
    }

    // First empty cell above the ground in a column, searched downward from
    // `from`. Falls back to the starting cell when the column has no floor
    // within reach, so a brush over open air still writes somewhere sensible.
    private int FindFloorY(Vector3I from)
    {
        for (int dy = 0; dy <= floorSearchDepth; dy++)
        {
            int y = from.Y - dy;
            if (VoxelTypeInfo.IsSolid(_worldState.GetVoxelWorld(from.X, y, from.Z)))
            {
                return y + 1;
            }
        }
        return from.Y;
    }

    // Writes one brush's worth of cells and rebuilds. Cells at or above the clip
    // plane are dropped rather than aborting the brush — a tall shape whose top
    // pokes through the cutaway still lays down the part you can see.
    private void PaintCells(List<Vector3I> cells, EEditorBrushOperation operation, Vector3I rebuildOrigin)
    {
        int clipFloor = Mathf.FloorToInt(_clipY);
        VoxelType type = operation == EEditorBrushOperation.Erase ? VoxelType.Air : PlaceableTypes[_voxelTypeIndex];
        var changed = new List<Vector3I>();

        foreach (Vector3I target in cells)
        {
            if (target.Y >= clipFloor)
            {
                continue;
            }
            _worldState.SetVoxelWorld(target.X, target.Y, target.Z, type);
            if (type == VoxelType.Terrain)
            {
                _worldState.SetTerrainIdWorld(target.X, target.Y, target.Z, _terrainBrushId);
            }
            changed.Add(target);
            _lastPaintedBlocks.Add(target);
        }

        if (changed.Count > 0)
        {
            _world.UpdateLighting(changed);
            _world.RebuildNearbyChunkMeshes(new Vector3(rebuildOrigin.X, rebuildOrigin.Y, rebuildOrigin.Z), changed);
        }
    }

    private void HandleEntityClick(Vector2 screenPos, bool delete)
    {
        // Deleting picks the entity under the cursor directly. Placing still
        // goes through the terrain raycast — it needs a surface, not an entity.
        if (delete)
        {
            Node3D picked = PickEntityAt(screenPos, out _);
            if (picked != null)
            {
                DeletePickedEntity(picked);
            }
            return;
        }

        var result = Raycast(screenPos);
        if (result.Count == 0)
        {
            return;
        }

        Vector3 hitPos = (Vector3)result["position"];
        Vector3 hitNormal = (Vector3)result["normal"];

        // Place on the surface
        PlaceEntity(hitPos + hitNormal * 0.5f);
    }

    // Nearest loaded entity whose visual bounds the cursor ray enters.
    //
    // Props carry no collider the physics raycast could find — trees and grass
    // are visual-only, so Raycast's Solid|Water mask passes straight through
    // them into the terrain behind, and deleting used to search around that
    // unrelated point. Rather than bolt an Area3D onto every prop scene (and
    // spend a physics layer on it), pick with a CPU ray/AABB test over the
    // loaded set: the entity count in view is small and this needs no authoring.
    private Node3D PickEntityAt(Vector2 screenPos, out Aabb pickedBounds)
    {
        Vector3 rayOrigin = camera.ProjectRayOrigin(screenPos);
        Vector3 rayDir = camera.ProjectRayNormal(screenPos);
        Node3D best = null;
        pickedBounds = default;
        float bestDistance = float.MaxValue;

        foreach (List<Node3D> entities in _world.ActiveEntities.Values)
        {
            foreach (Node3D entity in entities)
            {
                // CullProps hides everything above the clip plane; picking what
                // you can't see would delete things off-screen.
                if (!IsInstanceValid(entity) || !entity.Visible)
                {
                    continue;
                }
                Aabb bounds = WorldBoundsOf(entity);
                if (RayHitsAabb(rayOrigin, rayDir, bounds, out float distance) && distance < bestDistance)
                {
                    bestDistance = distance;
                    pickedBounds = bounds;
                    best = entity;
                }
            }
        }
        return best;
    }

    // Removes the state behind a picked node. Every spawned entity's state
    // carries a RuntimeNode back-reference, so match on that rather than
    // re-searching by position — proximity picks whatever state happens to sit
    // nearest, which is the wrong one wherever entities overlap and finds
    // nothing at all if a node sits even slightly off its authored position.
    private void DeletePickedEntity(Node3D picked)
    {
        Vector3I centerChunk = Sim.WorldToChunkCoord(picked.GlobalPosition);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    Vector3I coord = centerChunk + new Vector3I(dx, dy, dz);
                    List<EntitySimState> states = _worldState.GetEntities(coord);
                    if (states == null)
                    {
                        continue;
                    }
                    foreach (EntitySimState state in states)
                    {
                        if (state.RuntimeNode != picked)
                        {
                            continue;
                        }
                        _worldState.RemoveEntity(state);
                        _world.RemoveEntity(picked);
                        picked.QueueFree();
                        return;
                    }
                }
            }
        }
        GD.PushWarning($"WorldEditor: picked entity '{picked.Name}' has no sim state filed near {centerChunk}; nothing deleted.");
    }

    // World-space bounds of an entity's visuals, expanded to at least
    // minimumPickExtents so a flat or mesh-less entity is still clickable.
    private Aabb WorldBoundsOf(Node3D entity)
    {
        Aabb? combined = null;
        foreach (Node descendant in entity.FindChildren("*", "VisualInstance3D", true, false))
        {
            if (descendant is VisualInstance3D visual)
            {
                Aabb world = TransformAabb(visual.GetAabb(), visual.GlobalTransform);
                combined = combined.HasValue ? combined.Value.Merge(world) : world;
            }
        }
        if (entity is VisualInstance3D selfVisual)
        {
            Aabb world = TransformAabb(selfVisual.GetAabb(), selfVisual.GlobalTransform);
            combined = combined.HasValue ? combined.Value.Merge(world) : world;
        }

        Aabb bounds = combined ?? new Aabb(entity.GlobalPosition, Vector3.Zero);
        Vector3 grow = new Vector3(
            Mathf.Max(0f, minimumPickExtents.X - bounds.Size.X * 0.5f),
            Mathf.Max(0f, minimumPickExtents.Y - bounds.Size.Y * 0.5f),
            Mathf.Max(0f, minimumPickExtents.Z - bounds.Size.Z * 0.5f));
        return new Aabb(bounds.Position - grow, bounds.Size + grow * 2f);
    }

    // Godot's C# bindings don't expose the Transform3D * Aabb operator, so
    // transform the eight corners and re-fit. Same approach as MeshAutoCollider.
    private static Aabb TransformAabb(Aabb aabb, Transform3D transform)
    {
        Vector3 p = aabb.Position;
        Vector3 s = aabb.Size;
        Vector3 min = transform * p;
        Vector3 max = min;
        for (int corner = 1; corner < 8; corner++)
        {
            Vector3 world = transform * (p + new Vector3(
                (corner & 1) != 0 ? s.X : 0f,
                (corner & 2) != 0 ? s.Y : 0f,
                (corner & 4) != 0 ? s.Z : 0f));
            min = min.Min(world);
            max = max.Max(world);
        }
        return new Aabb(min, max - min);
    }

    // Slab test. `distance` is where the ray enters the box, so callers can take
    // the nearest of several overlapping hits.
    private static bool RayHitsAabb(Vector3 rayOrigin, Vector3 rayDir, Aabb box, out float distance)
    {
        distance = 0f;
        Vector3 min = box.Position;
        Vector3 max = box.End;
        float tMin = 0f;
        float tMax = float.MaxValue;
        const float PARALLEL_EPSILON = 1e-6f;

        for (int axis = 0; axis < 3; axis++)
        {
            float direction = rayDir[axis];
            float origin = rayOrigin[axis];
            if (Mathf.Abs(direction) < PARALLEL_EPSILON)
            {
                // Parallel to this slab: a miss unless the ray already lies in it.
                if (origin < min[axis] || origin > max[axis])
                {
                    return false;
                }
                continue;
            }
            float inverse = 1f / direction;
            float near = (min[axis] - origin) * inverse;
            float far = (max[axis] - origin) * inverse;
            if (near > far)
            {
                (near, far) = (far, near);
            }
            tMin = Mathf.Max(tMin, near);
            tMax = Mathf.Min(tMax, far);
            if (tMin > tMax)
            {
                return false;
            }
        }
        distance = tMin;
        return true;
    }

    private void PlaceEntity(Vector3 position)
    {
        if (_entityTypeIndex < 0 || _entityTypeIndex >= _entityBrushes.Count)
        {
            return;
        }
        EntityBrush brush = _entityBrushes[_entityTypeIndex];

        if (brush.Kind == EEditorEntityKind.PlayerSpawn)
        {
            _worldState.Spawn = position;
            GD.Print($"Player spawn set to {position}");
            return;
        }

        EntitySimState simState = CreateEntitySimState(brush, position);
        if (simState == null)
        {
            return;
        }

        _worldState.AddEntity(simState);
        // Spawn through the normal streaming path instead of instantiating here.
        // A directly-created node is never filed in Sim.ActiveEntities and never
        // gets its state's RuntimeNode back-reference set, so it's invisible to
        // every consumer that walks the active set — culling, eviction, and the
        // editor's own entity picking.
        ReloadChunkEntities(Sim.WorldToChunkCoord(position));
    }

    // Props place the brush's own library entry — worldgen rolls a kit's
    // weighted palette, but an author picking a brush has already chosen. Every
    // other kind stamps a fixed prefab from the EditorBrushPalette, the editor's
    // standalone library of authorable interactives / mobs, independent of
    // worldgen's per-zone spawn lists.
    private EntitySimState CreateEntitySimState(EntityBrush brush, Vector3 position)
    {
        switch (brush.Kind)
        {
            case EEditorEntityKind.Prop:
                return new PropSimState(brush.Prop.propType, position, brush.Prop.scene);
            case EEditorEntityKind.Loot:
            {
                ItemDescriptor lootItem = brushPalette?.lootItem;
                if (lootItem?.item == null) { return null; }
                var lootSim = new LootSimState(position, lootItem.item);
                if (lootItem.NeedsComposedState)
                {
                    lootSim.Item = lootItem.CreateState();
                }
                return lootSim;
            }
            case EEditorEntityKind.Chest:
                return brushPalette?.chestScene != null
                    ? new ChestSimState(position, brushPalette.chestScene) { LootItems = ChestSpawnEntry.Resolve(brushPalette.chestLoot, new Random()) }
                    : null;
            case EEditorEntityKind.Torch:
                return brushPalette?.torchScene != null
                    ? new TorchSimState(position, brushPalette.torchScene)
                    : null;
            case EEditorEntityKind.Door:
                return brushPalette?.doorScene != null
                    ? new DoorSimState(position, 0f, brushPalette.doorScene)
                    : null;
            case EEditorEntityKind.ClimbableTree:
                return brushPalette?.climbableTreeScene != null
                    ? new ClimbableTreeSimState(position, brushPalette.climbableTreeScene)
                    : null;
            case EEditorEntityKind.SpikeTrap:
                return brushPalette?.spikeTrapScene != null
                    ? new TrapSimState(position, brushPalette.spikeTrapScene)
                    : null;
            case EEditorEntityKind.Goblin:
            {
                MobData data = brushPalette?.goblinMob;
                return data?.mobScene != null ? new MobSimState(position, 0f, data.mobScene, data) : null;
            }
            case EEditorEntityKind.KunKun:
            {
                MobData data = brushPalette?.kunKunMob;
                return data?.mobScene != null ? new MobSimState(position, 0f, data.mobScene, data) : null;
            }
            default:
                return null;
        }
    }

    private void ReloadChunkEntities(Vector3I coord)
    {
        // Unload then reload entities for this chunk so the newly added one appears
        _world.UnloadChunkEntities(coord);
        _world.LoadChunkEntities(coord);
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

    private void UpdateHud()
    {
        editorHud.SetVoxelBrush(_voxelTypeIndex);
        editorHud.SetEntityBrush(_entityTypeIndex);
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

    // Ctrl+S. The first save of a world asks for a name; afterwards the file
    // exists and every save overwrites it silently.
    private void Save()
    {
        string path = CVars.worldFile.Value;
        if (!string.IsNullOrEmpty(path) && File.Exists(ProjectSettings.GlobalizePath(path)))
        {
            SaveTo(path);
            return;
        }
        PromptForSaveName(path);
    }

    private void PromptForSaveName(string suggestedPath)
    {
        if (saveDialog == null || saveNameEdit == null)
        {
            // No dialog wired (headless / stripped scene) — keep the old
            // behavior rather than silently dropping the save.
            SaveTo(suggestedPath);
            return;
        }
        string suggestedName = string.IsNullOrEmpty(suggestedPath) ? "" : suggestedPath.GetFile();
        saveNameEdit.Text = suggestedName;
        saveDialog.PopupCentered();
        saveNameEdit.GrabFocus();
        // Select the stem only, so typing replaces the name but keeps ".hike".
        int dot = suggestedName.LastIndexOf('.');
        saveNameEdit.CaretColumn = suggestedName.Length;
        saveNameEdit.Select(0, dot > 0 ? dot : suggestedName.Length);
    }

    // ConfirmationDialog "Save" (also fired by Enter via RegisterTextEnter).
    public void OnSaveNameConfirmed()
    {
        string name = saveNameEdit?.Text.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            GD.PrintErr("Save cancelled: no file name entered.");
            return;
        }
        // Take the file part only so a stray path can't escape the save dir.
        name = name.GetFile();
        if (name.GetExtension() != WORLD_FILE_EXTENSION)
        {
            name = $"{name}.{WORLD_FILE_EXTENSION}";
        }
        string dir = CVars.worldFile.Value.GetBaseDir();
        if (string.IsNullOrEmpty(dir))
        {
            dir = defaultSaveDir;
        }
        string path = $"{dir.TrimEnd('/')}/{name}";
        CVars.worldFile.Value = path;
        SaveTo(path);
    }

    private void SaveTo(string path)
    {
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
            MusicManager.Instance?.SetEditor(false);
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
                entityChunks.Add(Sim.WorldToChunkCoord(worldPos));
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

    // floorKit is the Terrain brush's kit (Main reads it off the editor scene's
    // palette) so the starting floor matches what painting Terrain on top of it
    // produces. Requires WorldGen.BindActivePalettes to have run.
    public static WorldState CreateEmptyWorld(WorldGenData genData, TerrainKitData floorKit)
    {
        WorldGen.TryGetTerrainId(floorKit, out byte floorTerrainId);

        var min = new Vector3I(-4, -1, -4);
        var max = new Vector3I(3, 1, 3);
        var ws = new WorldState(min, max, genData.simData);

        // Mirror WorldGen's zone setup so the sky preview has something
        // to blend in the editor. ZoneIndex stays 0 across all chunks
        // here (the editor's empty stub is a single uniform area); the
        // full editor will paint indices when authoring.
        ws.Zones = new ZoneState[genData.ZoneGens.Length];
        for (int i = 0; i < genData.ZoneGens.Length; i++)
        {
            ws.Zones[i] = new ZoneState
            {
                Data = genData.ZoneGens[i]?.zone,
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
                        chunk.TerrainId[x, 0, z] = floorTerrainId;
                    }
                }
            }
        }

        ws.Spawn = new Vector3(0, 1, 0);
        // Author under a high sun. The default 0.0 is sunrise, which lights the
        // stub world too dimly to judge what you're building.
        ws.TimeOfDay01 = WorldState.NoonTimeOfDay01;

        // Compute initial sunlight so the world isn't pitch black
        LightEngine.ComputeSunlight(ws);

        return ws;
    }
}
