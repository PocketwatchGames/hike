using Godot;
using System;
using System.Collections.Generic;
using System.IO;

// Which tool a click drives. Each owns a bottom-bar palette and a left-hand
// options panel, and exactly one is active at a time.
//   Voxel  — paint / erase terrain with the brush shapes.
//   Entity — stamp and select placed entities.
//   Roof   — drag a footprint and generate a sloped roof over it.
public enum EEditorTool
{
    Voxel,
    Entity,
    Roof,
}

// What a click does while the Roofs tool is active.
//   Draw — drag a footprint and generate a new roof.
//   Edit — click a placed roof and push the panel's current settings onto it.
//          Roof shape is baked at placement (the mesh is regenerated from it),
//          so without this, retuning a pitch or a brokenness means deleting the
//          roof and redrawing it.
public enum EEditorRoofMode
{
    Draw,
    Edit,
}

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
    DartTrap,
    ClimbableTree,
    Campfire,
    Forge,
    Well,
    HealingFountain,
    ManaFountain,
    Goblin,
    KunKun,
    // Player-operated floor trapdoor (interact to toggle). LinkTag empty.
    Trapdoor,
    // Perception-gated drop trap and step-and-it-breaks crumbling floor — both
    // Trap compositions over a TrapdoorPanel, distinguished only by scene.
    TrapdoorTrap,
    CrumblingFloor,
    // Lever + its linked trapdoor. Like Marker these are expanded per tag from
    // EditorBrushPalette.linkTags: placing "Lever: gate" and "Trapdoor: gate"
    // shares the "gate" link so the lever throws that trapdoor.
    Lever,
    LinkedTrapdoor,
    // A tagged position with no body — the subscene spawn point. One brush per
    // pool name, expanded from EditorBrushPalette.markerTags.
    Marker,
    // A tagged position a ROAD should reach — a front door, a square's gate.
    // One brush per hint name, expanded from EditorBrushPalette.pathHintTags.
    PathHint,
}

// What a click does while the entity tool is active.
//   Place  — stamp the palette's selected brush onto the surface under the
//            cursor (Ctrl still erases what's under it).
//   Select — pick already-placed entities and transform them with the gizmo.
public enum EEditorEntityMode
{
    Place,
    Select,
}

// Sections of the entity palette, in tab order. Purely an authoring grouping —
// nothing about a placed entity depends on which tab it was picked from. Must
// stay index-aligned with the GridContainers wired on EditorHud.
public enum EEditorEntityTab
{
    Interactives,
    Trees,
    Rocks,
    // Natural clutter that isn't a tree or a rock (grass, foliage).
    Nature,
    Furniture,
    // Man-made objects that are neither interactive nor furniture.
    Props,
}

// One entry in the editor's entity palette.
public readonly struct EntityBrush
{
    public readonly string Name;
    public readonly EEditorEntityKind Kind;
    public readonly EEditorEntityTab Tab;
    // Set only for Prop. Carries the scene AND the behavior (PropType) the
    // placed entity gets, so the editor never has to infer one from the other.
    public readonly PropLibraryEntry Prop;
    // Set only for Marker (the variant pool a placed marker joins) and PathHint
    // (the hint's name within the scene).
    public readonly string Tag;

    public EntityBrush(string name, EEditorEntityKind kind, EEditorEntityTab tab, PropLibraryEntry prop = null, string tag = "")
    {
        Name = name;
        Kind = kind;
        Tab = tab;
        Prop = prop;
        Tag = tag;
    }
}

// What the editor session is editing, fixed when it opens and derived from the
// document's file extension. It decides what Ctrl+S writes — there is no
// save-time choice between the two.
//   Scene — a `.hikescene`, the normal case: one building / dungeon / landmark,
//           authored in a blank world and stamped into real worlds later.
//   World — a `.hike`, a whole playable world.
public enum EEditorDocumentKind
{
    Scene,
    World,
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
//   Room   — Floor's footprint plus a wall-height perimeter shell on top of it,
//            hollow inside.
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
    Room,
}

// How the mesher is told to treat the painted cells' edges — the authoring face
// of SharpAxes, which is per-voxel state the brush has until now
// left to each material's default.
//   Auto    — the material's DefaultShape (blocky for Stone, Y-snapped for the
//             natural grounds). Also the only mode that PRESERVES the shape
//             already on a cell when a paint doesn't change its material.
//   Blocky  — SharpAxes.All: square edges on every axis, flat-shaded. Buildings.
//   Stepped — SharpAxes.Y: hard height steps, walls keep their organic curve.
//             What natural ground uses.
//   Smooth  — SharpAxes.None: the plain surface-nets average. Ramps and blends.
// X / Z alone are legal SharpAxes but have no authoring use, so they aren't
// offered — a shape that needs them can still arrive from worldgen.
public enum EEditorVoxelEdges
{
    Auto,
    Blocky,
    Stepped,
    Smooth,
}

// One entry in the editor's voxel palette — one button per block in the
// catalog, in catalog order. Air is absent: erasing writes it.
public readonly struct VoxelBrush
{
    public readonly string Name;
    public readonly int BlockId;
    // Resolves the button's tile icon.
    public readonly BlockData Block;

    public VoxelBrush(BlockData block)
    {
        Name = block.blockName.ToString();
        BlockId = block.blockId;
        Block = block;
    }
}

[GlobalClass]
public partial class WorldEditor : Node3D
{
    [Export] public GameCamera camera;
    [Export] public EditorHud editorHud;
    [Export] public EditorBrushPalette brushPalette;
    // Renders palette-button images for the brushes with no authored art.
    [Export] public EditorIconBaker iconBaker;
    // Filename prompt shown the first time a document is saved (the menu mints
    // a path for a new document, but nothing is on disk until it's named).
    [Export] public ConfirmationDialog saveDialog;
    [Export] public LineEdit saveNameEdit;
    // Where a newly named document is written, per kind. Scenes live under
    // res:// because worldgen's SubscenePlacement references them by res:// path.
    [Export] public string defaultSceneDir = SubsceneFile.DEFAULT_SCENE_DIR;
    [Export] public string defaultWorldDir = "user://";
    // Blank-document extent, in chunks — also the floor on a scene workspace.
    [Export] public Vector3I emptyWorldMinChunk = new Vector3I(-4, -1, -4);
    [Export] public Vector3I emptyWorldMaxChunk = new Vector3I(3, 1, 3);
    // Chunks of empty space left around an opened scene so there's room to
    // keep building outward.
    [Export] public Vector3I sceneWorkspacePadChunks = new Vector3I(2, 1, 2);

    [ExportGroup("Render Rig")]
    // The editor renders through the same low-res-viewport + bloom/tonemap
    // upscale chain the game does, so what you paint is what you'll play.
    // The Sim, the camera and the lights all live INSIDE sceneViewport (it
    // owns its own World3D), which is why picking has to convert window
    // coordinates through viewportRig before touching the camera.
    [Export] public SubViewport sceneViewport;
    [Export] public ViewportRig viewportRig;
    // Inner-scene environment, so the lighting toggle can drop the black
    // distance fog for the flat authoring view.
    [Export] public WorldEnvironment sceneEnvironment;
    // Bound to the volumetric fog / sun-shaft quad, same as GameClient's.
    [Export] public ShaderMaterial fogMaterial;

    [ExportGroup("Lighting")]
    // Time-of-day the editor opens at, and what the HUD slider is seated on.
    // The sim clock never advances here (no Sim.Tick), so the world sits at
    // whatever the slider was last dragged to. Without this it would stay at
    // whatever the world was seeded with — SimData.initialTimeOfDay, i.e.
    // dawn, a grazing sun and a very dark scene. Just shy of noon (0.25) is
    // the neutral default: high sun, but still enough of an angle to read
    // voxel face orientation off the shading.
    [Export(PropertyHint.Range, "0,1,0.01")] public float editorTimeOfDay = 0.225f;
    // Flat-view readability: how far the eye-adaptation tone curve lifts
    // unlit interiors when Lighting is unchecked. 1 = no lift.
    [Export(PropertyHint.Range, "1,16,0.1")] public float flatViewDarkGain = 12f;
    // Keep this at 1. The game's night-eyes curve deliberately pushes BRIGHT
    // fragments past 1.0 so the filmic tonemap blows them out — the opposite of
    // what the flat authoring view wants. Above the knee every sunlit surface is
    // already near clipping, so any lift here whites out the whole scene.
    [Export(PropertyHint.Range, "1,8,0.05")] public float flatViewLightGain = 1f;
    [Export(PropertyHint.Range, "0.1,3,0.05")] public float flatViewKnee = 1.5f;

    [ExportGroup("Brush Shapes")]
    [Export(PropertyHint.Range, "1,16,1")] public int wallHeight = 4;
    [Export(PropertyHint.Range, "1,16,1")] public int fillHeight = 4;
    [Export(PropertyHint.Range, "1,8,1")] public int doorHeight = 2;
    // Cells between the column's floor and the bottom of a window.
    [Export(PropertyHint.Range, "0,8,1")] public int windowFloorOffset = 1;
    // How far down a column FindFloorY looks for ground before giving up.
    [Export(PropertyHint.Range, "1,128,1")] public int floorSearchDepth = 64;
    // Drafting elevation the editor opens at, before the cutaway has been moved.
    // From then on R/F drive it (see _buildY), so this is only the seed.
    [Export] public int emptyAirPaintY = 0;
    [Export] public Color regionPreviewColor = new Color(0.3f, 1f, 0.5f);
    // The cell a click would target, boxed under the cursor before committing.
    [Export] public Color brushPreviewColor = new Color(1f, 1f, 1f);

    [ExportGroup("Ground Plane")]
    // Translucent sheet on the drafting plane — the surface a click into empty
    // air paints onto. It is the only read on where the brush will build once
    // the cutaway has been walked below the terrain, so it tracks _buildY rather
    // than sitting at a fixed y=0. Recentred on the cursor each frame — the mesh
    // only has to cover the view, not the world.
    [Export] public MeshInstance3D groundPlane;

    [ExportGroup("Marker Overlay")]
    // How far around the cursor the invisible Opening / Barrier voxels are
    // outlined. Every cell in the box is read per frame, so this is a cost knob
    // as much as a reach one — keep it to a building's worth of voxels.
    [Export(PropertyHint.Range, "0,64,1")] public int markerOverlayRadiusXZ = 16;
    [Export(PropertyHint.Range, "0,64,1")] public int markerOverlayRadiusY = 12;
    [Export] public Color openingMarkerColor = new Color(0.3f, 0.9f, 1f);
    [Export] public Color barrierMarkerColor = new Color(1f, 0.55f, 0.15f);
    // What the shells fade to where a wall is in front of them. Desaturated and
    // near-transparent on purpose: it only has to say "a marker is back there"
    // without competing with the geometry being worked on. Alpha is absolute
    // here, not a multiplier — this replaces DebugDraw's default dimming.
    [Export] public Color markerOccludedColor = new Color(0.6f, 0.65f, 0.7f, 0.1f);

    [ExportGroup("Roofs")]
    // Pitch the Roofs tool opens on, and what the HUD slider is seated to. The
    // slider's own range/step are authored on it in the scene.
    [Export(PropertyHint.Range, "1,75,1")] public float defaultRoofSlopeDegrees = 35f;
    [Export] public ERoofSeamAxis defaultRoofSeamAxis = ERoofSeamAxis.AlongX;
    [Export] public ERoofForm defaultRoofForm = ERoofForm.Gable;
    // How derelict a newly dragged roof is. Per-roof, so a ruined hut can sit
    // beside an intact one of the same style.
    [Export(PropertyHint.Range, "0,1,0.01")] public float defaultRoofBroken = 0f;
    [Export] public Color roofPreviewColor = new Color(1f, 0.75f, 0.3f);

    [ExportGroup("Entity Picking")]
    // Floor on an entity's click box per axis, so a flat prop (tall grass) or a
    // mesh-less marker still presents something to hit.
    [Export] public Vector3 minimumPickExtents = new Vector3(0.35f, 0.35f, 0.35f);
    [Export] public Color entityHoverColor = new Color(1f, 0.4f, 0.3f);

    [ExportGroup("Entity Selection")]
    // Translate / rotate handles for the current selection.
    [Export] public EditorGizmo gizmo;
    [Export] public Color selectionColor = new Color(1f, 0.85f, 0.2f);

    [ExportGroup("Entity Snapping")]
    // Grid entity positions land on while Snap to Grid is checked, in metres.
    [Export(PropertyHint.Range, "0.05,4,0.05")] public float entityGridSnap = 0.5f;
    // Increment entity facings land on while Snap Rotation is checked.
    [Export(PropertyHint.Range, "1,180,1")] public float entityRotationSnapDegrees = 45f;
    // How far the cursor must leave a just-placed entity before the still-held
    // button starts aiming it — closer in, the drag direction is noise.
    [Export(PropertyHint.Range, "0.05,4,0.05")] public float placeAimDeadzone = 0.4f;

    [ExportGroup("History")]
    // Undo steps kept. One step is one action — a whole drag, not each cell —
    // and only the cells that actually changed are stored, so this can be deep.
    [Export(PropertyHint.Range, "1,512,1")] public int undoDepth = 128;

    [ExportGroup("Camera")]
    // The editor runs its own framing rather than the game's camera_preset:
    // perspective, close in, and free to pitch. The shipping orthographic angle
    // is locked to one pitch, which can't get inside a room and look around it.
    [Export(PropertyHint.Range, "10,120,1")] public float cameraFov = 55f;
    // How far back from the cursor the camera sits. The cursor is what WASD
    // moves and what the cutaway, entity streaming and the subscene commands all
    // resolve against, so it stays the anchor — free look just swings the camera
    // about the eye instead of about the cursor (see ApplyFreeLook).
    [Export(PropertyHint.Range, "1,100,0.5")] public float cameraDistance = 20f;
    // Pitch the editor opens on; free look retunes it from there.
    [Export(PropertyHint.Range, "-89,89,1")] public float cameraStartPitchDegrees = -40f;
    [Export(PropertyHint.Range, "1,200,1")] public float moveSpeed = 20f;
    // Hold-Shift multiplier while flying.
    [Export(PropertyHint.Range, "1,20,0.5")] public float flyBoostMultiplier = 4f;
    // Radians of look rotation per pixel of right-drag mouse motion.
    [Export(PropertyHint.Range, "0.0005,0.05,0.0005")] public float lookSensitivity = 0.005f;
    // Fly-speed multiplier applied per mouse-wheel notch while flying.
    [Export(PropertyHint.Range, "1.02,2,0.01")] public float flySpeedStep = 1.2f;
    // Cap on how far from the cursor the Q/E orbit pivot may sit, so a grazing
    // centre-ray hit far downhill doesn't turn a rotation into a wide sweep.
    [Export(PropertyHint.Range, "1,200,1")] public float orbitPivotMaxRadius = 40f;

    // Live editor instance, exposed for console-driven subscene commands
    // (subscene_corner / subscene_save / subscene_stamp). Mirrors the
    // Sim.Current pattern used by world_export and friends. Cleared in
    // _ExitTree so a CVar fired after the editor closes no-ops gracefully.
    public static WorldEditor Current;

    public Action onQuitToMenu;

    // Gimbal guard on the free-look pitch — straight up / straight down would
    // leave the yaw axis degenerate.
    private const float PITCH_LIMIT_DEGREES = 89f;
    private const float FLY_SPEED_SCALE_MIN = 0.1f;
    private const float FLY_SPEED_SCALE_MAX = 8f;
    private const float CLIP_START_OFFSET = 10f;
    private const float CLIP_VISUAL_BIAS = 0.05f;
    public const string WORLD_FILE_EXTENSION = "hike";
    public const string SCENE_FILE_EXTENSION = "hikescene";

    // Fixed-prefab entity brushes, in palette order. The scene-palette brushes
    // (trees, tall grass) aren't here — they're expanded from the world's kits
    // at Init, one brush per scene, and appended after these.
    private static readonly EEditorEntityKind[] FixedEntityKinds =
    {
        EEditorEntityKind.PlayerSpawn, EEditorEntityKind.Loot, EEditorEntityKind.Chest,
        EEditorEntityKind.Torch, EEditorEntityKind.Door, EEditorEntityKind.SpikeTrap,
        EEditorEntityKind.DartTrap, EEditorEntityKind.ClimbableTree, EEditorEntityKind.Campfire, EEditorEntityKind.Forge,
        EEditorEntityKind.Well, EEditorEntityKind.HealingFountain, EEditorEntityKind.ManaFountain,
        EEditorEntityKind.Goblin, EEditorEntityKind.KunKun,
        EEditorEntityKind.Trapdoor, EEditorEntityKind.TrapdoorTrap, EEditorEntityKind.CrumblingFloor,
    };

    // Index-aligned with the voxel palette buttons.
    private readonly List<VoxelBrush> _voxelBrushes = new List<VoxelBrush>();

    // Palette-index-aligned with the entity buttons, which are spread across the
    // palette tabs but share one index space (and one selection).
    private readonly List<EntityBrush> _entityBrushes = new List<EntityBrush>();

    // Index-aligned with the Roofs palette buttons.
    private readonly List<RoofStyleData> _roofStyles = new List<RoofStyleData>();

    // Index-aligned with the Weather dropdown's items.
    private readonly List<WeatherData> _weatherPresets = new List<WeatherData>();

    private Sim _world;
    private WorldState _worldState;
    private EditorHistory _history;
    private Vector3 _cursorPosition;
    // The cutaway. Always finite, even when "off" — off parks it above the
    // world's highest voxel rather than at infinity, so the camera, the cap mask
    // and PaintCells all keep working on a real number. Seeded in Init.
    private float _clipY;
    private bool _clipOff = true;
    // The drafting plane: what a click that hits no geometry resolves against,
    // and where the ground sheet draws. Always a plateau increment (see
    // SetClipY), so it reads the same for every brush shape. Held across a
    // clip-off toggle, so looking at the roof doesn't move where the brush
    // builds.
    private int _buildY;
    private int _voxelTypeIndex = 0;
    private int _entityTypeIndex = 0;
    private int _roofStyleIndex = 0;
    private EEditorTool _tool = EEditorTool.Voxel;
    private EEditorEntityMode _entityToolMode = EEditorEntityMode.Place;
    private ERoofSeamAxis _roofSeamAxis;
    private ERoofForm _roofForm;
    private EEditorRoofMode _roofMode = EEditorRoofMode.Draw;
    private float _roofSlopeDegrees;
    private float _roofBroken;
    private EEditorBrushOperation _operation = EEditorBrushOperation.Paint;
    private EEditorBrushShape _brushShape = EEditorBrushShape.Voxel;
    private EEditorVoxelEdges _voxelEdges = EEditorVoxelEdges.Auto;
    // Plateau snap, remembered per brush shape — turning it off to drop a floor
    // at an arbitrary height mustn't also unsnap the Room selected next. Every
    // slot starts on; the shapes that can't snap are gated by SupportsPlateauSnap
    // rather than by their stored value, so switching through one doesn't clear
    // the choice made for the shapes that can.
    private readonly bool[] _plateauSnapByShape = new bool[Enum.GetValues<EEditorBrushShape>().Length];
    // Entity snapping, shared by placement and the gizmo. Both start on: the
    // grid divides a voxel evenly, so furniture lines up with the walls around
    // it without anyone having to aim.
    private bool _snapToGrid = true;
    private bool _snapRotation = true;
    // The entity the still-held left button just placed. Motion aims it until
    // the release commits, so dropping a prop and turning it is one gesture.
    private EntitySimState _placingEntity;
    // Authored state of the inner environment's built-in depth fog, captured
    // before the lighting toggle ever touches it — the flat view only ever
    // turns it OFF, so restoring means restoring what game.tscn ships with.
    private bool _authoredDepthFog;
    // Q/E orbit state. The camera frames the cursor, but the cursor is wherever
    // the fly cam left it and can sit well above the ground that fills the view — yawing
    // about its column swings that ground through an arc of (cursorY - groundY) /
    // tan(pitch). So the orbit runs about the geometry under the view centre
    // instead: the cursor is swung around that pivot in lockstep with the eased
    // yaw, which pins the pivot on screen for the whole rotation.
    private bool _orbiting;
    private Vector3 _orbitPivot;
    private Vector3 _orbitStartCursor;
    private float _orbitStartYaw;
    // Free-look pitch in degrees (negative = looking down), pushed into the
    // camera each frame. Yaw lives on the camera, which owns the Q/E tween.
    private float _cameraPitchDegrees;
    // Right-mouse fly cam: pointer captured, mouse looks, WASD/E/Q flies.
    private bool _flying;
    private float _flySpeedScale = 1f;

    private bool _dragActive = false;
    private EEditorBrushOperation _dragOperation = EEditorBrushOperation.Paint;
    private int _dragBaseY = 0;
    private readonly HashSet<Vector3I> _lastPaintedBlocks = new HashSet<Vector3I>();

    // What the mouse resolves to this frame, from the same pick a click runs —
    // _hoverHit is the cell the ray landed in, _hoverBase the cell the current
    // operation would write. Feeds both the HUD coordinate readout and the brush
    // preview, so neither can disagree with what clicking would actually do.
    private bool _hoverValid;
    private Vector3I _hoverHit;
    private Vector3I _hoverBase;
    private Vector3I _hoverAir;

    // Bodies every editor pick ray skips — see Raycast.
    private readonly Godot.Collections.Array<Rid> _boundaryExclude = new Godot.Collections.Array<Rid>();

    // Entity selection (Select mode) and the gizmo drag acting on it. The drag
    // records each entity's transform at press time and re-derives from it every
    // frame, so the result never accumulates drift and a drag that returns to
    // where it started really is a no-op.
    private readonly EditorEntitySelection _selection = new EditorEntitySelection();
    private EGizmoHandle _hotHandle = EGizmoHandle.None;
    private EGizmoHandle _gizmoDrag = EGizmoHandle.None;
    private Vector3 _gizmoDragPivot;
    private Vector3 _gizmoDragStartPlaneHit;
    private float _gizmoDragStartY;
    private float _gizmoDragStartAngle;
    private readonly List<SelectedTransform> _gizmoDragStart = new List<SelectedTransform>();

    // A selected entity's transform as it was when a gizmo drag began.
    private readonly struct SelectedTransform
    {
        public readonly EntitySimState State;
        public readonly Vector3 Position;
        public readonly float RotationY;

        public SelectedTransform(EntitySimState state)
        {
            State = state;
            Position = state.WorldPosition;
            RotationY = state.RotationY;
        }
    }

    // Drag-fill state for the region shapes (Floor / Wall). The anchor is the
    // press cell, _regionCurrent tracks the cursor, and the region is committed
    // on release — nothing is written until then, so the wireframe preview is
    // the only feedback mid-drag.
    private Vector3I? _regionAnchor;
    private Vector3I _regionCurrent;
    private EEditorBrushOperation _regionOperation;

    // The Roofs tool's footprint drag. Same anchor-on-press / resolve-against-
    // the-anchor's-plane / commit-on-release shape as a region fill, kept in its
    // own fields because the voxel drag is gated on the brush SHAPE and a roof
    // has none — entangling them would mean a shape check on every roof path.
    private Vector3I? _roofAnchor;
    private Vector3I _roofCurrent;

    // The placed roof the Roofs panel is retuning (Edit mode). Clicking a roof
    // latches it here and it stays latched, so the sliders and toggles keep
    // acting on it without another click. Replaced wholesale on every push —
    // RoofSimState's shape is readonly — so this always names the LIVE state.
    private RoofSimState _editingRoof;
    private Vector3I _editingRoofBucket;

    // Optional two-corner bbox override for subscene save. Each is the floored
    // voxel coordinate of the editor cursor at the time the corner was marked.
    // Unset (the normal case) means the save auto-fits the bbox to the world's
    // voxels. Marking a third corner overwrites A and clears B.
    private Vector3I? _subsceneCornerA;
    private Vector3I? _subsceneCornerB;

    // The open document. Kind is set at Init and never changes for the session;
    // the path is empty until the first save names the file. IncludeEnv is a
    // scene-document property, read off the file it was opened from.
    private EEditorDocumentKind _documentKind = EEditorDocumentKind.Scene;
    private string _documentPath = "";
    private bool _documentIncludeEnv;

    // A path's document kind. Extension is the whole signal, which is what lets
    // the menu hand the editor one path and nothing else.
    public static EEditorDocumentKind KindForPath(string path)
    {
        return path.GetExtension() == SCENE_FILE_EXTENSION
            ? EEditorDocumentKind.Scene
            : EEditorDocumentKind.World;
    }

    private string DocumentExtension =>
        _documentKind == EEditorDocumentKind.Scene ? SCENE_FILE_EXTENSION : WORLD_FILE_EXTENSION;

    private string DocumentDefaultDir =>
        _documentKind == EEditorDocumentKind.Scene ? defaultSceneDir : defaultWorldDir;

    private string DocumentKindLabel =>
        _documentKind == EEditorDocumentKind.Scene ? "Scene" : "World";

    // documentPath is what Ctrl+S writes back to — a real file when the menu
    // opened one, or a not-yet-existing path it minted for a new document
    // (whose extension still fixes the kind). includeEnv only means anything
    // for a scene document.
    public void Init(WorldState worldState, string documentPath, bool includeEnv)
    {
        Current = this;
        MusicManager.Instance?.SetEditor(true);
        _documentPath = documentPath ?? "";
        _documentKind = KindForPath(_documentPath);
        _documentIncludeEnv = includeEnv;
        _worldState = worldState;
        _cursorPosition = worldState.Spawn;
        // Open with nothing cut away, so the top of every roof is on screen and
        // R/F only ever has to travel DOWN to reach the storey being edited. The
        // drafting plane can't be derived from a parked cutaway (it would sit up
        // in the sky), so it starts at the authored ground line and becomes
        // clip-driven from the first R/F press.
        _clipOff = true;
        _clipY = ClipCeiling();
        _buildY = SnapToPlateau(emptyAirPaintY);
        // Before the Sim is built — it latches day/night off the clock.
        ApplyTimeOfDay(editorTimeOfDay);

        _world = new Sim();
        // Into the scene viewport, not under us — it owns its own World3D and
        // everything the scene camera must see has to live in it.
        sceneViewport.AddChild(_world);

        _world.Initialize(worldState, _cursorPosition, camera, fogMaterial, () => _cursorPosition);

        _world.EnableEditorMode(_cursorPosition);
        _world.UpdateEntityLoading(_cursorPosition);
        // Built once: the boundary is created in Initialize above and lives as
        // long as the Sim does.
        foreach (Rid rid in _world.BoundaryRids)
        {
            _boundaryExclude.Add(rid);
        }

        _history = new EditorHistory(worldState, _world, undoDepth);

        camera.Init(sceneViewport);
        camera.ManualClipMode = true;
        ApplyEditorCameraSettings();
        camera.SetInitialPosition(_cursorPosition);
        camera.SetClip(_clipY - CLIP_VISUAL_BIAS, _cursorPosition, allowMaxClip: false);

        // Enter in the name field confirms the dialog.
        if (saveDialog != null && saveNameEdit != null)
        {
            saveDialog.RegisterTextEnter(saveNameEdit);
        }

        UpdateDocumentHud();
        GD.Print($"[Editor] editing {DocumentKindLabel} document: {(string.IsNullOrEmpty(_documentPath) ? "(unsaved)" : _documentPath)}");
        BuildToolPalette();
        editorHud.onVoxelBrushSelected += index => { _voxelTypeIndex = index; UpdateHud(); };
        editorHud.onEntityBrushSelected += index => { _entityTypeIndex = index; UpdateHud(); };
        editorHud.onRoofBrushSelected += index => { _roofStyleIndex = index; UpdateHud(); };
        editorHud.onToolChanged += tool =>
        {
            _tool = tool;
            CancelGizmoDrag();
            _selection.Clear();
            _editingRoof = null;
            EndDrag();
            UpdateHud();
        };
        editorHud.onRoofSeamAxisChanged += axis => _roofSeamAxis = axis;
        editorHud.onRoofFormChanged += form => _roofForm = form;
        editorHud.onRoofSlopeChanged += degrees => _roofSlopeDegrees = degrees;
        editorHud.onRoofBrokenChanged += broken => _roofBroken = broken;
        // Every roof-panel change lands here once it has settled, and retunes
        // the latched roof in place. Registered after the setters above so it
        // reads the value they just stored.
        editorHud.onRoofSettingsCommitted += PushRoofSettings;
        // Leaving Edit mode drops the retune target, so a stray slider nudge
        // can't reach back and reshape a roof that's no longer being pointed at.
        editorHud.onRoofModeChanged += mode =>
        {
            _roofMode = mode;
            _editingRoof = null;
        };
        // Leaving Select mode (or the entity tool entirely) drops the selection
        // rather than leaving an invisible group armed for the next Delete.
        editorHud.onEntityToolModeChanged += mode =>
        {
            _entityToolMode = mode;
            CancelGizmoDrag();
            _selection.Clear();
            UpdateHud();
        };
        editorHud.onSnapToGridChanged += snap => _snapToGrid = snap;
        editorHud.onSnapRotationChanged += snap => _snapRotation = snap;
        editorHud.onOperationSelected += operation => _operation = operation;
        editorHud.onShapeSelected += shape =>
        {
            _brushShape = shape;
            PushPlateauSnap();
        };
        editorHud.onPlateauSnapChanged += snap => _plateauSnapByShape[(int)_brushShape] = snap;
        editorHud.onVoxelEdgesSelected += edges => _voxelEdges = edges;
        editorHud.onLightingChanged += ApplyLighting;
        editorHud.onTimeOfDayChanged += ApplyTimeOfDay;
        editorHud.onWeatherSelected += ApplyWeather;
        editorHud.onInteriorClassSelected += index => CVars.subsceneInteriorClass.Value = index;
        Array.Fill(_plateauSnapByShape, true);
        editorHud.SetEntitySnaps(_snapToGrid, _snapRotation);
        editorHud.SetShape(_brushShape);
        PushPlateauSnap();
        editorHud.SetTimeOfDay(editorTimeOfDay);
        _roofSeamAxis = defaultRoofSeamAxis;
        _roofForm = defaultRoofForm;
        _roofSlopeDegrees = defaultRoofSlopeDegrees;
        _roofBroken = defaultRoofBroken;
        editorHud.SetRoofSeamAxis(_roofSeamAxis);
        editorHud.SetRoofForm(_roofForm);
        editorHud.SetRoofSlope(_roofSlopeDegrees);
        editorHud.SetRoofBroken(_roofBroken);
        BuildWeatherPresets();
        // After _worldState is assigned above — the menu is the world's own
        // space-class palette, not an editor-side list.
        editorHud.BuildInteriorClassOptions(_worldState?.SimData?.interiorAmbiences);
        _authoredDepthFog = sceneEnvironment?.Environment?.FogEnabled ?? false;
        ApplyLighting(editorHud.LightingChecked);
        UpdateHud();
    }

    // The whole sky reads off WorldState each frame (SkyController re-derives
    // the sun arc, palette and weather from TimeOfDay01 in _Process), so
    // writing the clock is the entire implementation — the scene relights on
    // the next frame with no further plumbing.
    private void ApplyTimeOfDay(float timeOfDay01)
    {
        _worldState.TimeOfDay01 = Mathf.Clamp(timeOfDay01, 0f, 1f);
        _worldState.TimeOfDayAbsolute = _worldState.DayNumber + _worldState.TimeOfDay01;
    }

    // Fills the Weather dropdown from the palette and forces the first preset.
    // Nulls are dropped here rather than in the HUD so the menu's item indices
    // stay aligned with _weatherPresets.
    private void BuildWeatherPresets()
    {
        _weatherPresets.Clear();
        foreach (WeatherData preset in brushPalette?.weatherPresets ?? System.Array.Empty<WeatherData>())
        {
            if (preset != null)
            {
                _weatherPresets.Add(preset);
            }
        }
        editorHud.BuildWeatherOptions(_weatherPresets);
        ApplyWeather(0);
    }

    // Holds the sky at one authored forecast. With no presets the override
    // clears and the editor falls back to the zone's own simulated weather,
    // which is the pre-toggle behavior.
    private void ApplyWeather(int index)
    {
        if (SkyController.Current == null)
        {
            return;
        }
        SkyController.Current.WeatherOverride =
            index >= 0 && index < _weatherPresets.Count ? _weatherPresets[index] : null;
    }

    // Lighting on = the shipping look. Off = a flat authoring view: no haze,
    // no distance fog, and the eye-adaptation tone curve pinned open so caves
    // and interiors stay readable while painting. The sun keeps running (the
    // shading is what gives voxel faces their orientation), it's just no
    // longer allowed to hide anything.
    private void ApplyLighting(bool enabled)
    {
        _world?.SetFogVolumetricEnabled(enabled);
        if (sceneEnvironment?.Environment != null)
        {
            sceneEnvironment.Environment.FogEnabled = enabled && _authoredDepthFog;
        }
        // The game pushes these every frame from GameClient; nothing does in
        // the editor, so a one-shot write at toggle time is enough.
        RenderingServer.GlobalShaderParameterSet("eye_adaptation", enabled ? 0f : 1f);
        RenderingServer.GlobalShaderParameterSet("eye_adapt_dark_gain", flatViewDarkGain);
        RenderingServer.GlobalShaderParameterSet("eye_adapt_light_gain", flatViewLightGain);
        RenderingServer.GlobalShaderParameterSet("eye_adapt_knee", flatViewKnee);
    }

    // The atlas manifest is loaded here and nowhere else — see EditorBrushIcons
    // for why it must stay off the game's normal load path.
    private void BuildToolPalette()
    {
        VoxelAtlasManifest manifest = EditorBrushIcons.LoadManifest(brushPalette?.atlasManifestPath);

        BuildVoxelBrushes();
        var voxels = new EditorBrushEntry[_voxelBrushes.Count];
        for (int i = 0; i < _voxelBrushes.Count; i++)
        {
            VoxelBrush brush = _voxelBrushes[i];
            voxels[i] = new EditorBrushEntry(
                brush.Name,
                EditorBrushIcons.ForBlock(brush.Block, manifest));
        }

        BuildEntityBrushes();
        var entities = new EditorBrushEntry[_entityBrushes.Count];
        var bakeRequests = new List<IconBakeRequest>();
        for (int i = 0; i < _entityBrushes.Count; i++)
        {
            EntityBrush brush = _entityBrushes[i];
            Texture2D icon = AuthoredIconFor(brush);
            entities[i] = new EditorBrushEntry(brush.Name, icon, brush.Tab);
            PackedScene bakeScene = icon == null ? BakeSceneFor(brush) : null;
            if (bakeScene != null)
            {
                bakeRequests.Add(new IconBakeRequest(i, bakeScene));
            }
        }

        editorHud.BuildToolButtons(voxels, entities, BuildRoofBrushes());
        iconBaker?.Bake(bakeRequests, _cursorPosition, editorHud.SetEntityIcon);
    }

    // One button per catalog block, in catalog order. Air is skipped — the
    // Erase operation writes it — and so is anything the catalog left
    // unauthored. Painting a block is now the whole story: no kit indirection,
    // no separate auto/literal split, and every block in the catalog is
    // reachable rather than just the handful a VoxelType named.
    private void BuildVoxelBrushes()
    {
        _voxelBrushes.Clear();
        foreach (BlockData block in BlockCatalog.Active.blocks ?? System.Array.Empty<BlockData>())
        {
            if (block == null || block.blockId == Blocks.AirId)
            {
                continue;
            }
            _voxelBrushes.Add(new VoxelBrush(block));
        }
        if (_voxelBrushes.Count == 0)
        {
            GD.PushWarning("WorldEditor: the block catalog is empty; nothing to paint with.");
            return;
        }

        // Open on ordinary ground rather than whatever sorts first.
        int index = _voxelBrushes.FindIndex(b => b.BlockId == Blocks.GroundId);
        _voxelTypeIndex = index >= 0 ? index : 0;
    }

    // Roof styles are surfaces, not scenes, so there is nothing for the icon
    // baker to render — an entry with no authored icon falls back to its name
    // label, which is all a short palette needs.
    private EditorBrushEntry[] BuildRoofBrushes()
    {
        _roofStyles.Clear();
        foreach (RoofStyleData style in brushPalette?.roofLibrary?.styles ?? System.Array.Empty<RoofStyleData>())
        {
            if (style != null)
            {
                _roofStyles.Add(style);
            }
        }
        var entries = new EditorBrushEntry[_roofStyles.Count];
        for (int i = 0; i < _roofStyles.Count; i++)
        {
            RoofStyleData style = _roofStyles[i];
            string name = string.IsNullOrEmpty(style.displayName)
                ? style.ResourcePath.GetFile().GetBaseName()
                : style.displayName;
            entries[i] = new EditorBrushEntry(name, style.icon);
        }
        return entries;
    }

    // Art the brush's own data already carries. Nothing is authored per-brush
    // for the editor: these are the images the game shows for the same thing
    // elsewhere, so a palette button matches the inventory / bestiary entry.
    // Null means there's nothing authored and the icon has to be rendered.
    private Texture2D AuthoredIconFor(EntityBrush brush)
    {
        return brush.Kind switch
        {
            EEditorEntityKind.Prop => brush.Prop?.icon,
            EEditorEntityKind.Loot => brushPalette?.lootItem?.item?.inventorySprite,
            EEditorEntityKind.Goblin => brushPalette?.goblinMob?.bestiaryPortrait,
            EEditorEntityKind.KunKun => brushPalette?.kunKunMob?.bestiaryPortrait,
            _ => null,
        };
    }

    // What the icon baker renders for a brush with no authored art — the same
    // scene the brush stamps, so the button shows the actual thing placed.
    // Mobs are deliberately absent: their scenes expect a MobSimState to drive
    // them, and they have bestiary portraits already. PlayerSpawn has nothing
    // to render at all (it moves a coordinate) and keeps its name label.
    private PackedScene BakeSceneFor(EntityBrush brush)
    {
        return brush.Kind switch
        {
            EEditorEntityKind.Prop => brush.Prop?.scene,
            EEditorEntityKind.Chest => brushPalette?.chestScene,
            EEditorEntityKind.Torch => brushPalette?.torchScene,
            EEditorEntityKind.Door => brushPalette?.doorScene,
            EEditorEntityKind.SpikeTrap => brushPalette?.spikeTrapScene,
            EEditorEntityKind.DartTrap => brushPalette?.dartTrapScene,
            EEditorEntityKind.Trapdoor => brushPalette?.trapdoorScene,
            EEditorEntityKind.LinkedTrapdoor => brushPalette?.trapdoorScene,
            EEditorEntityKind.TrapdoorTrap => brushPalette?.trapdoorTrapScene,
            EEditorEntityKind.CrumblingFloor => brushPalette?.crumblingFloorScene,
            EEditorEntityKind.Lever => brushPalette?.leverScene,
            EEditorEntityKind.ClimbableTree => brushPalette?.climbableTreeScene,
            EEditorEntityKind.Campfire => brushPalette?.campfireScene,
            EEditorEntityKind.Forge => brushPalette?.forgeScene,
            EEditorEntityKind.Well => brushPalette?.wellScene,
            EEditorEntityKind.HealingFountain => brushPalette?.healingFountainScene,
            EEditorEntityKind.ManaFountain => brushPalette?.manaFountainScene,
            EEditorEntityKind.Marker => brushPalette?.markerScene,
            EEditorEntityKind.PathHint => brushPalette?.pathHintScene,
            _ => null,
        };
    }

    // Fixed prefabs first, then the prop library. The fixed ones all land in the
    // Interactives tab: they act rather than decorate (chests, doors, traps,
    // mobs) or mark the world (the spawn point), and none of them is a prop.
    private void BuildEntityBrushes()
    {
        _entityBrushes.Clear();
        foreach (EEditorEntityKind kind in FixedEntityKinds)
        {
            _entityBrushes.Add(new EntityBrush(kind.ToString(), kind, EEditorEntityTab.Interactives));
        }
        AddMarkerBrushes();
        AddPathHintBrushes();
        AddLinkedTrapdoorBrushes();
        AddPropBrushes();
    }

    // One Lever + one linked Trapdoor brush per authored link tag, mirroring
    // AddMarkerBrushes — placing "Lever: gate" and "Trapdoor: gate" shares the
    // "gate" link so the lever throws that trapdoor. A new linked pair is a
    // string in EditorBrushPalette.linkTags, not a code change.
    private void AddLinkedTrapdoorBrushes()
    {
        foreach (string tag in brushPalette?.linkTags ?? System.Array.Empty<string>())
        {
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }
            if (brushPalette?.leverScene != null)
            {
                _entityBrushes.Add(new EntityBrush($"Lever: {tag}", EEditorEntityKind.Lever,
                    EEditorEntityTab.Interactives, prop: null, tag: tag));
            }
            if (brushPalette?.trapdoorScene != null)
            {
                _entityBrushes.Add(new EntityBrush($"Trapdoor: {tag}", EEditorEntityKind.LinkedTrapdoor,
                    EEditorEntityTab.Interactives, prop: null, tag: tag));
            }
        }
    }

    // One brush per authored pool name. Like the prop brushes these are expanded
    // from the palette rather than fixed in the enum — a new spawn-point pool is
    // a string in EditorBrushPalette.markerTags, not a code change.
    private void AddMarkerBrushes()
    {
        if (brushPalette?.markerScene == null)
        {
            return;
        }
        foreach (string tag in brushPalette.markerTags ?? System.Array.Empty<string>())
        {
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }
            _entityBrushes.Add(new EntityBrush($"Spawn: {tag}", EEditorEntityKind.Marker,
                EEditorEntityTab.Interactives, prop: null, tag: tag));
        }
    }

    // One brush per authored path-hint name, same expansion as the marker
    // brushes. The tag is both the hint's name inside the scene (a road names
    // "<placement>.<tag>") and what picks the tread an auto-linked spur gets
    // (WorldGenData.pathHintProfiles), so "door" and "gate" are different
    // brushes rather than one brush with a setting.
    private void AddPathHintBrushes()
    {
        if (brushPalette?.pathHintScene == null)
        {
            return;
        }
        foreach (string tag in brushPalette.pathHintTags ?? System.Array.Empty<string>())
        {
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }
            _entityBrushes.Add(new EntityBrush($"Path: {tag}", EEditorEntityKind.PathHint,
                EEditorEntityTab.Interactives, prop: null, tag: tag));
        }
    }

    // One brush per library entry PER CATEGORY FLAG it ticks, in library order —
    // the tabs do the grouping now, so nothing here has to sort. An entry with
    // several flags deliberately appears under each of those tabs; one with none
    // still gets a button, in the Props catch-all, rather than vanishing.
    private void AddPropBrushes()
    {
        PropLibraryEntry[] entries = brushPalette?.propLibrary?.entries;
        if (entries == null)
        {
            return;
        }
        foreach (PropLibraryEntry entry in entries)
        {
            if (entry?.scene == null)
            {
                continue;
            }
            string name = string.IsNullOrEmpty(entry.displayName)
                ? entry.scene.ResourcePath.GetFile().GetBaseName()
                : entry.displayName;
            bool placed = false;
            foreach (EPropCategory category in Enum.GetValues<EPropCategory>())
            {
                if ((entry.category & category) == 0)
                {
                    continue;
                }
                _entityBrushes.Add(new EntityBrush(name, EEditorEntityKind.Prop, TabForCategory(category), entry));
                placed = true;
            }
            if (!placed)
            {
                _entityBrushes.Add(new EntityBrush(name, EEditorEntityKind.Prop, EEditorEntityTab.Props, entry));
            }
        }
    }

    // One authoring category flag to its tab. Other is the man-made catch-all,
    // which is exactly what the Props tab holds.
    private static EEditorEntityTab TabForCategory(EPropCategory category)
    {
        return category switch
        {
            EPropCategory.Tree => EEditorEntityTab.Trees,
            EPropCategory.Rock => EEditorEntityTab.Rocks,
            EPropCategory.Foliage => EEditorEntityTab.Nature,
            EPropCategory.Furniture => EEditorEntityTab.Furniture,
            _ => EEditorEntityTab.Props,
        };
    }

    public override void _Process(double deltaTime)
    {
        // Ends the drag before the early-out below, so the pointer can't stay
        // captured behind a console or dialog that opened mid-flight. Polling the
        // button (rather than trusting the release event) means a release that
        // something else swallowed still lands.
        if (_flying && (ConsoleUI.IsOpen || IsSaveDialogOpen || !Input.IsMouseButtonPressed(MouseButton.Right)))
        {
            EndFly();
        }

        // Movement polls Input directly, which ignores focus — without this the
        // camera flies around while the save-name field is being typed into.
        if (ConsoleUI.IsOpen || IsSaveDialogOpen)
        {
            return;
        }

        float dt = (float)deltaTime;

        // Ahead of the orbit below, which needs this frame's final yaw to place
        // the cursor; UpdateCamera then skips its own tick.
        camera.TickRotation(dt);

        Vector3 move = _flying ? FlyMove(dt) : PanMove(dt);

        if (_orbiting)
        {
            TickCameraOrbit(move);
            _orbiting = camera.IsRotating;
        }
        else
        {
            _cursorPosition += move;
        }

        camera.pitchDegrees = _cameraPitchDegrees;
        camera.UpdateCamera(deltaTime, _cursorPosition, 0f, tickRotation: false);
        camera.SetClip(_clipY - CLIP_VISUAL_BIAS, _cursorPosition, allowMaxClip: false);
        if (groundPlane != null)
        {
            groundPlane.GlobalPosition = new Vector3(_cursorPosition.X, _buildY, _cursorPosition.Z);
        }
        // Pixel-snap and refresh the upscale uniforms before anything reads the
        // camera pose — SyncCapMaskCamera below and this frame's picking both
        // have to match the pose the scene actually renders at.
        viewportRig?.SnapAndUpscale();
        // The editor holds the cutaway permanently engaged (ManualClipMode), so
        // the cap plane draws every frame and the cap mask must be kept in sync
        // — unsynced, the mask stays at its white "draw the cap here" clear and
        // the fullscreen cap plane paints over the entire world. Mask size
        // matches the inner pre-upscale viewport for 1:1 SCREEN_UV alignment.
        camera.SyncCapMaskCamera(sceneViewport.Size);
        CullProps(camera.Clip);
        _world.UpdateEntityLoading(_cursorPosition);

        // Same `nav_grid` overlay the game draws around the player, centred on
        // the edit cursor instead — the editor has no player, so Sim's own call
        // never fires here. Ahead of the fly / over-UI bails below so it keeps
        // drawing while the view is being moved: it reads the cursor, not the
        // pick ray, and has nothing to do with what the pointer is over.
        if (CVars.navGridDebug.Value)
        {
            NavGridDebug.Draw(_world, _cursorPosition);
        }

        // Alongside it, and for the same reason: the Opening / Barrier markers
        // are invisible, so they must keep drawing while the view is moved.
        if (CVars.editorMarkerOverlay.Value)
        {
            EditorMarkerOverlay.Draw(_worldState, _cursorPosition, _clipY,
                markerOverlayRadiusXZ, markerOverlayRadiusY,
                openingMarkerColor, barrierMarkerColor, markerOccludedColor);
        }

        editorHud.UpdateClip(_clipY, _clipOff, _buildY);
        bool overUi = editorHud.IsPointerOverUi();
        // Ahead of the fly-cam bail below: the selection's boxes are immediate-
        // mode, so they'd blink out for the length of every flight.
        TickSelection(overUi);

        // Nothing to pick against while flying: the pointer is captured, so its
        // position is frozen and every preview would just sit wherever it was
        // when the drag began.
        if (_flying)
        {
            _hoverValid = false;
            editorHud.UpdatePosition(null);
            editorHud.SetHeldOverride(null);
            return;
        }

        // Same over the HUD: the ray passes behind the panel, so every preview
        // would chase a cell the click can't reach anyway (the panels stop the
        // press). Drags already in flight keep drawing — the pointer is only
        // crossing the HUD on its way somewhere.
        if (overUi)
        {
            _hoverValid = false;
            editorHud.UpdatePosition(null);
            editorHud.SetHeldOverride(null);
            DrawRegionPreview();
            DrawRoofPreview();
            return;
        }

        // Ctrl / Alt momentarily override the selected operation; poll them so
        // the button row and the hover preview track the modifier even with no
        // click event in flight. Keycode vs physical matters (see
        // DrawEntityHoverBox), and both readings must feed the same override or
        // the preview lands a cell off what the click will actually target.
        bool ctrl = Input.IsKeyPressed(Key.Ctrl) || Input.IsPhysicalKeyPressed(Key.Ctrl);
        bool alt = Input.IsKeyPressed(Key.Alt) || Input.IsPhysicalKeyPressed(Key.Alt);
        UpdateHoverTarget(OperationFor(ctrl, alt));
        editorHud.UpdatePosition(_hoverValid ? _hoverHit : null);
        editorHud.SetHeldOverride(ModifierOverride(ctrl, alt));
        DrawBrushPreview();
        DrawRegionPreview();
        DrawRoofPreview();
        DrawEntityHoverBox();
    }

    // Selection upkeep and its immediate-mode visuals. Runs after the entity
    // streaming pass above, so a selection that lost its entities to an undo or
    // a chunk eviction is pruned before anything draws or drags it.
    private void TickSelection(bool overUi)
    {
        if (!IsSelectMode)
        {
            _hotHandle = EGizmoHandle.None;
            editorHud.SetSelectionCount(0);
            return;
        }
        editorHud.SetSelectionCount(_selection.Count);

        foreach (EntitySimState state in _selection.States)
        {
            Node3D node = state.RuntimeNode;
            if (node != null && IsInstanceValid(node))
            {
                Aabb bounds = WorldBoundsOf(node);
                DebugDraw.Box(bounds.Position, bounds.End, selectionColor);
            }
        }

        if (_selection.IsEmpty || gizmo == null)
        {
            _hotHandle = EGizmoHandle.None;
            return;
        }
        Vector3 pivot = _gizmoDrag != EGizmoHandle.None ? _gizmoDragPivot : _selection.Pivot;
        // A drag holds its handle lit even when the cursor wanders off it — or
        // onto the HUD, which otherwise lights whatever handle sits behind it.
        if (_gizmoDrag == EGizmoHandle.None)
        {
            Vector2 mouse = ToScenePos(GetViewport().GetMousePosition());
            _hotHandle = overUi ? EGizmoHandle.None : gizmo.Pick(pivot, camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse));
        }
        gizmo.Draw(pivot, _gizmoDrag != EGizmoHandle.None ? _gizmoDrag : _hotHandle);
    }

    private bool IsSelectMode => _tool == EEditorTool.Entity && _entityToolMode == EEditorEntityMode.Select;

    // Perspective and close in, with the pitch under free look. Applied after
    // camera.Init, which seats whatever the game's camera_preset CVar says — the
    // editor's framing is its own concern and mustn't ride on that setting.
    private void ApplyEditorCameraSettings()
    {
        _cameraPitchDegrees = Mathf.Clamp(cameraStartPitchDegrees, -PITCH_LIMIT_DEGREES, PITCH_LIMIT_DEGREES);
        camera.ApplyAngleSettings(new CameraAngleSettings
        {
            Perspective = true,
            Fov = cameraFov,
            Distance = cameraDistance,
            PitchDegrees = _cameraPitchDegrees,
        });
        // SetInitialPosition places the camera off its own basis, so the pose has
        // to carry the new pitch before it runs.
        camera.GlobalRotation = new Vector3(Mathf.DegToRad(_cameraPitchDegrees), camera.Yaw, 0f);
    }

    // This frame's camera orientation, built from the editor's own angles rather
    // than read off the node — by the time movement runs, the node still holds
    // last frame's pose, pixel-snapped by the viewport rig.
    private Basis CameraBasis()
    {
        return Basis.FromEuler(new Vector3(Mathf.DegToRad(_cameraPitchDegrees), camera.Yaw, 0f));
    }

    // WASD panning on the XZ plane relative to camera yaw — the navigation with
    // no button held.
    private Vector3 PanMove(float dt)
    {
        Vector2 input = Input.GetVector("MoveLeft", "MoveRight", "MoveUp", "MoveDown");
        if (input.LengthSquared() <= 0f)
        {
            return Vector3.Zero;
        }
        float yaw = camera.Yaw;
        Vector3 back = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
        Vector3 right = new Vector3(back.Z, 0, -back.X);
        return (back * input.Y + right * input.X) * moveSpeed * _flySpeedScale * dt;
    }

    // Camera-relative flight while the right button is held: WASD along the view
    // axes (so forward follows the pitch), E/Q straight up/down, Shift to boost.
    // The camera is placed at cursor + back * distance every frame, so flying is
    // just translating the cursor.
    private Vector3 FlyMove(float dt)
    {
        Vector2 input = Input.GetVector("MoveLeft", "MoveRight", "MoveUp", "MoveDown");
        Basis basis = CameraBasis();
        // MoveUp is the negative half of the pair (W reads -1) and basis.Z points
        // back out of the screen; the two signs cancel to "W flies forward".
        Vector3 dir = basis.X * input.X + basis.Z * input.Y;
        if (Input.IsPhysicalKeyPressed(Key.E)) { dir += Vector3.Up; }
        if (Input.IsPhysicalKeyPressed(Key.Q)) { dir -= Vector3.Up; }
        if (dir.LengthSquared() <= 0f)
        {
            return Vector3.Zero;
        }
        float speed = moveSpeed * _flySpeedScale;
        if (Input.IsPhysicalKeyPressed(Key.Shift)) { speed *= flyBoostMultiplier; }
        return dir.Normalized() * speed * dt;
    }

    private void BeginFly()
    {
        if (_flying)
        {
            return;
        }
        _flying = true;
        // A left drag still held would never see its release once the pointer is
        // captured, leaving the edit open and the stroke re-painting on the way
        // out — so close it exactly as a release would.
        FinishStroke();
        // A Q/E orbit still easing would keep swinging the cursor about its
        // pivot and fight the look.
        _orbiting = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private void EndFly()
    {
        if (!_flying)
        {
            return;
        }
        _flying = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    // Right-drag look. Turns the camera in place: the eye is held where it is and
    // the cursor — which the camera is actually placed off — is re-derived behind
    // the new orientation. Orbiting the cursor instead would swing the camera
    // through whatever is `distance` behind it, which in a corridor is the wall.
    private void ApplyFreeLook(Vector2 relative)
    {
        Vector3 eye = _cursorPosition + CameraBasis().Z * camera.distance;
        _cameraPitchDegrees = Mathf.Clamp(
            _cameraPitchDegrees - Mathf.RadToDeg(relative.Y * lookSensitivity),
            -PITCH_LIMIT_DEGREES,
            PITCH_LIMIT_DEGREES);
        camera.SetYaw(camera.Yaw - relative.X * lookSensitivity);
        _cursorPosition = eye - CameraBasis().Z * camera.distance;
    }

    // Latches what the camera is looking at so the cursor can be swung around it
    // while the yaw eases. Re-pressing mid-rotation just re-latches from the
    // current pose, so a double tap orbits about wherever the view is by then.
    private void BeginCameraOrbit()
    {
        _orbitPivot = ResolveOrbitPivot();
        _orbitStartCursor = _cursorPosition;
        _orbitStartYaw = camera.Yaw;
        _orbiting = true;
    }

    // The world point under the view centre. Falls back to the cursor itself —
    // a no-op pivot, i.e. plain yaw about the cursor column — when the centre ray
    // hits nothing (looking out over open air).
    private Vector3 ResolveOrbitPivot()
    {
        // Already scene-viewport pixels: the camera lives in that viewport, so its
        // centre needs no ToScenePos conversion.
        Godot.Collections.Dictionary hit = Raycast((Vector2)sceneViewport.Size * 0.5f);
        if (hit == null || hit.Count == 0)
        {
            return _cursorPosition;
        }
        Vector3 pivot = (Vector3)hit["position"];
        Vector3 offset = pivot - _cursorPosition;
        offset.Y = 0f;
        float radius = offset.Length();
        if (radius > orbitPivotMaxRadius)
        {
            pivot = _cursorPosition + offset * (orbitPivotMaxRadius / radius);
        }
        return pivot;
    }

    // Swings the cursor around the pivot by however far the eased yaw has turned
    // since the rotation began. The camera places itself at cursor + basis.Z *
    // distance, so turning the cursor through the same angle about the pivot holds
    // the pivot's screen position. Absolute rather than incremental so it can't
    // drift; WASD during a rotation carries the pivot along with it.
    private void TickCameraOrbit(Vector3 move)
    {
        _orbitStartCursor += move;
        _orbitPivot += move;
        float turned = Mathf.AngleDifference(_orbitStartYaw, camera.Yaw);
        _cursorPosition = _orbitPivot + (_orbitStartCursor - _orbitPivot).Rotated(Vector3.Up, turned);
    }

    // Window pixel → scene-viewport pixel. Mouse events arrive in window
    // coordinates but the scene camera lives in the low-res inner viewport, so
    // every position that reaches camera.ProjectRay* must come through here or
    // the ray lands `pixel_scale`× too far from the cursor.
    private Vector2 ToScenePos(Vector2 windowPos)
    {
        return viewportRig != null ? viewportRig.ScreenToInner(windowPos) : windowPos;
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
        Vector2 mouse = ToScenePos(GetViewport().GetMousePosition());

        if (debug)
        {
            // Unconditional reference box at the cursor's ground position. If this
            // is invisible too, the problem is DebugDraw in the editor, not the pick.
            Vector3 c = _cursorPosition;
            DebugDraw.Box(c - Vector3.One, c + Vector3.One, Colors.Yellow);
        }

        // Place mode boxes what Ctrl would erase; Select mode boxes what a plain
        // click would pick, so it needs no modifier. Suppressed mid-drag, where
        // the box would just chase the cursor over the entities being moved.
        bool hovering = IsSelectMode ? _gizmoDrag == EGizmoHandle.None && _hotHandle == EGizmoHandle.None : _tool == EEditorTool.Entity && ctrl;
        Node3D hovered = null;
        Aabb bounds = default;
        if (hovering)
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
        string summary = $"[EditorPick] tool={_tool} ctrl={ctrl} mouse={mouse} ray={rayDir} "
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
    // Runs the click-time pick against the current mouse position, once a frame,
    // ahead of anything that reads the hover fields.
    private void UpdateHoverTarget(EEditorBrushOperation operation)
    {
        Vector2 mouse = ToScenePos(GetViewport().GetMousePosition());
        _hoverValid = ComputeVoxelTarget(mouse, Overwrites(operation), out _hoverHit, out _hoverBase, out _hoverAir);
    }

    // Boxes the cell a click would base the brush at. Voxel tool only, and
    // dropped once a region drag is in flight, where DrawRegionPreview shows the
    // real footprint.
    private void DrawBrushPreview()
    {
        if (_tool != EEditorTool.Voxel || !_hoverValid || _regionAnchor.HasValue)
        {
            return;
        }
        Vector3I cell = ResolveBrushCell();
        DebugDraw.Box(cell, cell + Vector3I.One, brushPreviewColor);
    }

    // Where the brush would base if you clicked right now. The target cell alone
    // isn't it: a region shape snaps its elevation onto the cutaway band grid at
    // press, and Window / Door take theirs from the column's floor rather than
    // from the click. Both derivations are repeated from the press path, so the
    // box lands where the shape does instead of one band or one storey off.
    private Vector3I ResolveBrushCell()
    {
        Vector3I cell = _hoverBase;
        if (IsRegionShape(_brushShape))
        {
            if (_plateauSnapByShape[(int)_brushShape] && SupportsPlateauSnap(_brushShape))
            {
                cell.Y = SnapToPlateau(cell.Y);
            }
            return cell;
        }
        switch (_brushShape)
        {
            case EEditorBrushShape.Window:
                return new Vector3I(_hoverHit.X, FindFloorY(_hoverAir) + windowFloorOffset, _hoverHit.Z);
            case EEditorBrushShape.Door:
                return new Vector3I(_hoverHit.X, FindFloorY(_hoverAir), _hoverHit.Z);
            default:
                return cell;
        }
    }

    private void DrawRegionPreview()
    {
        if (!_regionAnchor.HasValue)
        {
            return;
        }
        BuildRegionCells(_regionAnchor.Value, _regionCurrent, out Vector3I min, out Vector3I max);
        if (_brushShape == EEditorBrushShape.Room)
        {
            // Slab and walls get their own boxes: a Room's bounding volume would
            // promise a solid block, when what lands is a one-course floor with a
            // hollow shell standing on it.
            DebugDraw.Box(min, new Vector3I(max.X, min.Y, max.Z) + Vector3I.One, regionPreviewColor);
            DebugDraw.Box(new Vector3I(min.X, min.Y + 1, min.Z), max + Vector3I.One, regionPreviewColor);
            return;
        }
        DebugDraw.Box(min, max + Vector3I.One, regionPreviewColor);
    }

    // Below this a preview ridge segment has collapsed to a point and drawing it
    // would just stack a dot on the rafters already meeting there.
    private const float MIN_PREVIEW_RIDGE_LENGTH = 0.01f;

    // Wireframe of the roof a release would generate — the eave rectangle
    // (overhang included), the ridge, and a rafter from each corner up to it. A
    // box wouldn't do: the form, the seam axis and the pitch are the whole point
    // of the drag, and none of them is visible in a bounding volume.
    private void DrawRoofPreview()
    {
        if (!_roofAnchor.HasValue)
        {
            return;
        }
        RoofStyleData style = CurrentRoofStyle;
        if (style == null)
        {
            return;
        }
        BuildRoofFootprint(_roofAnchor.Value, _roofCurrent, out Vector3 center, out float sizeX, out float sizeZ);
        // Straight off the mesh builder's own dimensions, so the wireframe can't
        // drift from what the release actually generates.
        var size = new RoofDimensions(style, sizeX, sizeZ, _roofSeamAxis, _roofSlopeDegrees, _roofForm);
        // How far the ridge reaches from the centre. A gable's runs the whole
        // length; a hip's is pulled in from every eave by the slope's own run,
        // which closes it to a point on a square footprint.
        float ridgeAcross = size.Form == ERoofForm.Hip ? size.HalfAcross - size.RidgeRun : 0f;
        float ridgeSeam = size.Form == ERoofForm.Hip ? size.HalfSeam - size.RidgeRun : size.HalfSeam;
        Vector3 peak = Vector3.Up * (size.Rise + size.Thickness);

        // Corner signs wound around the footprint, so consecutive entries share
        // an eave edge. Each corner rises to the ridge end on its own side.
        Vector2[] corners = { new Vector2(1f, 1f), new Vector2(1f, -1f), new Vector2(-1f, -1f), new Vector2(-1f, 1f) };
        for (int i = 0; i < corners.Length; i++)
        {
            Vector2 corner = corners[i];
            Vector2 next = corners[(i + 1) % corners.Length];
            Vector3 eave = center + size.Across * (corner.X * size.HalfAcross) + size.Seam * (corner.Y * size.HalfSeam);
            Vector3 eaveNext = center + size.Across * (next.X * size.HalfAcross) + size.Seam * (next.Y * size.HalfSeam);
            Vector3 ridge = center + size.Across * (corner.X * ridgeAcross) + size.Seam * (corner.Y * ridgeSeam) + peak;
            Vector3 ridgeNext = center + size.Across * (next.X * ridgeAcross) + size.Seam * (next.Y * ridgeSeam) + peak;
            DebugDraw.Line(eave, eaveNext, roofPreviewColor);
            DebugDraw.Line(eave, ridge, roofPreviewColor);
            // Skipped where the ridge has closed on this axis — both corners
            // land on the same point and the line is a dot.
            if (ridge.DistanceSquaredTo(ridgeNext) > MIN_PREVIEW_RIDGE_LENGTH * MIN_PREVIEW_RIDGE_LENGTH)
            {
                DebugDraw.Line(ridge, ridgeNext, roofPreviewColor);
            }
        }
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

    // Everything the fly cam consumes has to be caught ahead of the GUI: with the
    // pointer captured it sits frozen whereever it was when the drag started, and
    // a HUD panel under it would eat the motion long before _UnhandledInput.
    public override void _Input(InputEvent e)
    {
        if (!_flying)
        {
            return;
        }

        if (e is InputEventMouseMotion motion)
        {
            ApplyFreeLook(motion.Relative);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (e is InputEventMouseButton button && button.Pressed)
        {
            // Wheel retunes the fly speed, the usual fly-cam convention. It also
            // scales panning, so the speed you settled on carries back out.
            if (button.ButtonIndex == MouseButton.WheelUp || button.ButtonIndex == MouseButton.WheelDown)
            {
                float step = button.ButtonIndex == MouseButton.WheelUp ? flySpeedStep : 1f / flySpeedStep;
                _flySpeedScale = Mathf.Clamp(_flySpeedScale * step, FLY_SPEED_SCALE_MIN, FLY_SPEED_SCALE_MAX);
                editorHud.ShowToast($"Fly speed {moveSpeed * _flySpeedScale:0.#} m/s", success: true);
                GetViewport().SetInputAsHandled();
            }
        }
        else if (e is InputEventMouseButton release && release.ButtonIndex == MouseButton.Right)
        {
            EndFly();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (ConsoleUI.IsOpen || IsSaveDialogOpen)
        {
            return;
        }

        // Right-drag flies the camera. Started here rather than in _Input so a
        // press that lands on the HUD stays the HUD's.
        if (e is InputEventMouseButton flyButton && flyButton.ButtonIndex == MouseButton.Right && flyButton.Pressed)
        {
            BeginFly();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (e.IsActionPressed("TogglePause"))
        {
            onQuitToMenu?.Invoke();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Ahead of the action checks: Godot matches actions ignoring modifiers,
        // and CameraLeft is bound to Z — so Ctrl+Z would orbit the camera.
        // Undo / redo honour key repeat (holding walks the stack); saving on
        // repeat would just rewrite the file over and over.
        if (e is InputEventKey keyEvent && keyEvent.Pressed && keyEvent.CtrlPressed)
        {
            switch (keyEvent.Keycode)
            {
                case Key.S:
                    if (!keyEvent.Echo)
                    {
                        if (keyEvent.ShiftPressed)
                        {
                            SaveAs();
                        }
                        else
                        {
                            Save();
                        }
                    }
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.Z:
                    StepHistory(redo: keyEvent.ShiftPressed);
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.Y:
                    StepHistory(redo: true);
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.D:
                    // Only meaningful with a selection to copy; otherwise it
                    // falls through so the key keeps whatever else it does.
                    if (IsSelectMode && !_selection.IsEmpty)
                    {
                        if (!keyEvent.Echo)
                        {
                            DuplicateSelection();
                        }
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    break;
            }
        }

        // Delete clears the entity selection. Not an input action: it's editor
        // chrome, and binding it would put a destructive key into the game's map.
        if (e is InputEventKey deleteKey && deleteKey.Pressed && !deleteKey.Echo
            && (deleteKey.Keycode == Key.Delete || deleteKey.Keycode == Key.Backspace)
            && IsSelectMode && !_selection.IsEmpty)
        {
            DeleteSelection();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Suppressed mid-flight: a 90° orbit swings the cursor about a pivot,
        // which fights the free look driving the same two values.
        if (e.IsActionPressed("CameraLeft") && !_flying)
        {
            BeginCameraOrbit();
            camera.RotateLeft();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (e.IsActionPressed("CameraRight") && !_flying)
        {
            BeginCameraOrbit();
            camera.RotateRight();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Clip only — the camera stays put. Moving the framing anchor with the
        // cutaway made every clip change a camera move, which is disorienting
        // when all you wanted was to see one storey lower. The cutaway also
        // carries the drafting plane (see StepClip), so this is how you get a
        // floor down to a chosen elevation, below y=0 included.
        //
        // Godot matches actions ignoring modifiers, so Shift+R/F arrives here as
        // a plain EditorUp/EditorDown and the modifier has to be read off the
        // event.
        if (e.IsActionPressed("EditorUp") || e.IsActionPressed("EditorDown"))
        {
            bool fine = e is InputEventWithModifiers modifiers && modifiers.ShiftPressed;
            StepClip(e.IsActionPressed("EditorUp"), fine);
            GetViewport().SetInputAsHandled();
            return;
        }

        // Left click: paint/erase/replace (with drag support in voxel mode).
        // Ignored while flying — the captured pointer isn't over anything.
        if (e is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left && !_flying)
        {
            if (mouseButton.Pressed)
            {
                Vector2 scenePos = ToScenePos(mouseButton.Position);
                EEditorBrushOperation operation = OperationFor(mouseButton.CtrlPressed, mouseButton.AltPressed);
                if (IsSelectMode)
                {
                    // A press that lands on a gizmo handle starts a transform
                    // drag; anything else re-picks the selection.
                    if (!TryBeginGizmoDrag(scenePos))
                    {
                        HandleSelectClick(scenePos, mouseButton.ShiftPressed);
                    }
                }
                else if (_tool == EEditorTool.Entity)
                {
                    bool erase = operation == EEditorBrushOperation.Erase;
                    _placingEntity = HandleEntityClick(_history.Begin(erase ? "Delete Entity" : "Place Entity"), scenePos, erase);
                    // A placement stays open while the button is held so motion
                    // can aim what was just dropped; a delete (or a click that
                    // stamped nothing) has no drag to stay open for.
                    if (_placingEntity == null)
                    {
                        _history.Commit();
                    }
                }
                else if (_tool == EEditorTool.Roof)
                {
                    // Ctrl deletes the roof under the cursor instead of starting
                    // a footprint, matching the entity tool's erase modifier.
                    if (operation == EEditorBrushOperation.Erase)
                    {
                        HandleEntityClick(_history.Begin("Delete Roof"), scenePos, delete: true);
                        _history.Commit();
                    }
                    else if (_roofMode == EEditorRoofMode.Edit)
                    {
                        SelectRoofForEdit(scenePos);
                    }
                    else if (ComputeVoxelTarget(scenePos, overwriteHitBlock: false, out _, out Vector3I roofTarget, out _))
                    {
                        // A roof is a ceiling, so it snaps to the cutaway band
                        // grid unconditionally — an eave that straddles two bands
                        // reveals half a building at a time.
                        roofTarget.Y = SnapToPlateau(roofTarget.Y);
                        _dragActive = true;
                        _roofAnchor = roofTarget;
                        _roofCurrent = roofTarget;
                    }
                }
                else if (ComputeVoxelTarget(scenePos, Overwrites(operation), out Vector3I hitBlock, out Vector3I baseTarget, out Vector3I airTarget))
                {
                    // One edit spans the whole stroke — opened here, committed
                    // on release, so a drag undoes as a single action.
                    EditorEdit edit = _history.Begin($"{operation} {_brushShape}");
                    _dragActive = true;
                    _dragOperation = operation;
                    if (IsRegionShape(_brushShape))
                    {
                        // A region's base is the elevation of the open space
                        // against the surface being pointed at — airTarget's —
                        // whatever the operation, so paint and erase agree on
                        // which band they mean and a stroke can be taken back
                        // with the same shape that laid it down. Replace still
                        // takes its XZ from the cell the ray HIT, which is what
                        // lets a room share an existing wall rather than stand a
                        // second one in front of it; taking Y from there too
                        // would cost a whole band on a top-face click (a
                        // band-aligned wall's top course, y=11, snaps down to 8)
                        // and leave the drag plane three cells below the surface
                        // the cursor is riding.
                        baseTarget.Y = airTarget.Y;
                        // Snap at press, not at fill time, so the drag plane and
                        // the preview box sit on the same elevation the region
                        // will actually be written at.
                        if (_plateauSnapByShape[(int)_brushShape] && SupportsPlateauSnap(_brushShape))
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
                        StampAt(edit, baseTarget, hitBlock, airTarget, operation);
                        _dragBaseY = baseTarget.Y;
                    }
                }
                GetViewport().SetInputAsHandled();
            }
            else
            {
                FinishStroke();
            }
        }

        if (e is InputEventMouseMotion gizmoMotion && _gizmoDrag != EGizmoHandle.None)
        {
            UpdateGizmoDrag(ToScenePos(gizmoMotion.Position));
            return;
        }

        if (e is InputEventMouseMotion placeMotion && _placingEntity != null)
        {
            UpdatePlaceAim(ToScenePos(placeMotion.Position));
            return;
        }

        if (e is InputEventMouseMotion mouseMotion && _dragActive)
        {
            Vector2 scenePos = ToScenePos(mouseMotion.Position);
            // A region drag resolves against the flat plane through its anchor,
            // not against geometry — the press picks the elevation and the rest
            // of the drag stays on it, so sweeping across a hill or an existing
            // wall doesn't drag the far corner up onto whatever the ray hits.
            if (_regionAnchor.HasValue)
            {
                if (ResolvePlaneTarget(scenePos, _regionAnchor.Value.Y, out Vector3I planeTarget))
                {
                    _regionCurrent = planeTarget;
                }
                return;
            }

            // Same flat-plane resolve as a region fill: the press picks the eave
            // elevation and the rest of the drag stays on it.
            if (_roofAnchor.HasValue)
            {
                if (ResolvePlaneTarget(scenePos, _roofAnchor.Value.Y, out Vector3I roofTarget))
                {
                    _roofCurrent = roofTarget;
                }
                return;
            }

            if (ComputeVoxelTarget(scenePos, Overwrites(_dragOperation), out Vector3I hitBlock, out Vector3I baseTarget, out Vector3I airTarget))
            {
                // Skip if the ray hits a block we just painted (for place) or if the
                // base target is one we already modified.
                if (baseTarget.Y == _dragBaseY
                    && !_lastPaintedBlocks.Contains(baseTarget)
                    && !_lastPaintedBlocks.Contains(hitBlock))
                {
                    StampAt(_history.Current, baseTarget, hitBlock, airTarget, _dragOperation);
                }
            }
        }
    }

    // Closes out whatever the left button was holding: the gizmo drag, the
    // pending region / roof footprint, then the edit they wrote into. Called on
    // release, and on anything else that takes the pointer away mid-stroke (the
    // fly cam captures it, so the release event never lands).
    private void FinishStroke()
    {
        if (_gizmoDrag != EGizmoHandle.None)
        {
            CommitGizmoDrag();
        }
        if (_regionAnchor.HasValue)
        {
            FillRegion(_history.Current, _regionAnchor.Value, _regionCurrent, _regionOperation);
        }
        if (_roofAnchor.HasValue)
        {
            // Opened here rather than on press: unlike a voxel stroke nothing is
            // written until release, so an aborted drag would otherwise leave an
            // empty edit for the history to drop.
            PlaceRoof(_history.Begin("Place Roof"), _roofAnchor.Value, _roofCurrent);
        }
        EndDrag();
        _history.Commit();
    }

    // Ends a stroke without committing it — the drag state and the open edit are
    // separate lifetimes, and undo needs to clear the former while the history
    // deals with the latter.
    private void EndDrag()
    {
        _dragActive = false;
        _dragOperation = EEditorBrushOperation.Paint;
        _regionAnchor = null;
        _roofAnchor = null;
        _placingEntity = null;
        _lastPaintedBlocks.Clear();
    }

    private void StepHistory(bool redo)
    {
        // A stroke still in flight would keep writing into an edit that the
        // history is about to close, so end it first.
        EndDrag();
        CancelGizmoDrag();
        // A step can add OR remove roofs, so diff the two sides: the region to
        // re-propagate is what the roofs that appeared or vanished covered. Most
        // steps touch no roof at all and skip the sun work entirely.
        Dictionary<RoofSimState, VoxelBox> roofsBefore = CollectRoofFootprints();
        EditorEdit edit = redo ? _history.Redo() : _history.Undo();
        string action = redo ? "Redo" : "Undo";
        GD.Print(edit != null ? $"{action}: {edit.Name}" : $"Nothing to {action.ToLowerInvariant()}");
        // Undoing a placement or a delete swaps out the very states the
        // selection points at, so drop the ones the world no longer holds.
        _selection.Prune(_worldState);
        RefreshRoofSunOcclusion(RegionOfDifference(roofsBefore, CollectRoofFootprints()));
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

        // First check whether the ray starts inside a solid block at the clip
        // plane. Trimesh colliders are single-sided, so a ray originating inside
        // geometry would pass through without any hit. Detect this case by
        // sampling the voxel at the ray/clip-plane intersection and, if it's
        // solid, synthesize a hit on the top of that block. With the clip off
        // the sampled cell is above every voxel, so this falls through.
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
                int voxel = _worldState.GetBlockWorld(vx, vy, vz);
                if (voxel != Blocks.AirId && !Blocks.IsWater(voxel))
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
            // No geometry under the mouse — resolve against the drafting plane
            // instead, so a blank world, a click out over open air, or a click
            // into a hollow already dug below the terrain all land on the
            // elevation the cutaway is parked at (and the ground sheet draws).
            if (!ResolvePlaneTarget(screenPos, _buildY, out Vector3I planeTarget))
            {
                return false;
            }
            hitBlock = planeTarget;
            airTarget = planeTarget;
            baseTarget = planeTarget;
            return true;
        }

        hitPos = (Vector3)result["position"];
        hitNormal = (Vector3)result["normal"];

        FinalizeTarget(hitPos, hitNormal, overwriteHitBlock, out hitBlock, out baseTarget, out airTarget);
        return true;
    }

    // The solid cell behind a surface hit — half a cell along -normal, so a
    // grazing hit lands in the block that was actually clicked rather than its
    // neighbour.
    private static Vector3I HitBlockOf(Vector3 hitPos, Vector3 hitNormal)
    {
        return new Vector3I(
            Mathf.FloorToInt(hitPos.X - hitNormal.X * 0.5f),
            Mathf.FloorToInt(hitPos.Y - hitNormal.Y * 0.5f),
            Mathf.FloorToInt(hitPos.Z - hitNormal.Z * 0.5f));
    }

    private static void FinalizeTarget(Vector3 hitPos, Vector3 hitNormal, bool overwriteHitBlock, out Vector3I hitBlock, out Vector3I baseTarget, out Vector3I airTarget)
    {
        hitBlock = HitBlockOf(hitPos, hitNormal);

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
            || shape == EEditorBrushShape.Fill
            || shape == EEditorBrushShape.Room;
    }

    // Only the drag-fill shapes have a base elevation to snap; the stamp shapes
    // take their height from the column's floor, so the toggle is meaningless
    // for them and the panel greys it out.
    public static bool SupportsPlateauSnap(EEditorBrushShape shape)
    {
        return IsRegionShape(shape);
    }

    // Seats the toggle on the selected shape's remembered state. WorldEditor owns
    // that state, so the HUD never has to carry it across a shape change.
    private void PushPlateauSnap()
    {
        editorHud.SetPlateauSnap(_plateauSnapByShape[(int)_brushShape], SupportsPlateauSnap(_brushShape));
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

    // The band boundary above a voxel level — the lowest clip height that leaves
    // that voxel fully revealed, landing on the same grid the plateau-snapped
    // brushes build to.
    private static int PlateauAbove(int y)
    {
        int step = Mathf.RoundToInt(GameCamera.PLATEAU_STEP);
        if (step <= 0)
        {
            return y + 1;
        }
        return SnapToPlateau(y) + step;
    }

    // Lowest cutaway height that leaves the whole world visible. Stands in for
    // "no clip" so every consumer keeps a finite number; CLIP_START_OFFSET is
    // the fallback for a world with no voxels at all (a new document).
    private int ClipCeiling()
    {
        int? highestY = _worldState?.GetHighestSolidVoxelY();
        return highestY.HasValue ? PlateauAbove(highestY.Value) : Mathf.CeilToInt(_cursorPosition.Y + CLIP_START_OFFSET);
    }

    // R/F walk the cutaway. A bare press steps a whole storey band — the common
    // case, and the grid the plateau-snapped brushes build to; Shift steps a
    // single course, for terrain, which doesn't sit on the storey grid at all.
    // Stepping up past the top of the world turns the cutaway off rather than
    // climbing into empty sky, so roofs are always one keystroke away.
    private void StepClip(bool up, bool fine)
    {
        int step = fine ? 1 : Mathf.RoundToInt(GameCamera.PLATEAU_STEP);
        int ceiling = ClipCeiling();
        if (_clipOff)
        {
            // Re-engaging drops straight to the world's top band; walking back
            // down from the ceiling one step at a time would take dozens of
            // presses on a tall world.
            if (!up)
            {
                SetClipY(ceiling - step);
            }
            return;
        }
        float next = _clipY + (up ? step : -step);
        if (up && next >= ceiling)
        {
            SetClipOff();
            return;
        }
        SetClipY(next);
    }

    // The drafting plane follows the cutaway, but lands on the FLOOR of the band
    // the cut reveals, not on the course directly under it: with the cut at 4 you
    // are standing in the storey spanning 0..3, so that storey's floor is y=0.
    // Keeping it on the band grid also keeps it shape-independent — a Voxel stamp
    // and a snapped Floor drop onto the same plane, which is the one the ground
    // sheet draws.
    //
    // A fine (Shift) step therefore only moves the drafting plane when it crosses
    // a band boundary. That's the intent: fine stepping is for seeing one course
    // more or less, not for drafting off the storey grid.
    private void SetClipY(float clipY)
    {
        _clipOff = false;
        _clipY = clipY;
        _buildY = SnapToPlateau(Mathf.FloorToInt(clipY) - 1);
    }

    // _buildY deliberately survives — turning the roof back on to look at it
    // shouldn't move where the next brush stroke lands.
    private void SetClipOff()
    {
        _clipOff = true;
        _clipY = ClipCeiling();
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
    private void StampAt(EditorEdit edit, Vector3I baseTarget, Vector3I hitBlock, Vector3I airTarget, EEditorBrushOperation operation)
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
        PaintCells(edit, cells, operation);
        // Window / Door write at the column's floor, not at baseTarget, so the
        // drag's "already did this one" guard needs the target logged too.
        _lastPaintedBlocks.Add(baseTarget);
    }

    // Commits a Floor / Wall / Fill / Room drag.
    private void FillRegion(EditorEdit edit, Vector3I anchor, Vector3I current, EEditorBrushOperation operation)
    {
        BuildRegionCells(anchor, current, out Vector3I min, out Vector3I max);
        // Erasing a room means clearing the whole volume, so only a Room that's
        // building anything carves itself hollow.
        bool hollow = _brushShape == EEditorBrushShape.Room && operation != EEditorBrushOperation.Erase;
        var cells = new List<Vector3I>();
        var interiorCells = new List<Vector3I>();
        for (int x = min.X; x <= max.X; x++)
        {
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                {
                    // A Room is a shell: the bottom course is a solid floor slab,
                    // everything above it keeps only the perimeter walls.
                    bool interior = y > min.Y && x > min.X && x < max.X && z > min.Z && z < max.Z;
                    if (hollow && interior)
                    {
                        interiorCells.Add(new Vector3I(x, y, z));
                        continue;
                    }
                    cells.Add(new Vector3I(x, y, z));
                }
            }
        }
        PaintCells(edit, cells, operation);
        // The room's inside is cleared, not merely skipped, so dragging one into
        // a hillside (or over existing geometry) hollows out a space to stand in.
        if (hollow)
        {
            PaintCells(edit, interiorCells, EEditorBrushOperation.Erase);
        }
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

        // Floor, Fill and Room share one XZ footprint; only the extrusion differs.
        // A Room hangs its floor slab one course BELOW the anchor so its walls
        // and its interior fill exactly the cutaway band the anchor snapped to,
        // the slab being the ceiling course of the band underneath — which is
        // how storeys stack. Sitting the slab ON the anchor instead would leave
        // the top course of the walls in the next band up, and the cutaway lops
        // that course off while you're standing in the room.
        int baseY = _brushShape == EEditorBrushShape.Room ? anchor.Y - 1 : anchor.Y;
        int height = _brushShape switch
        {
            EEditorBrushShape.Fill => fillHeight,
            EEditorBrushShape.Room => wallHeight + 1,
            _ => 1,
        };
        min = new Vector3I(Math.Min(anchor.X, current.X), baseY, Math.Min(anchor.Z, current.Z));
        max = new Vector3I(Math.Max(anchor.X, current.X), baseY + height - 1, Math.Max(anchor.Z, current.Z));
    }

    // ----- Roofs -----------------------------------------------------------

    private RoofStyleData CurrentRoofStyle =>
        _roofStyleIndex >= 0 && _roofStyleIndex < _roofStyles.Count ? _roofStyles[_roofStyleIndex] : null;

    // Inclusive cell bounds of a roof drag, as a continuous footprint. A cell
    // covers [x, x+1), so a one-cell drag is a 1m roof and `center` lands on the
    // cell's middle rather than its corner. Y is the anchor's, i.e. the eave.
    private static void BuildRoofFootprint(Vector3I anchor, Vector3I current, out Vector3 center, out float sizeX, out float sizeZ)
    {
        int minX = Math.Min(anchor.X, current.X);
        int maxX = Math.Max(anchor.X, current.X);
        int minZ = Math.Min(anchor.Z, current.Z);
        int maxZ = Math.Max(anchor.Z, current.Z);
        sizeX = maxX - minX + 1;
        sizeZ = maxZ - minZ + 1;
        center = new Vector3(minX + sizeX * 0.5f, anchor.Y, minZ + sizeZ * 0.5f);
    }

    // Picks the roof under the cursor as the retune target and pushes the panel
    // onto it. It STAYS the target, so every later panel change re-pushes on its
    // own — retuning a pitch is a slider drag, not a click per value.
    private void SelectRoofForEdit(Vector2 screenPos)
    {
        Node3D picked = PickEntityAt(screenPos, out _);
        _editingRoof = null;
        if (picked == null || !TryFindEntityState(picked, out EntitySimState state, out Vector3I bucket)
            || state is not RoofSimState roof)
        {
            return;
        }
        _editingRoof = roof;
        _editingRoofBucket = bucket;
        PushRoofSettings();
    }

    // Rebuilds the retune target with the panel's current seam / form / slope /
    // brokenness. Called for every roof-panel change and no-ops unless a roof is
    // being edited, so no handler has to know whether one is.
    //
    // The shape fields are readonly and the mesh is regenerated from them, so
    // this swaps in a new state carrying the same footprint rather than mutating
    // in place — then re-spawns and re-stamps exactly as a placement does.
    private void PushRoofSettings()
    {
        if (!EditingRoofIsLive())
        {
            return;
        }
        RoofSimState roof = _editingRoof;
        // Nothing to do when the panel already matches — a re-pressed toggle
        // would otherwise cost an undo slot and a chunk reload for no change.
        if (roof.SeamAxis == _roofSeamAxis && roof.Form == _roofForm
            && Mathf.IsEqualApprox(roof.SlopeDegrees, _roofSlopeDegrees)
            && Mathf.IsEqualApprox(roof.Broken, _roofBroken))
        {
            return;
        }
        List<EntitySimState> entities = _worldState.GetEntities(_editingRoofBucket);
        int index = entities.IndexOf(roof);

        EditorEdit edit = _history.Begin("Edit Roof");
        edit?.TouchEntityChunk(_editingRoofBucket);

        var replacement = new RoofSimState(
            roof.WorldPosition, roof.Style, roof.SizeX, roof.SizeZ,
            _roofSeamAxis, _roofForm, _roofSlopeDegrees, _roofBroken)
        {
            RotationY = roof.RotationY,
        };
        entities[index] = replacement;
        // The panel keeps pointing at the roof, not at the state object it
        // happened to have when it was picked.
        _editingRoof = replacement;
        Node3D node = roof.RuntimeNode;
        if (node != null && IsInstanceValid(node))
        {
            _world.RemoveEntity(node);
            node.QueueFree();
        }
        ReloadChunkEntities(_editingRoofBucket);
        // Re-styling changes the footprint, so both shapes need re-propagating.
        RefreshRoofSunOcclusion(RoofSunStamper.FootprintBox(roof).Union(RoofSunStamper.FootprintBox(replacement)));
        _history.Commit();
    }

    // Whether a roof is still under the panel's control. A delete or an undo can
    // swap out the very state it points at, so the target is dropped the moment
    // it's no longer filed in the world.
    private bool EditingRoofIsLive()
    {
        if (_editingRoof == null)
        {
            return false;
        }
        List<EntitySimState> entities = _worldState.GetEntities(_editingRoofBucket);
        if (entities == null || !entities.Contains(_editingRoof))
        {
            _editingRoof = null;
            return false;
        }
        return true;
    }

    // Commits a roof footprint drag. Like every other entity placement this goes
    // through the streaming path rather than instantiating directly, so the
    // roof lands in Sim.ActiveEntities and gets its RuntimeNode back-reference —
    // without which the editor couldn't pick, move or delete it.
    private void PlaceRoof(EditorEdit edit, Vector3I anchor, Vector3I current)
    {
        RoofStyleData style = CurrentRoofStyle;
        if (style == null)
        {
            GD.PushWarning("WorldEditor: no roof style selected (is the brush palette's roofLibrary wired?); nothing placed.");
            return;
        }
        BuildRoofFootprint(anchor, current, out Vector3 center, out float sizeX, out float sizeZ);
        var simState = new RoofSimState(center, style, sizeX, sizeZ, _roofSeamAxis, _roofForm, _roofSlopeDegrees, _roofBroken);
        edit?.TouchEntitiesAt(center);
        _worldState.AddEntity(simState);
        ReloadChunkEntities(Sim.WorldToChunkCoord(center));
        RefreshRoofSunOcclusion(RoofSunStamper.FootprintBox(simState));
    }

    // Rebuilds sun occlusion over the region a roof edit covers and relights it,
    // so a roof shades the volumetrics as soon as it is placed instead of only
    // after a reload. `region` must span the footprint BOTH before and after the
    // edit — a delete or a move leaves columns that still need re-propagating
    // even though nothing covers them now.
    //
    // Every pass here is regional. Doing this world-wide was affordable when
    // only roof placement paid for it, but undo runs it too, so a one-voxel
    // brush undo was costing a full relight plus a full enclosure flood — and
    // the world-wide re-derive silently overwrote authored enclosure everywhere.
    private void RefreshRoofSunOcclusion(VoxelBox region)
    {
        if (region.IsEmpty)
        {
            return;
        }
        // The stamp is add-only with no way to subtract one roof, so the region
        // is cleared and rebuilt; sunlight is then recomputed rather than
        // propagated incrementally, because the incremental path keys off VOXEL
        // changes and marking a column opaque changes no voxel.
        FoliageStamper.RestampRegion(_worldState, region);
        LightEngine.RelightRegion(_worldState, region);
        // Block light reads the same cover (LightEngine.IsOpenForLight), and a
        // roof edit changes no voxel, so nothing else would re-derive the
        // torches under it.
        LightEngine.RefloodSourcesIn(_worldState, region);
        // Roofs are cover, so placing or breaking one changes enclosure —
        // re-derive rather than leaving the ambience on the pre-edit bake.
        EnvTagGen.ComputeEnvTagGrid(_worldState, InteriornessGen.ComputeRegion(_worldState, region));
        // Re-mesh as well as relight. Terrain takes its sun from a PER-VERTEX
        // bake frozen into the chunk mesh (ChunkMesherDC.BakeVertexSun), not
        // from the light volume — relighting alone updates props and volumetrics
        // while the floor under the roof stays exactly as bright as it was.
        // RelightRegion leaves SunlightChunkDirty naming exactly what moved.
        _world.RebuildChunkMeshes(EditorRefresh.GrowByOne(_worldState.SunlightChunkDirty));
        _world.FlushLighting();
    }

    // What every roof in the world currently covers. Cheap enough to run on both
    // sides of an undo step (arithmetic only, no rasterization), which is how
    // the history path learns whether a step touched cover at all.
    private Dictionary<RoofSimState, VoxelBox> CollectRoofFootprints()
    {
        var roofs = new Dictionary<RoofSimState, VoxelBox>();
        foreach (List<EntitySimState> bucket in _worldState._entities.Values)
        {
            foreach (EntitySimState state in bucket)
            {
                if (state is RoofSimState roof)
                {
                    roofs[roof] = RoofSunStamper.FootprintBox(roof);
                }
            }
        }
        return roofs;
    }

    // Footprint spanning every roof an undo step added, removed or MOVED —
    // compared by what each covers, not by identity, because undoing a gizmo
    // drag puts the same state object back at a different position. A re-style
    // shows up as an add plus a remove; it swaps the object rather than mutating
    // it.
    private static VoxelBox RegionOfDifference(Dictionary<RoofSimState, VoxelBox> before, Dictionary<RoofSimState, VoxelBox> after)
    {
        VoxelBox region = VoxelBox.Empty;
        foreach (KeyValuePair<RoofSimState, VoxelBox> kvp in before)
        {
            if (!after.TryGetValue(kvp.Key, out VoxelBox now) || !now.Equals(kvp.Value))
            {
                region = region.Union(kvp.Value);
            }
        }
        foreach (KeyValuePair<RoofSimState, VoxelBox> kvp in after)
        {
            if (!before.TryGetValue(kvp.Key, out VoxelBox was) || !was.Equals(kvp.Value))
            {
                region = region.Union(kvp.Value);
            }
        }
        return region;
    }

    // First empty cell above the ground in a column, searched downward from
    // `from`. Falls back to the starting cell when the column has no floor
    // within reach, so a brush over open air still writes somewhere sensible.
    private int FindFloorY(Vector3I from)
    {
        for (int dy = 0; dy <= floorSearchDepth; dy++)
        {
            int y = from.Y - dy;
            if (Blocks.IsSolid(_worldState.GetBlockWorld(from.X, y, from.Z)))
            {
                return y + 1;
            }
        }
        return from.Y;
    }

    // Auto's value is never read — PaintCells routes it to the overload that
    // resolves the material's own default instead.
    private static SharpAxes ShapeFor(EEditorVoxelEdges edges)
    {
        return edges switch
        {
            EEditorVoxelEdges.Blocky => SharpAxes.All,
            EEditorVoxelEdges.Stepped => SharpAxes.Y,
            EEditorVoxelEdges.Smooth => SharpAxes.None,
            _ => SharpAxes.None,
        };
    }

    // Writes one brush's worth of cells and rebuilds. Cells at or above the clip
    // plane are dropped rather than aborting the brush — a tall shape whose top
    // pokes through the cutaway still lays down the part you can see.
    private void PaintCells(EditorEdit edit, List<Vector3I> cells, EEditorBrushOperation operation)
    {
        int clipFloor = Mathf.FloorToInt(_clipY);
        bool erasing = operation == EEditorBrushOperation.Erase;
        VoxelBrush brush = _voxelBrushes[_voxelTypeIndex];
        int type = erasing ? Blocks.AirId : brush.BlockId;
        // Air has no edges to shape, and Auto defers to the shape-less overload,
        // which keeps whatever a repainted cell already carried.
        bool explicitShape = !erasing && _voxelEdges != EEditorVoxelEdges.Auto;
        SharpAxes shape = ShapeFor(_voxelEdges);
        var changed = new List<Vector3I>();

        foreach (Vector3I target in cells)
        {
            if (target.Y >= clipFloor)
            {
                continue;
            }
            edit?.TouchVoxel(target);
            if (explicitShape)
            {
                _worldState.SetBlockWorld(target.X, target.Y, target.Z, type, shape);
            }
            else
            {
                _worldState.SetBlockWorld(target.X, target.Y, target.Z, type);
            }
            changed.Add(target);
            _lastPaintedBlocks.Add(target);
        }

        if (changed.Count > 0)
        {
            var refresh = new EditorRefresh();
            refresh.AddVoxels(changed);
            refresh.Apply(_world);
        }
    }

    // Returns the state a place-click stamped so the caller can keep aiming it
    // while the button is held; null for a delete, a miss, or a brush that
    // writes no entity of its own.
    private EntitySimState HandleEntityClick(EditorEdit edit, Vector2 screenPos, bool delete)
    {
        // Deleting picks the entity under the cursor directly. Placing still
        // goes through the terrain raycast — it needs a surface, not an entity.
        if (delete)
        {
            Node3D picked = PickEntityAt(screenPos, out _);
            if (picked != null)
            {
                DeletePickedEntity(edit, picked);
            }
            return null;
        }

        var result = Raycast(screenPos);
        if (result.Count == 0)
        {
            return null;
        }

        // The hit is already on the terrain's visible surface (the collider is
        // the meshed surface, smoothing included), and entity scenes anchor at
        // their base — so the raw hit IS the anchor. Don't offset along the
        // normal the way the voxel path does; that lifts props off the ground.
        var hitPos = (Vector3)result["position"];
        Vector3 seat = TryApertureSeat(hitPos, (Vector3)result["normal"], out Vector3 apertureSeat)
            ? apertureSeat
            : SnapPosition(hitPos);
        return PlaceEntity(edit, seat);
    }

    // An aperture prop (a window frame) IS the hole, so it seats in the wall cell
    // it was clicked onto rather than on the face of it — both because that's
    // where a window belongs, and because the cell it carves is derived from
    // where it stands (PropSimState.ResolveStamp). Centred horizontally, sitting
    // on the cell's floor; grid snap is skipped because the cell IS the snap.
    private bool TryApertureSeat(Vector3 hitPos, Vector3 hitNormal, out Vector3 seat)
    {
        seat = default;
        if (_entityTypeIndex < 0 || _entityTypeIndex >= _entityBrushes.Count)
        {
            return false;
        }
        PropLibraryEntry prop = _entityBrushes[_entityTypeIndex].Prop;
        if (prop?.scene == null || PropInstance.GetApertureHeight(prop.scene) <= 0)
        {
            return false;
        }
        const float CELL_MIDPOINT = 0.5f;
        Vector3I cell = HitBlockOf(hitPos, hitNormal);
        seat = new Vector3(cell.X + CELL_MIDPOINT, cell.Y, cell.Z + CELL_MIDPOINT);
        return true;
    }

    // ----- Entity snapping -------------------------------------------------

    // All three axes: on an interior floor the height snap is a no-op (floors
    // sit at whole metres) and out on terrain it keeps a row of props level
    // with each other rather than each riding its own bump.
    private Vector3 SnapPosition(Vector3 position)
    {
        return _snapToGrid && entityGridSnap > 0f
            ? position.Snapped(Vector3.One * entityGridSnap)
            : position;
    }

    // A drag delta snapped about where it started, so `origin` lands exactly on
    // the grid however far off it began.
    private float SnapDelta(float origin, float delta)
    {
        return _snapToGrid && entityGridSnap > 0f
            ? Mathf.Snapped(origin + delta, entityGridSnap) - origin
            : delta;
    }

    private float SnapRotation(float radians)
    {
        return _snapRotation && entityRotationSnapDegrees > 0f
            ? Mathf.Snapped(radians, Mathf.DegToRad(entityRotationSnapDegrees))
            : radians;
    }

    // Same idea as SnapDelta, referenced off the first selected entity's own
    // facing: a lone selection lands exactly on an increment, and a group turns
    // by one common angle so its internal arrangement survives.
    private float SnapRotationDelta(float rotation)
    {
        if (_gizmoDragStart.Count == 0)
        {
            return rotation;
        }
        float anchor = _gizmoDragStart[0].RotationY;
        return SnapRotation(anchor + rotation) - anchor;
    }

    // Motion while the placing button is still held turns the entity to face the
    // cursor. Under the deadzone the direction from the drop point is noise, so
    // the facing it was placed with stands.
    private void UpdatePlaceAim(Vector2 screenPos)
    {
        Vector3 rayOrigin = camera.ProjectRayOrigin(screenPos);
        Vector3 rayDir = camera.ProjectRayNormal(screenPos);
        Vector3 anchor = _placingEntity.WorldPosition;
        if (!EditorGizmo.RayPlaneY(rayOrigin, rayDir, anchor.Y, out Vector3 planeHit))
        {
            return;
        }
        var offset = new Vector2(planeHit.X - anchor.X, planeHit.Z - anchor.Z);
        if (offset.Length() < placeAimDeadzone)
        {
            return;
        }
        // Atan2(x, z) is the project's yaw convention, so the entity's front
        // ends up pointing at the cursor.
        _placingEntity.RotationY = SnapRotation(Mathf.Atan2(offset.X, offset.Y));
        Node3D node = _placingEntity.RuntimeNode;
        if (node != null && IsInstanceValid(node))
        {
            _placingEntity.SeatTransform(node);
        }
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
                // you can't see would delete things off-screen. Roofs are exempt
                // from that hide (they clip in-shader so their shadow survives),
                // so they need the elevation test applied directly or a cut-away
                // roof stays clickable.
                if (!IsInstanceValid(entity) || !entity.Visible
                    || entity.GlobalPosition.Y >= camera.Clip)
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
    private void DeletePickedEntity(EditorEdit edit, Node3D picked)
    {
        if (!TryFindEntityState(picked, out EntitySimState state, out Vector3I bucket))
        {
            GD.PushWarning($"WorldEditor: picked entity '{picked.Name}' has no sim state filed near {Sim.WorldToChunkCoord(picked.GlobalPosition)}; nothing deleted.");
            return;
        }
        DeleteEntityState(edit, state, bucket);
    }

    // Drops one entity's state and its live node. `bucket` is the chunk the
    // state is actually filed under, which isn't necessarily the one its
    // position maps to (a mob that walked over a boundary, an entity mid-move).
    private void DeleteEntityState(EditorEdit edit, EntitySimState state, Vector3I bucket)
    {
        // Captured before the removal: once the roof is gone its columns can't
        // be enumerated, and they're exactly the ones needing re-propagation.
        VoxelBox roofRegion = state is RoofSimState roof ? RoofSunStamper.FootprintBox(roof) : VoxelBox.Empty;
        edit?.TouchEntityChunk(bucket);
        _worldState.GetEntities(bucket)?.Remove(state);
        Node3D node = state.RuntimeNode;
        if (node != null && IsInstanceValid(node))
        {
            _world.RemoveEntity(node);
            node.QueueFree();
        }
        RefreshRoofSunOcclusion(roofRegion);
    }

    // ----- Entity selection ------------------------------------------------

    // A plain click selects just what's under the cursor (and clears the group
    // when that's nothing); Shift adds it if it wasn't in the group and removes
    // it if it was. Shift on empty space leaves the group alone — dropping it
    // there would make a near-miss while building up a selection destructive.
    private void HandleSelectClick(Vector2 screenPos, bool shift)
    {
        Node3D picked = PickEntityAt(screenPos, out _);
        if (picked == null || !TryFindEntityState(picked, out EntitySimState state, out _))
        {
            if (!shift)
            {
                _selection.Clear();
            }
            return;
        }
        if (shift)
        {
            _selection.Toggle(state);
        }
        else
        {
            _selection.SetSingle(state);
        }
    }

    // Ctrl+D. Copies the selection in place and hands the selection to the
    // copies, so the gizmo drag that usually follows moves the new entities and
    // leaves the originals where they were.
    private void DuplicateSelection()
    {
        List<EntitySimState> copies = EntitySerializer.CloneList(_selection.States);
        if (copies.Count == 0)
        {
            return;
        }

        EditorEdit edit = _history.Begin(copies.Count > 1 ? $"Duplicate {copies.Count} Entities" : "Duplicate Entity");
        var refresh = new EditorRefresh();
        VoxelBox roofRegion = VoxelBox.Empty;
        foreach (EntitySimState copy in copies)
        {
            Vector3 position = copy.WorldPosition;
            edit?.TouchEntitiesAt(position);
            _worldState.AddEntity(copy);
            refresh.AddEntityChunk(Sim.WorldToChunkCoord(position));
            if (copy is RoofSimState roof)
            {
                roofRegion = roofRegion.Union(RoofSunStamper.FootprintBox(roof));
            }
        }
        // Respawn before selecting: the copies only get their RuntimeNode
        // back-reference from the streaming path, and without it the gizmo has
        // nothing to draw over and picking can't find them again.
        refresh.Apply(_world);
        RefreshRoofSunOcclusion(roofRegion);
        _selection.SetMany(copies);
        _history.Commit();
    }

    private void DeleteSelection()
    {
        EditorEdit edit = _history.Begin(_selection.Count > 1 ? $"Delete {_selection.Count} Entities" : "Delete Entity");
        // Copy first: DeleteEntityState mutates the buckets the selection is
        // pruned against, and the selection itself is cleared below.
        var doomed = new List<EntitySimState>(_selection.States);
        foreach (EntitySimState state in doomed)
        {
            if (TryFindEntityState(state.RuntimeNode, out _, out Vector3I bucket))
            {
                DeleteEntityState(edit, state, bucket);
            }
            else
            {
                // No live node to locate it by (culled above the clip plane, or
                // its chunk streamed out mid-selection) — fall back to the
                // bucket its position maps to.
                DeleteEntityState(edit, state, Sim.WorldToChunkCoord(state.WorldPosition));
            }
        }
        _selection.Clear();
        _history.Commit();
    }

    // ----- Gizmo drag ------------------------------------------------------

    // True when the press landed on a handle and a transform drag is now open.
    private bool TryBeginGizmoDrag(Vector2 screenPos)
    {
        if (_selection.IsEmpty || gizmo == null)
        {
            return false;
        }
        Vector3 pivot = _selection.Pivot;
        Vector3 rayOrigin = camera.ProjectRayOrigin(screenPos);
        Vector3 rayDir = camera.ProjectRayNormal(screenPos);
        EGizmoHandle handle = gizmo.Pick(pivot, rayOrigin, rayDir);
        if (handle == EGizmoHandle.None)
        {
            return false;
        }

        _gizmoDrag = handle;
        _gizmoDragPivot = pivot;
        _gizmoDragStart.Clear();
        // One edit for the whole drag, opened here and committed on release.
        EditorEdit edit = _history.Begin(handle == EGizmoHandle.Rotate ? "Rotate Entities" : "Move Entities");
        foreach (EntitySimState state in _selection.States)
        {
            _gizmoDragStart.Add(new SelectedTransform(state));
            edit?.TouchEntityTransform(state);
        }

        EditorGizmo.RayPlaneY(rayOrigin, rayDir, pivot.Y, out _gizmoDragStartPlaneHit);
        gizmo.TryVerticalY(pivot, rayOrigin, rayDir, out _gizmoDragStartY);
        gizmo.TryRotateAngle(pivot, rayOrigin, rayDir, out _gizmoDragStartAngle);
        return true;
    }

    // Re-derives every selected transform from the one captured at press time,
    // so the drag can't accumulate rounding drift and returning the cursor to
    // where it started restores the original transforms exactly.
    private void UpdateGizmoDrag(Vector2 screenPos)
    {
        Vector3 rayOrigin = camera.ProjectRayOrigin(screenPos);
        Vector3 rayDir = camera.ProjectRayNormal(screenPos);
        Vector3 translation = Vector3.Zero;
        float rotation = 0f;

        switch (_gizmoDrag)
        {
            case EGizmoHandle.Ground:
                if (!EditorGizmo.RayPlaneY(rayOrigin, rayDir, _gizmoDragPivot.Y, out Vector3 planeHit))
                {
                    return;
                }
                translation = planeHit - _gizmoDragStartPlaneHit;
                // Snapped about the pivot — a lone entity (whose pivot is its
                // own position) lands on the grid, and a group moves by one
                // common delta so its internal arrangement survives.
                translation = new Vector3(
                    SnapDelta(_gizmoDragPivot.X, translation.X),
                    0f,
                    SnapDelta(_gizmoDragPivot.Z, translation.Z));
                break;
            case EGizmoHandle.Vertical:
                if (!gizmo.TryVerticalY(_gizmoDragPivot, rayOrigin, rayDir, out float y))
                {
                    return;
                }
                translation = new Vector3(0f, SnapDelta(_gizmoDragPivot.Y, y - _gizmoDragStartY), 0f);
                break;
            case EGizmoHandle.Rotate:
                if (!gizmo.TryRotateAngle(_gizmoDragPivot, rayOrigin, rayDir, out float angle))
                {
                    return;
                }
                rotation = SnapRotationDelta(Mathf.AngleDifference(_gizmoDragStartAngle, angle));
                break;
            default:
                return;
        }

        foreach (SelectedTransform start in _gizmoDragStart)
        {
            // Rotation orbits each entity about the pivot as well as turning it,
            // so a table and its chairs swing round together rather than each
            // spinning where it stands. For a single entity the orbit is a no-op.
            Vector3 position = start.Position;
            if (rotation != 0f)
            {
                position = _gizmoDragPivot + (position - _gizmoDragPivot).Rotated(Vector3.Up, rotation);
            }
            start.State.WorldPosition = position + translation;
            start.State.RotationY = start.RotationY + rotation;
            // Move the live node directly rather than respawning the chunk every
            // frame — the entity buckets are only re-filed once, on release.
            Node3D node = start.State.RuntimeNode;
            if (node != null && IsInstanceValid(node))
            {
                start.State.SeatTransform(node);
            }
        }
    }

    // Re-files anything that crossed a chunk boundary and respawns the affected
    // chunks, so the nodes end up owned by the chunk they now sit in.
    private void CommitGizmoDrag()
    {
        var refresh = new EditorRefresh();
        EditorEdit edit = _history.Current;

        // Snapshot every destination bucket before touching any of them: with
        // two entities swapping chunks, capturing one bucket's "before" after
        // the other has already been re-filed would record a mutated state.
        foreach (SelectedTransform start in _gizmoDragStart)
        {
            Vector3I from = Sim.WorldToChunkCoord(start.Position);
            Vector3I to = Sim.WorldToChunkCoord(start.State.WorldPosition);
            if (from != to)
            {
                edit?.TouchEntityChunk(to);
            }
        }
        // A roof that moved covers different columns at each end of the drag, and
        // its cover is stamped, not derived from the node — so both need
        // re-propagating once the states are settled.
        VoxelBox roofRegion = VoxelBox.Empty;
        foreach (SelectedTransform start in _gizmoDragStart)
        {
            Vector3I from = Sim.WorldToChunkCoord(start.Position);
            Vector3I to = Sim.WorldToChunkCoord(start.State.WorldPosition);
            refresh.AddEntityChunk(from);
            if (start.State is RoofSimState roof)
            {
                roofRegion = roofRegion
                    .Union(RoofSunStamper.FootprintBox(roof).Union(FootprintBoxAt(roof, start.Position)));
            }
            if (from == to)
            {
                continue;
            }
            // Pull it out of the bucket it is actually filed under — the
            // position has already moved, so RemoveEntity would look in the
            // wrong chunk and quietly find nothing.
            _worldState.GetEntities(from)?.Remove(start.State);
            _worldState.AddEntity(start.State);
            refresh.AddEntityChunk(to);
        }

        _gizmoDrag = EGizmoHandle.None;
        _gizmoDragStart.Clear();
        refresh.Apply(_world);
        RefreshRoofSunOcclusion(roofRegion);
    }

    // The footprint a roof HAD, by measuring it at a position it no longer
    // occupies. Restoring the position is the only way to ask — the footprint
    // depends on style, size, form and rotation as well as where it sits.
    private static VoxelBox FootprintBoxAt(RoofSimState roof, Vector3 position)
    {
        Vector3 current = roof.WorldPosition;
        roof.WorldPosition = position;
        VoxelBox box = RoofSunStamper.FootprintBox(roof);
        roof.WorldPosition = current;
        return box;
    }

    // Abandons a drag without committing it — used when the mode changes out
    // from under it. The transforms already written stay; the open edit is the
    // history's business.
    private void CancelGizmoDrag()
    {
        _gizmoDrag = EGizmoHandle.None;
        _gizmoDragStart.Clear();
        _hotHandle = EGizmoHandle.None;
    }

    // The sim state behind a picked node, and the chunk bucket holding it. Every
    // spawned entity's state carries a RuntimeNode back-reference, so match on
    // that rather than re-searching by position — proximity picks whatever state
    // happens to sit nearest, which is the wrong one wherever entities overlap
    // and finds nothing at all if a node sits even slightly off its authored
    // position. The 3x3x3 sweep covers a state filed in a neighbouring bucket.
    private bool TryFindEntityState(Node3D picked, out EntitySimState found, out Vector3I bucket)
    {
        found = null;
        bucket = default;
        if (picked == null)
        {
            return false;
        }
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
                        if (state.RuntimeNode == picked)
                        {
                            found = state;
                            bucket = coord;
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    // World-space bounds of an entity's visuals, expanded to at least
    // minimumPickExtents so a flat or mesh-less entity is still clickable.
    private Aabb WorldBoundsOf(Node3D entity)
    {
        Aabb bounds = VisualBounds.Of(entity) ?? new Aabb(entity.GlobalPosition, Vector3.Zero);
        Vector3 grow = new Vector3(
            Mathf.Max(0f, minimumPickExtents.X - bounds.Size.X * 0.5f),
            Mathf.Max(0f, minimumPickExtents.Y - bounds.Size.Y * 0.5f),
            Mathf.Max(0f, minimumPickExtents.Z - bounds.Size.Z * 0.5f));
        return new Aabb(bounds.Position - grow, bounds.Size + grow * 2f);
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

    // The state that was placed, or null when the brush wrote no entity of its
    // own (the player spawn, an unconfigured palette slot).
    private EntitySimState PlaceEntity(EditorEdit edit, Vector3 position)
    {
        if (_entityTypeIndex < 0 || _entityTypeIndex >= _entityBrushes.Count)
        {
            return null;
        }
        EntityBrush brush = _entityBrushes[_entityTypeIndex];

        if (brush.Kind == EEditorEntityKind.PlayerSpawn)
        {
            edit?.TouchSpawn();
            _worldState.Spawn = position;
            GD.Print($"Player spawn set to {position}");
            return null;
        }

        EntitySimState simState = CreateEntitySimState(brush, position);
        if (simState == null)
        {
            return null;
        }

        edit?.TouchEntitiesAt(position);
        _worldState.AddEntity(simState);
        // Before the spawn: an entity that stamps voxels on spawn (a door) would
        // otherwise write them itself, outside the undo step and without a
        // rebuild.
        StampEntityVoxels(edit, simState);
        // Spawn through the normal streaming path instead of instantiating here.
        // A directly-created node is never filed in Sim.ActiveEntities and never
        // gets its state's RuntimeNode back-reference set, so it's invisible to
        // every consumer that walks the active set — culling, eviction, and the
        // editor's own entity picking.
        ReloadChunkEntities(Sim.WorldToChunkCoord(position));
        return simState;
    }

    // Voxels an entity owns (a window frame's aperture, a door's occluder) are
    // reconciled at world load by EntityVoxelStamper, which is far too late for
    // the author who just placed one — so the editor applies the same stamp on
    // the spot. Touching the cells first is what puts them in the current undo
    // step, and the refresh is what makes a carved aperture actually appear:
    // unlike the load pass, this one runs after the chunk has a mesh.
    private void StampEntityVoxels(EditorEdit edit, EntitySimState state)
    {
        if (state is not IVoxelStamper stamper)
        {
            return;
        }
        VoxelStamp stamp = stamper.ResolveStamp(_worldState);
        if (!stamp.Any)
        {
            return;
        }
        var cells = new List<Vector3I>();
        EntityVoxelStamper.Cells(stamp, cells);
        foreach (Vector3I cell in cells)
        {
            edit?.TouchVoxel(cell);
        }

        var changed = new List<Vector3I>();
        EntityVoxelStamper.Apply(_worldState, stamp, changed);
        if (changed.Count > 0)
        {
            var refresh = new EditorRefresh();
            refresh.AddVoxels(changed);
            refresh.Apply(_world);
        }
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
            case EEditorEntityKind.DartTrap:
                return brushPalette?.dartTrapScene != null
                    ? new TrapSimState(position, brushPalette.dartTrapScene)
                    : null;
            case EEditorEntityKind.Trapdoor:
                return brushPalette?.trapdoorScene != null
                    ? new TrapdoorSimState(position, 0f, brushPalette.trapdoorScene)
                    : null;
            case EEditorEntityKind.LinkedTrapdoor:
                return brushPalette?.trapdoorScene != null
                    ? new TrapdoorSimState(position, 0f, brushPalette.trapdoorScene) { LinkTag = brush.Tag }
                    : null;
            case EEditorEntityKind.TrapdoorTrap:
                return brushPalette?.trapdoorTrapScene != null
                    ? new TrapSimState(position, brushPalette.trapdoorTrapScene) { HazardRadius = TrapSimState.DefaultHazardRadius }
                    : null;
            case EEditorEntityKind.CrumblingFloor:
                return brushPalette?.crumblingFloorScene != null
                    ? new TrapSimState(position, brushPalette.crumblingFloorScene) { HazardRadius = TrapSimState.DefaultHazardRadius }
                    : null;
            case EEditorEntityKind.Lever:
                return brushPalette?.leverScene != null
                    ? new LeverSimState(position, 0f, brushPalette.leverScene) { TargetLinkTag = brush.Tag }
                    : null;
            case EEditorEntityKind.Campfire:
                // Always unlit: lighting one douses every other, so a placed
                // campfire must not steal the world's lit one at load.
                return brushPalette?.campfireScene != null
                    ? new CampfireSimState(position, brushPalette.campfireScene) { HazardRadius = CampfireSimState.DefaultHazardRadius }
                    : null;
            case EEditorEntityKind.Forge:
                // Slot/level come from the palette until the tool grows a picker;
                // None derives a stable slot from the position, as worldgen does.
                return brushPalette?.forgeScene != null
                    ? new ForgeSimState(
                        position,
                        brushPalette.forgeScene,
                        brushPalette.forgeLevel,
                        brushPalette.forgeSlot != EUpgradeSlot.None ? brushPalette.forgeSlot : ForgeOffer.SlotFor(position))
                    : null;
            case EEditorEntityKind.Well:
                return brushPalette?.wellScene != null
                    ? new WellSimState(position, brushPalette.wellScene)
                    : null;
            // Two brushes rather than one plus a picker: which resource a fountain
            // refills is an [Export] on the scene's Fountain node, so the scene IS
            // the variant.
            case EEditorEntityKind.HealingFountain:
                return brushPalette?.healingFountainScene != null
                    ? new FountainSimState(position, brushPalette.healingFountainScene)
                    : null;
            case EEditorEntityKind.ManaFountain:
                return brushPalette?.manaFountainScene != null
                    ? new FountainSimState(position, brushPalette.manaFountainScene)
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
            // The brush IS the pool — one button per tag, so what gets placed is
            // decided by which marker brush is selected.
            case EEditorEntityKind.Marker:
                return brushPalette?.markerScene != null
                    ? new MarkerSimState(position, brush.Tag, brushPalette.markerScene)
                    : null;
            // Likewise one button per hint name — stand it in the doorway (or in
            // the gap in the square's wall) and worldgen brings a path to it.
            case EEditorEntityKind.PathHint:
                return brushPalette?.pathHintScene != null
                    ? new PathHintSimState(position, brush.Tag, brushPalette.pathHintScene)
                    : null;
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

        // Start the ray just below the clip plane so we don't hit collision
        // geometry above the clip that was culled visually.
        if (rayDir.Y < 0f)
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

        // The Sim's world, not ours — the chunk colliders live inside
        // sceneViewport's World3D and this node is outside it.
        var spaceState = _world.GetWorld3D().DirectSpaceState;
        using var query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd);
        query.CollisionMask = (uint)(ECollisionLayer.Solid | ECollisionLayer.Water);
        // The world boundary is invisible, sits outside the world, and is on
        // Environment like real terrain — so a click out over open air lands on
        // the floor under the bottom chunk (y = -16 in a scene workspace)
        // instead of falling through to the empty-air plane.
        query.Exclude = _boundaryExclude;
        return spaceState.IntersectRay(query);
    }

    private void UpdateHud()
    {
        editorHud.SetVoxelBrush(_voxelTypeIndex);
        editorHud.SetEntityBrush(_entityTypeIndex);
        editorHud.SetRoofBrush(_roofStyleIndex);
    }

    private void CullProps(float cameraClip)
    {
        foreach (List<Node3D> entities in _world.ActiveEntities.Values)
        {
            foreach (Node3D entity in entities)
            {
                // See GameClient.CullProps: a roof clips itself in-shader and
                // owns passes that have to survive the cutaway.
                if (entity is Roof)
                {
                    continue;
                }
                entity.Visible = entity.GlobalPosition.Y < cameraClip;
            }
        }
    }

    // Ctrl+S. The first save asks for a name; afterwards the file exists and
    // every save overwrites it silently. What gets written is decided by the
    // document kind, fixed when the editor opened — never at save time.
    private void Save()
    {
        if (!string.IsNullOrEmpty(_documentPath) && File.Exists(ProjectSettings.GlobalizePath(_documentPath)))
        {
            SaveDocument(_documentPath);
            return;
        }
        PromptForSaveName(_documentPath);
    }

    // Ctrl+Shift+S. Always re-prompts; the kind (and so the extension and
    // default directory) still comes from the open document.
    private void SaveAs()
    {
        PromptForSaveName(_documentPath);
    }

    private void PromptForSaveName(string suggestedPath)
    {
        if (saveDialog == null || saveNameEdit == null)
        {
            // No dialog wired (headless / stripped scene) — keep the old
            // behavior rather than silently dropping the save.
            SaveDocument(suggestedPath);
            return;
        }
        string suggestedName = string.IsNullOrEmpty(suggestedPath) ? "" : suggestedPath.GetFile();
        saveNameEdit.Text = suggestedName;
        saveDialog.PopupCentered();
        // AcceptDialog focuses its OK button while popping up, so claiming focus
        // inline here would be overwritten — defer to the end of the frame.
        Callable.From(FocusSaveNameEdit).CallDeferred();
    }

    private void FocusSaveNameEdit()
    {
        if (saveNameEdit == null || !saveDialog.Visible)
        {
            return;
        }
        saveNameEdit.GrabFocus();
        // Select the stem only, so typing replaces the name but keeps ".hike".
        string name = saveNameEdit.Text;
        int dot = name.LastIndexOf('.');
        saveNameEdit.CaretColumn = name.Length;
        saveNameEdit.Select(0, dot > 0 ? dot : name.Length);
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
        if (name.GetExtension() != DocumentExtension)
        {
            name = $"{name}.{DocumentExtension}";
        }
        // Save-As into the document's own folder; a first save lands in the
        // default one for its kind.
        string dir = string.IsNullOrEmpty(_documentPath) ? "" : _documentPath.GetBaseDir();
        if (string.IsNullOrEmpty(dir))
        {
            dir = DocumentDefaultDir;
        }
        // PathJoin, not manual concat: trimming slashes off a bare "user://"
        // leaves "user:", which globalizes to a bogus relative path.
        SaveDocument(dir.PathJoin(name));
    }

    // Writes the open document and re-points the editor at the path it landed
    // on, so the next Ctrl+S overwrites silently.
    private void SaveDocument(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            GD.PrintErr("Save failed: no document path.");
            editorHud?.ShowToast("Save failed: no document path.", success: false);
            return;
        }
        try
        {
            if (_documentKind == EEditorDocumentKind.Scene)
            {
                WriteSubscene(path, _documentIncludeEnv);
            }
            else
            {
                WorldFile.Write(path, _worldState);
                GD.Print($"World saved to {path}");
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"Save failed: {e.Message}");
            editorHud?.ShowToast($"Save failed: {e.Message}", success: false);
            return;
        }

        _documentPath = path;
        if (_documentKind == EEditorDocumentKind.World)
        {
            // Keep world_file pointing at the world being edited, so the game
            // and a later autostart load what was just saved.
            CVars.worldFile.Value = path;
        }
        UpdateDocumentHud();
        editorHud?.ShowToast($"Saved {path.GetFile()}", success: true);
    }

    private void UpdateDocumentHud()
    {
        bool saved = !string.IsNullOrEmpty(_documentPath)
            && File.Exists(ProjectSettings.GlobalizePath(_documentPath));
        editorHud?.SetDocument(DocumentKindLabel, _documentPath, saved);
    }

    public override void _ExitTree()
    {
        // Closing mid-drag would otherwise leave the menu with a captured pointer.
        EndFly();
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

    // Inclusive voxel bounds a subscene save covers. Two marked corners win;
    // otherwise the box auto-fits every non-air voxel in the world, which is
    // the normal path — an editor world starts blank, so whatever you built
    // IS the subscene.
    private bool ResolveSaveBounds(out Vector3I min, out Vector3I max)
    {
        if (_subsceneCornerA != null && _subsceneCornerB != null)
        {
            min = ComponentMin(_subsceneCornerA.Value, _subsceneCornerB.Value);
            max = ComponentMax(_subsceneCornerA.Value, _subsceneCornerB.Value);
            return true;
        }

        if (!SubsceneBuilder.TryGetContentBounds(_worldState, out min, out max))
        {
            return false;
        }
        return true;
    }

    // Console entry point (subscene_save / subscene_save_env), kept for saving
    // a scene out of a world document — the scene document's own Ctrl+S goes
    // through SaveDocument instead.
    public void SaveSubscene(string path, bool includeEnv)
    {
        try
        {
            WriteSubscene(path, includeEnv);
        }
        catch (Exception e)
        {
            GD.PrintErr($"subscene_save failed: {e.Message}");
            editorHud?.ShowToast($"Save failed: {e.Message}", success: false);
            return;
        }
        editorHud?.ShowToast($"Saved {path.GetFile()}", success: true);
    }

    // Throws on failure so callers can report it their own way. The bbox comes
    // from ResolveSaveBounds — normally auto-fit to every non-air voxel, which
    // is why a scene document is authored in a world that starts blank.
    private void WriteSubscene(string path, bool includeEnv)
    {
        bool explicitBox = _subsceneCornerA != null && _subsceneCornerB != null;
        if (!ResolveSaveBounds(out Vector3I min, out Vector3I max))
        {
            throw new InvalidOperationException("nothing to save — the world has no voxels.");
        }
        int interiorClass = CVars.subsceneInteriorClass.Value;
        SubsceneState sub = SubsceneBuilder.Build(_worldState, min, max, includeEnv, filterEntitiesToBox: explicitBox, interiorClassOverride: interiorClass);
        SubsceneFile.Write(path, sub);
        string interiorNote = interiorClass >= 0 ? $", interiorClass={interiorClass}" : "";
        GD.Print($"subscene_save: wrote {path} (bbox min={min} max={max} size={sub.Size}, env={(includeEnv ? "yes" : "no")}, entities={sub.Entities.Count}{interiorNote})");
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
        Vector3I worldOriginI = SubsceneStamper.ComputeWorldOrigin(sub, anchor);
        Vector3I size = sub.Size;

        // Build the changed list so the refresh knows which voxels to recompute
        // around. Cheaper than enumerating every voxel: list the cells we will
        // write (presence mask).
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
                Vector3 worldPos = e.WorldPosition + SubsceneStamper.WorldOffset(sub, anchor);
                entityChunks.Add(Sim.WorldToChunkCoord(worldPos));
            }
        }

        // The stamper writes straight into WorldState, so the edit is told what
        // it is about to overwrite up front — voxel cells (which carry their
        // chunks' env overrides with them) and the entity buckets it lands in.
        EditorEdit edit = _history?.Begin($"Stamp {Path.GetFileName(path)}");
        edit?.TouchVoxels(changed);
        foreach (Vector3I cc in entityChunks)
        {
            edit?.TouchEntityChunk(cc);
        }

        SubsceneStamper.StampAll(_worldState, sub, anchor);

        var refresh = new EditorRefresh();
        refresh.AddVoxels(changed);
        foreach (Vector3I cc in entityChunks)
        {
            refresh.AddEntityChunk(cc);
        }
        refresh.Apply(_world);
        _history?.Commit();

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

    // Chunks allocated, not one voxel written. Subscene authoring saves the
    // bbox of whatever voxels exist, so anything baked in here would end up in
    // every subscene; the brush falls back to the drafting plane when there's no
    // geometry to click on (see ComputeVoxelTarget).
    public WorldState CreateEmptyWorld(WorldGenData genData)
    {
        return CreateEmptyWorld(genData, emptyWorldMinChunk, emptyWorldMaxChunk);
    }

    // Open a .hikescene as the editor's document: a blank world big enough to
    // hold the scene with room to keep building around it, the scene stamped
    // at the origin. includeEnv reports whether the file carried env overrides
    // so a re-save preserves them.
    public WorldState CreateSubsceneWorld(WorldGenData genData, string path, out bool includeEnv)
    {
        SubsceneState sub = SubsceneFile.Read(path);
        includeEnv = sub.EnvTag != null;

        // Where the scene lands when stamped at the origin: local cell
        // floor(Anchor) sits at (0,0,0), so the bbox starts at -Anchor. Because
        // the anchor's Y is the y=0 plane, this reopens the scene at the exact
        // elevation it was authored at — a basement comes back below y=0 rather
        // than being lifted to rest on it.
        var worldMin = SubsceneStamper.ComputeWorldOrigin(sub, Vector3.Zero);
        Vector3I worldMax = worldMin + sub.Size - Vector3I.One;
        Vector3I minChunk = ComponentMin(ChunkOf(worldMin) - sceneWorkspacePadChunks, emptyWorldMinChunk);
        Vector3I maxChunk = ComponentMax(ChunkOf(worldMax) + sceneWorkspacePadChunks, emptyWorldMaxChunk);

        int entityCount = sub.Entities?.Count ?? 0;
        WorldState ws = CreateEmptyWorld(genData, minChunk, maxChunk);
        // Safe here in a way it isn't during WorldGen.Generate: the default
        // env bakes never ran on this stub, so authored overrides can't be
        // clobbered by one.
        SubsceneStamper.StampAll(ws, sub, Vector3.Zero);
        // Rasterize the non-voxel sun occluders the scene just brought in —
        // foliage canopies and roofs — BEFORE relighting. Without this an opened
        // scene's roofs neither darken the room beneath them nor hold any dust,
        // so a broken roof shows holes but drops no light shaft through them.
        // Main does the same pair when it loads a world; the editor's open path
        // was only doing the second half.
        FoliageStamper.Stamp(ws);
        // The stub's bake ran on an empty world; redo it now there's geometry.
        LightEngine.Relight(ws);
        // The editor is the only place these are heard while authoring, and
        // nothing else runs them outside WorldGen.Generate — without this the
        // whole world classifies outdoor and the audio you hear while editing
        // is the raycast enclosure probe alone.
        InteriornessGen.Compute(ws);
        EnvTagGen.ComputeEnvTagGrid(ws);
        ws.Spawn = new Vector3(
            worldMin.X + sub.Size.X * 0.5f,
            worldMax.Y + 1,
            worldMin.Z + sub.Size.Z * 0.5f);
        GD.Print($"[Editor] opened scene {path.GetFile()} (size={sub.Size}, entities={entityCount}, env={(includeEnv ? "yes" : "no")}, workspace chunks {minChunk}..{maxChunk})");
        return ws;
    }

    private static Vector3I ChunkOf(Vector3I voxel)
    {
        return new Vector3I(
            (int)Math.Floor((double)voxel.X / ChunkState.SIZE),
            (int)Math.Floor((double)voxel.Y / ChunkState.SIZE),
            (int)Math.Floor((double)voxel.Z / ChunkState.SIZE));
    }

    private static WorldState CreateEmptyWorld(WorldGenData genData, Vector3I min, Vector3I max)
    {
        // Its own palette, like any other world. A scratch world used to inherit
        // whatever the process happened to have bound, which worked only because
        // Main had bound the same genData moments earlier.
        var ws = new WorldState(min, max, genData.simData,
            KitPalette.Build(genData.kitPalette, genData.ZoneGens));

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

        ws.Spawn = Vector3.Zero;
        // Author under a high sun. The default 0.0 is sunrise, which lights the
        // stub world too dimly to judge what you're building.
        ws.TimeOfDay01 = WorldState.NoonTimeOfDay01;

        // Same pairing as the scene path: occluders first, then light.
        FoliageStamper.Stamp(ws);
        // Compute initial sunlight so the world isn't pitch black
        LightEngine.Relight(ws);
        // The editor is the only place these are heard while authoring, and
        // nothing else runs them outside WorldGen.Generate — without this the
        // whole world classifies outdoor and the audio you hear while editing
        // is the raycast enclosure probe alone.
        InteriornessGen.Compute(ws);
        EnvTagGen.ComputeEnvTagGrid(ws);

        return ws;
    }
}
