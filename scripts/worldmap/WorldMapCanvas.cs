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

    // Display pixels per map metre. The painter renders the image at this
    // resolution and the canvas draws it 1:1, so a metre is exactly this many
    // screen pixels and the step outlines stay crisp.
    public int PixelsPerTexel = 3;

    // (texel coord, isErase) — isErase is the right mouse button.
    public Action<Vector2I, bool> OnPaint;

    // (texel, modifiers) on button press, before the stroke paints.
    public Action<Vector2I, EStrokeMods> OnStrokeStart;

    // Texel coord under the cursor on mouse motion (for the HUD readout).
    public Action<Vector2I> OnHover;

    // One wheel notch, +1 up / -1 down. The painter decides what it means.
    public Action<int> OnAdjustRadius;

    // Same, with ctrl held: the painter re-renders the map at a new scale and
    // assigns PixelsPerTexel before returning.
    public Action<int> OnZoom;

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
                        Zoom(dir, mb.Position);
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
                        | (mb.CtrlPressed ? EStrokeMods.ConstrainAbove : EStrokeMods.None);
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
                    _painting = false;
                    _holdingPick = false;
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
    private void Zoom(int dir, Vector2 local)
    {
        int oldPpt = Mathf.Max(1, PixelsPerTexel);
        Vector2 texel = (local - ImageRect().Position) / oldPpt;
        OnZoom?.Invoke(dir);
        if (PixelsPerTexel != oldPpt)
        {
            // _imgW/_imgH were replaced by the painter's SetDisplay above.
            _pan = local - texel * PixelsPerTexel - (Size - new Vector2(_imgW, _imgH)) * 0.5f;
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
            float radius = Mathf.Max(2f, CursorRadiusTexels * PixelsPerTexel);
            // Dark backing arc on BOTH sides of the coloured one: a band colour
            // can be as dark as 0.2 and would otherwise vanish into the terrain
            // it is being compared against.
            DrawArc(_mouseLocal, radius - 1f, 0f, Mathf.Tau, 48, new Color(0f, 0f, 0f, 0.7f), 1f, true);
            DrawArc(_mouseLocal, radius, 0f, Mathf.Tau, 48, new Color(CursorColor.R, CursorColor.G, CursorColor.B, 0.95f), 2f, true);
            DrawArc(_mouseLocal, radius + 1.5f, 0f, Mathf.Tau, 48, new Color(0f, 0f, 0f, 0.7f), 1f, true);
        }
    }

    // The image at its NATIVE pixel size, centred, plus any pan. Deliberately
    // not fitted to the control: a fitted map resamples metres to fractional
    // pixels, which smears the one-pixel step outlines away.
    private Rect2 ImageRect()
    {
        Vector2 c = Size;
        if (_imgW <= 0 || _imgH <= 0 || c.X <= 0f || c.Y <= 0f)
        {
            return new Rect2(Vector2.Zero, c);
        }
        var size = new Vector2(_imgW, _imgH);
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
        int ppt = Mathf.Max(1, PixelsPerTexel);
        texel = new Vector2I(Mathf.FloorToInt(rel.X) / ppt, Mathf.FloorToInt(rel.Y) / ppt);
        return true;
    }
}
