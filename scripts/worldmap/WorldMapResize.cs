using Godot;

// Resizes a painted document to a new chunk footprint, resampling every layer.
//
// The whole problem is that these layers are CATEGORICAL, not continuous. An
// elevation 6 beside an elevation 8 is two terraces with a wall between them; a
// filter that averages puts a 7 in the gap and invents a terrace nobody painted
// — and the same filter on the ground layer would blend "forest" and "desert"
// into whichever index sits between them. So every resampler here obeys one
// rule: an output pixel is a VERBATIM COPY of some input pixel. Nothing is ever
// averaged, interpolated or blended.
//
// That alone gives a correct but staircased result, because nearest-neighbour
// scaling turns every diagonal boundary into steps the size of the scale factor.
// The fix is EPX / Scale2x, the pixel-art corner rule: a source pixel becomes
// four, and a quadrant takes the value of the two neighbours meeting at its
// corner when those two AGREE and the opposite two DISAGREE. That guard is what
// makes it safe here — it identifies a corner and never a line, so a one-metre
// ridge or an isolated cell survives untouched.
//
// Two things were tried first and measured worse, both worth not repeating:
//
// - A MAJORITY filter. Sized to see across a step (radius ~ the scale factor) it
//   erodes any feature that small, and after an upscale a one-metre ridge is
//   exactly that size; sized smaller it does nothing at all (zero pixels changed
//   at 3x).
// - ONE corner-cut pass at the full factor. It only nips the corner of an
//   N-pixel step, leaving the staircase: 99 px of boundary error at 4x against
//   96 for plain nearest. Cutting deeper (half the cell instead of an eighth)
//   made it worse still, 246, because the cuts on either side of the boundary
//   land a cell apart and zigzag.
//
// So the doubling is ITERATED — Scale4x is Scale2x twice — and each pass works
// on the previous one's chamfer, which is what actually straightens a diagonal.
// Any leftover factor lands with a final exact resample.
//
// Tunnels ARE resampled with everything else, in XZ — they have to be, or a
// passage would no longer meet the hillside it was bored into. Only their Y is
// left alone, for the same reason heights are: the vertical world does not
// change, so a tunnel stays at the depth it was cut.
//
// Heights are NOT scaled with the footprint. Doubling the map's width doubles
// how far it is across a valley; it must not double how tall its walls are,
// because wall height is a gameplay quantity (climbable below 4m, scaleless
// above 12m) that the terrain rules pin independently of extent.
public static class WorldMapResize
{
    // Resize `data` in place and rewrite every layer file it points at.
    //
    // Do NOT run this with the painter open: it holds the old images in memory
    // and its next save would put them straight back.
    public static bool Run(WorldMapData data, int newChunksX, int newChunksZ)
    {
        if (data == null)
        {
            GD.PrintErr("worldmap_resize: no document.");
            return false;
        }
        if (newChunksX < 1 || newChunksZ < 1 || newChunksX > 256 || newChunksZ > 256)
        {
            GD.PrintErr($"worldmap_resize: chunk size {newChunksX}x{newChunksZ} out of range (1..256).");
            return false;
        }
        if (newChunksX == data.sizeChunksX && newChunksZ == data.sizeChunksZ)
        {
            GD.Print("worldmap_resize: already that size, nothing to do.");
            return true;
        }

        int oldChunksX = data.sizeChunksX;
        int oldChunksZ = data.sizeChunksZ;
        int oldW = data.ImageWidth;
        int oldH = data.ImageHeight;

        // Loaded at the OLD extent — the state reads data.ImageWidth, which is
        // still the old one until the swap below.
        var state = new WorldMapState(data);

        data.sizeChunksX = newChunksX;
        data.sizeChunksZ = newChunksZ;
        int newW = data.ImageWidth;
        int newH = data.ImageHeight;

        // Per-column layers.
        state.Elevation = Resample(state.Elevation, newW, newH);
        state.Water = Resample(state.Water, newW, newH);
        state.Ground = Resample(state.Ground, newW, newH);
        state.WaterType = Resample(state.WaterType, newW, newH);
        state.Paving = Resample(state.Paving, newW, newH);
        state.Scatter = Resample(state.Scatter, newW, newH);
        state.Mobs = Resample(state.Mobs, newW, newH);
        // Scalars carry a continuous field (mob level) beside a flag (climb), so
        // they go through the categorical path too: a lerp would be marginally
        // smoother for the level and would quietly turn the flag into a fraction.
        state.Scalars = Resample(state.Scalars, newW, newH);

        // Per-CHUNK layers scale by the chunk count, not the texel count.
        state.Region = Resample(state.Region, newChunksX, newChunksZ);
        state.Zone = Resample(state.Zone, newChunksX, newChunksZ);
        state.Wind = Resample(state.Wind, newChunksX, newChunksZ);

        state.Tunnels = ResampleTunnels(state.Tunnels, oldW, oldH, newW, newH, data.VoxelHeight);
        state.InvalidateVoxelEdits();

        MovePlacements(state, oldW, oldH, newW, newH, oldChunksX, oldChunksZ, data);

        state.Save();
        if (!string.IsNullOrEmpty(data.ResourcePath))
        {
            Error err = ResourceSaver.Save(data, data.ResourcePath);
            if (err != Error.Ok)
            {
                GD.PrintErr($"worldmap_resize: layers were rewritten but {data.ResourcePath} could not be saved ({err}) — "
                    + $"set sizeChunksX/sizeChunksZ to {newChunksX}/{newChunksZ} by hand or the document will not match its images.");
                return false;
            }
        }
        GD.Print($"worldmap_resize: {oldChunksX}x{oldChunksZ} chunks ({oldW}x{oldH} m) -> "
            + $"{newChunksX}x{newChunksZ} ({newW}x{newH} m). Heights unchanged.");
        return true;
    }

    // Change the EXTENT without touching the content: every painted metre stays
    // the metre it was, the map just gains or loses ground around it.
    //
    // The complement of Run, and the one you want far more often. A resize
    // rescales the world — the same coastline, bigger — while this one keeps the
    // coastline exactly as painted and gives you more sea to work in. Nothing is
    // resampled, so nothing can be lost except what falls outside a shrink, and
    // placements do not move at all: they are authored in WORLD coordinates and
    // the world origin does not shift.
    public static bool Recanvas(WorldMapData data, int newChunksX, int newChunksZ)
    {
        if (data == null)
        {
            GD.PrintErr("worldmap_canvas: no document.");
            return false;
        }
        if (newChunksX < 1 || newChunksZ < 1 || newChunksX > 256 || newChunksZ > 256)
        {
            GD.PrintErr($"worldmap_canvas: chunk size {newChunksX}x{newChunksZ} out of range (1..256).");
            return false;
        }
        if (newChunksX == data.sizeChunksX && newChunksZ == data.sizeChunksZ)
        {
            GD.Print("worldmap_canvas: already that size, nothing to do.");
            return true;
        }

        int oldChunksX = data.sizeChunksX;
        int oldChunksZ = data.sizeChunksZ;
        int oldW = data.ImageWidth;
        int oldH = data.ImageHeight;
        int oldMinChunkX = -(oldChunksX / 2);
        int oldMinChunkZ = -(oldChunksZ / 2);

        var state = new WorldMapState(data);

        data.sizeChunksX = newChunksX;
        data.sizeChunksZ = newChunksZ;
        int newW = data.ImageWidth;
        int newH = data.ImageHeight;

        // Both extents are centred on the origin, so the shift is the difference
        // between the two lower corners — in texels for the per-column layers and
        // in chunks for the per-chunk ones.
        var texelShift = new Vector2I(oldMinChunkX * ChunkState.SIZE - data.WorldMinX,
                                      oldMinChunkZ * ChunkState.SIZE - data.WorldMinZ);
        var chunkShift = new Vector2I(oldMinChunkX - data.MinChunk.X, oldMinChunkZ - data.MinChunk.Z);

        state.Elevation = Recanvas(state.Elevation, newW, newH, texelShift);
        state.Water = Recanvas(state.Water, newW, newH, texelShift);
        state.Ground = Recanvas(state.Ground, newW, newH, texelShift);
        state.WaterType = Recanvas(state.WaterType, newW, newH, texelShift);
        state.Paving = Recanvas(state.Paving, newW, newH, texelShift);
        state.Scatter = Recanvas(state.Scatter, newW, newH, texelShift);
        state.Mobs = Recanvas(state.Mobs, newW, newH, texelShift);
        state.Scalars = Recanvas(state.Scalars, newW, newH, texelShift);
        state.Region = Recanvas(state.Region, newChunksX, newChunksZ, chunkShift);
        state.Zone = Recanvas(state.Zone, newChunksX, newChunksZ, chunkShift);
        state.Wind = Recanvas(state.Wind, newChunksX, newChunksZ, chunkShift);
        state.Tunnels = RecanvasTunnels(state.Tunnels, oldW, oldH, newW, newH, texelShift, data.VoxelHeight);
        state.InvalidateVoxelEdits();

        state.Save();
        if (!string.IsNullOrEmpty(data.ResourcePath))
        {
            Error err = ResourceSaver.Save(data, data.ResourcePath);
            if (err != Error.Ok)
            {
                GD.PrintErr($"worldmap_canvas: layers were rewritten but {data.ResourcePath} could not be saved ({err}) — "
                    + $"set sizeChunksX/sizeChunksZ to {newChunksX}/{newChunksZ} by hand or the document will not match its images.");
                return false;
            }
        }
        int lostX = Mathf.Max(0, oldW - newW);
        int lostZ = Mathf.Max(0, oldH - newH);
        string cropped = lostX > 0 || lostZ > 0 ? $" CROPPED {lostX}x{lostZ} m away." : "";
        GD.Print($"worldmap_canvas: {oldChunksX}x{oldChunksZ} chunks ({oldW}x{oldH} m) -> "
            + $"{newChunksX}x{newChunksZ} ({newW}x{newH} m), content unmoved in world space.{cropped}");
        return true;
    }

    // New canvas, old pixels copied in at `shift`. Growing leaves the new margin
    // at zero, which is what every layer already means by unpainted: sea level
    // for the heights, "no set" for the indices.
    private static Image Recanvas(Image src, int newW, int newH, Vector2I shift)
    {
        if (src == null)
        {
            return null;
        }
        Image dst = Image.CreateEmpty(newW, newH, false, src.GetFormat());
        dst.Fill(new Color(0f, 0f, 0f, 1f));

        // Clip the copy to the overlap by hand rather than trusting BlitRect to
        // reject a partly out-of-bounds destination.
        int x0 = Mathf.Max(0, -shift.X);
        int z0 = Mathf.Max(0, -shift.Y);
        int x1 = Mathf.Min(src.GetWidth(), newW - shift.X);
        int z1 = Mathf.Min(src.GetHeight(), newH - shift.Y);
        if (x1 > x0 && z1 > z0)
        {
            dst.BlitRect(src, new Rect2I(x0, z0, x1 - x0, z1 - z0), new Vector2I(x0 + shift.X, z0 + shift.Y));
        }
        return dst;
    }

    private static byte[,,] RecanvasTunnels(byte[,,] src, int oldW, int oldH, int newW, int newH,
        Vector2I shift, int voxelHeight)
    {
        if (src == null)
        {
            return null;
        }
        var dst = new byte[newW, voxelHeight, newH];
        for (int x = 0; x < oldW; x++)
        {
            int nx = x + shift.X;
            if (nx < 0 || nx >= newW)
            {
                continue;
            }
            for (int z = 0; z < oldH; z++)
            {
                int nz = z + shift.Y;
                if (nz < 0 || nz >= newH)
                {
                    continue;
                }
                for (int y = 0; y < voxelHeight; y++)
                {
                    dst[nx, y, nz] = src[x, y, z];
                }
            }
        }
        return dst;
    }

    // Stamps keep their SIZE — a house does not grow with the map — so they are
    // moved to the same relative spot instead of being scaled. The world is
    // centred on the origin at both extents, so a corner-to-corner texel map
    // sends the old centre to the new one.
    private static void MovePlacements(WorldMapState state, int oldW, int oldH, int newW, int newH,
        int oldChunksX, int oldChunksZ, WorldMapData data)
    {
        int oldMinX = -(oldChunksX / 2) * ChunkState.SIZE;
        int oldMinZ = -(oldChunksZ / 2) * ChunkState.SIZE;
        foreach (SubscenePlacement placement in state.Placements.placements)
        {
            if (placement == null)
            {
                continue;
            }
            int tx = Mathf.FloorToInt((placement.anchorXZ.X - oldMinX) * (float)newW / oldW);
            int tz = Mathf.FloorToInt((placement.anchorXZ.Y - oldMinZ) * (float)newH / oldH);
            placement.anchorXZ = new Vector2I(data.WorldMinX + tx, data.WorldMinZ + tz);
        }
    }

    // Any EDIT wins over the covered region, and nothing is smoothed: a tunnel
    // that shrinks out of existence is a passage that silently seals, which is
    // worse than one that comes out a metre wide. A carve beats an added voxel
    // for the same reason — a sealed passage is the worse of the two failures.
    private static byte[,,] ResampleTunnels(byte[,,] src, int oldW, int oldH, int newW, int newH, int voxelHeight)
    {
        if (src == null)
        {
            return null;
        }
        var dst = new byte[newW, voxelHeight, newH];
        for (int x = 0; x < newW; x++)
        {
            int sx0 = x * oldW / newW;
            int sx1 = Mathf.Max(sx0 + 1, (x + 1) * oldW / newW);
            for (int z = 0; z < newH; z++)
            {
                int sz0 = z * oldH / newH;
                int sz1 = Mathf.Max(sz0 + 1, (z + 1) * oldH / newH);
                for (int y = 0; y < voxelHeight; y++)
                {
                    byte v = WorldMapState.EditNone;
                    for (int sx = sx0; sx < sx1 && v != WorldMapState.EditCarve; sx++)
                    {
                        for (int sz = sz0; sz < sz1 && v != WorldMapState.EditCarve; sz++)
                        {
                            byte sv = src[sx, y, sz];
                            if (sv != WorldMapState.EditNone)
                            {
                                v = sv;
                            }
                        }
                    }
                    dst[x, y, z] = v;
                }
            }
        }
        return dst;
    }

    // ---- The categorical resampler ---------------------------------------

    // Works on the image's RAW BYTES, a whole pixel at a time, so it is
    // format-agnostic and — more importantly — physically incapable of producing
    // a value that was not in the source: every output pixel is a memcpy of an
    // input pixel. It also keeps a pixel's channels TOGETHER, which matters for
    // the spawn layers, where R is a set index and G is that set's density and a
    // per-channel filter would pair one set's index with another's density.
    public static Image Resample(Image src, int newW, int newH)
    {
        if (src == null)
        {
            return null;
        }
        int w = src.GetWidth();
        int h = src.GetHeight();
        if (w == newW && h == newH)
        {
            return src;
        }
        Image.Format format = src.GetFormat();
        byte[] d = src.GetData();
        int stride = d.Length / Mathf.Max(1, w * h);
        if (stride < 1 || stride > 8)
        {
            // Nothing here can key a pixel wider than a ulong. A plain nearest
            // resize is still value-preserving, just staircased.
            Image fallback = Image.CreateFromData(w, h, false, format, d);
            fallback.Resize(newW, newH, Image.Interpolation.Nearest);
            return fallback;
        }

        // Double with Scale2x for as long as the target has room, THEN land on
        // the exact size. Iterating is the whole trick: one corner-cut pass at
        // 4x only nips the corner of a four-pixel step and leaves the staircase
        // (measured: 99 px of boundary error against 96 for plain nearest — no
        // improvement worth the code), while two 2x passes let the second one
        // work on the first one's chamfer and actually straighten the diagonal.
        while (w * 2 <= newW && h * 2 <= newH)
        {
            d = Scale2x(d, stride, w, h);
            w *= 2;
            h *= 2;
        }
        return Image.CreateFromData(newW, newH, false, format, ResampleExact(d, stride, w, h, newW, newH));
    }

    // The EPX / Scale2x kernel. Each source pixel becomes four, and a quadrant
    // takes the value of the two neighbours meeting at its corner — but only
    // when those two AGREE and the opposite two DISAGREE. That guard is the
    // whole safety property: it identifies a corner and never a line, so a
    // one-pixel ridge or an isolated cell cannot be eroded (verified).
    private static byte[] Scale2x(byte[] d, int stride, int w, int h)
    {
        int dw = w * 2;
        var dst = new byte[w * h * 4 * stride];
        for (int z = 0; z < h; z++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = x + z * w;
                ulong pk = Key(d, p, stride);
                int up = Index(w, h, x, z - 1);
                int down = Index(w, h, x, z + 1);
                int left = Index(w, h, x - 1, z);
                int right = Index(w, h, x + 1, z);
                ulong a = Key(d, up, stride);
                ulong b = Key(d, right, stride);
                ulong c = Key(d, left, stride);
                ulong e = Key(d, down, stride);

                int tl = (c == a && c != e && a != b) ? left : p;
                int tr = (a == b && a != c && b != e) ? right : p;
                int bl = (e == c && e != b && c != a) ? left : p;
                int br = (b == e && b != a && e != c) ? right : p;

                Put(d, dst, stride, tl, (x * 2) + (z * 2) * dw);
                Put(d, dst, stride, tr, (x * 2 + 1) + (z * 2) * dw);
                Put(d, dst, stride, bl, (x * 2) + (z * 2 + 1) * dw);
                Put(d, dst, stride, br, (x * 2 + 1) + (z * 2 + 1) * dw);
            }
        }
        return dst;
    }

    // Land on the exact target size. Each destination pixel takes the MODE of
    // the source region it covers — the honest categorical decimation when
    // shrinking, where picking a single sample would drop thin features at
    // random, and plain nearest when the region is one pixel.
    private static byte[] ResampleExact(byte[] sd, int stride, int oldW, int oldH, int newW, int newH)
    {
        var dd = new byte[newW * newH * stride];
        var counts = new System.Collections.Generic.Dictionary<ulong, int>();
        for (int x = 0; x < newW; x++)
        {
            int sx0 = x * oldW / newW;
            int sx1 = Mathf.Max(sx0 + 1, (x + 1) * oldW / newW);
            for (int z = 0; z < newH; z++)
            {
                int sz0 = z * oldH / newH;
                int sz1 = Mathf.Max(sz0 + 1, (z + 1) * oldH / newH);
                int best = ((sx0 + sx1 - 1) / 2) + ((sz0 + sz1 - 1) / 2) * oldW;
                if (sx1 - sx0 > 1 || sz1 - sz0 > 1)
                {
                    best = ModeOfRegion(sd, stride, oldW, sx0, sx1, sz0, sz1, best, counts);
                }
                Put(sd, dd, stride, best, x + z * newW);
            }
        }
        return dd;
    }

    private static int ModeOfRegion(byte[] d, int stride, int w,
        int x0, int x1, int z0, int z1, int fallbackIndex,
        System.Collections.Generic.Dictionary<ulong, int> counts)
    {
        counts.Clear();
        int bestIndex = fallbackIndex;
        int bestCount = 0;
        ulong fallbackKey = Key(d, fallbackIndex, stride);
        for (int z = z0; z < z1; z++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = x + z * w;
                ulong key = Key(d, i, stride);
                counts.TryGetValue(key, out int seen);
                seen++;
                counts[key] = seen;
                // Ties go to the pixel the destination is centred on, so a 50/50
                // split resolves to what was actually under the sample rather
                // than to whichever value the scan happened to meet first.
                if (seen > bestCount || (seen == bestCount && key == fallbackKey))
                {
                    bestCount = seen;
                    bestIndex = i;
                }
            }
        }
        return bestIndex;
    }

    private static void Put(byte[] src, byte[] dst, int stride, int srcPixel, int dstPixel)
    {
        System.Array.Copy(src, srcPixel * stride, dst, dstPixel * stride, stride);
    }

    // Clamped, so the map border behaves like flat ground continuing outwards
    // rather than like a corner to be cut.
    private static int Index(int w, int h, int x, int z)
    {
        return Mathf.Clamp(x, 0, w - 1) + Mathf.Clamp(z, 0, h - 1) * w;
    }

    private static ulong Key(byte[] d, int pixelIndex, int stride)
    {
        int at = pixelIndex * stride;
        ulong key = 0;
        for (int i = 0; i < stride; i++)
        {
            key |= (ulong)d[at + i] << (i * 8);
        }
        return key;
    }
}
