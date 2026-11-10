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
        View = new CutawayGroundView();
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

    public Color[] OptionColors(WorldMapInk ink) => null;

    // No 1-9: .hikescene files are discovered on disk, so the first nine rows are an arbitrary prefix that
    // moves whenever one is added.
    public bool NumberKeys => false;

    public int OptionIndex
    {
        get => SceneIndex;
        set => SceneIndex = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapInk ink) => ink.Data.placementInk;

    public string HintText(WorldMapState ctx)
        => "Click to place, drag to move, R/F to turn, RMB to delete; "
            + "alt+click seats on that floor, shift+R/F raise/lower, X pins/unpins height";

    public string StatusText(WorldMapState ctx, WorldMapView view)
    {
        string[] paths = Paths();
        if (paths.Length == 0)
        {
            return "No ." + WorldEditor.SCENE_FILE_EXTENSION + " files in " + SubsceneFile.DEFAULT_SCENE_DIR;
        }
        return paths[Mathf.Clamp(SceneIndex, 0, paths.Length - 1)].GetFile().GetBaseName();
    }

    public string LevelText(WorldMapState ctx, WorldMapView view)
    {
        if (Selected == null)
        {
            return "";
        }
        string seat = Selected.IsFixed
            ? $"pinned at Y={Selected.fixedY}"
            : $"on ground{(Selected.yOffset == 0 ? "" : $" {Selected.yOffset:+#;-#}")}, seats at Y={ctx.SeatY(Selected)}";
        return $"Selected {(int)Selected.rotation * 90} deg, {seat}";
    }

    // The press decides what the stroke is ABOUT — the stamp under the cursor,
    // or none — and nothing else. It must not place: the right button fires this
    // too, and a right-click on empty ground would drop a building for the erase
    // that follows to delete again.
    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
        _pressHit = ctx.PlacementAt(texel.X, texel.Y);
        _strokeActed = false;
        _dirty = null;

        // alt+click SEATS the stamp on the floor under the cursor: point at the
        // terrace (or the passage floor under the cut) you want
        // its floor on. Sampling a height beats stepping an offset blind, because
        // the number that matters is where the floor LANDS, not how far it moved.
        //
        // A pick paints nothing (the canvas skips the stamp entirely), so this
        // is the whole of it.
        if ((mods & EStrokeMods.Pick) != 0)
        {
            SubscenePlacement target = _pressHit ?? Selected;
            if (target != null)
            {
                SeatOnFloorAt(ctx, view, target, texel);
                Selected = target;
            }
        }
    }

    // The floor the map is SHOWING at the column — under the cut, so a pick in a
    // passage lands in the passage rather than on the hilltop over it. A floor
    // with something above it (a passage, under a deck) pins the stamp, because
    // a nudge measured from the surface would drag it through the rock whenever
    // the hill is repainted; an open floor keeps the stamp's mode and, if it
    // follows the ground, solves the nudge.
    private static void SeatOnFloorAt(WorldMapState ctx, WorldMapView view, SubscenePlacement target, Vector2I texel)
    {
        int floor = ctx.CutawayFloor(texel.X, texel.Y, view.CutawayY, out _);
        if (floor < ctx.Data.WorldMinY)
        {
            return;
        }
        bool roofed = ctx.FloorForEntity(texel.X, texel.Y, view.CutawayY) != EntityPlacement.OnTheGround;
        if (target.IsFixed || roofed)
        {
            target.fixedY = floor;
        }
        else
        {
            target.yOffset = floor - ctx.FootprintGroundY(target);
        }
    }

    // Shift+R/F: a metre up or down, in whichever terms the stamp is seated.
    public bool AdjustSecondaryLevel(WorldMapState ctx, int dir)
    {
        if (Selected == null)
        {
            return true;
        }
        if (Selected.IsFixed)
        {
            Selected.fixedY += dir;
        }
        else
        {
            Selected.yOffset += dir;
        }
        return true;
    }

    // X: pin the selected stamp at the height it seats at now, or hand it back
    // to the ground with the nudge that keeps it there. Either way it does not
    // move on the press — the mode says what it follows LATER.
    public bool ToggleMode(WorldMapState ctx)
    {
        SubscenePlacement p = Selected;
        if (p == null)
        {
            return true;
        }
        if (p.IsFixed)
        {
            p.yOffset = p.fixedY - ctx.FootprintGroundY(p);
            p.fixedY = SubscenePlacement.FollowsGround;
        }
        else
        {
            p.fixedY = ctx.SeatY(p);
        }
        return true;
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
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
                // Dropped on a floor with something over it — a passage the cut
                // exposes — it is pinned there; on open ground it follows the
                // ground as before.
                if (ctx.FloorForEntity(texel.X, texel.Y, view.CutawayY) != EntityPlacement.OnTheGround)
                {
                    SeatOnFloorAt(ctx, view, Selected, texel);
                }
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
