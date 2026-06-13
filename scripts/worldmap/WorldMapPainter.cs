using System;
using System.Collections.Generic;
using Godot;

// In-game world-map painting mode — the first step in the authoring chain.
// Paints layered raster documents (v1: elevation + region), bakes them live
// into a real WorldState so the 3D preview updates under the brush, and
// exports a .hike for the downstream WorldEditor / game to load.
//
// Structure mirrors WorldEditor (Node3D host + GameCamera + World preview),
// but it edits the WorldMapData *document* (images) and re-bakes, rather than
// writing voxels directly.
[GlobalClass]
public partial class WorldMapPainter : Node3D
{
    [Export] public GameCamera camera;
    [Export] public WorldMapCanvas canvas;
    [Export] public WorldMapHud hud;
    [Export] public WorldMapData data;
    [Export] public WorldMapBrush brush;

    // Live instance, mirrors WorldEditor.Current / World.Current for any
    // console-driven hooks. Cleared in _ExitTree.
    public static WorldMapPainter Current;

    public Action onQuitToMenu;

    private const float MOVE_SPEED = 30f;
    // Clip plane parked far above the world so the preview shows everything
    // (no ceiling cutaway — there's no player to occlude around).
    private const float PREVIEW_CLIP = 100000f;

    private enum ELayer
    {
        Elevation = 0,
        Region = 1,
    }

    private World _world;
    private WorldState _worldState;
    private Image _elevation;   // Rf, per-column normalized height (truth)
    private Image _region;      // R8, per-chunk region index
    private Image _display;     // Rgba8, colourised active layer for the canvas
    private ImageTexture _displayTex;

    private ELayer _layer = ELayer.Elevation;
    private int _regionIndex = 1;     // selected region to paint (0 = none/border)
    private bool _preview;            // false = 2D map paint, true = 3D fly-over
    private Vector3 _cursorPosition;

    public void Init()
    {
        Current = this;

        _elevation = data.LoadOrCreateElevation();
        _region = data.LoadOrCreateRegion();
        _worldState = WorldMapBake.Build(data, _elevation, _region);
        _cursorPosition = _worldState.Spawn;

        _world = new World();
        AddChild(_world);
        _world.Initialize(_worldState, _cursorPosition, camera, null, () => _cursorPosition);
        _world.EnableEditorMode();
        _world.UpdateEntityLoading(_cursorPosition);

        camera.Init(this);
        camera.ManualClipMode = true;
        camera.SetInitialPosition(_cursorPosition);
        camera.SetClip(PREVIEW_CLIP, _cursorPosition);

        _display = Image.CreateEmpty(data.ImageWidth, data.ImageHeight, false, Image.Format.Rgba8);
        RebuildDisplay(new Rect2I(0, 0, data.ImageWidth, data.ImageHeight));
        _displayTex = ImageTexture.CreateFromImage(_display);

        canvas.SetDisplay(_displayTex, data.ImageWidth, data.ImageHeight);
        canvas.CursorRadiusTexels = brush.Radius;
        canvas.OnPaint = OnCanvasPaint;

        if (data.RegionCount <= 0)
        {
            _regionIndex = 0;
        }

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
        if (e.IsActionPressed("UseItem"))
        {
            CycleTool(-1);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e.IsActionPressed("Interact"))
        {
            CycleTool(1);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            if (k.Keycode == Key.S && k.CtrlPressed)
            {
                Save();
                GetViewport().SetInputAsHandled();
                return;
            }
            switch (k.Keycode)
            {
                case Key.Tab:
                    CycleLayer();
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.Space:
                    SetPreview(!_preview);
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.Bracketleft:
                    brush.Radius = Mathf.Max(0.5f, brush.Radius - 1f);
                    canvas.CursorRadiusTexels = brush.Radius;
                    UpdateHud();
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.Bracketright:
                    brush.Radius = Mathf.Min(256f, brush.Radius + 1f);
                    canvas.CursorRadiusTexels = brush.Radius;
                    UpdateHud();
                    GetViewport().SetInputAsHandled();
                    return;
            }
        }
    }

    private void OnCanvasPaint(Vector2I texel, bool erase)
    {
        if (_layer == ELayer.Elevation)
        {
            PaintElevation(texel, erase);
        }
        else
        {
            PaintRegion(texel, erase);
        }
    }

    private void PaintElevation(Vector2I texel, bool erase)
    {
        EBrushOp op = erase ? EBrushOp.Lower : brush.Op;
        Rect2I rect = brush.Stamp(_elevation, texel, op);
        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
        {
            return;
        }

        var changed = new List<Vector3I>();
        WorldMapBake.RebakeElevation(_worldState, data, _elevation, rect, changed);
        if (changed.Count > 0)
        {
            _world.UpdateLighting(changed);
            Vector3 center = new Vector3(
                data.WorldMinX + texel.X + 0.5f,
                _cursorPosition.Y,
                data.WorldMinZ + texel.Y + 0.5f);
            _world.RebuildNearbyChunkMeshes(center, changed);
        }
        RebuildDisplay(rect);
        PushDisplay();
    }

    private void PaintRegion(Vector2I texel, bool erase)
    {
        if (data.RegionCount <= 0)
        {
            return;
        }
        byte value = (byte)(erase ? 0 : Mathf.Clamp(_regionIndex, 0, data.RegionCount - 1));

        int r = Mathf.CeilToInt(brush.Radius);
        int x0 = Mathf.Max(0, texel.X - r);
        int x1 = Mathf.Min(data.ImageWidth - 1, texel.X + r);
        int z0 = Mathf.Max(0, texel.Y - r);
        int z1 = Mathf.Min(data.ImageHeight - 1, texel.Y + r);

        bool any = false;
        int minCx = int.MaxValue, minCz = int.MaxValue, maxCx = int.MinValue, maxCz = int.MinValue;
        for (int px = x0; px <= x1; px++)
        {
            for (int pz = z0; pz <= z1; pz++)
            {
                float dx = px - texel.X;
                float dz = pz - texel.Y;
                if (dx * dx + dz * dz > brush.Radius * brush.Radius)
                {
                    continue;
                }
                Vector2I rt = data.ColumnTexelToRegionTexel(px, pz);
                _region.SetPixel(rt.X, rt.Y, new Color(value / 255f, 0f, 0f, 1f));
                any = true;
                minCx = Mathf.Min(minCx, rt.X);
                minCz = Mathf.Min(minCz, rt.Y);
                maxCx = Mathf.Max(maxCx, rt.X);
                maxCz = Mathf.Max(maxCz, rt.Y);
            }
        }
        if (!any)
        {
            return;
        }

        WorldMapBake.RebakeRegion(_worldState, data, _region,
            new Rect2I(minCx, minCz, maxCx - minCx + 1, maxCz - minCz + 1));
        RebuildDisplay(new Rect2I(x0, z0, x1 - x0 + 1, z1 - z0 + 1));
        PushDisplay();
    }

    private void CycleTool(int dir)
    {
        if (_layer == ELayer.Elevation)
        {
            int n = System.Enum.GetValues<EBrushOp>().Length;
            brush.Op = (EBrushOp)(((int)brush.Op + dir + n) % n);
        }
        else if (data.RegionCount > 0)
        {
            _regionIndex = (_regionIndex + dir + data.RegionCount) % data.RegionCount;
        }
        UpdateHud();
    }

    private void CycleLayer()
    {
        _layer = _layer == ELayer.Elevation ? ELayer.Region : ELayer.Elevation;
        RebuildDisplay(new Rect2I(0, 0, data.ImageWidth, data.ImageHeight));
        PushDisplay();
        UpdateHud();
    }

    private void SetPreview(bool preview)
    {
        _preview = preview;
        canvas.Visible = !preview;
        UpdateHud();
    }

    // Recolour the active layer into the display image over the given column
    // rect (clamped). The canvas shows whatever the active layer renders.
    private void RebuildDisplay(Rect2I rect)
    {
        int x0 = Mathf.Max(0, rect.Position.X);
        int z0 = Mathf.Max(0, rect.Position.Y);
        int x1 = Mathf.Min(data.ImageWidth, rect.Position.X + rect.Size.X);
        int z1 = Mathf.Min(data.ImageHeight, rect.Position.Y + rect.Size.Y);
        for (int px = x0; px < x1; px++)
        {
            for (int pz = z0; pz < z1; pz++)
            {
                _display.SetPixel(px, pz, ColorForActiveLayer(px, pz));
            }
        }
    }

    private void PushDisplay()
    {
        _displayTex.Update(_display);
        canvas.Refresh();
    }

    private Color ColorForActiveLayer(int px, int pz)
    {
        if (_layer == ELayer.Elevation)
        {
            return ElevationColor(_elevation.GetPixel(px, pz).R);
        }
        Vector2I rt = data.ColumnTexelToRegionTexel(px, pz);
        byte idx = (byte)Mathf.RoundToInt(_region.GetPixel(rt.X, rt.Y).R * 255f);
        return RegionColor(idx);
    }

    // Hypsometric-ish ramp: water at/below sea, then green -> brown -> white.
    private static Color ElevationColor(float v)
    {
        if (v <= 0.0001f)
        {
            return new Color(0.13f, 0.32f, 0.55f);
        }
        Color low = new Color(0.27f, 0.5f, 0.22f);
        Color mid = new Color(0.5f, 0.4f, 0.26f);
        Color high = new Color(0.95f, 0.95f, 0.95f);
        return v < 0.5f ? low.Lerp(mid, v / 0.5f) : mid.Lerp(high, (v - 0.5f) / 0.5f);
    }

    // Distinct hue per region index; 0 = unpainted/border (dim grey).
    private static Color RegionColor(int idx)
    {
        if (idx <= 0)
        {
            return new Color(0.22f, 0.22f, 0.24f);
        }
        float hue = (idx * 0.61803398875f) % 1f;
        return Color.FromHsv(hue, 0.55f, 0.85f);
    }

    private void Save()
    {
        data.SaveElevation(_elevation);
        data.SaveRegion(_region);
        if (!string.IsNullOrEmpty(data.OutputWorldPath))
        {
            try
            {
                WorldFile.Write(data.OutputWorldPath, _worldState);
                GD.Print($"WorldMapPainter: saved layers + baked world to {data.OutputWorldPath}");
            }
            catch (Exception e)
            {
                GD.PrintErr($"WorldMapPainter: world export failed: {e.Message}");
            }
        }
        else
        {
            GD.Print("WorldMapPainter: saved layers (no OutputWorldPath set, skipped .hike export)");
        }
    }

    private void UpdateHud()
    {
        hud.SetView(_preview);
        hud.SetLayer(_layer == ELayer.Elevation ? "Elevation" : "Region");
        hud.SetTool(_layer == ELayer.Elevation ? brush.Op.ToString() : $"Region {_regionIndex}");
        hud.SetRadius(brush.Radius);
    }

    public override void _ExitTree()
    {
        if (Current == this)
        {
            Current = null;
        }
    }
}
