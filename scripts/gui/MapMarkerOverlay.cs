using Godot;

// Draws discovered map markers over a minimap-style surface. One instance
// overlays the HUD minimap, another the full world-map screen (created in code by
// each host and parented over its map surface). Projects each marker's world XZ
// to panel pixels with the SAME framing the minimap shader renders with (center
// world XZ + view radius), so icons sit on the terrain beneath them.
//
// Marker source depends on the host: the HUD minimap includes the active
// member's provisional field markers (IncludeProvisional, EnumerateMarkers =
// party ∪ active), so a marker charted in the field shows there immediately; the
// world map is banked-only (EnumerateBankedMarkers), so a field marker appears on
// it only after camping (mirrors region labels / fog-of-war). Sensed markers draw
// a shared "?" (UnknownIcon); Identified markers draw the record's own icon. Until
// art is wired both fall back to a drawn placeholder ("?" glyph / filled dot) so
// the system is visible immediately.
[GlobalClass]
public partial class MapMarkerOverlay : Control
{
    // Shared "?" icon for Sensed (existence-known-but-unidentified) markers. Set
    // by the host from its own [Export]; null falls back to a drawn "?" glyph.
    public Texture2D UnknownIcon;
    // Drawn icon size in panel pixels (square).
    public float IconSize = 24f;
    // True (minimap): draw party ∪ active. False (world map): banked-only.
    public bool IncludeProvisional;

    private GameClient _gameClient;
    // Framing pushed by the host each frame before the redraw.
    private Vector2 _centerWorldXZ;
    private float _viewRadiusMeters;
    private bool _framed;

    public static MapMarkerOverlay Create(GameClient gameClient, Texture2D unknownIcon, float iconSize, bool includeProvisional)
    {
        var overlay = new MapMarkerOverlay
        {
            _gameClient = gameClient,
            UnknownIcon = unknownIcon,
            IconSize = iconSize,
            IncludeProvisional = includeProvisional,
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true,
        };
        overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        return overlay;
    }

    // Host pushes the current framing each frame, then the overlay redraws.
    // center = world XZ at panel center; viewRadiusMeters = half the panel's world
    // extent. Rotation is inherited from the parent surface (the HUD minimap
    // TextureRect is rotated to camera yaw; the world map isn't) — see _Draw.
    public void SetFraming(Vector2 centerWorldXZ, float viewRadiusMeters)
    {
        _centerWorldXZ = centerWorldXZ;
        _viewRadiusMeters = viewRadiusMeters;
        _framed = true;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_framed || _viewRadiusMeters <= 0f)
        {
            return;
        }
        WorldSimState sim = _gameClient?.World?.WorldState?.SimState;
        if (sim == null)
        {
            return;
        }
        Vector2 panel = Size;
        if (panel.X <= 0f || panel.Y <= 0f)
        {
            return;
        }
        Vector2 center = panel * 0.5f;
        float diameter = _viewRadiusMeters * 2f;
        // Icons stay upright even when the parent surface is rotated: the parent's
        // transform carries our whole canvas, so counter-rotate each icon by
        // -parentRotation. World-map parent is un-rotated → 0.
        float counterRot = -((GetParent() as Control)?.Rotation ?? 0f);
        float radiusSq = _viewRadiusMeters * _viewRadiusMeters;
        // World-map icons fade in with their ground during the campfire reveal
        // sweep; the minimap (provisional) overlay is never gated.
        Minimap minimap = IncludeProvisional ? null : _gameClient?.World?.Minimap;
        System.Collections.Generic.IEnumerable<MapMarkerRecord> markers =
            IncludeProvisional ? sim.EnumerateMarkers() : sim.EnumerateBankedMarkers();
        foreach (MapMarkerRecord record in markers)
        {
            if (record == null || record.Level < EMapMarkerLevel.Sensed)
            {
                continue;
            }
            Vector2 worldOffset = new Vector2(
                record.WorldPosition.X - _centerWorldXZ.X,
                record.WorldPosition.Z - _centerWorldXZ.Y);
            if (worldOffset.LengthSquared() > radiusSq)
            {
                continue; // outside the visible area
            }
            // Unrotated local px — the parent's transform (including its rotation)
            // maps it to screen exactly as it maps the map texture beneath.
            Vector2 px = center + worldOffset / diameter * panel;
            float revealAlpha = minimap?.BankedRevealAlphaAt(record.WorldPosition) ?? 1f;
            DrawSetTransform(px, counterRot, Vector2.One);
            DrawMarker(record, sim, revealAlpha);
        }
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    // Draws one marker centered at the current canvas origin (set via
    // DrawSetTransform). Gates on Level, not on whether an icon was stored, so a
    // Sensed marker always reads as "?" even though its real icon is already known.
    // For two-state markers (campfire) the Identified icon is tinted by the LIVE
    // active state (lit/unlit), read from `sim` each draw.
    // revealAlpha (0..1) fades the icon in with its ground during the camp reveal
    // sweep — 1 outside the animation, so normal display is unaffected.
    private void DrawMarker(MapMarkerRecord record, WorldSimState sim, float revealAlpha)
    {
        if (revealAlpha <= 0f)
        {
            return;
        }
        bool identified = record.Level >= EMapMarkerLevel.Identified;
        Texture2D tex = identified ? record.Icon : UnknownIcon;
        float half = IconSize * 0.5f;
        Color modulate = Colors.White;
        if (identified)
        {
            modulate = (record.HasActiveState && sim.IsMarkerActive(record.WorldPosition))
                ? record.ActiveModulate
                : record.IconModulate;
        }
        modulate.A *= revealAlpha;
        if (tex != null)
        {
            DrawTextureRect(tex, new Rect2(-half, -half, IconSize, IconSize), false, modulate);
            return;
        }
        // Placeholder until art is wired.
        if (identified)
        {
            DrawCircle(Vector2.Zero, half * 0.55f, new Color(0.95f, 0.85f, 0.4f, revealAlpha));
            return;
        }
        Font font = GetThemeDefaultFont();
        if (font != null)
        {
            int fs = Mathf.RoundToInt(IconSize);
            Vector2 sz = font.GetStringSize("?", HorizontalAlignment.Left, -1, fs);
            DrawString(font, new Vector2(-sz.X * 0.5f, sz.Y * 0.35f), "?",
                HorizontalAlignment.Left, -1, fs, new Color(0.9f, 0.9f, 0.9f, revealAlpha));
        }
    }
}
