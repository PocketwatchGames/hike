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
    // Round-minimap mask: icons fade out and cull as they reach this fraction of
    // the panel's half-extent from center (matching the map shader's mask_radius),
    // so none poke past the circle. 0 disables it (the square world map).
    public float CircleMaskFraction;

    private GameClient _gameClient;
    // Framing pushed by the host each frame before the redraw.
    private Vector2 _centerWorldXZ;
    private float _viewRadiusMeters;
    // Matches the map shader's map_rotation — the world sampling spin that puts
    // game-north up. Applied to each icon's world offset so icons track the
    // rotated terrain. 0 for the HUD minimap (it rotates its whole TextureRect
    // instead); −π/4 for the world map.
    private float _mapRotation;
    private bool _framed;

    public static MapMarkerOverlay Create(GameClient gameClient, Texture2D unknownIcon, float iconSize, bool includeProvisional, float circleMaskFraction)
    {
        var overlay = new MapMarkerOverlay
        {
            _gameClient = gameClient,
            UnknownIcon = unknownIcon,
            IconSize = iconSize,
            IncludeProvisional = includeProvisional,
            CircleMaskFraction = circleMaskFraction,
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true,
        };
        overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        return overlay;
    }

    // Host pushes the current framing each frame, then the overlay redraws.
    // center = world XZ at panel center; viewRadiusMeters = half the panel's world
    // extent; mapRotation matches the shader's map_rotation (world map spins its
    // sampling to put game-north up; the minimap passes 0 and rotates its whole
    // TextureRect to camera yaw instead) — see _Draw.
    public void SetFraming(Vector2 centerWorldXZ, float viewRadiusMeters, float mapRotation = 0f)
    {
        _centerWorldXZ = centerWorldXZ;
        _viewRadiusMeters = viewRadiusMeters;
        _mapRotation = mapRotation;
        _framed = true;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_framed || _viewRadiusMeters <= 0f)
        {
            return;
        }
        SimState sim = _gameClient?.Sim?.WorldState?.SimState;
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
        Minimap minimap = IncludeProvisional ? null : _gameClient?.Sim?.Minimap;
        System.Collections.Generic.IEnumerable<MapMarkerRecord> markers =
            IncludeProvisional ? sim.EnumerateMarkers() : sim.EnumerateWorldMapMarkers();
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
            // World offset → panel px. Rotate by -mapRotation to invert the
            // shader's world sampling spin (screen offset → world offset) so the
            // icon lands on the same terrain the shader draws under it. For the
            // minimap (mapRotation 0) the parent TextureRect's own rotation
            // carries the icon to camera yaw exactly as it does the map beneath.
            Vector2 px = center + (worldOffset / diameter).Rotated(-_mapRotation) * panel;
            float revealAlpha = minimap?.BankedRevealAlphaAt(record.WorldPosition) ?? 1f;
            float edgeFade = CircleEdgeFade(px, center, panel);
            if (edgeFade <= 0f)
            {
                continue;
            }
            revealAlpha *= edgeFade;
            DrawSetTransform(px, counterRot, Vector2.One);
            DrawMarker(record, sim, revealAlpha);
        }
        DrawLiveMarkers(center, panel, diameter, radiusSq, counterRot);
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    // Round-minimap edge fade for an icon centered at px (pixel space): 1 in the
    // interior, ramping to 0 as the icon's outer edge (IconSize/2) reaches the
    // circle mask so it never pokes past. Returns 1 when there's no circle mask
    // (the square world map). Centered + symmetric, so correct under yaw rotation.
    private float CircleEdgeFade(Vector2 px, Vector2 center, Vector2 panel)
    {
        if (CircleMaskFraction <= 0f)
        {
            return 1f;
        }
        float circleR = Mathf.Min(panel.X, panel.Y) * CircleMaskFraction;
        float maxDist = circleR - IconSize * 0.5f;
        float band = IconSize * 0.5f;
        return Mathf.Clamp((maxDist - px.DistanceTo(center)) / Mathf.Max(band, 1f), 0f, 1f);
    }

    // Live entity markers (talkable NPCs, fallen party members): drawn at each
    // entity's CURRENT position every redraw, always visible — no fog-reveal gate
    // and no camp banking, so they show on both the minimap and world map the
    // instant the entity exists. Sourced from the live World registry rather than
    // the discovered-Knowledge stores the static markers above come from.
    private void DrawLiveMarkers(Vector2 center, Vector2 panel, float diameter, float radiusSq, float counterRot)
    {
        Sim sim = _gameClient?.Sim;
        if (sim == null)
        {
            return;
        }
        float half = IconSize * 0.5f;
        System.Collections.Generic.IReadOnlyList<LiveMapMarker> markers = sim.LiveMapMarkers;
        for (int i = 0; i < markers.Count; i++)
        {
            LiveMapMarker marker = markers[i];
            if (marker == null || !marker.IsActive)
            {
                continue;
            }
            Texture2D tex = marker.Icon;
            if (tex == null)
            {
                continue;
            }
            Vector3 wp = marker.WorldPosition;
            Vector2 worldOffset = new Vector2(wp.X - _centerWorldXZ.X, wp.Z - _centerWorldXZ.Y);
            if (worldOffset.LengthSquared() > radiusSq)
            {
                continue;
            }
            Vector2 px = center + (worldOffset / diameter).Rotated(-_mapRotation) * panel;
            float edgeFade = CircleEdgeFade(px, center, panel);
            if (edgeFade <= 0f)
            {
                continue;
            }
            Color modulate = marker.Modulate;
            modulate.A *= edgeFade;
            DrawSetTransform(px, counterRot, Vector2.One);
            DrawTextureRect(tex, new Rect2(-half, -half, IconSize, IconSize), false, modulate);
        }
    }

    // Draws one marker centered at the current canvas origin (set via
    // DrawSetTransform). Gates on Level, not on whether an icon was stored, so a
    // Sensed marker always reads as "?" even though its real icon is already known.
    // For two-state markers (campfire) the Identified icon is tinted by the LIVE
    // active state (lit/unlit), read from `sim` each draw.
    // revealAlpha (0..1) fades the icon in with its ground during the camp reveal
    // sweep — 1 outside the animation, so normal display is unaffected.
    private void DrawMarker(MapMarkerRecord record, SimState sim, float revealAlpha)
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

        // Forge markers: swap in the icon for the forge's fixed slot (derived from its
        // position, identical to the in-world floating model, so map and world agree)
        // and stamp its level. Resolvable while the chunk is unloaded — the slot is a
        // pure function of position and the level rides the always-resident forge cache.
        int forgeLevel = 0;
        if (identified && sim.TryGetForgeMarker(record.WorldPosition, out ForgeMarkerInfo forge))
        {
            forgeLevel = forge.Level;
            SimData simData = Sim.Current?.SimData;
            if (simData != null)
            {
                Texture2D slotIcon = simData.GetForgeSlotIcon(forge.Slot);
                if (slotIcon != null)
                {
                    tex = slotIcon;
                }
            }
        }

        if (tex != null)
        {
            DrawTextureRect(tex, new Rect2(-half, -half, IconSize, IconSize), false, modulate);
            if (forgeLevel > 0)
            {
                DrawLevelBadge(forgeLevel, half, revealAlpha);
            }
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

    // Stamp a small level number at the icon's bottom-right corner, with a dark
    // drop-shadow for legibility over any map ground. Used by forge markers.
    private void DrawLevelBadge(int level, float half, float alpha)
    {
        Font font = GetThemeDefaultFont();
        if (font == null)
        {
            return;
        }
        int fs = Mathf.Max(8, Mathf.RoundToInt(IconSize * 0.6f));
        string txt = level.ToString();
        Vector2 sz = font.GetStringSize(txt, HorizontalAlignment.Left, -1, fs);
        Vector2 pos = new Vector2(half - sz.X + 1f, half + sz.Y * 0.15f);
        DrawString(font, pos + new Vector2(1f, 1f), txt, HorizontalAlignment.Left, -1, fs, new Color(0f, 0f, 0f, alpha));
        DrawString(font, pos, txt, HorizontalAlignment.Left, -1, fs, new Color(1f, 1f, 1f, alpha));
    }
}
