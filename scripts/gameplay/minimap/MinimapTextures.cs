using Godot;

// Owns the GPU-side resources for the overworld minimap and the CPU-side
// byte buffers that back them.
//
// Layout:
//   _surfaceData  — RGBA8, full world XZ extent at OutdoorMetersPerPixel
//                    R = height low byte, G = height high byte,
//                    B = resolved tile id, A = foliage id.
//                    Caller-side monotonic merge: only overwrite a pixel
//                    when the new height >= existing (vertically stacked
//                    chunk loads must converge to the highest surface).
//   _exploration  — R8, same dimensions. The MINIMAP's display buffer = party
//                    pool ∪ active member's provisional reveal. Reveal writes
//                    into it live (max(existing, falloff)) so the controlled
//                    player's freshly-charted ground shows immediately.
//   _explorationBanked — R8, same dimensions. The WORLD MAP's display buffer =
//                    party pool ONLY. Updated solely by RebuildExploration, so
//                    un-banked field reveal stays off the world map until it's
//                    recorded at a campfire.
//
// Sized once at construction from WorldState.Min/Max; one full-extent
// upload per flush.
public class MinimapTextures
{
    public const int BytesPerSurfacePixel = 4; // RGBA8

    private readonly byte[] _surfaceData;
    private readonly byte[] _exploration;
    private readonly byte[] _explorationBanked;
    private readonly Image _surfaceImage;
    private readonly Image _explorationImage;
    private readonly Image _explorationBankedImage;
    private readonly ImageTexture _surfaceTexture;
    private readonly ImageTexture _explorationTexture;
    private readonly ImageTexture _explorationBankedTexture;

    private readonly int _widthPixels;
    private readonly int _heightPixels;
    // Subtracted from every stored height; see MinimapData.HeightBias.
    private readonly int _heightBias;
    // World-XZ corner of pixel (0,0) in the texture, in voxel coords.
    private readonly Vector2I _worldOriginXZ;
    // Chunk-coord origin (Min.X, Min.Z).
    private readonly Vector2I _chunkOriginXZ;
    private readonly int _chunksWide;
    private readonly int _chunksTall;

    private bool _surfaceDirty;
    private bool _explorationDirty;
    private bool _explorationBankedDirty;

    public ImageTexture SurfaceTexture => _surfaceTexture;
    // Minimap display (party ∪ active); world map display (party only).
    public ImageTexture ExplorationTexture => _explorationTexture;
    public ImageTexture ExplorationBankedTexture => _explorationBankedTexture;
    // Length of the outdoor exploration buffer — callers size their per-member
    // ExplorationMask.Outdoor to this so pixel indices line up 1:1 with display.
    public int ExplorationBufferSize => _widthPixels * _heightPixels;
    public int WidthPixels => _widthPixels;
    public int HeightPixels => _heightPixels;
    public Vector2I WorldOriginXZ => _worldOriginXZ;
    public Vector2 ExtentMeters => new Vector2(
        _widthPixels * MinimapData.OutdoorMetersPerPixel,
        _heightPixels * MinimapData.OutdoorMetersPerPixel);

    public MinimapTextures(WorldState world)
    {
        _chunkOriginXZ = new Vector2I(world.Min.X, world.Min.Z);
        _chunksWide = world.Max.X - world.Min.X + 1;
        _chunksTall = world.Max.Z - world.Min.Z + 1;
        _widthPixels = _chunksWide * MinimapData.OutdoorPixelsPerChunk;
        _heightPixels = _chunksTall * MinimapData.OutdoorPixelsPerChunk;
        _worldOriginXZ = new Vector2I(world.Min.X * ChunkState.SIZE, world.Min.Z * ChunkState.SIZE);
        _heightBias = MinimapData.HeightBias(world);

        _surfaceData = new byte[_widthPixels * _heightPixels * BytesPerSurfacePixel];
        _exploration = new byte[_widthPixels * _heightPixels];
        _explorationBanked = new byte[_widthPixels * _heightPixels];

        _surfaceImage = Image.CreateFromData(_widthPixels, _heightPixels, false, Image.Format.Rgba8, _surfaceData);
        _explorationImage = Image.CreateFromData(_widthPixels, _heightPixels, false, Image.Format.R8, _exploration);
        _explorationBankedImage = Image.CreateFromData(_widthPixels, _heightPixels, false, Image.Format.R8, _explorationBanked);
        _surfaceTexture = ImageTexture.CreateFromImage(_surfaceImage);
        _explorationTexture = ImageTexture.CreateFromImage(_explorationImage);
        _explorationBankedTexture = ImageTexture.CreateFromImage(_explorationBankedImage);
    }

    // Apply one chunk's surface contribution. cells.Length must be at least
    // OutdoorPixelsPerChunkSq. Foliage palette is consulted for priority
    // resolution; null = priority always wins (treat new stamp as priority 1).
    //
    // Monotonic merge:
    //   - Higher new height: overwrite (new column wins).
    //   - Equal height: overwrite (later chunk loads refresh existing data).
    //   - Lower height: skip (existing column was contributed by a chunk
    //     above this one — it wins).
    //
    // Foliage merge is independent of height: priority comparison only.
    public void ApplyChunkSurface(Vector3I chunkCoord, MinimapData.SurfaceCell[] cells, MinimapFoliageColors foliagePalette)
    {
        int chunkPxOriginX = (chunkCoord.X - _chunkOriginXZ.X) * MinimapData.OutdoorPixelsPerChunk;
        int chunkPxOriginZ = (chunkCoord.Z - _chunkOriginXZ.Y) * MinimapData.OutdoorPixelsPerChunk;
        if (chunkPxOriginX < 0 || chunkPxOriginZ < 0
            || chunkPxOriginX + MinimapData.OutdoorPixelsPerChunk > _widthPixels
            || chunkPxOriginZ + MinimapData.OutdoorPixelsPerChunk > _heightPixels)
        {
            return;
        }

        bool changed = false;
        for (int pz = 0; pz < MinimapData.OutdoorPixelsPerChunk; pz++)
        {
            for (int px = 0; px < MinimapData.OutdoorPixelsPerChunk; px++)
            {
                MinimapData.SurfaceCell cell = cells[pz * MinimapData.OutdoorPixelsPerChunk + px];
                if (cell.Height == MinimapData.NoSurfaceHeight)
                {
                    continue;
                }

                int gx = chunkPxOriginX + px;
                int gz = chunkPxOriginZ + pz;
                int byteIdx = (gz * _widthPixels + gx) * BytesPerSurfacePixel;

                ushort existingHeight = (ushort)(_surfaceData[byteIdx] | (_surfaceData[byteIdx + 1] << 8));
                if (cell.Height < existingHeight)
                {
                    // Lower contribution loses; foliage check still needed below.
                    int existingFoliage = _surfaceData[byteIdx + 3];
                    int newPriority = ResolvePriority(cell.FoliageId, foliagePalette);
                    int existingPriority = ResolvePriority((byte)existingFoliage, foliagePalette);
                    if (cell.FoliageId != 0 && newPriority >= existingPriority)
                    {
                        _surfaceData[byteIdx + 3] = cell.FoliageId;
                        changed = true;
                    }
                    continue;
                }

                _surfaceData[byteIdx + 0] = (byte)(cell.Height & 0xFF);
                _surfaceData[byteIdx + 1] = (byte)((cell.Height >> 8) & 0xFF);
                _surfaceData[byteIdx + 2] = cell.TileId;

                int existingFoliageHi = _surfaceData[byteIdx + 3];
                int newPrio = ResolvePriority(cell.FoliageId, foliagePalette);
                int existingPrio = ResolvePriority((byte)existingFoliageHi, foliagePalette);
                if (cell.FoliageId != 0 && newPrio >= existingPrio)
                {
                    _surfaceData[byteIdx + 3] = cell.FoliageId;
                }
                else if (cell.Height > existingHeight && cell.FoliageId == 0)
                {
                    // Strictly higher new surface clears stale foliage that
                    // belonged to the lower terrain. Equal-height refreshes
                    // keep existing foliage (avoids wiping a tree on a chunk
                    // re-apply with no new scatter).
                    _surfaceData[byteIdx + 3] = 0;
                }
                changed = true;
            }
        }
        if (changed)
        {
            _surfaceDirty = true;
        }
    }

    // Stamp a foliage entry at a world XZ position (typically a prop's
    // origin) as a small disk so the result reads as a rounded blob with
    // crisp edges (the shader uses nearest-neighbor on foliage_id, so the
    // shape comes from the stamped texels themselves). Radius is in
    // source pixels: 0 = single pixel, 1 = 5-pixel plus, 2 = 13-pixel disk.
    public void StampFoliagePoint(Vector3 worldPos, byte foliageId, MinimapFoliageColors palette, int radiusPixels = 0)
    {
        if (foliageId == 0)
        {
            return;
        }
        int cx = (Mathf.FloorToInt(worldPos.X) - _worldOriginXZ.X) / MinimapData.OutdoorMetersPerPixel;
        int cz = (Mathf.FloorToInt(worldPos.Z) - _worldOriginXZ.Y) / MinimapData.OutdoorMetersPerPixel;
        int newPrio = ResolvePriority(foliageId, palette);
        int rSq = radiusPixels * radiusPixels + radiusPixels;
        bool changed = false;
        for (int dz = -radiusPixels; dz <= radiusPixels; dz++)
        {
            for (int dx = -radiusPixels; dx <= radiusPixels; dx++)
            {
                if (dx * dx + dz * dz > rSq)
                {
                    continue;
                }
                int px = cx + dx;
                int pz = cz + dz;
                if (px < 0 || pz < 0 || px >= _widthPixels || pz >= _heightPixels)
                {
                    continue;
                }
                int byteIdx = (pz * _widthPixels + px) * BytesPerSurfacePixel;
                int existingPrio = ResolvePriority(_surfaceData[byteIdx + 3], palette);
                if (newPrio >= existingPrio)
                {
                    _surfaceData[byteIdx + 3] = foliageId;
                    changed = true;
                }
            }
        }
        if (changed)
        {
            _surfaceDirty = true;
        }
    }

    // Reveal a circular disk in the exploration mask centered at world XZ.
    // `innerFraction` controls the soft-edge: inside pxRadius * innerFraction
    // the disk paints 255, from there it linearly falls to 0 at the outer
    // edge. 1.0 = hard edge, ~0.5 = wide soft fade.
    //
    // Apply one reveal sample: max-merge into the caller's provisional buffer
    // (banked at a campfire) and into the live minimap display buffer (party ∪
    // active, shown immediately). The banked world-map buffer is untouched.
    private void WriteReveal(byte[] individual, int idx, byte target)
    {
        if (target > individual[idx])
        {
            individual[idx] = target;
        }
        if (target > _exploration[idx])
        {
            _exploration[idx] = target;
            _explorationDirty = true;
        }
    }

    // Writes into the caller's per-member `individual` buffer (may be null
    // before a roster exists) — the active member's provisional field reveal —
    // AND into the live minimap display buffer (party ∪ active) so the
    // controlled player's newly-charted ground shows immediately. It does NOT
    // touch the banked world-map buffer: un-banked reveal stays off the world
    // map until it's recorded at a campfire, at which point RebuildExploration
    // recomposes both display buffers from the (now-merged) party pool.
    public void RevealCircle(Vector3 worldPosXZ, float radiusMeters, float innerFraction, byte[] individual)
    {
        if (individual == null)
        {
            return;
        }
        float pxRadius = radiusMeters / MinimapData.OutdoorMetersPerPixel;
        int cx = (Mathf.FloorToInt(worldPosXZ.X) - _worldOriginXZ.X) / MinimapData.OutdoorMetersPerPixel;
        int cz = (Mathf.FloorToInt(worldPosXZ.Z) - _worldOriginXZ.Y) / MinimapData.OutdoorMetersPerPixel;
        int r = Mathf.CeilToInt(pxRadius);
        int x0 = Mathf.Max(cx - r, 0);
        int x1 = Mathf.Min(cx + r, _widthPixels - 1);
        int z0 = Mathf.Max(cz - r, 0);
        int z1 = Mathf.Min(cz + r, _heightPixels - 1);
        float innerR = pxRadius * Mathf.Clamp(innerFraction, 0f, 1f);
        float innerSq = innerR * innerR;
        float outerSq = pxRadius * pxRadius;
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int dx = x - cx;
                int dz = z - cz;
                int distSq = dx * dx + dz * dz;
                if (distSq > outerSq)
                {
                    continue;
                }
                byte target;
                if (distSq <= innerSq)
                {
                    target = 255;
                }
                else
                {
                    float t = (outerSq - distSq) / (outerSq - innerSq);
                    target = (byte)Mathf.Clamp((int)(t * 255f), 0, 255);
                }
                int idx = z * _widthPixels + x;
                WriteReveal(individual, idx, target);
            }
        }
    }

    // Line-of-sight reveal: same soft disk as RevealCircle, but each cell's
    // value is additionally scaled by terrain occlusion (a mountain hides the
    // valley behind it) and volumetric fog accumulated along the sightline. Used
    // for ground-level outdoor reveal; bird's-eye uses RevealCircleFogged and the
    // los-disabled path uses RevealCircle. `eyeFootPos` is the player's feet;
    // the sightline origin is lifted by los.EyeHeightMeters.
    public void RevealViewshed(Vector3 eyeFootPos, float radiusMeters, float innerFraction, in MinimapLos los, WorldState ws, byte[] individual)
    {
        if (individual == null)
        {
            return;
        }
        Vector2 eyeXZ = new Vector2(eyeFootPos.X, eyeFootPos.Z);
        float eyeY = eyeFootPos.Y + los.EyeHeightMeters;
        float pxRadius = radiusMeters / MinimapData.OutdoorMetersPerPixel;
        int cx = (Mathf.FloorToInt(eyeFootPos.X) - _worldOriginXZ.X) / MinimapData.OutdoorMetersPerPixel;
        int cz = (Mathf.FloorToInt(eyeFootPos.Z) - _worldOriginXZ.Y) / MinimapData.OutdoorMetersPerPixel;
        int r = Mathf.CeilToInt(pxRadius);
        int x0 = Mathf.Max(cx - r, 0);
        int x1 = Mathf.Min(cx + r, _widthPixels - 1);
        int z0 = Mathf.Max(cz - r, 0);
        int z1 = Mathf.Min(cz + r, _heightPixels - 1);
        float innerR = pxRadius * Mathf.Clamp(innerFraction, 0f, 1f);
        float innerSq = innerR * innerR;
        float outerSq = pxRadius * pxRadius;
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int dx = x - cx;
                int dz = z - cz;
                int distSq = dx * dx + dz * dz;
                if (distSq > outerSq)
                {
                    continue;
                }
                float falloff = distSq <= innerSq
                    ? 1f
                    : (outerSq - distSq) / (outerSq - innerSq);
                int wx = _worldOriginXZ.X + x * MinimapData.OutdoorMetersPerPixel;
                int wz = _worldOriginXZ.Y + z * MinimapData.OutdoorMetersPerPixel;
                float terrainVis = ComputeGroundVisibility(eyeXZ, eyeY, wx, wz, los, ws, out float fogVis);
                byte target = (byte)Mathf.Clamp((int)(falloff * terrainVis * fogVis * 255f), 0, 255);
                int idx = z * _widthPixels + x;
                WriteReveal(individual, idx, target);
            }
        }
    }

    // Bird's-eye reveal: a plain filled disk (no terrain occlusion — scouting
    // from above looks straight down over the terrain) attenuated only by
    // LOCAL volumetric fog at each cell, scaled by distance. A distant column
    // buried in a painted fog volume stays uncharted; a near one reads through.
    public void RevealCircleFogged(Vector3 worldPosXZ, float radiusMeters, float innerFraction, WorldState ws, float fogFullBlockMeters, byte[] individual)
    {
        if (individual == null)
        {
            return;
        }
        bool fogOn = ws != null && fogFullBlockMeters > 0f;
        float pxRadius = radiusMeters / MinimapData.OutdoorMetersPerPixel;
        int cx = (Mathf.FloorToInt(worldPosXZ.X) - _worldOriginXZ.X) / MinimapData.OutdoorMetersPerPixel;
        int cz = (Mathf.FloorToInt(worldPosXZ.Z) - _worldOriginXZ.Y) / MinimapData.OutdoorMetersPerPixel;
        int r = Mathf.CeilToInt(pxRadius);
        int x0 = Mathf.Max(cx - r, 0);
        int x1 = Mathf.Min(cx + r, _widthPixels - 1);
        int z0 = Mathf.Max(cz - r, 0);
        int z1 = Mathf.Min(cz + r, _heightPixels - 1);
        float innerR = pxRadius * Mathf.Clamp(innerFraction, 0f, 1f);
        float innerSq = innerR * innerR;
        float outerSq = pxRadius * pxRadius;
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int dx = x - cx;
                int dz = z - cz;
                int distSq = dx * dx + dz * dz;
                if (distSq > outerSq)
                {
                    continue;
                }
                float falloff = distSq <= innerSq
                    ? 1f
                    : (outerSq - distSq) / (outerSq - innerSq);
                float fogVis = 1f;
                if (fogOn)
                {
                    int wx = _worldOriginXZ.X + x * MinimapData.OutdoorMetersPerPixel;
                    int wz = _worldOriginXZ.Y + z * MinimapData.OutdoorMetersPerPixel;
                    int th = GetHeightAtWorld(wx, wz);
                    int fy = th == 0 ? Mathf.FloorToInt(worldPosXZ.Y) : th - 1;
                    int fog = ws.GetFogWorld(wx, fy, wz);
                    if (fog > 0)
                    {
                        float distMeters = Mathf.Sqrt(distSq) * MinimapData.OutdoorMetersPerPixel;
                        fogVis = Mathf.Clamp(1f - (fog / 255f) * distMeters / fogFullBlockMeters, 0f, 1f);
                    }
                }
                byte target = (byte)Mathf.Clamp((int)(falloff * fogVis * 255f), 0, 255);
                int idx = z * _widthPixels + x;
                WriteReveal(individual, idx, target);
            }
        }
    }

    // Terrain-only line-of-sight visibility [0..1] of the column at world
    // (wx, wz) from the eye — 1 = clear, 0 = fully occluded. Used to gate the
    // slice-column reveal pass so cliff faces hidden behind a ridge stay dark.
    // Fog is intentionally excluded here (the slice pass is a secondary trace).
    public float ColumnVisibility(Vector2 eyeXZ, float eyeY, int wx, int wz, in MinimapLos los)
    {
        return ComputeGroundVisibility(eyeXZ, eyeY, wx, wz, los, null, out _);
    }

    // Marches the 2 m heightmap from the eye toward the target column, tracking
    // the maximum elevation angle of intervening terrain (the running horizon).
    // The target is visible when its own ground — lifted by ForgivenessMeters —
    // rises to that horizon, fading out over the forgiveness band below it. When
    // `ws` is non-null and fog is enabled, also accumulates fog optical depth
    // along the sightline into `fogVis`. Height 0 (unstamped column) is treated
    // as no occluder / no target terrain so map edges and water still reveal.
    private float ComputeGroundVisibility(Vector2 eyeXZ, float eyeY, int wx, int wz, in MinimapLos los, WorldState ws, out float fogVis)
    {
        fogVis = 1f;
        float dxw = wx - eyeXZ.X;
        float dzw = wz - eyeXZ.Y;
        float dist = Mathf.Sqrt(dxw * dxw + dzw * dzw);
        if (dist < 1f)
        {
            return 1f;
        }
        int th = GetHeightAtWorld(wx, wz);
        float targetGroundY = th == 0 ? eyeY : th - 1;

        float step = Mathf.Max(los.StepMeters, dist / MinimapData.LosMaxStepsPerRay);
        float invDist = 1f / dist;
        float nx = dxw * invDist;
        float nz = dzw * invDist;
        float maxHorizon = float.NegativeInfinity;
        float fogDepth = 0f;
        bool fogOn = ws != null && los.FogFullBlockMeters > 0f;

        for (float t = step; t < dist; t += step)
        {
            int sx = Mathf.RoundToInt(eyeXZ.X + nx * t);
            int sz = Mathf.RoundToInt(eyeXZ.Y + nz * t);
            int sh = GetHeightAtWorld(sx, sz);
            if (sh != 0)
            {
                float ang = ((sh - 1) - eyeY) / t;
                if (ang > maxHorizon)
                {
                    maxHorizon = ang;
                }
            }
            if (fogOn)
            {
                float lineY = eyeY + (targetGroundY - eyeY) * (t * invDist);
                int fog = ws.GetFogWorld(sx, Mathf.FloorToInt(lineY), sz);
                if (fog > 0)
                {
                    fogDepth += (fog / 255f) * step / los.FogFullBlockMeters;
                }
            }
        }

        float terrainVis;
        if (float.IsNegativeInfinity(maxHorizon))
        {
            terrainVis = 1f;
        }
        else
        {
            float horizonY = eyeY + maxHorizon * dist;
            terrainVis = Mathf.Clamp((targetGroundY + los.ForgivenessMeters - horizonY) / los.ForgivenessMeters, 0f, 1f);
        }
        if (fogOn)
        {
            fogVis = Mathf.Clamp(1f - fogDepth, 0f, 1f);
        }
        return terrainVis;
    }

    // Recompose both display exploration buffers. The minimap buffer is
    // party ∪ active (the controlled player's un-banked field reveal is shown),
    // the world-map buffer is party only. Called on bank, member switch, and
    // revive — the switch case is why the minimap buffer is fully rebuilt rather
    // than only accrued: it must drop the previous member's provisional reveal.
    // Either mask may be null (nothing banked / no active member yet).
    public void RebuildExploration(byte[] party, byte[] active)
    {
        for (int i = 0; i < _exploration.Length; i++)
        {
            byte p = (party != null && i < party.Length) ? party[i] : (byte)0;
            byte a = (active != null && i < active.Length) ? active[i] : (byte)0;
            _exploration[i] = a > p ? a : p;
            _explorationBanked[i] = p;
        }
        _explorationDirty = true;
        _explorationBankedDirty = true;
    }

    // Snapshot / drive the world-map (banked) outdoor buffer. The campfire reveal
    // animation captures the pre-bank buffer, then walks the displayed buffer from
    // that baseline up to the freshly-banked buffer over ~1.5s so newly charted
    // ground grows in on the world map instead of popping (see Minimap reveal anim).
    public byte[] CopyBankedOutdoor()
    {
        return (byte[])_explorationBanked.Clone();
    }

    public void SetBankedOutdoor(byte[] data)
    {
        if (data == null || data.Length != _explorationBanked.Length)
        {
            return;
        }
        System.Array.Copy(data, _explorationBanked, _explorationBanked.Length);
        _explorationBankedDirty = true;
    }

    // Fold a member's outdoor field reveal into the WORLD MAP's display buffer as
    // a one-shot snapshot (per-pixel max) — the tree-climb scout. The perched
    // wide reveal graduates onto the world map immediately (and stays frozen
    // there, since normal walking reveal only writes the minimap's _exploration),
    // without waiting for a campfire bank. A later RebuildExploration reseeds this
    // buffer from the party pool, at which point a banked snapshot persists.
    public void MergeActiveIntoBanked(byte[] activeOutdoor)
    {
        if (activeOutdoor == null)
        {
            return;
        }
        int n = System.Math.Min(_explorationBanked.Length, activeOutdoor.Length);
        bool changed = false;
        for (int i = 0; i < n; i++)
        {
            if (activeOutdoor[i] > _explorationBanked[i])
            {
                _explorationBanked[i] = activeOutdoor[i];
                changed = true;
            }
        }
        if (changed)
        {
            _explorationBankedDirty = true;
        }
    }

    // Normalized (0..1) world-map reveal value at world XZ — same world→pixel
    // mapping as IsRevealed, reading the banked display buffer. Out-of-bounds reads
    // as 0. Lets world-map marker icons fade in with their ground during the
    // campfire reveal sweep (Minimap.BankedRevealAlphaAt).
    public float SampleBankedOutdoorAlpha(Vector3 worldPosXZ)
    {
        int px = (Mathf.FloorToInt(worldPosXZ.X) - _worldOriginXZ.X) / MinimapData.OutdoorMetersPerPixel;
        int pz = (Mathf.FloorToInt(worldPosXZ.Z) - _worldOriginXZ.Y) / MinimapData.OutdoorMetersPerPixel;
        if (px < 0 || pz < 0 || px >= _widthPixels || pz >= _heightPixels)
        {
            return 0f;
        }
        return _explorationBanked[pz * _widthPixels + px] / 255f;
    }

    // True if world XZ is revealed (value > threshold) in the supplied outdoor
    // mask buffer (typically a member's ExplorationMask.Outdoor). Same world→pixel
    // mapping as RevealCircle. A null/short buffer or out-of-bounds position reads
    // as unrevealed. Drives reveal-gated map-marker discovery (Minimap).
    public bool IsRevealed(byte[] outdoor, Vector3 worldPosXZ, byte threshold = 0)
    {
        if (outdoor == null)
        {
            return false;
        }
        int px = (Mathf.FloorToInt(worldPosXZ.X) - _worldOriginXZ.X) / MinimapData.OutdoorMetersPerPixel;
        int pz = (Mathf.FloorToInt(worldPosXZ.Z) - _worldOriginXZ.Y) / MinimapData.OutdoorMetersPerPixel;
        if (px < 0 || pz < 0 || px >= _widthPixels || pz >= _heightPixels)
        {
            return false;
        }
        int idx = pz * _widthPixels + px;
        return idx < outdoor.Length && outdoor[idx] > threshold;
    }

    // Diagnostic (`minimap_probe`): what the shader's height-derived terms
    // actually see. The contour pass and the plateau banding are both authored in
    // absolute meters, so they silently stop working when the world's vertical
    // extent changes out from under them — these are the numbers that say by how
    // much. `metersPerScreenPixel` converts a per-texel delta into the fwidth the
    // contour anti-aliasing reads.
    public string FormatHeightStats(float referenceElevation, float metersPerScreenPixel)
    {
        int minH = int.MaxValue;
        int maxH = 0;
        int stamped = 0;
        int stepCells = 0;
        long deltaSum = 0;
        int maxDelta = 0;
        int aboveRef = 0;
        int belowRef = 0;
        int refPlateau = Mathf.FloorToInt(referenceElevation / MinimapData.PlateauHeight);
        for (int z = 0; z < _heightPixels; z++)
        {
            for (int x = 0; x < _widthPixels; x++)
            {
                int h = HeightAtPixel(x, z);
                if (h == 0)
                {
                    continue;
                }
                stamped++;
                minH = Mathf.Min(minH, h);
                maxH = Mathf.Max(maxH, h);
                int plateau = h / MinimapData.PlateauHeight;
                if (plateau > refPlateau) { aboveRef++; }
                else if (plateau < refPlateau) { belowRef++; }

                int d = 0;
                d = Mathf.Max(d, NeighborDelta(h, x - 1, z));
                d = Mathf.Max(d, NeighborDelta(h, x + 1, z));
                d = Mathf.Max(d, NeighborDelta(h, x, z - 1));
                d = Mathf.Max(d, NeighborDelta(h, x, z + 1));
                deltaSum += d;
                maxDelta = Mathf.Max(maxDelta, d);
                // Same test as the shader's is_step gate.
                if (d >= MinimapData.PlateauHeight * 0.9f) { stepCells++; }
            }
        }
        if (stamped == 0)
        {
            return "minimap_probe: outdoor heightmap is empty (no chunk has stamped a surface).";
        }
        float avgDelta = (float)deltaSum / stamped;
        // fwidth(h0) the contour AA reads, from the average and worst per-texel rise.
        float texelsPerScreenPixel = metersPerScreenPixel / MinimapData.OutdoorMetersPerPixel;
        float fwidthAvg = avgDelta * texelsPerScreenPixel;
        float fwidthMax = maxDelta * texelsPerScreenPixel;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== minimap probe (outdoor heightmap) ===");
        sb.AppendLine($"  texture           = {_widthPixels}x{_heightPixels} px @ {MinimapData.OutdoorMetersPerPixel} m/px");
        sb.AppendLine($"  stamped columns   = {stamped} / {_widthPixels * _heightPixels}");
        sb.AppendLine($"  world Y range     = {minH + _heightBias}..{maxH + _heightBias}  (vertical extent {maxH - minH} m, height bias {_heightBias})");
        sb.AppendLine($"  neighbor delta    = avg {avgDelta:F2} m, max {maxDelta} m  (per {MinimapData.OutdoorMetersPerPixel} m texel)");
        sb.AppendLine($"  CONTOUR: is_step gate (delta >= {MinimapData.PlateauHeight * 0.9f:F1} m) passes on {stepCells} cells ({100f * stepCells / stamped:F1}%)");
        sb.AppendLine($"           fwidth(h) ~ {fwidthAvg:F2} m/px avg, {fwidthMax:F2} m/px worst, at {metersPerScreenPixel:F2} m per screen pixel");
        sb.AppendLine($"           lines need fwidth well under contour_interval/2; pick contour_interval >= ~{Mathf.Max(4f, Mathf.Ceil(fwidthAvg * 8f)):F0} m");
        sb.AppendLine($"  BANDING: vs reference elevation {referenceElevation:F1} m -> {100f * aboveRef / stamped:F1}% above, {100f * belowRef / stamped:F1}% below, {100f * (stamped - aboveRef - belowRef) / stamped:F1}% same plateau");
        return sb.ToString();
    }

    private int HeightAtPixel(int px, int pz)
    {
        int byteIdx = (pz * _widthPixels + px) * BytesPerSurfacePixel;
        return _surfaceData[byteIdx] | (_surfaceData[byteIdx + 1] << 8);
    }

    private int NeighborDelta(int h, int px, int pz)
    {
        if (px < 0 || pz < 0 || px >= _widthPixels || pz >= _heightPixels)
        {
            return 0;
        }
        int n = HeightAtPixel(px, pz);
        return n == 0 ? 0 : Mathf.Abs(n - h);
    }

    // Returns the ABSOLUTE top-face world Y at world XZ, or 0 if the column is
    // out-of-bounds or hasn't been stamped yet. Used by the slice reveal
    // pass to find the ground elevation under each column it visits, so
    // cliffs / sloping terrain near the player reveal their proper slice
    // exploration even when the player never physically walks there.
    //
    // The buffer stores biased heights (MinimapData.HeightBias); the bias comes
    // off here so callers keep working in real world Y — they feed this straight
    // into voxel/fog lookups. Signed, since a world with terrain below Y=0 has
    // legitimately negative surface heights.
    public int GetHeightAtWorld(int wx, int wz)
    {
        int px = (wx - _worldOriginXZ.X) / MinimapData.OutdoorMetersPerPixel;
        int pz = (wz - _worldOriginXZ.Y) / MinimapData.OutdoorMetersPerPixel;
        if (px < 0 || pz < 0 || px >= _widthPixels || pz >= _heightPixels)
        {
            return 0;
        }
        int raw = HeightAtPixel(px, pz);
        return raw == 0 ? 0 : raw + _heightBias;
    }

    // Push CPU buffer changes to the GPU. Full-texture upload — region
    // updates aren't exposed cleanly via ImageTexture in Godot 4.
    public void Flush()
    {
        if (_surfaceDirty)
        {
            _surfaceImage.SetData(_widthPixels, _heightPixels, false, Image.Format.Rgba8, _surfaceData);
            _surfaceTexture.Update(_surfaceImage);
            _surfaceDirty = false;
        }
        if (_explorationDirty)
        {
            _explorationImage.SetData(_widthPixels, _heightPixels, false, Image.Format.R8, _exploration);
            _explorationTexture.Update(_explorationImage);
            _explorationDirty = false;
        }
        if (_explorationBankedDirty)
        {
            _explorationBankedImage.SetData(_widthPixels, _heightPixels, false, Image.Format.R8, _explorationBanked);
            _explorationBankedTexture.Update(_explorationBankedImage);
            _explorationBankedDirty = false;
        }
    }

    private static int ResolvePriority(byte foliageId, MinimapFoliageColors palette)
    {
        if (foliageId == 0)
        {
            return 0;
        }
        if (palette == null)
        {
            return 1;
        }
        MinimapFoliageEntry entry = palette.Get(foliageId);
        return entry?.priority ?? 1;
    }
}
