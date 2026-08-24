using Godot;

public class ChunkState
{
    public const int SIZE = 16;

    // Coarse environmental subgrid resolution. Each cell covers a
    // SIZE/ENV_SUBGRID_SIZE cube of voxels (currently 4³ voxels per cell,
    // so 64 cells per chunk). Shared by all per-chunk environmental
    // subgrids (wind factor, env-tag, …) so a single trilinear-sample
    // expression works regardless of which subgrid the caller is reading.
    // Bumping this is a wire format change, so keep it stable.
    public const int ENV_SUBGRID_SIZE = 4;
    public const int ENV_VOXELS_PER_CELL = SIZE / ENV_SUBGRID_SIZE;

    public readonly Vector3I ChunkCoord;

    // Coarse fill classification used by the minimap (and any future system
    // that wants to skip per-voxel work on uniform chunks). Computed lazily on
    // first GetFill() call and cached. Callers that mutate Voxels must call
    // InvalidateFill() — this class doesn't wrap the array writes since
    // callers (worldgen, mesher, voxel ops) write directly into Voxels.
    public enum EChunkFill : byte
    {
        Unknown = 0,
        Mixed = 1,   // contains a mix of voxel types
        Pure = 2,    // every voxel is the same type — read FillType
    }
    private EChunkFill _fill = EChunkFill.Unknown;
    private int _fillType;

    // Index into WorldState.Zones[]. Picks the zone this chunk
    // belongs to — drives ZoneBlend.Sample's per-chunk weighting so
    // an arbitrary zone shape can be authored just by stamping different
    // indices on chunks. Set by WorldGen (or a future editor) at world creation.
    public byte ZoneIndex;

    // Index into WorldState.Regions[]. Picks the named region this
    // chunk belongs to — orthogonal to ZoneIndex (a single named region
    // can span multiple zones / biomes, and a single zone can host
    // multiple regions). GameClient.UpdateRegion samples this to drive
    // the banner pulse and discovered-regions log. Set by WorldGen (or
    // a future editor) at world creation.
    public byte RegionIndex;

    public readonly byte[,,] Voxels;

    // Per-voxel shape tag: stores SharpAxes flags as a byte.
    // This is the shape channel — orthogonal to int (the material
    // channel). Worldgen writes it when placing voxels; the DC mesher reads
    // it to decide per-axis Y/X/Z snapping.
    // Authoring map:
    //   Building voxels (Stone/Wood/Barrier) → All    (fully blocky)
    //   Flat natural terrain / cave floor+ceiling → Y (snap vertical, smooth lateral)
    //   Ramps / path-band slopes → None               (smooth interpolation)
    public readonly byte[,,] Shape;

    // Per-voxel terrain id. Index into the active world's terrain palette
    // (derived from the kit palette built by deduplicating each zone's
    // SurfaceKit/CaveKit/SubmergedKit refs and uploaded globally via
    // ChunkMesh.SetTerrains). Orthogonal to int: a voxel tagged
    // VoxelType.Terrain with TerrainId=2 means "AUTO land that reads from
    // palette slot 2's terrain." Per-voxel (not per-column) so caves beneath
    // overhangs can use a different terrain than the surface above. The DC
    // mesher majority-votes TerrainId over each cell's 27-voxel neighborhood
    // the same way it picks the dominant VoxelType.
    public readonly byte[,,] TerrainId;

    // Per-voxel authored overlay. 0 = none. Non-zero values select a tile
    // painted on top of the terrain's base (flat/wall) tile. Used for
    // features the per-fragment shader slope can't see on a box-smoothed normal
    // — 1-voxel bumps, walkable ramps, eroded edges — which worldgen detects
    // via per-voxel neighborhood slope and stamps here. Majority-voted in the
    // mesher parallel to int and TerrainId.
    public readonly byte[,,] OverlayId;

    // "this voxel wears no overlay". Named because several passes both read
    // and write the channel and a bare 0 reads as a tile index there.
    public const byte OVERLAY_NONE = 0;

    // Which of a voxel's six faces its OverlayId dresses (EVoxelFace bits).
    // 0 = all faces — see EVoxelFace for why zero cannot mean "none".
    //
    // The only LAZY channel here: null until something writes a non-zero mask.
    // Face-qualified overlay is sparse (a few ivy patches in a world), so the
    // 4 KB every other channel costs unconditionally would be paid by every
    // chunk to store nothing. Read through GetOverlayFaces, never the field.
    public byte[,,] OverlayFaces;

    // Painted detail-sprite scatter. Stored on the SOLID surface voxel (same
    // location convention as OverlayId) — the scatter pass places sprites on
    // top of the voxel.
    //   DetailGroup    : index into the world's DetailGroupData[] palette.
    //                    0 = no detail. Non-zero picks a group whose entries
    //                    are weighted-sampled per scattered instance.
    //   DetailStrength : 0..255, scatter density. 0 = no instances; 255 =
    //                    every candidate slot fills. Each candidate slot rolls
    //                    a per-slot hash and keeps the instance if the hash
    //                    falls under strength/255.
    public readonly byte[,,] DetailGroup;
    public readonly byte[,,] DetailStrength;

    // Sunlight: byte 0..LightEngine.MAX_LIGHT. Single source, max-fill BFS so
    // there's no overlap to worry about. Color tinting (sunset, etc.) happens
    // in the shader via the sun_color uniform — the storage is just a mask.
    public readonly byte[,,] Sunlight;

    // Bumped by every write to Sunlight. LightMap keys its cached dilated-sun
    // encode off this (its own plus its six face neighbours'), so the cache can
    // never go stale without a caller having to remember to invalidate it.
    // Anything writing Sunlight[] directly must call MarkSunlightChanged().
    public int SunlightVersion { get; private set; }

    // Sky exposure: byte 0..LightEngine.MAX_LIGHT. The PURELY VERTICAL sky
    // reach — the value the sunlight column scan computes on its way down
    // (open sky overhead, attenuated by overhead voxels, fog, and canopy)
    // BEFORE the horizontal BFS spread runs. Unlike Sunlight (= max(column,
    // BFS spread)), this never leaks sideways, so it answers "is there cover
    // straight up, and how much" without the cave-mouth bleed the spread
    // introduces. Gameplay "am I sheltered from rain / open to sky" probes
    // read this; the BFS Sunlight stays the lighting signal. Not serialized —
    // recomputed by ComputeSunlight on load alongside Sunlight (see
    // ChunkSerializer's note on BlockLight).
    public readonly byte[,,] SkyExposure;

    // Block light: per-color-channel additive sums of post-pow contributions
    // from registered LightSources. "Post-pow" means each light's BFS stores
    // pow(level/MAX_LIGHT, exp) * 255 * color.channel at deposit time, so the
    // shader can sum overlaps with correct perceptual brightness instead of
    // getting a "brilliance bonus" from sum-then-pow. Ushort holds the raw
    // sum so subtraction stays exact when stacked lights are removed; the
    // LightMap upload clamps to 0-255 per channel for the GPU.
    //
    // TEXTURE_MAX is that GPU clamp. AddBlockLight reports whether a write
    // moved the CLAMPED value, which is the only thing an upload could show —
    // a flicker roll that only shuffles the saturated core, or rounds to the
    // same byte out in the falloff tail, must not re-dirty the chunk.
    public const int BLOCK_LIGHT_TEXTURE_MAX = 255;
    public readonly ushort[,,] BlockLightR;
    public readonly ushort[,,] BlockLightG;
    public readonly ushort[,,] BlockLightB;

    // Coarse ENCLOSURE subgrid: 0 = fully open to the outdoors, 255 = deeply
    // enclosed. Baked by InteriornessGen as an aperture-weighted flood inward
    // from open sky, so it measures how hard it is for the outdoors to REACH a
    // cell rather than how much light does — which is the distinction every
    // light-based attempt at this failed on:
    //
    //   * under an eave — one wide step from open air, so ~0 despite full
    //     cover overhead;
    //   * a room with a window — the aperture is narrow, so the interior stays
    //     high even standing a metre from the glass;
    //   * under a broken roof — holes are narrow apertures, so the room stays
    //     a room and only softens by roughly how holed it is;
    //   * deep cave — saturated.
    //
    // Paired with EnvTag: that says WHICH class a cell is, this says HOW MUCH
    // of it applies. Both are trilinearly sampled together, which is what makes
    // thresholds crossfade instead of stepping.
    //
    // Also replaces the old baked WindFactor channel — ambient wind reach is
    // now derived from this and the class's windSuppression at upload time,
    // rather than being a second, separately-baked openness measure.
    public readonly byte[,,] Interiorness;

    // Coarse wind-velocity subgrid. One full XYZ vector per ENV cell.
    // Bytes 0..255 map to signed [-1, 1] via the (byte - 128) / 127
    // convention; the shader multiplies by the `wind_velocity_scale`
    // global to convert to world m/s. Wind has updrafts unlike water
    // currents, so all three axes are stored. Default (no override) is
    // the per-zone wind direction × a default base speed, baked by
    // WindGen alongside WindFactor. Authored cells override with a
    // custom velocity to model a windy mountain pass / cave draft /
    // localized gust. Uploaded to the GPU as the RGB channels of the
    // `wind_map` global texture (alpha = WindFactor).
    public readonly byte[,,] WindVelocityX;
    public readonly byte[,,] WindVelocityY;
    public readonly byte[,,] WindVelocityZ;

    // Coarse water-current subgrid. One 2D vector per ENV cell — CurrentX
    // is the world +X velocity component, CurrentZ is the world +Z
    // component. Bytes 0..255 map to signed [-1, 1] via the
    // (byte - 128) / 127 convention; the shader multiplies by the
    // `water_current_speed` global to convert to world m/s. Y is ignored
    // (currents are 2D in the XZ plane). Trilinearly sampled via the
    // `water_current_map` global texture, so chunk-edge / cave-mouth
    // transitions blend across roughly one cell footprint (~4m). Zero =
    // no current; the water shader's ripple_normal path early-outs to
    // the single-sample (non-flow) cost in that case.
    public readonly byte[,,] CurrentX;
    public readonly byte[,,] CurrentZ;

    // Fog density: 0 = clear air, 255 = thickest fog. Two consumers:
    //   - LightEngine BFS uses this as extra per-step falloff so torches dim
    //     faster in fog.
    //   - A Godot FogVolume binds this as its density texture so god rays /
    //     volumetric scattering carve correctly through caves vs foggy
    //     clearings.
    // Chunk-local by design — an unloaded neighbor reads as 0, which is the
    // correct streaming default (no fog where there's no data).
    public readonly byte[,,] FogDensity;

    public ChunkState(Vector3I chunkCoord)
    {
        ChunkCoord = chunkCoord;
        Voxels = new byte[SIZE, SIZE, SIZE];
        Shape = new byte[SIZE, SIZE, SIZE];
        TerrainId = new byte[SIZE, SIZE, SIZE];
        OverlayId = new byte[SIZE, SIZE, SIZE];
        DetailGroup = new byte[SIZE, SIZE, SIZE];
        DetailStrength = new byte[SIZE, SIZE, SIZE];
        Sunlight = new byte[SIZE, SIZE, SIZE];
        SkyExposure = new byte[SIZE, SIZE, SIZE];
        BlockLightR = new ushort[SIZE, SIZE, SIZE];
        BlockLightG = new ushort[SIZE, SIZE, SIZE];
        BlockLightB = new ushort[SIZE, SIZE, SIZE];
        FogDensity = new byte[SIZE, SIZE, SIZE];
        Interiorness = new byte[ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE];
        EnvTag = new byte[ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE];
        CurrentX = new byte[ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE];
        CurrentZ = new byte[ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE];
        WindVelocityX = new byte[ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE];
        WindVelocityY = new byte[ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE];
        WindVelocityZ = new byte[ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE];
        // Default = byte 128 = signed zero for all per-cell vector channels.
        // Plain `new byte[]` zeros to 0, which would mean "max negative" under
        // the byte-128-zero convention; initialize explicitly.
        for (int sx = 0; sx < ENV_SUBGRID_SIZE; sx++)
        {
            for (int sy = 0; sy < ENV_SUBGRID_SIZE; sy++)
            {
                for (int sz = 0; sz < ENV_SUBGRID_SIZE; sz++)
                {
                    CurrentX[sx, sy, sz] = 128;
                    CurrentZ[sx, sy, sz] = 128;
                    WindVelocityX[sx, sy, sz] = 128;
                    WindVelocityY[sx, sy, sz] = 128;
                    WindVelocityZ[sx, sy, sz] = 128;
                }
            }
        }
    }

    public Vector3 GetWindVelocity(int sx, int sy, int sz)
    {
        if (sx < 0 || sx >= ENV_SUBGRID_SIZE || sy < 0 || sy >= ENV_SUBGRID_SIZE || sz < 0 || sz >= ENV_SUBGRID_SIZE)
        {
            return Vector3.Zero;
        }
        float fx = (WindVelocityX[sx, sy, sz] - 128f) / 127f;
        float fy = (WindVelocityY[sx, sy, sz] - 128f) / 127f;
        float fz = (WindVelocityZ[sx, sy, sz] - 128f) / 127f;
        return new Vector3(fx, fy, fz);
    }

    public void SetWindVelocity(int sx, int sy, int sz, float fx, float fy, float fz)
    {
        if (sx < 0 || sx >= ENV_SUBGRID_SIZE || sy < 0 || sy >= ENV_SUBGRID_SIZE || sz < 0 || sz >= ENV_SUBGRID_SIZE)
        {
            return;
        }
        if (fx < -1f) { fx = -1f; }
        if (fx > 1f) { fx = 1f; }
        if (fy < -1f) { fy = -1f; }
        if (fy > 1f) { fy = 1f; }
        if (fz < -1f) { fz = -1f; }
        if (fz > 1f) { fz = 1f; }
        WindVelocityX[sx, sy, sz] = (byte)(Mathf.RoundToInt(fx * 127f) + 128);
        WindVelocityY[sx, sy, sz] = (byte)(Mathf.RoundToInt(fy * 127f) + 128);
        WindVelocityZ[sx, sy, sz] = (byte)(Mathf.RoundToInt(fz * 127f) + 128);
    }

    public Vector2 GetCurrent(int sx, int sy, int sz)
    {
        if (sx < 0 || sx >= ENV_SUBGRID_SIZE || sy < 0 || sy >= ENV_SUBGRID_SIZE || sz < 0 || sz >= ENV_SUBGRID_SIZE)
        {
            return Vector2.Zero;
        }
        float fx = (CurrentX[sx, sy, sz] - 128f) / 127f;
        float fz = (CurrentZ[sx, sy, sz] - 128f) / 127f;
        return new Vector2(fx, fz);
    }

    public void SetCurrent(int sx, int sy, int sz, float fx, float fz)
    {
        if (sx < 0 || sx >= ENV_SUBGRID_SIZE || sy < 0 || sy >= ENV_SUBGRID_SIZE || sz < 0 || sz >= ENV_SUBGRID_SIZE)
        {
            return;
        }
        if (fx < -1f) { fx = -1f; }
        if (fx > 1f) { fx = 1f; }
        if (fz < -1f) { fz = -1f; }
        if (fz > 1f) { fz = 1f; }
        CurrentX[sx, sy, sz] = (byte)(Mathf.RoundToInt(fx * 127f) + 128);
        CurrentZ[sx, sy, sz] = (byte)(Mathf.RoundToInt(fz * 127f) + 128);
    }

    // Out of bounds reads as 0 = fully open, matching an unloaded neighbour
    // contributing nothing rather than falsely enclosing the edge of the world.
    public int GetInteriorness(int sx, int sy, int sz)
    {
        if (sx < 0 || sx >= ENV_SUBGRID_SIZE || sy < 0 || sy >= ENV_SUBGRID_SIZE || sz < 0 || sz >= ENV_SUBGRID_SIZE)
        {
            return 0;
        }
        return Interiorness[sx, sy, sz];
    }

    // Ambient wind reaching a cell, 0..255 — DERIVED, not stored. An open cell
    // gets full ambient wind; an enclosed one is damped by its space class's
    // windSuppression, in proportion to how enclosed it actually is. Replaces
    // the old baked WindFactor channel, which was a second openness measure
    // computed from sunlight and needed its own bake pass to stay in step.
    //
    // Single definition because both the CPU sampler (WorldState) and the GPU
    // upload (WindMap) must agree exactly — the shaders read the same value the
    // audio and motes do.
    public int GetWindFactor(SimData simData, int sx, int sy, int sz)
    {
        int interiorness = GetInteriorness(sx, sy, sz);
        InteriorAmbienceData ambience = simData?.GetInteriorAmbience(GetEnvTag(sx, sy, sz));
        float suppression = ambience == null ? 0f : Mathf.Clamp(ambience.windSuppression, 0f, 1f);
        return 255 - (int)(interiorness * suppression);
    }

    public void SetInteriorness(int sx, int sy, int sz, int value)
    {
        if (value < 0) { value = 0; }
        if (value > 255) { value = 255; }
        Interiorness[sx, sy, sz] = (byte)value;
    }

    // Coarse space-class subgrid. One byte per cell (4³ voxels per cell, 64
    // cells per chunk), holding an INDEX into SimData.interiorAmbiences —
    // which decides the air (dust), the wind that reaches in, and the
    // acoustics of that pocket of space. Trilinearly sampled, so crossing a
    // threshold crossfades rather than snapping.
    //
    // Worldgen seeds a default from the wind/sunlight signal: open-sky cells →
    // outdoor, sealed cells → underground. Anything finer than that
    // distinction (a tidy hall vs a dusty cellar) is author-only, because no
    // generated signal separates them.
    public readonly byte[,,] EnvTag;

    // Index into SimData.interiorAmbiences. Out of bounds reads as 0, which
    // that palette pins to the outdoor entry.
    public byte GetEnvTag(int sx, int sy, int sz)
    {
        if (sx < 0 || sx >= ENV_SUBGRID_SIZE || sy < 0 || sy >= ENV_SUBGRID_SIZE || sz < 0 || sz >= ENV_SUBGRID_SIZE)
        {
            return 0;
        }
        return EnvTag[sx, sy, sz];
    }

    public void SetEnvTag(int sx, int sy, int sz, byte ambienceIndex)
    {
        EnvTag[sx, sy, sz] = ambienceIndex;
    }

    public int GetVoxel(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return Blocks.AirId;
        }
        return Voxels[x, y, z];
    }

    public SharpAxes GetShape(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return SharpAxes.None;
        }
        return (SharpAxes)Shape[x, y, z];
    }

    public void SetShape(int x, int y, int z, SharpAxes shape)
    {
        Shape[x, y, z] = (byte)shape;
    }

    public int GetTerrainId(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return TerrainId[x, y, z];
    }

    public void SetTerrainId(int x, int y, int z, int terrainId)
    {
        TerrainId[x, y, z] = (byte)terrainId;
    }

    public int GetOverlayId(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return OverlayId[x, y, z];
    }

    public void SetOverlayId(int x, int y, int z, int overlayId)
    {
        OverlayId[x, y, z] = (byte)overlayId;
    }

    public int GetOverlayFaces(int x, int y, int z)
    {
        if (OverlayFaces == null || x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return OverlayFaces[x, y, z];
    }

    public void SetOverlayFaces(int x, int y, int z, int faces)
    {
        if (OverlayFaces == null)
        {
            // 0 is the default this chunk already reads as, so storing one
            // must not be what forces the allocation.
            if (faces == 0)
            {
                return;
            }
            OverlayFaces = new byte[SIZE, SIZE, SIZE];
        }
        OverlayFaces[x, y, z] = (byte)faces;
    }

    public int GetDetailGroup(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return DetailGroup[x, y, z];
    }

    public void SetDetailGroup(int x, int y, int z, int groupId)
    {
        DetailGroup[x, y, z] = (byte)groupId;
    }

    public int GetDetailStrength(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return DetailStrength[x, y, z];
    }

    public void SetDetailStrength(int x, int y, int z, int strength)
    {
        if (strength < 0) { strength = 0; }
        if (strength > 255) { strength = 255; }
        DetailStrength[x, y, z] = (byte)strength;
    }

    public int GetSunlight(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return Sunlight[x, y, z];
    }

    public void SetSunlight(int x, int y, int z, int level)
    {
        Sunlight[x, y, z] = (byte)level;
        SunlightVersion++;
    }

    // Invalidates anything cached off SunlightVersion. For the paths that fill
    // Sunlight[] wholesale rather than through SetSunlight — the chunk decoder
    // and WorldState.ClearSunlightAll.
    public void MarkSunlightChanged()
    {
        SunlightVersion++;
    }

    public int GetSkyExposure(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return SkyExposure[x, y, z];
    }

    public void SetSkyExposure(int x, int y, int z, int level)
    {
        SkyExposure[x, y, z] = (byte)level;
    }

    public void GetBlockLight(int x, int y, int z, out int r, out int g, out int b)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            r = 0;
            g = 0;
            b = 0;
            return;
        }
        r = BlockLightR[x, y, z];
        g = BlockLightG[x, y, z];
        b = BlockLightB[x, y, z];
    }

    // Signed per-channel accumulate. Returns true if this moved the value the
    // LightMap would upload (see BLOCK_LIGHT_TEXTURE_MAX) on any channel, so
    // the caller can skip re-dirtying a chunk nothing visible changed in.
    public bool AddBlockLight(int x, int y, int z, int r, int g, int b)
    {
        ushort or = BlockLightR[x, y, z];
        ushort og = BlockLightG[x, y, z];
        ushort ob = BlockLightB[x, y, z];
        ushort nr = Accumulate(or, r);
        ushort ng = Accumulate(og, g);
        ushort nb = Accumulate(ob, b);
        BlockLightR[x, y, z] = nr;
        BlockLightG[x, y, z] = ng;
        BlockLightB[x, y, z] = nb;
        return Encoded(or) != Encoded(nr)
            || Encoded(og) != Encoded(ng)
            || Encoded(ob) != Encoded(nb);
    }

    public bool SubtractBlockLight(int x, int y, int z, int r, int g, int b)
    {
        return AddBlockLight(x, y, z, -r, -g, -b);
    }

    private static ushort Accumulate(ushort current, int delta)
    {
        int sum = current + delta;
        if (sum < 0) { return 0; }
        if (sum > ushort.MaxValue) { return ushort.MaxValue; }
        return (ushort)sum;
    }

    private static int Encoded(ushort v)
    {
        return v > BLOCK_LIGHT_TEXTURE_MAX ? BLOCK_LIGHT_TEXTURE_MAX : v;
    }

    // Returns this chunk's fill classification. Computes on first call;
    // subsequent calls hit the cache until InvalidateFill() is called.
    // When the result is Pure, fillType holds the uniform voxel type.
    public EChunkFill GetFill(out int fillType)
    {
        if (_fill == EChunkFill.Unknown)
        {
            ComputeFill();
        }
        fillType = _fillType;
        return _fill;
    }

    public void InvalidateFill()
    {
        _fill = EChunkFill.Unknown;
    }

    private void ComputeFill()
    {
        int first = Voxels[0, 0, 0];
        for (int x = 0; x < SIZE; x++)
        {
            for (int y = 0; y < SIZE; y++)
            {
                for (int z = 0; z < SIZE; z++)
                {
                    if (Voxels[x, y, z] != first)
                    {
                        _fill = EChunkFill.Mixed;
                        _fillType = default;
                        return;
                    }
                }
            }
        }
        _fill = EChunkFill.Pure;
        _fillType = first;
    }

    // Air density: authored/procedural fog raised by the space class's own
    // dust. DERIVED rather than baked, which is what makes tuning a class's
    // dustFloor take effect immediately instead of needing a WORLDGEN_VERSION
    // bump and a full regen.
    //
    // Scaled by interiorness so dust fades out toward openings the same way
    // everything else about the class does — a cave mouth thins rather than
    // stepping at the cell where the class flips.
    //
    // MAX, never a sum: authored mist already sitting here must not be thinned
    // by a drier class, nor doubled by a wetter one.
    public int GetFog(SimData simData, int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        int fog = FogDensity[x, y, z];
        // Dust is airborne; solids keep only whatever fog was authored into
        // them, so the fog volume's linear filter falls off across a wall face
        // instead of hazing through it.
        if (Voxels[x, y, z] != Blocks.AirId)
        {
            return fog;
        }
        int s = ENV_VOXELS_PER_CELL;
        InteriorAmbienceData ambience = simData?.GetInteriorAmbience(GetEnvTag(x / s, y / s, z / s));
        if (ambience == null || ambience.dustFloor <= 0f)
        {
            return fog;
        }
        int dust = (int)(ambience.dustFloor * GetInteriorness(x / s, y / s, z / s));
        return dust > fog ? dust : fog;
    }

    public void SetFog(int x, int y, int z, int density)
    {
        if (density < 0) { density = 0; }
        if (density > 255) { density = 255; }
        FogDensity[x, y, z] = (byte)density;
    }
}
