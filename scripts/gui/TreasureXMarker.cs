using Godot;

// Draws the "dig here" X at its own origin — the mark on a treasure map. A
// dedicated overlay control rather than WorldMapScreen._Draw, because a parent's
// _Draw is painted over by its child TextureRect (the map): this sits above the
// map as a sibling of the region-label / marker overlays. WorldMapScreen sets its
// Position to the dig spot's pixel (the same regionLabels-space projection the
// marker icons use — the dig spot is the view center, UV 0.5,0.5), and the X is
// drawn centered on that origin.
[GlobalClass]
public partial class TreasureXMarker : Control
{
    // Optional icon drawn centered instead of the drawn X.
    [Export] public Texture2D icon;
    // Half-length of each stroke of the drawn-X fallback, in pixels.
    [Export(PropertyHint.Range, "4,80,1")] public float armLength = 22f;
    [Export(PropertyHint.Range, "1,16,0.5")] public float lineWidth = 4f;

    public override void _Draw()
    {
        Vector2 center = Vector2.Zero;
        if (icon != null)
        {
            Vector2 size = icon.GetSize();
            DrawTexture(icon, center - size * 0.5f);
            return;
        }
        Vector2 d1 = new Vector2(armLength, armLength);
        Vector2 d2 = new Vector2(armLength, -armLength);
        Color outline = new Color(0f, 0f, 0f, 0.85f);
        Color red = new Color(0.9f, 0.12f, 0.12f);
        DrawLine(center - d1, center + d1, outline, lineWidth * 2f);
        DrawLine(center - d2, center + d2, outline, lineWidth * 2f);
        DrawLine(center - d1, center + d1, red, lineWidth);
        DrawLine(center - d2, center + d2, red, lineWidth);
    }
}
