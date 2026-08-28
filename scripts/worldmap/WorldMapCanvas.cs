using System;
using Godot;

// The 2D paint surface: a full-screen Control that shows the active layer's
// colourised image (the "visualized as image" view) and turns mouse strokes
// into texel-space paint callbacks. Deliberately dumb — the painter owns the
// layer data, colouring, and bake; the canvas only fits the image, draws the
// brush cursor, and reports where the user painted.
[GlobalClass]
public partial class WorldMapCanvas : Control
{
    // Set by the painter; the canvas just blits it. Updated in place by the
    // painter (ImageTexture.Update) on each stroke.
    public Texture2D DisplayTex;

    // Brush radius in texels, for the on-screen cursor ring.
    public float CursorRadiusTexels = 12f;

    // Ring colour, set by the painter from the active tool.
    public Color CursorColor = Colors.White;

    // Pixels per map metre in the IMAGE — the resolution the painter rasterised
    // at, which is what keeps a step outline a thin line on a voxel edge rather
    // than a metre-wide block.
    public int PixelsPerTexel = 3;

    // Integer magnification applied at DRAW time. Split from the raster because
    // zooming in changes nothing about the image, only how big it is drawn: at
    // this end a notch is one QueueRedraw instead of a reallocation, a full
    // repaint and a texture upload of up to 72 MB. Nearest filtering (set in
    // _Ready) is what makes the magnified pixels stay crisp squares.
    public int Zoom = 1;

    // Screen pixels per map metre — the two multiplied.
    public float ScreenPerTexel => Mathf.Max(1, PixelsPerTexel) * Mathf.Max(1, Zoom);

    private Vector2 DisplaySize => new Vector2(_imgW, _imgH) * Mathf.Max(1, Zoom);

    // (texel coord, isErase) — isErase is the right mouse button.
    public Action<Vector2I, bool> OnPaint;

    // (texel, modifiers) on button press, before the stroke paints.
    public Action<Vector2I, EStrokeMods> OnStrokeStart;

    // Button released (or the press ended any other way). The whole drag is one
    // undoable edit, so this is where it closes.
    public Action OnStrokeEnd;

    // Texel coord under the cursor on mouse motion (for the HUD readout).
    public Action<Vector2I> OnHover;

    // One wheel notch, +1 up / -1 down. The painter decides what it means.
    public Action<int> OnAdjustRadius;

    // Same, with ctrl held: the painter re-renders the map at a new scale and
    // assigns PixelsPerTexel before returning.
    public Action<int> OnZoom;

    // Same, with alt held: one metre of cutaway per notch. The plane spans the
    // whole world's height and T/G walks it a metre a press, so on a tall
    // document reaching the ground from the top took most of a hundred presses
    // — which reads as the control doing nothing at all.
    public Action<int> OnAdjustCutaway;

    // Display-image size in PIXELS (texels * PixelsPerTexel).
    private int _imgW;
    private int _imgH;
    // Pan offset from centred, in pixels. Only meaningful once the image is
    // bigger than the control; clamped so it can never be dragged off-screen.
    private Vector2 _pan;
    private bool _panning;
    private bool _painting;
    private bool _erase;
    // An alt stroke holds its first stamp until the cursor leaves the metre it
    // sampled, so a plain alt+click is a pure eyedropper and only an actual drag
    // starts spreading that height.
    private bool _holdingPick;
    private Vector2I _pickTexel;
    private bool _hasMouse;
    private Vector2 _mouseLocal;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    public void SetDisplay(Texture2D tex, int imgWidth, int imgHeight)
    {
        DisplayTex = tex;
        _imgW = imgWidth;
        _imgH = imgHeight;
        QueueRedraw();
    }

    public void Refresh()
    {
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown)
            {
                // A notch reports both a press and a release; act on one of them.
                if (mb.Pressed)
                {
                    int dir = mb.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                    if (mb.CtrlPressed)
                    {
                        ZoomAbout(dir, mb.Position);
                    }
                    else if (mb.AltPressed)
                    {
                        OnAdjustCutaway?.Invoke(dir);
                    }
                    else
                    {
                        OnAdjustRadius?.Invoke(dir);
                    }
                }
                AcceptEvent();
                return;
            }
            if (mb.ButtonIndex == MouseButton.Middle)
            {
                _panning = mb.Pressed;
                AcceptEvent();
                return;
            }
            if (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right)
            {
                if (mb.Pressed)
                {
                    bool pick = mb.AltPressed && TryTexel(mb.Position, out _pickTexel);
                    EStrokeMods mods = (pick ? EStrokeMods.Pick : EStrokeMods.None)
                        | (mb.ShiftPressed ? EStrokeMods.Constrain : EStrokeMods.None)
                        | (mb.CtrlPressed ? EStrokeMods.ConstrainAbove : EStrokeMods.None)
                        | (mb.ButtonIndex == MouseButton.Right
                            ? EStrokeMods.Secondary : EStrokeMods.None);
                    _painting = true;
                    _erase = mb.ButtonIndex == MouseButton.Right;
                    _holdingPick = pick;
                    if (TryTexel(mb.Position, out Vector2I start))
                    {
                        OnStrokeStart?.Invoke(start, mods);
                    }
                    if (!pick)
                    {
                        PaintAt(mb.Position);
                    }
                }
                else
                {
                    bool was = _painting;
                    _painting = false;
                    _holdingPick = false;
                    if (was)
                    {
                        OnStrokeEnd?.Invoke();
                    }
                }
                AcceptEvent();
            }
        }
        else if (e is InputEventMouseMotion mm)
        {
            _hasMouse = true;
            _mouseLocal = mm.Position;
            if (_panning)
            {
                _pan += mm.Relative;
                QueueRedraw();
                return;
            }
            if (_painting && _holdingPick)
            {
                if (TryTexel(mm.Position, out Vector2I moved) && moved != _pickTexel)
                {
                    _holdingPick = false;
                    PaintAt(mm.Position);
                }
            }
            else if (_painting)
            {
                PaintAt(mm.Position);
            }
            if (OnHover != null && TryTexel(mm.Position, out Vector2I hovered))
            {
                OnHover(hovered);
            }
            QueueRedraw();
        }
    }

    // Zoom about the cursor: whichever metre is under the pointer stays under it,
    // so zooming in on a feature does not throw it off screen.
    private void ZoomAbout(int dir, Vector2 local)
    {
        float oldScale = ScreenPerTexel;
        Vector2 texel = (local - ImageRect().Position) / oldScale;
        OnZoom?.Invoke(dir);
        float newScale = ScreenPerTexel;
        if (!Mathf.IsEqualApprox(newScale, oldScale))
        {
            // PixelsPerTexel / Zoom / _imgW were all replaced by the painter
            // before this returned.
            _pan = local - texel * newScale - (Size - DisplaySize) * 0.5f;
        }
        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        if (what == (int)Control.NotificationMouseExit)
        {
            _hasMouse = false;
            _painting = false;
            _holdingPick = false;
            _panning = false;
            QueueRedraw();
        }
    }

    private void PaintAt(Vector2 local)
    {
        if (!TryTexel(local, out Vector2I texel))
        {
            return;
        }
        OnPaint?.Invoke(texel, _erase);
    }

    public override void _Draw()
    {
        Rect2 fit = ImageRect();
        if (DisplayTex != null)
        {
            DrawTextureRect(DisplayTex, fit, false);
            DrawRect(fit, new Color(0f, 0f, 0f, 0.6f), false, 2f);
        }
        if (_hasMouse && _imgW > 0)
        {
            float radius = Mathf.Max(2f, CursorRadiusTexels * ScreenPerTexel);
            // Dark backing arc on BOTH sides of the coloured one: a band colour
            // can be as dark as 0.2 and would otherwise vanish into the terrain
            // it is being compared against.
            DrawArc(_mouseLocal, radius - 1f, 0f, Mathf.Tau, 48, new Color(0f, 0f, 0f, 0.7f), 1f, true);
            DrawArc(_mouseLocal, radius, 0f, Mathf.Tau, 48, new Color(CursorColor.R, CursorColor.G, CursorColor.B, 0.95f), 2f, true);
            DrawArc(_mouseLocal, radius + 1.5f, 0f, Mathf.Tau, 48, new Color(0f, 0f, 0f, 0.7f), 1f, true);
        }
    }

    // The image at an INTEGER multiple of its native pixel size, centred, plus
    // any pan. Deliberately never fitted to the control: a fitted map resamples
    // metres to fractional pixels, which smears the one-pixel step outlines
    // away. An integer multiple cannot, so magnifying stays exact.
    private Rect2 ImageRect()
    {
        Vector2 c = Size;
        if (_imgW <= 0 || _imgH <= 0 || c.X <= 0f || c.Y <= 0f)
        {
            return new Rect2(Vector2.Zero, c);
        }
        Vector2 size = DisplaySize;
        // Pan only has room where the image overflows; otherwise it stays put.
        float slackX = Mathf.Max(0f, (size.X - c.X) * 0.5f);
        float slackY = Mathf.Max(0f, (size.Y - c.Y) * 0.5f);
        _pan = new Vector2(
            Mathf.Clamp(_pan.X, -slackX, slackX),
            Mathf.Clamp(_pan.Y, -slackY, slackY));
        return new Rect2(((c - size) * 0.5f + _pan).Round(), size);
    }

    private bool TryTexel(Vector2 local, out Vector2I texel)
    {
        texel = default;
        Rect2 fit = ImageRect();
        if (fit.Size.X <= 0f || fit.Size.Y <= 0f)
        {
            return false;
        }
        Vector2 rel = local - fit.Position;
        if (rel.X < 0f || rel.X >= fit.Size.X || rel.Y < 0f || rel.Y >= fit.Size.Y)
        {
            return false;
        }
        float scale = ScreenPerTexel;
        texel = new Vector2I(Mathf.FloorToInt(rel.X / scale), Mathf.FloorToInt(rel.Y / scale));
        return true;
    }
}
