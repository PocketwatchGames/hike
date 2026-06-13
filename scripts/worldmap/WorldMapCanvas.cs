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

    // (texel coord, isErase) — isErase is the right mouse button.
    public Action<Vector2I, bool> OnPaint;

    private int _imgW;
    private int _imgH;
    private bool _painting;
    private bool _erase;
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
            if (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right)
            {
                if (mb.Pressed)
                {
                    _painting = true;
                    _erase = mb.ButtonIndex == MouseButton.Right;
                    PaintAt(mb.Position);
                }
                else
                {
                    _painting = false;
                }
                AcceptEvent();
            }
        }
        else if (e is InputEventMouseMotion mm)
        {
            _hasMouse = true;
            _mouseLocal = mm.Position;
            if (_painting)
            {
                PaintAt(mm.Position);
            }
            QueueRedraw();
        }
    }

    public override void _Notification(int what)
    {
        if (what == (int)Control.NotificationMouseExit)
        {
            _hasMouse = false;
            _painting = false;
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
        Rect2 fit = FitRect();
        if (DisplayTex != null)
        {
            DrawTextureRect(DisplayTex, fit, false);
            DrawRect(fit, new Color(0f, 0f, 0f, 0.6f), false, 2f);
        }
        if (_hasMouse && _imgW > 0)
        {
            float scale = fit.Size.X / _imgW;
            float radius = Mathf.Max(2f, CursorRadiusTexels * scale);
            DrawArc(_mouseLocal, radius, 0f, Mathf.Tau, 48, new Color(1f, 1f, 1f, 0.9f), 1.5f, true);
            DrawArc(_mouseLocal, radius + 1f, 0f, Mathf.Tau, 48, new Color(0f, 0f, 0f, 0.7f), 1f, true);
        }
    }

    // Letterboxed fit rect preserving the image's aspect ratio.
    private Rect2 FitRect()
    {
        Vector2 c = Size;
        if (_imgW <= 0 || _imgH <= 0 || c.X <= 0f || c.Y <= 0f)
        {
            return new Rect2(Vector2.Zero, c);
        }
        float imgAspect = (float)_imgW / _imgH;
        float ctrlAspect = c.X / c.Y;
        float dw, dh;
        if (ctrlAspect > imgAspect)
        {
            dh = c.Y;
            dw = dh * imgAspect;
        }
        else
        {
            dw = c.X;
            dh = dw / imgAspect;
        }
        Vector2 off = (c - new Vector2(dw, dh)) * 0.5f;
        return new Rect2(off, new Vector2(dw, dh));
    }

    private bool TryTexel(Vector2 local, out Vector2I texel)
    {
        texel = default;
        Rect2 fit = FitRect();
        if (fit.Size.X <= 0f || fit.Size.Y <= 0f)
        {
            return false;
        }
        Vector2 rel = (local - fit.Position) / fit.Size;
        if (rel.X < 0f || rel.X >= 1f || rel.Y < 0f || rel.Y >= 1f)
        {
            return false;
        }
        texel = new Vector2I(
            Mathf.FloorToInt(rel.X * _imgW),
            Mathf.FloorToInt(rel.Y * _imgH));
        return true;
    }
}
