using System;
using Godot;

// In-game world-map painting mode — the first step in the authoring chain.
// Hosts a live World preview and a 2D paint canvas. All behaviour is delegated
// to the active IWorldMapTool: it owns the brush size + its parameters, paints
// its layer, drives the live re-bake, and supplies the IWorldMapView that
// colours the 2D map. Switching tools switches both the edit and the view.
[GlobalClass]
public partial class WorldMapPainter : Node3D
{
    [Export] public GameCamera camera;
    [Export] public WorldMapCanvas canvas;
    [Export] public WorldMapHud hud;
    [Export] public WorldMapData data;
    [Export] public WorldMapBrush brush;

    public static WorldMapPainter Current;
    public Action onQuitToMenu;

    private const float MOVE_SPEED = 30f;
    // Clip plane parked far above the world so the preview shows everything.
    private const float PREVIEW_CLIP = 100000f;

    private World _world;
    private WorldMapState _ctx;
    private IWorldMapTool[] _tools;
    private int _toolIndex;

    private Image _display;
    private ImageTexture _displayTex;
    private bool _preview;
    private Vector3 _cursorPosition;

    private IWorldMapTool ActiveTool => _tools[_toolIndex];

    public void Init()
    {
        Current = this;

        _ctx = new WorldMapState(data);
        _ctx.BuildWorld();
        _cursorPosition = _ctx.WorldState.Spawn;

        _world = new World();
        AddChild(_world);
        _ctx.World = _world;
        _world.Initialize(_ctx.WorldState, _cursorPosition, camera, null, () => _cursorPosition);
        _world.EnableEditorMode();
        _world.UpdateEntityLoading(_cursorPosition);

        camera.Init(this);
        camera.ManualClipMode = true;
        camera.SetInitialPosition(_cursorPosition);
        camera.SetClip(PREVIEW_CLIP, _cursorPosition);

        _tools = new IWorldMapTool[]
        {
            new ElevationTool(),
            new WaterTool(),
            new TunnelTool(),
            new RegionTool(),
            new ZoneTool(),
        };
        _toolIndex = 0;

        _display = Image.CreateEmpty(data.ImageWidth, data.ImageHeight, false, Image.Format.Rgba8);
        RebuildDisplay(new Rect2I(0, 0, data.ImageWidth, data.ImageHeight));
        _displayTex = ImageTexture.CreateFromImage(_display);
        canvas.SetDisplay(_displayTex, data.ImageWidth, data.ImageHeight);
        canvas.CursorRadiusTexels = ActiveTool.Radius;
        canvas.OnPaint = OnCanvasPaint;

        SetPreview(false);
        UpdateHud();
    }

    public override void _Process(double delta)
    {
        if (ConsoleUI.IsOpen)
        {
            return;
        }
        float dt = (float)delta;

        if (_preview)
        {
            Vector2 input = Input.GetVector("MoveLeft", "MoveRight", "MoveUp", "MoveDown");
            if (input.LengthSquared() > 0f)
            {
                float yaw = camera.Yaw;
                Vector3 forward = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
                Vector3 right = new Vector3(forward.Z, 0, -forward.X);
                _cursorPosition += (forward * input.Y + right * input.X) * MOVE_SPEED * dt;
            }
            _world.UpdateEntityLoading(_cursorPosition);
        }

        camera.UpdateCamera(delta, _cursorPosition, 0f);
        camera.SetClip(PREVIEW_CLIP, _cursorPosition);
        hud.SetCoords(_cursorPosition);
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
        if (e.IsActionPressed("EditorUp"))  // active elevation / cross-section up
        {
            ActiveTool.AdjustLevel(_ctx, 1);
            RebuildFull();
            UpdateHud();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e.IsActionPressed("EditorDown"))
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
                case Key.Space:
                    SetPreview(!_preview);
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

    private void SetPreview(bool preview)
    {
        _preview = preview;
        canvas.Visible = !preview;
        UpdateHud();
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
        hud.SetView(_preview);
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
