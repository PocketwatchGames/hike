using Godot;

// Places `.hikescene` stamps: click empty ground to drop one, click a stamp to
// select it, drag to move it, R/F to turn it, RMB to delete it.
//
// A stamp is not a raster. Every other layer answers a question per column, and
// a per-column byte cannot hold "this scene, facing that way" — nor two of them
// overlapping, nor a footprint that has to move as one thing. So placements are
// a LIST (WorldMapPlacements, saved beside the layer images) and this tool edits
// it directly rather than through the brush.
//
// The select / move / rotate / delete mechanics are deliberately written to be
// the model for placing individual interactives later: the only scene-specific
// pieces are the palette (which files exist) and the footprint (the loaded
// scene's Size). An interactive replaces those with a prop list and a 1x1
// footprint and keeps everything else.
public class SceneTool : IWorldMapTool
{
    public string Name => "Scenes";
    public IWorldMapView View { get; }

    // Not a brush — a ring would say the stamp covers the cursor's disk, which
    // it does not. Kept small so the cursor reads as a pointer.
    public float Radius { get; set; } = 1f;

    public int SceneIndex = 0;
    public SubscenePlacement Selected;

    public SubscenePlacement SelectedPlacement => Selected;

    private string[] _paths;
    private SubscenePlacement _pressHit;
    private Vector2I _grabOffset;
    private bool _strokeActed;
    private Rect2I? _dirty;

    public SceneTool()
    {
        View = new SceneView();
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => _dirty;

    // Scenes are discovered on disk rather than authored into a palette: a
    // .hikescene is made in the world editor, and having to register it in a
    // second resource before it could be placed is a step that would be
    // forgotten every time.
    private string[] Paths()
    {
        if (_paths != null)
        {
            return _paths;
        }
        var found = new System.Collections.Generic.List<string>();
        using (DirAccess dir = DirAccess.Open(SubsceneFile.DEFAULT_SCENE_DIR))
        {
            if (dir != null)
            {
                foreach (string file in dir.GetFiles())
                {
                    // An exported build serves "x.hikescene.remap"; the loader
                    // still wants the original name.
                    string name = file.EndsWith(".remap") ? file.Substr(0, file.Length - 6) : file;
                    if (name.EndsWith("." + WorldEditor.SCENE_FILE_EXTENSION))
                    {
                        found.Add(SubsceneFile.DEFAULT_SCENE_DIR + name);
                    }
                }
            }
        }
        found.Sort();
        _paths = found.ToArray();
        return _paths;
    }

    public string[] Options(WorldMapState ctx)
    {
        string[] paths = Paths();
        var names = new string[paths.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = paths[i].GetFile().GetBaseName();
        }
        return names;
    }

    public Color[] OptionColors(WorldMapState ctx) => null;

    public int OptionIndex
    {
        get => SceneIndex;
        set => SceneIndex = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapState ctx) => ctx.Data.placementInk;

    public string HintText(WorldMapState ctx)
        => "Click to place, drag to move, R/F to turn, RMB to delete";

    public string StatusText(WorldMapState ctx)
    {
        string[] paths = Paths();
        if (paths.Length == 0)
        {
            return "No ." + WorldEditor.SCENE_FILE_EXTENSION + " files in " + SubsceneFile.DEFAULT_SCENE_DIR;
        }
        return paths[Mathf.Clamp(SceneIndex, 0, paths.Length - 1)].GetFile().GetBaseName();
    }

    public string LevelText(WorldMapState ctx)
    {
        if (Selected == null)
        {
            return "";
        }
        string nudge = Selected.yOffset == 0 ? "" : $", Y{Selected.yOffset:+#;-#}";
        return $"Selected {(int)Selected.rotation * 90} deg{nudge} (seats at Y={ctx.SeatY(Selected)})";
    }

    // The press decides what the stroke is ABOUT — the stamp under the cursor,
    // or none — and nothing else. It must not place: the right button fires this
    // too, and a right-click on empty ground would drop a building for the erase
    // that follows to delete again.
    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
        _pressHit = ctx.PlacementAt(texel.X, texel.Y);
        _strokeActed = false;
        _dirty = null;

        // alt+click SEATS the stamp on the ground under the cursor: point at the
        // terrace you want its floor on, and the nudge is solved to put it
        // there. Sampling a height beats stepping an offset blind, because the
        // number that matters is where the floor LANDS, not how far it moved.
        //
        // A pick paints nothing (the canvas skips the stamp entirely), so this
        // is the whole of it.
        if ((mods & EStrokeMods.Pick) != 0)
        {
            SubscenePlacement target = _pressHit ?? Selected;
            if (target != null)
            {
                // Solve against the seat WITHOUT the current nudge, or each pick
                // would move it by the offset it already had.
                int autoSeat = ctx.SeatY(target) - target.yOffset;
                target.yOffset = ctx.TerrainHeight(texel.X, texel.Y) - autoSeat;
                Selected = target;
            }
        }
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        _dirty = null;
        if (erase)
        {
            // Only the stamp under the PRESS, and only once: a right-drag across
            // a village must not sweep it away.
            if (!_strokeActed && _pressHit != null)
            {
                _strokeActed = true;
                _dirty = ctx.FootprintOf(_pressHit);
                if (Selected == _pressHit)
                {
                    Selected = null;
                }
                ctx.RemovePlacement(_pressHit);
            }
            return;
        }

        if (!_strokeActed)
        {
            _strokeActed = true;
            if (_pressHit != null)
            {
                // Grab where you clicked, so a drag slides the stamp instead of
                // snapping its anchor to the cursor.
                Selected = _pressHit;
                _grabOffset = Selected.anchorXZ - ctx.WorldXZ(texel);
            }
            else
            {
                string[] paths = Paths();
                if (paths.Length == 0)
                {
                    return;
                }
                Selected = new SubscenePlacement
                {
                    path = paths[Mathf.Clamp(SceneIndex, 0, paths.Length - 1)],
                    anchorXZ = ctx.WorldXZ(texel),
                };
                _grabOffset = Vector2I.Zero;
                ctx.AddPlacement(Selected);
                _dirty = ctx.FootprintOf(Selected);
                return;
            }
        }

        if (Selected == null)
        {
            return;
        }
        Vector2I moved = ctx.WorldXZ(texel) + _grabOffset;
        if (moved == Selected.anchorXZ)
        {
            return;
        }
        // Both footprints: the ground it left needs repainting as much as the
        // ground it arrived on.
        Rect2I before = ctx.FootprintOf(Selected);
        Selected.anchorXZ = moved;
        _dirty = before.Merge(ctx.FootprintOf(Selected));
    }

    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = Mathf.Max(1, Paths().Length);
        SceneIndex = ((SceneIndex + dir) % n + n) % n;
    }

    // R/F turn the SELECTED stamp rather than stepping a tool parameter. The
    // scene spins about its ANCHOR, so one anchored at a corner swings around it
    // — the host rebuilds the whole map after this, which a footprint sweeping
    // across the map needs anyway.
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        if (Selected == null)
        {
            return;
        }
        Selected.rotation = (ESubsceneRotation)(((int)Selected.rotation + dir) & 3);
    }
}

// The plain ground map. The stamps themselves are drawn by the painter over
// whatever view is active (WorldMapState.StampColorAt), so this view only has to
// choose the ground UNDER them — and ground rather than elevation, because a
// building is placed against what is around it: the road it fronts, the props it
// displaces. The terrain shape still reads through the step outlines.
public class SceneView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.Props;

    public Color ColorAt(WorldMapState ctx, int px, int pz) => ctx.GroundColorAt(px, pz);
}
