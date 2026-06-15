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

    public static WorldMapPainter Current;
    public Action onQuitToMenu;

    private WorldMapState _ctx;
    private IWorldMapTool[] _tools;
    private int _toolIndex;

    private Image _display;
    private ImageTexture _displayTex;

    private IWorldMapTool ActiveTool => _tools[_toolIndex];

    public void Init()
    {
        Current = this;

        _ctx = new WorldMapState(data);

        _tools = new IWorldMapTool[]
        {
            new ElevationTool(),
            new WaterTool(),
            new TunnelTool(),
            new RegionTool(),
            new ZoneTool(),
            new ScatterTool(),
        };
        _toolIndex = 0;

        _display = Image.CreateEmpty(data.ImageWidth, data.ImageHeight, false, Image.Format.Rgba8);
        RebuildDisplay(new Rect2I(0, 0, data.ImageWidth, data.ImageHeight));
        _displayTex = ImageTexture.CreateFromImage(_display);
        canvas.SetDisplay(_displayTex, data.ImageWidth, data.ImageHeight);
        canvas.CursorRadiusTexels = ActiveTool.Radius;
        canvas.OnPaint = OnCanvasPaint;
        canvas.OnHover = hud.SetCoords;

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
        if (e.IsActionPressed("UseItem"))   // Q — cycle tool parameter
        {
            ActiveTool.Cycle(_ctx, -1);
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e.IsActionPressed("Interact"))  // E — cycle tool parameter
        {
            ActiveTool.Cycle(_ctx, 1);
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e.IsActionPressed("EditorUp"))  // R — active elevation / cross-section up
        {
            ActiveTool.AdjustLevel(_ctx, 1);
            RebuildFull();
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e.IsActionPressed("EditorDown"))  // F — active elevation / cross-section down
        {
            ActiveTool.AdjustLevel(_ctx, -1);
            RebuildFull();
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            if (k.Keycode == Key.S && k.CtrlPressed)
            {
                _ctx.Save();
                GetViewport().SetInputAsHandled();
                return;
            }
            switch (k.Keycode)
            {
                case Key.Tab:
                    _toolIndex = (_toolIndex + 1) % _tools.Length;
                    canvas.CursorRadiusTexels = ActiveTool.Radius;
                    RebuildFull();
                    UpdateHud();
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.Bracketleft:
                    ActiveTool.Radius = Mathf.Max(0.5f, ActiveTool.Radius - 1f);
                    canvas.CursorRadiusTexels = ActiveTool.Radius;
                    UpdateHud();
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.Bracketright:
                    ActiveTool.Radius = Mathf.Min(256f, ActiveTool.Radius + 1f);
                    canvas.CursorRadiusTexels = ActiveTool.Radius;
                    UpdateHud();
                    GetViewport().SetInputAsHandled();
                    return;
            }
        }
    }

    private void OnCanvasPaint(Vector2I texel, bool erase)
    {
        ActiveTool.Paint(_ctx, brush, texel, erase);
        RebuildDisplay(ExpandToChunks(BrushRect(texel, ActiveTool.Radius)));
        PushDisplay();
    }

    private void RebuildFull()
    {
        RebuildDisplay(new Rect2I(0, 0, data.ImageWidth, data.ImageHeight));
        PushDisplay();
    }

    private void RebuildDisplay(Rect2I rect)
    {
        IWorldMapView view = ActiveTool.View;
        int x0 = Mathf.Max(0, rect.Position.X);
        int z0 = Mathf.Max(0, rect.Position.Y);
        int x1 = Mathf.Min(data.ImageWidth, rect.Position.X + rect.Size.X);
        int z1 = Mathf.Min(data.ImageHeight, rect.Position.Y + rect.Size.Y);
        for (int px = x0; px < x1; px++)
        {
            for (int pz = z0; pz < z1; pz++)
            {
                _display.SetPixel(px, pz, view.ColorAt(_ctx, px, pz));
            }
        }
    }

    private void PushDisplay()
    {
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
        hud.SetTool(ActiveTool.Name);
        string status = ActiveTool.StatusText(_ctx);
        string level = ActiveTool.LevelText(_ctx);
        hud.SetStatus(string.IsNullOrEmpty(level) ? status : $"{status}  |  {level}");
        hud.SetRadius(ActiveTool.Radius);
    }

    public override void _ExitTree()
    {
        if (Current == this)
        {
            Current = null;
        }
    }
}
