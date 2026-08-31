using Godot;

// Paints the per-chunk prevailing wind — a direction and a strength.
//
// Direction is a GESTURE, not a number typed into a field: sweeping the brush
// lays the wind along the sweep, and the two radial modes lay it toward or away
// from the brush centre, so "everything blows toward the middle of the map" is
// one click with a map-wide radius rather than a per-zone angle to work out. The
// angle the last gesture produced is held, so a click with no drag re-stamps it.
//
// The layer is per CHUNK because that is the granularity the bake seeds the wind
// velocity subgrid at (WindGen writes one velocity per chunk). Strength 0 on the
// image means UNPAINTED — such a chunk inherits its zone's prevailing direction,
// which is what every chunk did before this layer existed.
public class WindTool : IWorldMapTool
{
    // How the stamp decides each texel's angle.
    private enum EWindMode
    {
        // Along the drag. A click with no motion repeats the held angle.
        Stroke = 0,
        // Toward the brush centre — a convergent field in one stamp.
        Inward = 1,
        // Away from the brush centre.
        Outward = 2,
    }

    // Below this many texels a drag is treated as jitter and the held angle is
    // kept, so a slow hand does not scribble noise into the field.
    private const float MinDragTexels = 2.5f;

    // Strength step per AdjustLevel press.
    private const float StrengthStep = 0.05f;

    public string Name => "Wind";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 48f;

    private EWindMode _mode = EWindMode.Stroke;

    // Held angle in radians, measured in MAP space: 0 = +X (right), rising
    // toward +Z (down the raster). The world uses the same XZ pair, so this is
    // the baked direction with no conversion.
    private float _angle = 0f;
    private float _strength = 0.5f;

    // Last texel this stroke painted at, for the Stroke-mode delta.
    private Vector2I _lastTexel;
    private bool _hasLastTexel;

    public WindTool()
    {
        View = new WindView();
    }

    public string[] Options(WorldMapState ctx) => new[] { "Stroke", "Inward", "Outward" };

    public Color[] OptionColors(WorldMapInk ink) => null;

    public int OptionIndex
    {
        get => (int)_mode;
        set => _mode = (EWindMode)Mathf.Clamp(value, 0, 2);
    }

    public string HintText(WorldMapState ctx)
        => "Drag to lay the wind along the sweep; RMB clears back to the zone's wind; Alt picks up what is under the cursor";

    public string StatusText(WorldMapState ctx, WorldMapView view)
        => $"{_mode} {Arrow(_angle)} {Mathf.RoundToInt(Mathf.RadToDeg(_angle))}°";

    public string LevelText(WorldMapState ctx, WorldMapView view)
        => $"{_strength * ctx.Data.windPaintMaxSpeed:0.#} m/s";

    public Color CursorColor(WorldMapInk ink) => WindView.AngleColor(_angle, _strength);

    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
        _lastTexel = texel;
        _hasLastTexel = true;
        if ((mods & EStrokeMods.Pick) != 0 && ctx.WindAtColumn(texel.X, texel.Y, out float angle, out float strength))
        {
            _angle = angle;
            _strength = strength;
        }
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        if (_mode == EWindMode.Stroke && _hasLastTexel)
        {
            Vector2 drag = new Vector2(texel.X - _lastTexel.X, texel.Y - _lastTexel.Y);
            if (drag.Length() >= MinDragTexels)
            {
                _angle = drag.Angle();
                _lastTexel = texel;
            }
        }

        float held = _angle;
        float strength = _strength;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            if (erase)
            {
                ctx.ClearWindAtColumn(px, pz);
                return;
            }
            ctx.SetWindAtColumn(px, pz, AngleFor(px, pz, texel, held), strength);
        });
    }

    // The radial modes aim each texel individually, which is what makes one
    // wide stamp a whole convergent field. A texel sitting exactly on the brush
    // centre has no direction of its own and keeps the held one.
    private float AngleFor(int px, int pz, Vector2I center, float held)
    {
        if (_mode == EWindMode.Stroke)
        {
            return held;
        }
        var toCenter = new Vector2(center.X - px, center.Y - pz);
        if (toCenter.LengthSquared() < 1e-4f)
        {
            return held;
        }
        return _mode == EWindMode.Inward ? toCenter.Angle() : (-toCenter).Angle();
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;

    public Rect2I? LastPaintRect => null;

    public void Cycle(WorldMapState ctx, int dir)
    {
        _mode = (EWindMode)Mathf.PosMod((int)_mode + dir, 3);
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        _strength = Mathf.Clamp(_strength + dir * StrengthStep, 0f, 1f);
    }

    // Eight-point glyph for the HUD. +X is right and +Z is DOWN the raster,
    // matching how the map is drawn.
    private static string Arrow(float angle)
    {
        string[] glyphs = { "→", "↘", "↓", "↙", "←", "↖", "↑", "↗" };
        int octant = Mathf.PosMod(Mathf.RoundToInt(angle / Mathf.Tau * 8f), 8);
        return glyphs[octant];
    }
}

// Hue is the compass angle, so two places with the same wind are the same
// colour, and a sawtooth ramp runs ALONG the flow so the field reads as
// streamlines with a direction — a stripe pattern alone would look identical
// under a 180° flip.
public class WindView : IWorldMapView
{
    // Texels per sawtooth repeat. Around half a chunk, so the ramp is visible at
    // the zoom the wind is painted at without aliasing when zoomed out.
    private const float StreakPeriodTexels = 8f;

    private static readonly Color Unpainted = new Color(0.20f, 0.21f, 0.24f);

    public ESpawnPreview PreviewLayer => ESpawnPreview.None;

    // Colour is a direction, not a height, so the steps are the only landform
    // information on screen and all of them are wanted.
    public bool ShowsAllSteps => true;
    public bool DrawsWater => false;

    public Color ColorAt(WorldMapInk ink, int px, int pz)
    {
        if (!ink.Map.WindAtColumn(px, pz, out float angle, out float strength))
        {
            return Unpainted;
        }
        float dirX = Mathf.Cos(angle);
        float dirZ = Mathf.Sin(angle);
        float t = Mathf.PosMod(px * dirX + pz * dirZ, StreakPeriodTexels) / StreakPeriodTexels;
        return AngleColor(angle, strength) * (0.6f + 0.5f * t);
    }

    public static Color AngleColor(float angle, float strength)
    {
        float hue = Mathf.PosMod(angle, Mathf.Tau) / Mathf.Tau;
        return Color.FromHsv(hue, 0.55f, 0.35f + 0.6f * Mathf.Clamp(strength, 0f, 1f));
    }
}
