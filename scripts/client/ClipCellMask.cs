using System;
using Godot;

// Ceiling cutaway driven by the CELL-REGION decomposition (CellField /
// CellRegions) rather than by a per-column band test.
//
// The player occupies exactly one cell, which belongs to exactly one region. Cut
// that region: everything above its cut height, over its columns plus the walls
// they claim. Sky regions never cut, which is the whole of the outdoor case.
//
// Three concepts from the column rule do not exist here and must not come back:
// HEADROOM (the cut height is the region's own ceiling, not a fixed height above
// the player's feet), WALL_KEEP (walls cut at the same height as the space they
// enclose, because they are part of the same footprint), and "blocked columns
// keep to infinity" (a column is in the footprint or it isn't).
//
// The heavy lifting is upstream: this class turns a labelled region into a
// smoothed 2D mask and uploads it. It owns no geometry logic at all.
public class ClipCellMask : IClipMask
{
    // One column per cell, and the window IS the CellField's — the mask cannot
    // reach past the cells it is derived from, and sharing the anchor means the
    // two can never disagree about which column an index means.
    public const int GRID_SIZE = CellField.GRID_SIZE;

    private const float CLOSED_EPSILON = 0.01f;
    private const float TAIL_RATE_FRACTION = 0.15f;
    private const float BINARY_THRESHOLD = 0.5f;

    // Tuning, copied in by GameClient each tick from its [Export]s.
    public float OpenSeconds = 0.35f;
    public float CloseSeconds = 0.25f;
    // How far below the region's ceiling the clip plane sits. A voxel index y
    // spans world [y, y+1), so a ceiling's underside — the only face visible from
    // beneath it — is at exactly its own index, and the shaders cut on `>`. With
    // no clearance that face survives and the cutaway reads as having done
    // nothing. Parks the plane just inside the air gap instead.
    //
    // This is also why the cut height is NOT rounded up to a plateau boundary.
    // Plateaus index solid TOPS; a standable floor and its ceiling sit one voxel
    // above that grid, so an interior built on terrain has an off-grid ceiling
    // (13, not 12) and rounding up overshoots it by nearly a whole plateau,
    // leaving the room sealed. The plateau stays where it belongs — in the
    // region BUCKET, deciding which cells are one space.
    public float Clearance = 0.5f;
    // Fraction of a mesh's footprint that must sit over the cut set before it
    // starts taking the cut; participation reaches full at twice this.
    public float FootprintCoverageThreshold = 0.15f;

    private float[] _accumulated = new float[GRID_SIZE * GRID_SIZE];
    private float[] _scratch = new float[GRID_SIZE * GRID_SIZE];
    private readonly float[] _target = new float[GRID_SIZE * GRID_SIZE];
    private readonly bool[] _binary = new bool[GRID_SIZE * GRID_SIZE];
    // Two bytes per column — RG8. G is the per-column height offset, which every
    // column of one region shares, so it is uniformly zero until stage 3 starts
    // revealing regions that cut at their own heights.
    private readonly byte[] _bytes = new byte[GRID_SIZE * GRID_SIZE * 2];
    private ImageTexture _texture;

    private int _minColumnX;
    private int _minColumnZ;
    private int _prevMinColumnX;
    private int _prevMinColumnZ;
    private bool _anchored;

    public float ClipY { get; private set; } = float.PositiveInfinity;
    public bool AnyClipped { get; private set; }
    public bool IsOpen { get; private set; }
    public bool MaskChanged { get; private set; }
    // Every column of the player's region cuts at one height, so there is no
    // spread to encode. Stage 3 (revealing other regions at their own heights) is
    // what makes this non-zero.
    public float HeightSpan => 0f;
    // Diagnostics: the region's size and how many wall columns it carried.
    public int RegionCells { get; private set; }
    public int WallColumns { get; private set; }

    public Vector2 OriginXz => new Vector2(_minColumnX, _minColumnZ);
    public float Extent => GRID_SIZE;
    public Texture2D Texture => _texture;

    // `active` false skips the region work and drives the mask toward exempt, so
    // it closes on its authored curve instead of snapping — and costs nothing at
    // all once closed.
    public void Tick(bool active, CellField field, CellRegions regions, float deltaSeconds)
    {
        MaskChanged = false;
        if (!active && !IsOpen)
        {
            return;
        }

        using var _prof = Profiler.Sample("ClipCellMask.Tick");

        bool scrolled = field != null && Recenter(field);
        Array.Clear(_target, 0, _target.Length);
        RegionCells = 0;
        WallColumns = 0;
        AnyClipped = false;
        ClipY = float.PositiveInfinity;
        if (active && field != null && regions != null)
        {
            BuildTarget(field, regions);
        }

        bool anyOpen = false;
        bool changed = scrolled;
        for (int i = 0; i < _accumulated.Length; i++)
        {
            float target = _target[i];
            // Opening slower than closing, mirroring clipFadeDownSeconds /
            // clipFadeUpSeconds: the reveal is worth watching, the close is not.
            float timeConstant = Mathf.Max(target > _accumulated[i] ? OpenSeconds : CloseSeconds, 1e-3f);
            float eased = Mathf.Lerp(_accumulated[i], target, 1f - Mathf.Exp(-deltaSeconds / timeConstant));
            // Exponential smoothing settles at 0.998, not 1, and consumers
            // multiply this in — so a roof reads 0.998, misses the full-discard
            // path, and dithers, leaving the one Bayer cell whose threshold is
            // exactly 0 alive as a screen-space speckle sliding over geometry
            // that is supposed to be gone. Floor the rate so it actually lands.
            _accumulated[i] = Mathf.MoveToward(eased, target, (deltaSeconds / timeConstant) * TAIL_RATE_FRACTION);
            if (_accumulated[i] > CLOSED_EPSILON)
            {
                anyOpen = true;
            }
            bool binary = _accumulated[i] > BINARY_THRESHOLD;
            if (binary != _binary[i])
            {
                _binary[i] = binary;
                changed = true;
            }
        }

        IsOpen = anyOpen;
        MaskChanged = changed;
        if (!anyOpen)
        {
            AnyClipped = false;
            return;
        }
        Upload();
    }

    // The cut set: the player's region's own columns, plus the wall columns it
    // claimed. Walls are solid so they belong to no region, and without carrying
    // them the roof over a room cuts while the walls holding it up stand full
    // height — the defect that made the column rule need a WALL_KEEP height.
    private void BuildTarget(CellField field, CellRegions regions)
    {
        int player = regions.PlayerRegion;
        if (player < 0 || player >= regions.Regions.Count)
        {
            return;
        }
        CellRegion region = regions.Regions[player];
        // Standing outdoors. Sky regions never cut — no special case, no arming
        // scan, no cover probe: the decomposition already said there is no
        // ceiling here.
        if (region.IsSky)
        {
            return;
        }

        ClipY = region.MaxCeilingY - Clearance;
        AnyClipped = true;
        for (int gz = 0; gz < GRID_SIZE; gz++)
        {
            int row = gz * GRID_SIZE;
            for (int gx = 0; gx < GRID_SIZE; gx++)
            {
                bool inRegion = ColumnInRegion(field, regions, gx, gz, player);
                if (inRegion)
                {
                    RegionCells++;
                }
                else if (regions.IsWallColumn(gx, gz))
                {
                    WallColumns++;
                }
                else
                {
                    continue;
                }
                _target[row + gx] = 1f;
            }
        }
    }

    private static bool ColumnInRegion(CellField field, CellRegions regions, int gx, int gz, int regionId)
    {
        int count = field.CountAt(gx, gz);
        for (int slot = 0; slot < count; slot++)
        {
            if (regions.LabelAt(gx, gz, slot) == regionId)
            {
                return true;
            }
        }
        return false;
    }

    // Follows the CellField's window rather than computing its own, carrying
    // accumulated columns so each keeps describing the same world column.
    // Columns scrolling in from the edge start exempt and smooth up, which reads
    // correctly — a roof that just came into range was not cut a moment ago.
    private bool Recenter(CellField field)
    {
        int newMinX = field.MinColumnX;
        int newMinZ = field.MinColumnZ;
        if (!_anchored)
        {
            Array.Clear(_accumulated, 0, _accumulated.Length);
            _minColumnX = _prevMinColumnX = newMinX;
            _minColumnZ = _prevMinColumnZ = newMinZ;
            _anchored = true;
            return true;
        }

        _prevMinColumnX = _minColumnX;
        _prevMinColumnZ = _minColumnZ;
        int dx = newMinX - _minColumnX;
        int dz = newMinZ - _minColumnZ;
        if (dx == 0 && dz == 0)
        {
            return false;
        }
        _minColumnX = newMinX;
        _minColumnZ = newMinZ;
        if (Mathf.Abs(dx) >= GRID_SIZE || Mathf.Abs(dz) >= GRID_SIZE)
        {
            Array.Clear(_accumulated, 0, _accumulated.Length);
            return true;
        }

        // Destination column (gx, gz) holds the world column that lived at
        // (gx + dx, gz + dz) in the old window. Ping-pong rather than shifting in
        // place so overlapping ranges can't clobber themselves.
        Array.Clear(_scratch, 0, _scratch.Length);
        int copyWidth = GRID_SIZE - Mathf.Abs(dx);
        int dstX = Mathf.Max(0, -dx);
        int srcX = dstX + dx;
        for (int gz = 0; gz < GRID_SIZE; gz++)
        {
            int srcZ = gz + dz;
            if (srcZ < 0 || srcZ >= GRID_SIZE)
            {
                continue;
            }
            Array.Copy(_accumulated, srcZ * GRID_SIZE + srcX, _scratch, gz * GRID_SIZE + dstX, copyWidth);
        }
        (_accumulated, _scratch) = (_scratch, _accumulated);
        return true;
    }

    // Every participating column cuts at the same height, so the base is the
    // answer. The signature stays because stage 3 will have something to say.
    public float ClipHeightAt(Vector3 worldPosition, float baseClipY)
    {
        return baseClipY;
    }

    public bool IsClipped(Vector3 worldPosition)
    {
        int gx = Mathf.FloorToInt(worldPosition.X) - _minColumnX;
        int gz = Mathf.FloorToInt(worldPosition.Z) - _minColumnZ;
        if (gx < 0 || gz < 0 || gx >= GRID_SIZE || gz >= GRID_SIZE)
        {
            return false;
        }
        return _binary[gz * GRID_SIZE + gx];
    }

    // Coverage rather than a sampled point, for the reason spelled out on
    // Roof.UpdateClipParticipation: no single point is right for a mesh spanning
    // tens of metres, and every choice of one failed in play. Standing outside,
    // the street is a sky region and cuts nothing, so a roof's coverage is zero
    // however its footprint was painted; standing inside, the room under the roof
    // IS the cut set.
    public float RegionCoverage(Vector2 minXz, Vector2 maxXz)
    {
        int gx0 = Mathf.FloorToInt(minXz.X) - _minColumnX;
        int gz0 = Mathf.FloorToInt(minXz.Y) - _minColumnZ;
        int gx1 = Mathf.FloorToInt(maxXz.X) - _minColumnX;
        int gz1 = Mathf.FloorToInt(maxXz.Y) - _minColumnZ;
        // Total spans the whole footprint including any part outside the window —
        // a roof mostly beyond the mask's reach should read as mostly uncovered
        // rather than being judged on the sliver that happens to be in range.
        int total = (gx1 - gx0 + 1) * (gz1 - gz0 + 1);
        if (total <= 0)
        {
            return 0f;
        }
        gx0 = Mathf.Max(gx0, 0);
        gz0 = Mathf.Max(gz0, 0);
        gx1 = Mathf.Min(gx1, GRID_SIZE - 1);
        gz1 = Mathf.Min(gz1, GRID_SIZE - 1);

        float covered = 0f;
        for (int gz = gz0; gz <= gz1; gz++)
        {
            int row = gz * GRID_SIZE;
            for (int gx = gx0; gx <= gx1; gx++)
            {
                covered += _accumulated[row + gx];
            }
        }
        float threshold = Mathf.Max(FootprintCoverageThreshold, 1e-3f);
        return Mathf.Clamp((covered / total - threshold) / threshold, 0f, 1f);
    }

    public bool WindowTouchesChunk(Vector3I chunkCoord)
    {
        int minX = Mathf.Min(_minColumnX, _prevMinColumnX);
        int maxX = Mathf.Max(_minColumnX, _prevMinColumnX) + GRID_SIZE;
        int minZ = Mathf.Min(_minColumnZ, _prevMinColumnZ);
        int maxZ = Mathf.Max(_minColumnZ, _prevMinColumnZ) + GRID_SIZE;
        int chunkMinX = chunkCoord.X * ChunkState.SIZE;
        int chunkMinZ = chunkCoord.Z * ChunkState.SIZE;
        return chunkMinX + ChunkState.SIZE > minX && chunkMinX < maxX
            && chunkMinZ + ChunkState.SIZE > minZ && chunkMinZ < maxZ;
    }

    public string Describe(WorldState world, Vector3 playerPosition)
    {
        return $"clipY={ClipY} regionCols={RegionCells} wallCols={WallColumns} "
            + $"armed={AnyClipped} open={IsOpen} maskHere={IsClipped(playerPosition)}";
    }

    private void Upload()
    {
        for (int i = 0; i < _accumulated.Length; i++)
        {
            _bytes[i * 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(_accumulated[i] * 255f), 0, 255);
            _bytes[i * 2 + 1] = 0;
        }
        Image image = Image.CreateFromData(GRID_SIZE, GRID_SIZE, false, Image.Format.Rg8, _bytes);
        if (_texture == null)
        {
            _texture = ImageTexture.CreateFromImage(image);
            // The ImageTexture instance is stable across updates, so the global
            // only needs binding once.
            RenderingServer.GlobalShaderParameterSet("clip_mask_tex", _texture);
        }
        else
        {
            _texture.Update(image);
        }
    }
}
