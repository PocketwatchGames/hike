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

    // Pixels per metre the map is RASTERISED at — the detail in the buffer, not
    // the size on screen. Rendering rather than scaling is what lets a step
    // outline be a thin line on a voxel edge instead of a metre-wide block.
    [Export(PropertyHint.Range, "1,8,1")] public int rasterPixelsPerMeter = 3;

    // How far past the raster ctrl+wheel may magnify. These steps are FREE — the
    // same buffer drawn bigger with nearest filtering — so this is a taste bound
    // rather than a memory one.
    [Export(PropertyHint.Range, "1,8,1")] public int maxZoomFactor = 4;

    // Ctrl+wheel walks one ladder of SCREEN pixels per metre: 1..raster-1, then
    // the raster magnified 1..maxZoomFactor. So at raster 3 it is 1, 2, 3, 6, 9,
    // 12.
    //
    // The split is the whole point. Rasterising at the on-screen size made every
    // notch reallocate the buffer, repaint all ~295k cells into it, and re-upload
    // the result: 72 MB and ~240 ms of CPU at 8 px/m before the GPU saw any of
    // it. Above the raster nothing about the IMAGE changes when you zoom, only
    // how big it is drawn — so those steps now cost one QueueRedraw. Below it the
    // buffer shrinks, so the steps that do re-rasterise are the cheap ones.
    private int _zoomIndex;

    private int BelowRasterSteps => Mathf.Max(0, rasterPixelsPerMeter - 1);

    private int MaxZoomIndex => BelowRasterSteps + Mathf.Max(1, maxZoomFactor) - 1;

    private int ScreenPerMeter => _zoomIndex < BelowRasterSteps
        ? _zoomIndex + 1
        : rasterPixelsPerMeter * (_zoomIndex - BelowRasterSteps + 1);

    // What the buffer is rasterised at, and how much bigger it is drawn.
    private int pixelsPerMeter => Mathf.Min(ScreenPerMeter, rasterPixelsPerMeter);

    private int ZoomFactor => Mathf.Max(1, ScreenPerMeter / Mathf.Max(1, pixelsPerMeter));

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

    // Metres of headroom the cutaway keeps above the ground it is aimed at —
    // both when a tool asks for a plane on being picked up and when alt+RMB
    // aims one at a clicked floor. Enough to stand in, so the cut lands inside
    // the space you are about to make rather than in the rock over it.
    [Export(PropertyHint.Range, "1,16,1")] public int cutawayHeadroom = 3;

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
        "Tab tool  |  1-9 option  |  Q/E param  |  R/F level  |  T/G cutaway  |  Wheel brush  |  Ctrl+Wheel zoom  |  MMB pan  |  W water  |  Ctrl+Z undo  |  Ctrl+S save";

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
            new BlockTool(),
            new RegionTool(),
            new ZoneTool(),
            new WindTool(),
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

        // Opens at 1:1 — the raster drawn at its own size.
        _zoomIndex = BelowRasterSteps;
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
        canvas.OnHover = ReportHover;

        var toolNames = new string[_tools.Length];
        for (int i = 0; i < _tools.Length; i++)
        {
            toolNames[i] = _tools[i].Name;
        }
        hud.BuildToolButtons(toolNames, SelectTool);
        if (hud.entityInspector != null)
        {
            // One property change is one undo step, bracketed here because the
            // history belongs to the painter and the widgets belong to the panel.
            hud.entityInspector.BeforeEdit = () => _history.Begin(ActiveTool.Name).TouchPlacements(_ctx);
            hud.entityInspector.AfterEdit = () =>
            {
                _history.Commit();
                UpdateHud();
            };
        }
        SelectTool(0);
    }

    // What the MAP IS SHOWING at a column, not the raw height field. Under a
    // cutaway the two differ by a whole mountain, and a readout that reports the
    // hilltop while the map draws the passage beneath it makes every alt+click
    // look like it sampled the wrong thing — which is exactly how it read.
    private void ReportHover(Vector2I t)
    {
        int clip = ActiveTool.View.CutsAway ? _ctx.CutawayY : int.MaxValue;
        int shown = _ctx.CutawayFloor(t.X, t.Y, clip, out _);
        // Solid rock at the plane has no floor to report; fall back to the ground
        // above it rather than leaving the readout blank.
        if (shown < data.WorldMinY)
        {
            shown = _ctx.TerrainHeight(t.X, t.Y);
        }
        hud.SetCoords(t, shown, (shown - _ctx.SeaLevel) / _ctx.StepVoxels,
            _ctx.WaterSurface(t.X, t.Y), _ctx.HasWater(t.X, t.Y));
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
        // A tool that works under the ground brings the plane with it.
        int? wantCutaway = ActiveTool.CutawayFor(cutawayHeadroom);
        if (wantCutaway.HasValue)
        {
            SetCutaway(wantCutaway.Value);
        }
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

    // T/G. A view setting rather than a tool parameter, so it lives on the state
    // beside ShowWater: every cutting view cuts at the same plane, and switching
    // between them keeps the slice you were reading.
    private void AdjustCutaway(int dir)
    {
        SetCutaway(_ctx.CutawayY + dir);
    }

    private void SetCutaway(int y)
    {
        _ctx.CutawayY = Mathf.Clamp(y, data.WorldMinY, data.WorldMaxY);
    }

    // Q/E. The option row is refreshed as well as the HUD, since a tool whose
    // Cycle moves its option index would otherwise leave a stale button lit.
    private void CycleParameter(int dir)
    {
        ActiveTool.Cycle(_ctx, dir);
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
        // Q/E step the tool's Cycle parameter. For most tools that IS the option
        // index and the keys are simply a second way to reach the row; where the
        // row is already spoken for they reach the parameter it cannot show (the
        // tunnel tool's carve height).
        if (e.IsActionPressed("EditorParamLeft"))   // Q
        {
            CycleParameter(-1);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e.IsActionPressed("EditorParamRight"))  // E
        {
            CycleParameter(1);
            GetViewport().SetInputAsHandled();
            return;
        }
        // T/G. NOT bracketed as an edit: a cutaway is what the map is showing,
        // not anything the document holds, so there is nothing to undo.
        if (e.IsActionPressed("EditorClipUp"))    // T
        {
            AdjustCutaway(1);
            RebuildFull();
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e.IsActionPressed("EditorClipDown"))  // G
        {
            AdjustCutaway(-1);
            RebuildFull();
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e.IsActionPressed("EditorUp"))  // R — active elevation / carve level up
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
        if (e.IsActionPressed("EditorDown"))  // F — active elevation / carve level down
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
        canvas.Zoom = ZoomFactor;
        canvas.SetDisplay(_displayTex, DisplayWidth, DisplayHeight);
    }

    // Ctrl+wheel. The canvas anchors the pan on the metre under the cursor once
    // this returns, so it has to see the new scale before it does.
    private void AdjustZoom(int dir)
    {
        int next = Mathf.Clamp(_zoomIndex + dir, 0, MaxZoomIndex);
        if (next == _zoomIndex)
        {
            return;
        }
        int wasRaster = pixelsPerMeter;
        _zoomIndex = next;
        // Only a change of RASTER touches the buffer. Magnifying is a draw-time
        // property, so those notches reallocate nothing and repaint nothing.
        if (pixelsPerMeter != wasRaster)
        {
            AllocateDisplay();
        }
        canvas.Zoom = ZoomFactor;
        canvas.Refresh();
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
        // alt+RMB aims the CUTAWAY at the floor under it, with headroom to stand
        // in — the one gesture that moves the plane without hunting for T/G.
        // Only where a cutaway is on screen: elsewhere it would silently consume
        // a press whose effect nothing can show, so alt+RMB keeps its ordinary
        // tool-pick meaning there.
        if ((mods & (EStrokeMods.Pick | EStrokeMods.Secondary))
            == (EStrokeMods.Pick | EStrokeMods.Secondary)
            && ActiveTool.View.CutsAway)
        {
            int floor = _ctx.CutawayFloor(texel.X, texel.Y, _ctx.CutawayY, out _);
            if (floor >= data.WorldMinY)
            {
                SetCutaway(floor + cutawayHeadroom);
                RebuildFull();
            }
            // Withheld from the tool: this press aimed the plane, not the brush.
            mods &= ~EStrokeMods.Pick;
        }
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

    // Deferred to _Process, so several calls in one frame cost ONE rebuild. Key
    // repeat is why: T/G scrubs the cutaway and every press rebuilt the whole
    // map synchronously, so holding the key queued rebuilds faster than they
    // could run and the painter stopped answering the keyboard.
    private void RebuildFull()
    {
        _fullRebuildPending = true;
        PushDisplay();
    }

    private bool _fullRebuildPending;

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

        // Stamps draw on EVERY view, not only the scene tool's. A building is a
        // fact about the ground you need while painting the things that sit
        // beside it — the same argument climbing routes and spill edges are
        // inked everywhere — and a footprint you cannot see is one you scatter
        // props into. Resolved ONCE per rebuild: the hit test walks the
        // placement list, and asking per texel would make drawing the map cost
        // more the more buildings the document holds.
        bool cut = view.CutsAway && _ctx.IsCutAway;
        int clipY = cut ? _ctx.CutawayY : int.MaxValue;
        WorldMapState.StampPlan stamps =
            _ctx.PlanStamps(new Rect2I(x0, z0, x1 - x0, z1 - z0), clipY);
        // Only the scene tool answers with a selection, so the highlight shows
        // while stamps are being edited and the plan stays plain elsewhere.
        SubscenePlacement selectedStamp = ActiveTool.SelectedPlacement;

        // The cutaway's per-cell answers, resolved once and read by BOTH the
        // fill and the outline pass. CutawayFloor was being asked four times per
        // cell — once for the colour and three times over as each cell's
        // neighbours recomputed it.
        if (cut)
        {
            ResolveCutaway(x0, z0, x1, z1, clipY, _ctx.ShowWater && view.DrawsWater);
        }

        // OFF by default, and the guard is the point: C# evaluates arguments
        // eagerly, so `Lerp(1, ReliefShade(...), 0)` still paid for the hillshade
        // — four Image.GetPixel calls per cell, 120 ms of a 620 ms rebuild — to
        // multiply it by zero.
        bool relief = reliefStrength > 0f;

        for (int px = x0; px < x1; px++)
        {
            for (int pz = z0; pz < z1; pz++)
            {
                Color c = _ctx.StampColorAt(stamps, px, pz,
                    view.ColorAt(_ctx, px, pz), selectedStamp);
                if (relief)
                {
                    // Shading water would put the seabed's shape straight back
                    // into a colour whose whole job is to say you cannot see the
                    // ground.
                    float mul = _ctx.IsSubmerged(px, pz)
                        ? 1f
                        : Mathf.Lerp(1f, _ctx.ReliefShade(px, pz, light) / flat, reliefStrength);
                    c = new Color(
                        Mathf.Clamp(c.R * mul, 0f, 1f),
                        Mathf.Clamp(c.G * mul, 0f, 1f),
                        Mathf.Clamp(c.B * mul, 0f, 1f));
                }
                FillCell(px, pz, c, cut && _buried[pz * data.ImageWidth + px]);
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
                DrawStepEdges(px, pz, showMinor, waterVisible, cut);
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

    // Per-cell cutaway answers for the rebuild in progress: the surface the map
    // DREW at each cell, and whether it was found through rock. Map-sized and
    // reused, so a rebuild allocates nothing; only the rect being rebuilt is
    // written, and only the cells it wrote are read back.
    private int[] _cutSurface;
    private bool[] _buried;

    // One CutawayFloor per cell for the whole rebuild. Reaches one cell further
    // out on EVERY side than the fill: the outline pass starts a cell back,
    // because a cell's -X and -Z edges are owned by its left and upper
    // neighbours, and it also asks its +X and +Z neighbours for their height.
    // Leave either margin out and the outline reads a cell this pass never
    // wrote, which inks the rebuilt rect's own border wrong — a partial-rebuild
    // artefact that only shows where two rebuilds meet.
    private void ResolveCutaway(int x0, int z0, int x1, int z1, int clipY, bool waterVisible)
    {
        int w = data.ImageWidth;
        if (_cutSurface == null || _cutSurface.Length != w * data.ImageHeight)
        {
            _cutSurface = new int[w * data.ImageHeight];
            _buried = new bool[w * data.ImageHeight];
        }
        int ex1 = Mathf.Min(data.ImageWidth, x1 + 1);
        int ez1 = Mathf.Min(data.ImageHeight, z1 + 1);
        for (int px = Mathf.Max(0, x0 - 1); px < ex1; px++)
        {
            for (int pz = Mathf.Max(0, z0 - 1); pz < ez1; pz++)
            {
                int floor = _ctx.CutawayFloor(px, pz, clipY, out bool roofed);
                int i = pz * w + px;
                _buried[i] = roofed;
                // Rock with no floor under it reads as ONE flat level above
                // everything drawn, so only its edge inks and never a contour
                // inside it. Water only where the cut is open to it: a floor
                // seen through rock is not under the pool standing on that rock.
                _cutSurface[i] = floor < data.WorldMinY
                    ? clipY + 1
                    : waterVisible && !roofed
                        ? Mathf.Max(floor, Mathf.Min(_ctx.WaterSurface(px, pz), clipY))
                        : floor;
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
    private void DrawStepEdges(int px, int pz, bool showMinor, bool waterVisible, bool cut)
    {
        int h = SurfaceHeight(px, pz, waterVisible, cut);
        if (px + 1 < data.ImageWidth)
        {
            int hn = SurfaceHeight(px + 1, pz, waterVisible, cut);
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
            int hn = SurfaceHeight(px, pz + 1, waterVisible, cut);
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

    // What the outlines follow — whatever the view actually DREW, or they would
    // contour one surface while the colours show another. Off a cutaway that is
    // the top of the solid world with the edit layer included (a bridge deck
    // stands above the height map, a carved roof below it); on one it is the
    // per-cell answer ResolveCutaway already worked out for the fill pass.
    private int SurfaceHeight(int px, int pz, bool waterVisible, bool cut)
    {
        return cut
            ? _cutSurface[pz * data.ImageWidth + px]
            : _ctx.DisplaySurface(px, pz, waterVisible, int.MaxValue);
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

    // `dither` checkerboards the cell against the rock colour, which is how a
    // floor seen THROUGH rock is drawn: the band underneath keeps the exact
    // colour it was authored as — a tint would put it in a shade some other
    // height already owns — while the texture says you are looking at it through
    // something. Keyed on ABSOLUTE display pixels, not on the cell, so the
    // pattern runs continuously across a whole buried passage instead of
    // restarting every metre.
    private void FillCell(int px, int pz, Color c, bool dither = false)
    {
        byte r = (byte)(c.R * 255f);
        byte g = (byte)(c.G * 255f);
        byte b = (byte)(c.B * 255f);
        Color rock = data.cutawayRockColor;
        byte rr = (byte)(rock.R * 255f);
        byte rg = (byte)(rock.G * 255f);
        byte rb = (byte)(rock.B * 255f);
        for (int dz = 0; dz < pixelsPerMeter; dz++)
        {
            int y = pz * pixelsPerMeter + dz;
            int row = y * DisplayWidth;
            for (int dx = 0; dx < pixelsPerMeter; dx++)
            {
                int x = px * pixelsPerMeter + dx;
                int i = (row + x) * 4;
                bool ink = dither && ((x + y) & 1) == 0;
                _pixels[i] = ink ? rr : r;
                _pixels[i + 1] = ink ? rg : g;
                _pixels[i + 2] = ink ? rb : b;
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
        if (_fullRebuildPending)
        {
            _fullRebuildPending = false;
            RebuildDisplay(new Rect2I(0, 0, data.ImageWidth, data.ImageHeight));
        }
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
        hud.SetRadius(ActiveTool.Radius, ScreenPerMeter);

        hud.entityInspector?.Show(ActiveTool.SelectedEntity);

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
