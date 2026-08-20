using Godot;

public enum EBrushOp
{
    Raise = 0,
    Lower = 1,
    Flatten = 2,
    Smooth = 3,
    // Flatten with a feathered rim instead of a hard one. Appended, never
    // inserted: renumbering an enum silently rewrites any value already authored
    // against the old numbering.
    FlattenSoft = 4,
    // Offset a column ONCE per stroke. Raise accumulates per motion event, so
    // scrubbing an area compounds it and the middle of a region ends up higher
    // than its edges; Lift moves everything it touches by exactly its amount, so
    // a region keeps its own relief and simply sits higher.
    Lift = 5,
    // Ramp from where you pressed to where you drag. The only op that is about
    // the whole drag rather than the disk under the cursor: see SmearRamp.
    Smear = 6,
    // Weathers cliffs. The only op that does not write the elevation layer at
    // all — it paints a per-column strength and the erosion is recomputed from
    // the pristine heights whenever one is asked for. See RoughenStamp.
    Roughen = 7,
}

// Sculpts the per-column elevation layer, in voxels relative to sea level.
// AdjustLevel moves the target level Flatten paints to; the view bands every
// lattice step.
public class ElevationTool : IWorldMapTool
{
    public string Name => "Elevation";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 12f;

    public EBrushOp Op = EBrushOp.Raise;

    // Voxels a single stroke event moves a texel at full brush weight. A drag
    // fires one of these per mouse-motion event, so this is deliberately a
    // FRACTION of a lattice step — several events per visible step is what makes
    // the brush controllable instead of jumping tens of voxels per flick.
    public float VoxelsPerStroke = 0.5f;

    // Height Flatten drives toward, in voxels relative to sea level (0 = shore).
    // Explicit rather than sampled from the clicked column: flattening to
    // "whatever happened to be under the cursor" is unpredictable, and there was
    // no way to ask for a specific height at all.
    public int TargetVoxels = 0;

    // Roughen's strength, as stops rather than a free float so R/F walks it the
    // way it walks a target height. Its own field, never folded into
    // TargetVoxels: one is a height and one is a fraction, and sharing a number
    // between them is how a tool starts reinterpreting the same value two ways.
    public static readonly float[] RoughenStops = { 0.25f, 0.5f, 0.75f, 1f };
    public int RoughenStopIndex = 3;
    public float RoughenStrength => RoughenStops[Mathf.Clamp(RoughenStopIndex, 0, RoughenStops.Length - 1)];

    // Voxels one Lift applies. A delta, not a height — kept apart from
    // TargetVoxels so switching between Flatten and Lift cannot silently
    // reinterpret the same number as an absolute and then an offset.
    public int LiftVoxels = 4;

    private enum EConstraint
    {
        None,
        Equal,
        AtOrAbove,
    }

    // Per-stroke column ledger. It answers both "may this column be painted"
    // (the constraint, judged the FIRST time a column is touched and remembered,
    // so the mask is the PRE-stroke shape — re-testing live heights would let a
    // stroke erode its own mask) and "has Lift already moved this column".
    // Allocated for every stroke, not just constrained ones, because Lift needs
    // the ledger even unconstrained.
    private const byte MASK_UNKNOWN = 0;
    private const byte MASK_ELIGIBLE = 1;
    private const byte MASK_BLOCKED = 2;
    private const byte MASK_DONE = 3;
    private byte[] _mask;
    private EConstraint _constraint;
    private int _constrainHeight;

    // Smear's per-stroke state: where the drag began, and the heights that were
    // there before it started. See SmearRamp for why both ends must be read from
    // the pre-stroke map.
    private Vector2I _anchor;
    private bool _dragging;
    private float[] _pre;
    private bool[] _hasPre;
    private Rect2I? _smearDirty;

    // Roughen's repaint rect. Wider than the brush disk because talus spreads
    // out from the wall it came off, so columns the cursor never covered change
    // height — and the map has to redraw them or the erosion appears clipped to
    // the stroke until something else happens to repaint that ground.
    private Rect2I? _roughenDirty;


    public ElevationTool()
    {
        View = new ElevationView();
    }

    private static readonly string[] OP_NAMES = System.Enum.GetNames<EBrushOp>();

    public string[] Options(WorldMapState ctx) => OP_NAMES;

    public Color[] OptionColors(WorldMapState ctx) => null;

    public int OptionIndex
    {
        get => (int)Op;
        set => Op = (EBrushOp)Mathf.Clamp(value, 0, OP_NAMES.Length - 1);
    }

    // Only the ops that write one specific height can preview it; Raise, Lower
    // and Smooth move a column relative to what it already is.
    public Color CursorColor(WorldMapState ctx)
    {
        return Op == EBrushOp.Flatten || Op == EBrushOp.FlattenSoft
            ? ctx.ElevationColorAt(ctx.SnapVoxels(TargetVoxels))
            : Colors.White;
    }

    public string StatusText(WorldMapState ctx)
    {
        if (Op == EBrushOp.Smear)
        {
            return $"Smear  |  Ramp between the press and the cursor  |  Water {(ctx.ShowWater ? "on" : "off")}";
        }
        if (Op == EBrushOp.Roughen)
        {
            return $"Roughen  |  Weathers cliffs  |  Water {(ctx.ShowWater ? "on" : "off")}";
        }
        return $"{Op}  |  Water {(ctx.ShowWater ? "on" : "off")}  |  Alt pick, Shift constrain";
    }

    public string LevelText(WorldMapState ctx)
    {
        if (Op == EBrushOp.Smear)
        {
            // Both ends come from the map, so there is no level to aim.
            return "";
        }
        if (Op == EBrushOp.Roughen)
        {
            return $"Roughness {Mathf.RoundToInt(RoughenStrength * 100f)}%";
        }
        if (Op == EBrushOp.Lift)
        {
            return $"Lift by {LiftVoxels:+#;-#;0}v";
        }
        int level = TargetVoxels / ctx.StepVoxels;
        return $"Target {TargetVoxels:+#;-#;0}v (level {level:+#;-#;0}, Y={ctx.SeaLevel + TargetVoxels})";
    }

    public string HintText(WorldMapState ctx)
    {
        if (Op == EBrushOp.Smear)
        {
            return "Drag from the high ground to where the ramp should meet the low";
        }
        if (Op == EBrushOp.Roughen)
        {
            return $"R/F strength; weathers cliffs {ctx.Data.roughenMinCliffVoxels}m and taller; "
                + "RMB restores the sharp edge";
        }
        return "Alt+Click pick height  |  Shift+Drag only that elevation  |  Ctrl+Drag that elevation and up";
    }

    // Eyedropper: alt+click adopts the height under the cursor as the target.
    // Sampled ONCE, here, not per stamp — a brush that re-read the height as it
    // moved would chase what it had just painted and drift off the picked value.
    // The target persists after the stroke and shows in the HUD, so it doubles as
    // the fast way to aim Flatten at all: R/F walks one lattice step per press,
    // and picking a plateau you already built beats forty of them.
    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
        int here = ctx.SnapVoxels(ctx.ElevationVoxels(texel.X, texel.Y));
        if ((mods & EStrokeMods.Pick) != 0)
        {
            TargetVoxels = here;
        }

        // At-or-above wins when both are held: it is the broader of the two, and
        // silently painting less than asked is the worse failure.
        _constraint = (mods & EStrokeMods.ConstrainAbove) != 0
            ? EConstraint.AtOrAbove
            : (mods & EStrokeMods.Constrain) != 0 ? EConstraint.Equal : EConstraint.None;
        _constrainHeight = here;

        int cells = ctx.Data.ImageWidth * ctx.Data.ImageHeight;
        if (_mask == null || _mask.Length != cells)
        {
            _mask = new byte[cells];
        }
        else
        {
            System.Array.Clear(_mask, 0, cells);
        }

        _anchor = texel;
        _dragging = true;
        _smearDirty = null;
        if (Op == EBrushOp.Smear)
        {
            if (_pre == null || _pre.Length != cells)
            {
                _pre = new float[cells];
                _hasPre = new bool[cells];
            }
            else
            {
                System.Array.Clear(_hasPre, 0, cells);
            }
        }
    }

    // May this column still be painted by the current stroke?
    private bool Eligible(WorldMapState ctx, int px, int pz)
    {
        int i = pz * ctx.Data.ImageWidth + px;
        if (_mask[i] == MASK_UNKNOWN)
        {
            int h = ctx.SnapVoxels(ctx.ElevationVoxels(px, pz));
            bool ok = _constraint switch
            {
                EConstraint.Equal => h == _constrainHeight,
                EConstraint.AtOrAbove => h >= _constrainHeight,
                _ => true,
            };
            _mask[i] = ok ? MASK_ELIGIBLE : MASK_BLOCKED;
        }
        return _mask[i] == MASK_ELIGIBLE;
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        if (Op == EBrushOp.Smear)
        {
            SmearRamp(ctx, brush, texel, erase);
            return;
        }
        if (Op == EBrushOp.Roughen)
        {
            RoughenStamp(ctx, brush, texel, erase);
            return;
        }
        // Smooth averages over the WHOLE BRUSH, not the three cells next to each
        // texel. A brush is how big an area you meant to affect, and a fixed 3x3
        // kernel ignored it: widening the brush made a big patch of the same
        // barely-there smoothing instead of a broader, smoother shape.
        //
        // Prepared once per stamp as a separable box blur over the affected
        // region, because the direct form is quadratic in the radius per texel —
        // a radius-12 brush would be ~280k image reads per motion event, and this
        // is ~5k.
        if (Op == EBrushOp.Smooth)
        {
            PrepareBlur(ctx, texel, Mathf.CeilToInt(Radius));
        }
        // Erase inverts Lift rather than falling back to Lower, so RMB drops a
        // region by the same amount, once, the way LMB raised it.
        EBrushOp op = erase && Op != EBrushOp.Lift ? EBrushOp.Lower : Op;
        int lift = erase ? -LiftVoxels : LiftVoxels;
        int w = ctx.Data.ImageWidth;
        int h = ctx.Data.ImageHeight;
        float min = ctx.Data.minElevationVoxels;
        float max = ctx.Data.maxElevationVoxels;

        brush.Stamp(texel, Radius, w, h, (px, pz, weight) =>
        {
            if (_mask != null && !Eligible(ctx, px, pz))
            {
                return;
            }
            float v = ctx.Elevation.GetPixel(px, pz).R;
            float k = brush.flow * weight;
            switch (op)
            {
                case EBrushOp.Raise:
                    v += VoxelsPerStroke * k * (1f + brush.NoiseAt(px, pz));
                    break;
                case EBrushOp.Lower:
                    v -= VoxelsPerStroke * k * (1f + brush.NoiseAt(px, pz));
                    break;
                case EBrushOp.Flatten:
                    // Deliberately ignores falloff AND flow: a flatten is a
                    // plateau stamp, so every texel in the disk takes the target
                    // outright and the rim is one clean edge.
                    v = TargetVoxels;
                    break;
                case EBrushOp.FlattenSoft:
                    // Same target, eased in by brush weight so the rim ramps
                    // toward it. On a snapped lattice that ramp lands as a ring
                    // of terraces — right for grading a plateau into what
                    // surrounds it, and exactly what plain Flatten must not do.
                    v = Mathf.Lerp(v, TargetVoxels, k);
                    break;
                case EBrushOp.Lift:
                    v += lift;
                    // Marked done so the rest of the drag passes over it without
                    // stacking another lift on top.
                    _mask[pz * w + px] = MASK_DONE;
                    break;
                case EBrushOp.Smooth:
                    // Elevation-independent: a blur toward the neighbourhood
                    // average, with no target of its own.
                    v = Mathf.Lerp(v, BlurAt(px, pz), k);
                    break;
            }
            ctx.Elevation.SetPixel(px, pz, new Color(Mathf.Clamp(v, min, max), 0f, 0f, 1f));
        });
    }

    // Paints how weathered a cliff is.
    //
    // A LAYER, not an edit. This writes a strength per column and the erosion is
    // recomputed from the PRISTINE elevation whenever a height is asked for
    // (WorldMapState.RoughenDelta), so painting the same wall twice cannot
    // crumble it — the second pass only raises a strength already capped at 1.
    // An earlier version eroded the elevation raster in place and had exactly
    // that problem, plus a dependence on which way the drag swept, since raising
    // a foot column changed the drop its neighbour measured.
    //
    // The model: a cliff of roughenMinCliffVoxels (4) or taller has its height
    // minus roughenKeepBandVoxels (3) to give — one voxel at 4m, two at 5m — and
    // noise splits that budget between talus at the foot and a crumbled lip at
    // the top. Strength scales the budget, so a light pass barely marks a cliff
    // and a full one takes it down to the band.
    private void RoughenStamp(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Eases toward the target like the other continuous fields, so the
        // brush's falloff becomes the gradient of the weathering. Safe to hold
        // the brush still: the field is a strength, not an accumulation, and it
        // cannot pass the stop it is easing toward.
        float target = erase ? 0f : RoughenStrength;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            float k = brush.flow * weight;
            ctx.SetRoughnessAt(px, pz, Mathf.Lerp(ctx.RoughnessAt(px, pz), target, k));
        });

        // Repaint out to where talus can reach, not just where the disk landed.
        int reach = Mathf.CeilToInt(Radius) + Mathf.Max(0, ctx.Data.roughenMaxSpreadVoxels) + 1;
        int x0 = Mathf.Max(0, texel.X - reach);
        int z0 = Mathf.Max(0, texel.Y - reach);
        int x1 = Mathf.Min(ctx.Data.ImageWidth, texel.X + reach + 1);
        int z1 = Mathf.Min(ctx.Data.ImageHeight, texel.Y + reach + 1);
        _roughenDirty = new Rect2I(x0, z0, Mathf.Max(0, x1 - x0), Mathf.Max(0, z1 - z0));
    }

    // Smear rewrites the whole corridor back to the press, not the brush disk;
    // every other op stays under its own ring.
    //
    // Roughen is NOT here on purpose: it writes only the columns under the disk,
    // so the brush rect the host declares already covers the undo snapshot. Its
    // wider rect is a REPAINT concern (LastPaintRect), because the heights it
    // moves are recomputed rather than stored.
    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase)
        => Op == EBrushOp.Smear && _dragging ? Corridor(ctx, texel) : null;

    public Rect2I? LastPaintRect => Op switch
    {
        EBrushOp.Smear => _smearDirty,
        EBrushOp.Roughen => _roughenDirty,
        _ => null,
    };
    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = System.Enum.GetValues<EBrushOp>().Length;
        Op = (EBrushOp)(((int)Op + dir + n) % n);
    }

    // R/F edits whichever number the current op actually uses: Lift's offset, or
    // everything else's target height. Both step by one lattice step, so they
    // walk the same bands the map draws.
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        if (Op == EBrushOp.Smear)
        {
            return;
        }
        if (Op == EBrushOp.Roughen)
        {
            RoughenStopIndex = Mathf.Clamp(RoughenStopIndex + dir, 0, RoughenStops.Length - 1);
            return;
        }
        if (Op == EBrushOp.Lift)
        {
            LiftVoxels += dir * ctx.StepVoxels;
            return;
        }
        TargetVoxels = ctx.SnapVoxels(TargetVoxels + dir * ctx.StepVoxels);
    }

    // Pushes elevation from where you pressed to where you drag: a ramp cut
    // between two heights, along the direction of the drag.
    //
    // NOT a paint-program smudge, and that is the whole point. A smudge
    // repeatedly samples "a bit behind me" and blends it forward, which on a
    // heightfield leaves a rounded ditch down the middle of the stroke: the
    // leading edge carries the high ground forward while the ground it already
    // passed keeps sampling from further back, so the middle gets pulled down
    // twice. Every parameter that controls the effect also controls the
    // artefact, so it cannot be tuned away.
    //
    // Instead the profile is LINEAR BY CONSTRUCTION. The press anchors one end
    // at the height under it, the cursor is the other end at the height that was
    // there before the stroke, and every column between takes the height its
    // position along that line asks for. A straight line has no middle to dip.
    //
    // Consequences worth knowing, all of them falling out of the same
    // construction:
    //
    //   - The ends do not move. At the anchor the target IS the anchor's height,
    //     at the cursor it IS the ground already there, so a ramp meets both
    //     plateaus flush instead of leaving a lip to trip on.
    //   - The whole corridor is rewritten on every motion event, not just the
    //     disk under the cursor. Dragging further re-solves the ramp over its
    //     full length rather than leaving the part built earlier at an older,
    //     steeper slope.
    //   - Repeating a stroke converges instead of accumulating: the target
    //     depends only on the two pre-stroke heights, so holding still or
    //     scrubbing back and forth settles onto the ramp rather than digging.
    //
    // Radius is the half-WIDTH of the corridor here; its length is however far
    // you drag.
    private void SmearRamp(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        _smearDirty = null;
        if (erase || !_dragging)
        {
            return;
        }
        var axis = new Vector2(texel.X - _anchor.X, texel.Y - _anchor.Y);
        float length = axis.Length();
        if (length < 1f)
        {
            // No direction yet. A click that never moves is not a ramp, and
            // guessing one from a zero-length drag would spin the slope around
            // on the first pixel of movement.
            return;
        }
        Vector2 dir = axis / length;

        // Both ends read from BEFORE the stroke. The far end especially: it is
        // ground this stroke may already have ramped on an earlier event, and
        // reading it live would let the ramp chase its own tail.
        float startVoxels = PreVoxels(ctx, _anchor.X, _anchor.Y);
        float endVoxels = PreVoxels(ctx, texel.X, texel.Y);

        Rect2I rect = Corridor(ctx, texel);
        _smearDirty = rect;
        float min = ctx.Data.minElevationVoxels;
        float max = ctx.Data.maxElevationVoxels;
        for (int px = rect.Position.X; px < rect.Position.X + rect.Size.X; px++)
        {
            for (int pz = rect.Position.Y; pz < rect.Position.Y + rect.Size.Y; pz++)
            {
                var rel = new Vector2(px - _anchor.X, pz - _anchor.Y);
                float along = rel.Dot(dir);
                if (along < 0f || along > length)
                {
                    continue;
                }
                // Distance from the drag LINE, so the corridor is a band of
                // constant width rather than a disk that happens to be dragged.
                float across = Mathf.Abs(rel.X * dir.Y - rel.Y * dir.X);
                if (across > Radius)
                {
                    continue;
                }
                float edge = 1f - Mathf.SmoothStep(brush.hardness, 1f, across / Radius);
                if (edge <= 0f)
                {
                    continue;
                }
                PreVoxels(ctx, px, pz);
                float target = Mathf.Lerp(startVoxels, endVoxels, along / length);
                float now = ctx.ElevationVoxels(px, pz);
                float next = Mathf.Lerp(now, target, edge * brush.flow);
                ctx.Elevation.SetPixel(px, pz, new Color(Mathf.Clamp(next, min, max), 0f, 0f, 1f));
            }
        }
    }

    // Pre-stroke elevation at a column, remembered the first time it is asked
    // for — which is before it is written, so it is genuinely the old value.
    private float PreVoxels(WorldMapState ctx, int px, int pz)
    {
        int i = pz * ctx.Data.ImageWidth + px;
        if (_pre == null || i < 0 || i >= _pre.Length)
        {
            return ctx.ElevationVoxels(px, pz);
        }
        if (!_hasPre[i])
        {
            _hasPre[i] = true;
            _pre[i] = ctx.ElevationVoxels(px, pz);
        }
        return _pre[i];
    }

    // The band the ramp occupies: everything between the press and the cursor,
    // padded by the corridor's half-width.
    private Rect2I Corridor(WorldMapState ctx, Vector2I texel)
    {
        int pad = Mathf.CeilToInt(Radius) + 1;
        int x0 = Mathf.Max(0, Mathf.Min(_anchor.X, texel.X) - pad);
        int z0 = Mathf.Max(0, Mathf.Min(_anchor.Y, texel.Y) - pad);
        int x1 = Mathf.Min(ctx.Data.ImageWidth, Mathf.Max(_anchor.X, texel.X) + pad + 1);
        int z1 = Mathf.Min(ctx.Data.ImageHeight, Mathf.Max(_anchor.Y, texel.Y) + pad + 1);
        return new Rect2I(x0, z0, Mathf.Max(0, x1 - x0), Mathf.Max(0, z1 - z0));
    }

    // Blurred elevation over the last stamp's region, and where it starts.
    private float[] _blur;
    private Rect2I _blurRect;

    private float BlurAt(int px, int pz)
    {
        int x = Mathf.Clamp(px - _blurRect.Position.X, 0, _blurRect.Size.X - 1);
        int z = Mathf.Clamp(pz - _blurRect.Position.Y, 0, _blurRect.Size.Y - 1);
        return _blur[z * _blurRect.Size.X + x];
    }

    private void PrepareBlur(WorldMapState ctx, Vector2I center, int radius)
    {
        int w = ctx.Data.ImageWidth;
        int h = ctx.Data.ImageHeight;
        // The blur needs `radius` of input beyond every texel it will be asked
        // about, and it will be asked about the whole brush disk.
        int pad = radius * 2 + 1;
        int x0 = center.X - pad;
        int z0 = center.Y - pad;
        int sw = pad * 2 + 1;
        int sh = pad * 2 + 1;
        _blurRect = new Rect2I(x0, z0, sw, sh);

        var src = new float[sw * sh];
        for (int z = 0; z < sh; z++)
        {
            for (int x = 0; x < sw; x++)
            {
                // Clamped, so the map edge blurs against itself rather than
                // against zero — which would drag every coastline down.
                src[z * sw + x] = ctx.ElevationVoxels(
                    Mathf.Clamp(x0 + x, 0, w - 1), Mathf.Clamp(z0 + z, 0, h - 1));
            }
        }

        // Separable: rows, then columns. Two O(n) passes instead of O(n * r^2).
        var mid = new float[sw * sh];
        for (int z = 0; z < sh; z++)
        {
            for (int x = 0; x < sw; x++)
            {
                float sum = 0f;
                int n = 0;
                for (int d = -radius; d <= radius; d++)
                {
                    int sx = x + d;
                    if (sx < 0 || sx >= sw)
                    {
                        continue;
                    }
                    sum += src[z * sw + sx];
                    n++;
                }
                mid[z * sw + x] = sum / n;
            }
        }
        _blur = new float[sw * sh];
        for (int x = 0; x < sw; x++)
        {
            for (int z = 0; z < sh; z++)
            {
                float sum = 0f;
                int n = 0;
                for (int d = -radius; d <= radius; d++)
                {
                    int sz = z + d;
                    if (sz < 0 || sz >= sh)
                    {
                        continue;
                    }
                    sum += mid[sz * sw + x];
                    n++;
                }
                _blur[z * sw + x] = sum / n;
            }
        }
    }

}

// One banded colour per lattice step, with standing water drawn over it when
// ShowWater is on (W).
public class ElevationView : IWorldMapView
{
    public ESpawnPreview PreviewLayer => ESpawnPreview.None;

    public bool ShowsAllSteps => false;
    public bool DrawsWater => true;

    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        return ctx.WithWater(ctx.ElevationColor(px, pz), px, pz);
    }
}
