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
        => $"{Op}  |  Water {(ctx.ShowWater ? "on" : "off")}  |  Alt pick, Shift constrain";

    public string LevelText(WorldMapState ctx)
    {
        if (Op == EBrushOp.Lift)
        {
            return $"Lift by {LiftVoxels:+#;-#;0}v";
        }
        int level = TargetVoxels / ctx.StepVoxels;
        return $"Target {TargetVoxels:+#;-#;0}v (level {level:+#;-#;0}, Y={ctx.SeaLevel + TargetVoxels})";
    }

    public string HintText(WorldMapState ctx)
    {
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
                    v = Mathf.Lerp(v, BoxAverage(ctx.Elevation, px, pz, w, h), k);
                    break;
            }
            ctx.Elevation.SetPixel(px, pz, new Color(Mathf.Clamp(v, min, max), 0f, 0f, 1f));
        });
    }

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
        if (Op == EBrushOp.Lift)
        {
            LiftVoxels += dir * ctx.StepVoxels;
            return;
        }
        TargetVoxels = ctx.SnapVoxels(TargetVoxels + dir * ctx.StepVoxels);
    }

    private static float BoxAverage(Image img, int px, int pz, int w, int h)
    {
        float sum = 0f;
        int count = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            int nx = px + dx;
            if (nx < 0 || nx >= w)
            {
                continue;
            }
            for (int dz = -1; dz <= 1; dz++)
            {
                int nz = pz + dz;
                if (nz < 0 || nz >= h)
                {
                    continue;
                }
                sum += img.GetPixel(nx, nz).R;
                count++;
            }
        }
        return count > 0 ? sum / count : img.GetPixel(px, pz).R;
    }
}

// One banded colour per lattice step, with standing water drawn over it when
// ShowWater is on (W).
public class ElevationView : IWorldMapView
{
    public ESpawnPreview PreviewLayer => ESpawnPreview.None;
    public bool ShowsClimb => false;

    public bool ShowsAllSteps => false;
    public bool DrawsWater => true;

    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        return ctx.WithWater(ctx.ElevationColor(px, pz), px, pz);
    }
}
