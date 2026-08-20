using System;
using Godot;

// In-game world-map painting program — the first step in the authoring chain.
// A pure 2D map editor: it paints a layered raster *document* (WorldMapData) and
// bakes it to a WorldState / .hike on save. It intentionally does NOT build a
// live voxel world — every tool's view colours the 2D map directly from the
// layer images, so painting stays cheap and the screen opens instantly. (A 3D
// fly-over preview used to live here; it was removed and can return later as an
// on-demand feature built only when the user asks for it.)
[GlobalClass]
public partial class WorldMapPainter : Node3D
{
    [Export] public WorldMapCanvas canvas;
    [Export] public WorldMapHud hud;
    [Export] public WorldMapData data;
    [Export] public WorldMapBrush brush;

    // Strokes kept on the undo stack. Each holds only the tiles that actually
    // changed, so the cost is the area painted rather than the map size.
    [Export(PropertyHint.Range, "1,256,1")] public int undoDepth = 64;

    // Brush-size limits and how big one wheel notch / bracket press is, as a
    // FRACTION of the current radius: a fixed 1-texel step is unusably slow at
    // radius 200 and far too coarse at 2.
    [Export(PropertyHint.Range, "0.5,16,0.5")] public float minBrushRadius = 0.5f;
    [Export(PropertyHint.Range, "16,512,1")] public float maxBrushRadius = 256f;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float brushStepFraction = 0.15f;

    // Screen pixels per metre of map. The map is rendered at this resolution
    // rather than scaled up on display, so a step outline can be a thin line on
    // a voxel edge instead of a whole metre-wide block.
    [Export(PropertyHint.Range, "1,16,1")] public int pixelsPerMeter = 3;

    // Zoom limits for ctrl+wheel. The buffer grows with the SQUARE of this, so
    // the ceiling is a memory bound, not a taste one: at 8 px/m a 288x256 map is
    // already ~19MB of RGBA.
    [Export(PropertyHint.Range, "1,4,1")] public int minPixelsPerMeter = 1;
    [Export(PropertyHint.Range, "4,16,1")] public int maxPixelsPerMeter = 8;

    // Relief shading, OFF by default. Light from the NW at 45 degrees is the
    // cartographic convention, but hillshading fights the banded palette on two
    // counts: it multiplies every authored band colour by 0.6-1.1 so no band is
    // the colour it was authored as, and it puts a bright rim on one side of
    // every step and a dark rim on the other — which reads as a bevel around a
    // hard-edged flatten stroke that is, in the data, perfectly flat. The bands
    // and the step outlines already carry height. Raise this to get it back.
    [Export(PropertyHint.Range, "0,360,1")] public float reliefLightAzimuth = 315f;
    [Export(PropertyHint.Range, "1,89,1")] public float reliefLightAltitude = 45f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float reliefStrength = 0f;

    // Outline width as a fraction of a metre cell, so lines thicken with zoom
    // instead of staying a hairline that vanishes against 8px cells. Always at
    // least one pixel, never more than the cell.
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float edgeWidthFraction = 0.34f;

    // How long the finished-bake readout stays up before the panel hides.
    [Export(PropertyHint.Range, "0.5,10,0.5")] public float bakeResultHoldSeconds = 3f;

    private int EdgeWidth => Mathf.Clamp(Mathf.RoundToInt(pixelsPerMeter * edgeWidthFraction), 1, pixelsPerMeter);

    // Spill edges get their own, wider stroke off the document — see
    // WorldMapData.waterfallEdgeWidthFraction. At least 2px wherever the zoom
    // allows one, so a fall never comes out as the same hairline as a contour.
    private int WaterfallEdgeWidth => Mathf.Clamp(
        Mathf.RoundToInt(pixelsPerMeter * data.waterfallEdgeWidthFraction),
        Mathf.Min(2, pixelsPerMeter), pixelsPerMeter);

    public static WorldMapPainter Current;

    // The document most recently opened, kept AFTER the painter closes so the
    // console extent commands have something to act on without being handed a
    // path. Current goes null on exit; this deliberately does not.
    public static WorldMapData LastDocument;
    public Action onQuitToMenu;

    private WorldMapState _ctx;
    private MapHistory _history;
    private IWorldMapTool[] _tools;
    private int _toolIndex;

    // Background bake. The task owns its OWN WorldMapState, loaded from the files
    // Save just wrote, so it can never read the layer images the brush is still
    // painting into. Progress fields are written by the task and read on the main
    // thread in _Process.
    private System.Threading.Tasks.Task _bakeTask;
    private volatile float _bakeRatio;
    private volatile string _bakePhase = "";
    private volatile int _bakeResult;   // 0 running, 1 ok, 2 failed
    private System.Diagnostics.Stopwatch _bakeClock;
    private double _bakeHoldSeconds;

    private Image _display;
    private ImageTexture _displayTex;
    // RGBA8 scratch for the display image, written on the managed side and
    // handed over in one SetData call: a 3x buffer is ~660k pixels and
    // Image.SetPixel is a native call each.
    private byte[] _pixels;
    private bool _displayDirty;

    private int DisplayWidth => data.ImageWidth * pixelsPerMeter;
    private int DisplayHeight => data.ImageHeight * pixelsPerMeter;

    private IWorldMapTool ActiveTool => _tools[_toolIndex];

    // Bindings that mean the same thing whichever tool is active.
    private const string GLOBAL_HINT =
        "Tab tool  |  1-9 option  |  R/F level  |  Wheel brush  |  Ctrl+Wheel zoom  |  MMB pan  |  W water  |  Ctrl+Z undo  |  Ctrl+S save";

    // The document this painter is editing, for the console commands that act on
    // "whatever is open".
    public WorldMapData Document => data;

    public void Init()
    {
        Current = this;
        LastDocument = data;
        // Same treatment the world editor gets: the menu track is for the menu,
        // not for a work session that lasts as long as an authoring pass does.
        MusicManager.Instance?.SetEditor(true);

        _ctx = new WorldMapState(data);
        _history = new MapHistory(_ctx, undoDepth);

        _tools = new IWorldMapTool[]
        {
            new PresetTool(),
            new ElevationTool(),
            new WaterTool(),
            new TunnelTool(),
            new RegionTool(),
            new ZoneTool(),
            new GroundTool(),
            new ScatterTool(),
            new MobTool(),
            new MobLevelTool(),
            new ClimbTool(),
            new PaveTool(),
            new SceneTool(),
            new EntityTool(),
        };
        _toolIndex = 0;

        AllocateDisplay();
        canvas.CursorRadiusTexels = ActiveTool.Radius;
        canvas.OnPaint = OnCanvasPaint;
        canvas.OnStrokeStart = OnCanvasStrokeStart;
        canvas.OnStrokeEnd = () => _history.Commit();
        // Wheel UP shrinks the brush. The canvas reports the raw notch; the
        // mapping to brush size is policy and lives here, so [ and ] keep their
        // own (smaller / bigger) sense.
        canvas.OnAdjustRadius = notch => AdjustRadius(-notch);
        canvas.OnZoom = AdjustZoom;
        canvas.OnHover = t => hud.SetCoords(t, _ctx.TerrainHeight(t.X, t.Y), _ctx.LevelAt(t.X, t.Y),
            _ctx.WaterSurface(t.X, t.Y), _ctx.HasWater(t.X, t.Y));

        var toolNames = new string[_tools.Length];
        for (int i = 0; i < _tools.Length; i++)
        {
            toolNames[i] = _tools[i].Name;
        }
        hud.BuildToolButtons(toolNames, SelectTool);
        SelectTool(0);
    }

    // The single way the active tool changes — buttons, Tab and the number keys
    // all come through here, so the toolbar can never disagree with the map.
    private void SelectTool(int index)
    {
        if (index < 0 || index >= _tools.Length)
        {
            return;
        }
        _toolIndex = index;
        canvas.CursorRadiusTexels = ActiveTool.Radius;
        hud.SetActiveTool(index);
        hud.BuildOptionButtons(ActiveTool.Options(_ctx), ActiveTool.OptionColors(_ctx), SelectOption);
        hud.SetActiveOption(ActiveTool.OptionIndex);
        RebuildFull();
        UpdateHud();
    }

    // The tool's primary parameter, chosen from the option row. Q/E reaches the
    // same state through Cycle, so both refresh the row afterwards.
    private void SelectOption(int index)
    {
        if (index < 0 || index >= ActiveTool.Options(_ctx).Length)
        {
            return;
        }
        ActiveTool.OptionIndex = index;
        hud.SetActiveOption(ActiveTool.OptionIndex);
        UpdateHud();
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
        // Q/E only reach parameters the option row CANNOT show: a region or zone
        // index, a carve height. Where the parameter is a small fixed set, the
        // buttons and 1-9 own it and cycling is just a second way to be wrong
        // about which is selected.
        if (ActiveTool.Options(_ctx).Length == 0)
        {
            if (e.IsActionPressed("UseItem"))   // Q
            {
                ActiveTool.Cycle(_ctx, -1);
                UpdateHud();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (e.IsActionPressed("Interact"))  // E
            {
                ActiveTool.Cycle(_ctx, 1);
                UpdateHud();
                GetViewport().SetInputAsHandled();
                return;
            }
        }
        if (e.IsActionPressed("EditorUp"))  // R — active elevation / cross-section up
        {
            // Bracketed like a stroke: for most tools this only moves a tool
            // parameter and the edit drops itself, but the scene tool turns the
            // SELECTED stamp, which is document state.
            _history.Begin(ActiveTool.Name).TouchPlacements(_ctx);
            ActiveTool.AdjustLevel(_ctx, 1);
            _history.Commit();
            RebuildFull();
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e.IsActionPressed("EditorDown"))  // F — active elevation / cross-section down
        {
            _history.Begin(ActiveTool.Name).TouchPlacements(_ctx);
            ActiveTool.AdjustLevel(_ctx, -1);
            _history.Commit();
            RebuildFull();
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            // 1..9 pick the active tool's option — the thing you change often
            // mid-stroke. Switching tool is Tab or the toolbar.
            if (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key9)
            {
                int index = (int)(k.Keycode - Key.Key1);
                if (index < ActiveTool.Options(_ctx).Length)
                {
                    SelectOption(index);
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
            if (k.Keycode == Key.S && k.CtrlPressed)
            {
                SaveAndBake();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (k.Keycode == Key.Z && k.CtrlPressed)
            {
                // Ctrl+Shift+Z redoes as well as Ctrl+Y — both conventions are
                // in the wild and neither costs anything to answer.
                if (k.ShiftPressed) { Redo(); } else { Undo(); }
                GetViewport().SetInputAsHandled();
                return;
            }
            if (k.Keycode == Key.Y && k.CtrlPressed)
            {
                Redo();
                GetViewport().SetInputAsHandled();
                return;
            }
            switch (k.Keycode)
            {
                case Key.Tab:
                    SelectTool((_toolIndex + 1) % _tools.Length);
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.W:
                    _ctx.ShowWater = !_ctx.ShowWater;
                    RebuildFull();
                    UpdateHud();
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.Bracketleft:
                    AdjustRadius(-1);
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.Bracketright:
                    AdjustRadius(1);
                    GetViewport().SetInputAsHandled();
                    return;
            }
        }
    }

    // Write the document on the main thread — that part is fast and must not be
    // racy — then bake in the background off a private state loaded from what was
    // just written. Painting stays live while it runs.
    private void SaveAndBake()
    {
        _ctx.Save();
        if (_bakeTask != null && !_bakeTask.IsCompleted)
        {
            hud.SetStatus("Saved layers (bake already running)");
            return;
        }
        hud.SetStatus("Saved layers");

        WorldMapState snapshot;
        try
        {
            snapshot = new WorldMapState(data);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"WorldMapPainter: could not snapshot for bake: {e}");
            return;
        }

        _bakeRatio = 0f;
        _bakePhase = "Starting";
        _bakeResult = 0;
        _bakeClock = System.Diagnostics.Stopwatch.StartNew();
        _bakeHoldSeconds = 0d;
        _bakeTask = System.Threading.Tasks.Task.Run(() =>
        {
            bool ok = snapshot.Bake((r, phase) =>
            {
                _bakeRatio = r;
                _bakePhase = phase;
            });
            _bakeResult = ok ? 1 : 2;
        });
    }

    // Bake readout. Kept out of the paint path deliberately: the task only writes
    // plain fields, and the only thread touching Godot nodes is this one.
    private void TickBakeProgress(double delta)
    {
        if (_bakeTask == null)
        {
            return;
        }
        if (!_bakeTask.IsCompleted)
        {
            hud.SetBakeProgress(true, _bakeRatio, $"Baking: {_bakePhase} ({_bakeRatio * 100f:0}%)");
            return;
        }
        if (_bakeHoldSeconds <= 0d)
        {
            long ms = _bakeClock != null ? _bakeClock.ElapsedMilliseconds : 0;
            hud.SetBakeProgress(true, 1f, _bakeResult == 1
                ? $"Baked .hike in {ms / 1000f:0.0}s"
                : "Bake failed - see console");
            _bakeHoldSeconds = bakeResultHoldSeconds;
            return;
        }
        _bakeHoldSeconds -= delta;
        if (_bakeHoldSeconds <= 0d)
        {
            hud.SetBakeProgress(false, 0f, "");
            _bakeTask = null;
        }
    }

    // (Re)build the display image at the current scale. Called on open and on
    // every zoom, since the buffer, the image and the texture are all sized by
    // pixelsPerMeter.
    private void AllocateDisplay()
    {
        _display = Image.CreateEmpty(DisplayWidth, DisplayHeight, false, Image.Format.Rgba8);
        _pixels = new byte[DisplayWidth * DisplayHeight * 4];
        RebuildDisplay(new Rect2I(0, 0, data.ImageWidth, data.ImageHeight));
        _display.SetData(DisplayWidth, DisplayHeight, false, Image.Format.Rgba8, _pixels);
        _displayTex = ImageTexture.CreateFromImage(_display);
        canvas.PixelsPerTexel = pixelsPerMeter;
        canvas.SetDisplay(_displayTex, DisplayWidth, DisplayHeight);
    }

    // Ctrl+wheel. The canvas anchors the pan on the metre under the cursor once
    // this returns, so it has to see the new scale on canvas.PixelsPerTexel.
    private void AdjustZoom(int dir)
    {
        int next = Mathf.Clamp(pixelsPerMeter + dir, Mathf.Max(1, minPixelsPerMeter), maxPixelsPerMeter);
        if (next == pixelsPerMeter)
        {
            return;
        }
        pixelsPerMeter = next;
        AllocateDisplay();
        UpdateHud();
    }

    // Brush size by one step, shared by the mouse wheel and the [ ] keys.
    private void AdjustRadius(int dir)
    {
        float r = ActiveTool.Radius;
        float step = Mathf.Max(1f, r * brushStepFraction);
        ActiveTool.Radius = Mathf.Clamp(r + dir * step, minBrushRadius, maxBrushRadius);
        canvas.CursorRadiusTexels = ActiveTool.Radius;
        canvas.Refresh();   // the ring resizes now, without waiting for a mouse move
        UpdateHud();
    }

    // Resize or re-canvas the open document and pick the result back up.
    //
    // The extent operations read the layer FILES and rewrite them, so the live
    // images have to be saved first or a session's painting would be silently
    // replaced by whatever was last on disk. Afterwards everything sized by the
    // map is rebuilt: the state, the display buffer, and the history — whose
    // snapshots are tiles at the OLD extent and would restore garbage.
    public void ApplyExtentChange(System.Func<WorldMapData, int, int, bool> action, int chunksX, int chunksZ)
    {
        _ctx.Save();
        if (!action(data, chunksX, chunksZ))
        {
            return;
        }
        _ctx = new WorldMapState(data);
        _history = new MapHistory(_ctx, undoDepth);
        AllocateDisplay();
        RebuildFull();
        PushDisplay();
        UpdateHud();
    }

    private void Undo()
    {
        MapEdit edit = _history.Undo();
        if (edit == null)
        {
            return;
        }
        _ctx.InvalidateAllHeights();
        RebuildFull();
        PushDisplay();
        UpdateHud();
        GD.Print($"WorldMapPainter: undo {edit.Name}");
    }

    private void Redo()
    {
        MapEdit edit = _history.Redo();
        if (edit == null)
        {
            return;
        }
        _ctx.InvalidateAllHeights();
        RebuildFull();
        PushDisplay();
        UpdateHud();
        GD.Print($"WorldMapPainter: redo {edit.Name}");
    }

    private void OnCanvasStrokeStart(Vector2I texel, EStrokeMods mods)
    {
        // Opened on every press, including ones that paint nothing (an
        // alt+click pick, a click on empty ground): an edit that captured no
        // change is dropped at commit rather than costing an undo slot.
        //
        // The placement list is touched up front because a stamp's edit is not
        // spatial — the tool may add, delete or turn an entry, and the region
        // the brush covers says nothing about which.
        _history.Begin(ActiveTool.Name).TouchPlacements(_ctx);
        ActiveTool.BeginStroke(_ctx, texel, mods);
        if ((mods & EStrokeMods.Pick) != 0)
        {
            UpdateHud();   // the picked value is the tool's parameter now
        }
    }

    private void OnCanvasPaint(Vector2I texel, bool erase)
    {
        // BEFORE the write, which is the only time the old values are readable.
        // The brush rect rather than the tool's own dirty rect: that one is only
        // known after Paint has run, and a stamp's dirty rect covers a footprint
        // it did not raster-write anyway.
        _history.Begin(ActiveTool.Name).TouchRect(_ctx,
            ActiveTool.TouchRect(_ctx, texel, erase) ?? BrushRect(texel, ActiveTool.Radius));
        ActiveTool.Paint(_ctx, brush, texel, erase);
        // Elevation or roughness may have moved, and weathering is derived from
        // both over a neighbourhood, so the cached height field has to go.
        _ctx.InvalidateHeights(BrushRect(texel, ActiveTool.Radius));
        RebuildDisplay(ExpandToChunks(ActiveTool.LastPaintRect ?? BrushRect(texel, ActiveTool.Radius)));
        PushDisplay();
    }

    private void RebuildFull()
    {
        RebuildDisplay(new Rect2I(0, 0, data.ImageWidth, data.ImageHeight));
        PushDisplay();
    }

    // Colour every metre cell in the rect, then outline the voxel edges inside
    // it. Two passes, because an edge is drawn into one of the two cells that
    // share it and a single interleaved pass would paint base colour over a line
    // already laid down.
    //
    // Two different inflations, and the difference matters. Edges are drawn one
    // metre out from the stroke, because relief reads its neighbours and a cell
    // just outside the stroke owns the edge it shares with one inside it. Base
    // colour is filled one metre wider STILL, because an edge inks the higher of
    // the two cells it divides, which can be a cell beyond the edge range: fill
    // it and that pixel is fresh before the ink lands, skip it and every repaint
    // blends more ink onto the last, so the outline creeps darker as you paint
    // over the same ground. (Rebuilding one rect repeatedly over unchanged data
    // must be idempotent; it was not.)
    private void RebuildDisplay(Rect2I rect)
    {
        IWorldMapView view = ActiveTool.View;
        Vector3 light = LightVector();
        float flat = Mathf.Max(light.Y, 0.001f);   // shade of level ground

        int x0 = Mathf.Max(0, rect.Position.X - 1);
        int z0 = Mathf.Max(0, rect.Position.Y - 1);
        int x1 = Mathf.Min(data.ImageWidth, rect.Position.X + rect.Size.X + 1);
        int z1 = Mathf.Min(data.ImageHeight, rect.Position.Y + rect.Size.Y + 1);

        // ONE range for all three passes, and ink clipped to it. The invariant
        // that matters: every cell whose pixels are repainted must have all its
        // overlays redrawn, and nothing may ink a cell that was not repainted.
        // Ranges that differed per pass broke it in both directions — a wider
        // fill erased outlines and dots in the ring it repainted but did not
        // redraw (they flickered as you painted), and a wider ink pass blended
        // over pixels that were never refreshed (they darkened stroke by
        // stroke). Anything clipped away belongs to a cell this rebuild did not
        // touch, so its existing pixels are already correct.
        _clipX0 = x0 * pixelsPerMeter;
        _clipZ0 = z0 * pixelsPerMeter;
        _clipX1 = x1 * pixelsPerMeter;
        _clipZ1 = z1 * pixelsPerMeter;

        for (int px = x0; px < x1; px++)
        {
            for (int pz = z0; pz < z1; pz++)
            {
                Color c = view.ColorAt(_ctx, px, pz);
                // Shading water would put the seabed's shape straight back into
                // a colour whose whole job is to say you cannot see the ground.
                float mul = _ctx.IsSubmerged(px, pz)
                    ? 1f
                    : Mathf.Lerp(1f, _ctx.ReliefShade(px, pz, light) / flat, reliefStrength);
                FillCell(px, pz, new Color(
                    Mathf.Clamp(c.R * mul, 0f, 1f),
                    Mathf.Clamp(c.G * mul, 0f, 1f),
                    Mathf.Clamp(c.B * mul, 0f, 1f)));
            }
        }

        // Sub-2m lines only where colour is not already saying the height.
        bool showMinor = view.ShowsAllSteps;
        // Is water VISIBLE on this map right now? One question, two answers that
        // have to agree. It decides what the outlines follow — the water surface
        // where water is drawn, so the sea reads as one flat sheet with a line
        // only at its shore instead of contouring a seabed nothing can see
        // through the opaque water above it — and it decides whether spill edges
        // are inked at all, since a teal line on a map with no water on it marks
        // something that is not on screen.
        bool waterVisible = _ctx.ShowWater && view.DrawsWater;
        // Starts one cell BEFORE the repainted range: a cell's -X and -Z edges
        // are owned by its left and upper neighbours, so iterating only the
        // repainted cells leaves the first row and column of the rebuild without
        // the lines their neighbours draw. Ink is still clipped to the repainted
        // range, so reaching back cannot touch anything that was not refreshed.
        for (int px = Mathf.Max(0, x0 - 1); px < x1; px++)
        {
            for (int pz = Mathf.Max(0, z0 - 1); pz < z1; pz++)
            {
                DrawStepEdges(px, pz, showMinor, waterVisible);
            }
        }

        // Third pass: one dot per column that will really spawn. Runs the same
        // roll the bake runs, so this is the result rather than an impression of
        // it. Needs at least a couple of pixels per metre to read as dots
        // instead of a smear, so it is skipped when zoomed further out.
        // Over the FILL range, not the edge range: the fill runs one cell wider,
        // so dotting only the narrower range left a one-cell ring whose base
        // colour was repainted and whose dots were never put back. Every stroke
        // erased that ring, and the next stroke over it restored them — which
        // reads as the dots flickering while you paint.
        if (view.PreviewLayer != ESpawnPreview.None && pixelsPerMeter >= 2)
        {
            // Props first so mobs land on top of them where a column has both:
            // two dots cannot share a cell, and what lives somewhere is the more
            // urgent of the two answers.
            if (view.PreviewLayer.HasFlag(ESpawnPreview.Props))
            {
                DrawSpawnDots(x0, z0, x1, z1, _ctx.PropSets, _ctx.PreviewSpawnAt);
            }
            if (view.PreviewLayer.HasFlag(ESpawnPreview.Mobs))
            {
                DrawSpawnDots(x0, z0, x1, z1, _ctx.MobSets, _ctx.PreviewMobAt);
            }
        }
    }

    private Vector3 LightVector()
    {
        float az = Mathf.DegToRad(reliefLightAzimuth);
        float alt = Mathf.DegToRad(reliefLightAltitude);
        return new Vector3(
            Mathf.Cos(alt) * Mathf.Sin(az),
            Mathf.Sin(alt),
            Mathf.Cos(alt) * Mathf.Cos(az)).Normalized();
    }

    // The line goes on the HIGHER side of the boundary, so it reads as the rim
    // of the plateau rather than a fence between two cells.
    private void DrawStepEdges(int px, int pz, bool showMinor, bool waterVisible)
    {
        int h = SurfaceHeight(px, pz, waterVisible);
        if (px + 1 < data.ImageWidth)
        {
            int hn = SurfaceHeight(px + 1, pz, waterVisible);
            if (EdgeInk(h - hn, showMinor, IsRoutedWall(h, hn, px, pz, px + 1, pz),
                Spills(waterVisible, px, pz, px + 1, pz), out Color ink, out int w))
            {
                // Thickness grows INTO the higher cell, so a wider line never
                // spills across the boundary onto the lower plateau.
                int col = h > hn ? (px + 1) * pixelsPerMeter - w : (px + 1) * pixelsPerMeter;
                for (int d = 0; d < w; d++)
                {
                    for (int i = 0; i < pixelsPerMeter; i++)
                    {
                        BlendPixel(col + d, pz * pixelsPerMeter + i, ink);
                    }
                }
            }
        }
        if (pz + 1 < data.ImageHeight)
        {
            int hn = SurfaceHeight(px, pz + 1, waterVisible);
            if (EdgeInk(h - hn, showMinor, IsRoutedWall(h, hn, px, pz, px, pz + 1),
                Spills(waterVisible, px, pz, px, pz + 1), out Color ink, out int w))
            {
                int row = h > hn ? (pz + 1) * pixelsPerMeter - w : (pz + 1) * pixelsPerMeter;
                for (int d = 0; d < w; d++)
                {
                    for (int i = 0; i < pixelsPerMeter; i++)
                    {
                        BlendPixel(px * pixelsPerMeter + i, row + d, ink);
                    }
                }
            }
        }
    }

    private int SurfaceHeight(int px, int pz, bool waterVisible)
    {
        return waterVisible ? _ctx.VisibleSurface(px, pz) : _ctx.TerrainHeight(px, pz);
    }

    // Which authored ink an edge gets, by the size of its step: under 2m, exactly
    // 2m, and more than 2m. Colours (and their alphas) live on WorldMapData with
    // the rest of the map palette. The minor bucket is skipped entirely on views
    // whose colour already encodes elevation.
    private bool EdgeInk(int delta, bool showMinor, bool climbRoute, bool spills,
        out Color ink, out int width)
    {
        width = EdgeWidth;
        // A spill is drawn whatever its height, in its own bright teal and at
        // its own wider stroke. It is checked BEFORE the height buckets because
        // a one-metre lip is still a waterfall, and the minor bucket the drop
        // would otherwise fall into is not even drawn on the elevation map.
        if (spills)
        {
            ink = data.waterfallInk;
            width = WaterfallEdgeWidth;
            return true;
        }
        int d = Mathf.Abs(delta);
        if (d <= 0 || (d < 2 && !showMinor))
        {
            ink = default;
            return false;
        }
        // A routed wall takes the climb ink instead of its height ink. The same
        // line, recoloured — the outline pass has already found every wall and
        // knows how tall it is, so a separate overlay would only find them again.
        if (climbRoute)
        {
            ink = data.climbInk;
            return true;
        }
        ink = d < 2 ? data.edgeInkSub2m : d == 2 ? data.edgeInk2m : data.edgeInkOver2m;
        return true;
    }

    // Does water pour over this edge — one side wet, the other bare ground below
    // the wet side's surface?
    //
    // Inked on EVERY map that shows water, not just the water tool's: a spill is
    // a fact about the terrain you need while painting the things that sit
    // beside it, the same argument climbing routes are drawn everywhere. The
    // gate is water VISIBILITY rather than the active tool, so the one place it
    // stays quiet is a map where water is not on screen to be poured.
    //
    // Either side may be the pool, so the ordered rule is asked both ways. It is
    // the SAME rule the bake files waterfall entities from, so every edge inked
    // here is a cascade in the baked world.
    private bool Spills(bool waterVisible, int px, int pz, int nx, int nz)
    {
        return waterVisible
            && (_ctx.SpillsOver(px, pz, nx, nz) || _ctx.SpillsOver(nx, nz, px, pz));
    }

    // Does this edge carry a climbing route? Asked on EVERY view — a route is a
    // fact about the terrain, not a mode you switch into, and it has to stay
    // visible while you paint the things that route past it.
    //
    // Height first, coverage second: this now runs for every edge of every cell
    // on every rebuild, and the height is already in hand while the flag costs an
    // image read. Almost every edge is flat or a single step and never reaches it.
    private bool IsRoutedWall(int h, int hn, int px, int pz, int nx, int nz)
    {
        if (Mathf.Abs(h - hn) < data.climbRouteMinWallVoxels)
        {
            return false;
        }
        // The HIGHER column owns the wall, and is the one the bake walks the
        // exposed faces of.
        return h >= hn ? _ctx.ClimbRouteAt(px, pz) : _ctx.ClimbRouteAt(nx, nz);
    }

    private void DrawSpawnDots(int x0, int z0, int x1, int z1, SpawnSetData[] sets,
        System.Func<int, int, int> previewAt)
    {
        for (int px = x0; px < x1; px++)
        {
            for (int pz = z0; pz < z1; pz++)
            {
                int setIndex = previewAt(px, pz);
                if (setIndex < 0 || setIndex >= sets.Length)
                {
                    continue;
                }
                DrawSpawnDot(px, pz, sets[setIndex]?.mapColor ?? Colors.White);
            }
        }
    }

    // A centred square inside the metre cell, opaque so it reads against the
    // region wash under it.
    private void DrawSpawnDot(int px, int pz, Color c)
    {
        int size = Mathf.Max(1, pixelsPerMeter / 2 + 1);
        int off = (pixelsPerMeter - size) / 2;
        var ink = new Color(c.R, c.G, c.B, 1f);
        for (int dz = 0; dz < size; dz++)
        {
            for (int dx = 0; dx < size; dx++)
            {
                BlendPixel(px * pixelsPerMeter + off + dx, pz * pixelsPerMeter + off + dz, ink);
            }
        }
    }

    private void FillCell(int px, int pz, Color c)
    {
        byte r = (byte)(c.R * 255f);
        byte g = (byte)(c.G * 255f);
        byte b = (byte)(c.B * 255f);
        for (int dz = 0; dz < pixelsPerMeter; dz++)
        {
            int row = (pz * pixelsPerMeter + dz) * DisplayWidth;
            for (int dx = 0; dx < pixelsPerMeter; dx++)
            {
                int i = (row + px * pixelsPerMeter + dx) * 4;
                _pixels[i] = r;
                _pixels[i + 1] = g;
                _pixels[i + 2] = b;
                _pixels[i + 3] = 255;
            }
        }
    }

    // Pixel bounds of the rebuild in progress; ink outside them belongs to a
    // cell this pass did not repaint and must be left alone.
    private int _clipX0, _clipZ0, _clipX1, _clipZ1;

    private void BlendPixel(int x, int y, Color ink)
    {
        if (x < _clipX0 || x >= _clipX1 || y < _clipZ0 || y >= _clipZ1
            || x < 0 || x >= DisplayWidth || y < 0 || y >= DisplayHeight)
        {
            return;
        }
        int i = (y * DisplayWidth + x) * 4;
        float a = ink.A;
        _pixels[i] = (byte)Mathf.Lerp(_pixels[i], ink.R * 255f, a);
        _pixels[i + 1] = (byte)Mathf.Lerp(_pixels[i + 1], ink.G * 255f, a);
        _pixels[i + 2] = (byte)Mathf.Lerp(_pixels[i + 2], ink.B * 255f, a);
    }

    // Only flags; the upload happens once in _Process. A drag delivers several
    // motion events per frame and the buffer is 9x the map's area now, so
    // uploading per event would push the same megabytes several times a frame
    // for one visible result.
    private void PushDisplay()
    {
        _displayDirty = true;
    }

    public override void _Process(double delta)
    {
        TickBakeProgress(delta);
        if (!_displayDirty)
        {
            return;
        }
        _displayDirty = false;
        _display.SetData(DisplayWidth, DisplayHeight, false, Image.Format.Rgba8, _pixels);
        _displayTex.Update(_display);
        canvas.Refresh();
    }

    private Rect2I BrushRect(Vector2I center, float radius)
    {
        int r = Mathf.CeilToInt(radius);
        int x0 = Mathf.Max(0, center.X - r);
        int z0 = Mathf.Max(0, center.Y - r);
        int x1 = Mathf.Min(data.ImageWidth - 1, center.X + r);
        int z1 = Mathf.Min(data.ImageHeight - 1, center.Y + r);
        return new Rect2I(x0, z0, Mathf.Max(0, x1 - x0 + 1), Mathf.Max(0, z1 - z0 + 1));
    }

    // Round a texel rect out to chunk boundaries so chunk-resolution layers
    // (region / zone) recolour whole chunks, not just the columns under the disk.
    private Rect2I ExpandToChunks(Rect2I r)
    {
        int s = ChunkState.SIZE;
        int x0 = Mathf.Max(0, (r.Position.X / s) * s);
        int z0 = Mathf.Max(0, (r.Position.Y / s) * s);
        int x1 = Mathf.Min(data.ImageWidth, Mathf.CeilToInt((r.Position.X + r.Size.X) / (float)s) * s);
        int z1 = Mathf.Min(data.ImageHeight, Mathf.CeilToInt((r.Position.Y + r.Size.Y) / (float)s) * s);
        return new Rect2I(x0, z0, x1 - x0, z1 - z0);
    }

    private void UpdateHud()
    {
        // Every path that can change what the brush would write already comes
        // through here — tool, option, target level, eyedropper — so the ring
        // tracks it without its own set of hooks.
        canvas.CursorColor = ActiveTool.CursorColor(_ctx);
        canvas.Refresh();

        hud.SetTool(ActiveTool.Name);
        string status = ActiveTool.StatusText(_ctx);
        string level = ActiveTool.LevelText(_ctx);
        hud.SetStatus(string.IsNullOrEmpty(level) ? status : $"{status}  |  {level}");
        hud.SetRadius(ActiveTool.Radius, pixelsPerMeter);

        // Global bindings first, then whatever the active tool answers to.
        string toolHint = ActiveTool.HintText(_ctx);
        hud.SetHint(string.IsNullOrEmpty(toolHint) ? GLOBAL_HINT : $"{GLOBAL_HINT}\n{toolHint}");
    }

    public override void _ExitTree()
    {
        // _ExitTree rather than the quit-to-menu callback, so every way out of
        // the painter restores the menu track exactly once.
        MusicManager.Instance?.SetEditor(false);
        if (Current == this)
        {
            Current = null;
        }
    }
}
