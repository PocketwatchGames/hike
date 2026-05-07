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
    private VoxelType _fillType;

    // Index into WorldState.Zones[]. Picks the zone this chunk
    // belongs to — drives ZoneBlend.Sample's per-chunk weighting so
    // an arbitrary zone shape (not just the legacy 4-quadrant layout)
    // can be authored just by stamping different indices on chunks.
    // Set by WorldGen (or a future editor) at world creation.
    public byte ZoneIndex;

    public readonly VoxelType[,,] Voxels;

    // Per-voxel shape tag: stores VoxelTypeInfo.SharpAxes flags as a byte.
    // This is the shape channel — orthogonal to VoxelType (the material
    // channel). Worldgen writes it when placing voxels; the DC mesher reads
    // it to decide per-axis Y/X/Z snapping. Declaring intent here replaces
    // the mesher's old geometric heuristics (cliff-top probe, CaveCeilingReach).
    // Authoring map:
    //   Building voxels (Stone/Wood/Barrier) → All    (fully blocky)
    //   Flat natural terrain / cave floor+ceiling → Y (snap vertical, smooth lateral)
    //   Ramps / path-band slopes → None               (smooth interpolation)
    public readonly byte[,,] Shape;

    // Per-voxel environment kit id. Index into the active world's kit palette
    // (ZoneGenData.Kits, uploaded globally via ChunkMesh.SetKits). Orthogonal to
    // VoxelType: a voxel tagged VoxelType.Terrain with KitId=2 means "AUTO land
    // that reads from the underwater kit's palette." Per-voxel (not per-column)
    // so caves beneath overhangs can use a different kit than the surface above.
    // The DC mesher majority-votes KitId over each cell's 27-voxel neighborhood
    // the same way it picks the dominant VoxelType.
    public readonly byte[,,] KitId;

    // Per-voxel authored overlay. 0 = none. Non-zero values select a kit-owned
    // overlay tile painted on top of the kit's base (flat/wall) tile. Used for
    // features the per-fragment shader slope can't see on a box-smoothed normal
    // — 1-voxel bumps, walkable ramps, eroded edges — which worldgen detects
    // via per-voxel neighborhood slope and stamps here. Majority-voted in the
    // mesher parallel to VoxelType and KitId.
    public readonly byte[,,] OverlayId;

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

    // Block light: per-color-channel additive sums of post-pow contributions
    // from registered LightSources. "Post-pow" means each light's BFS stores
    // pow(level/MAX_LIGHT, exp) * 255 * color.channel at deposit time, so the
    // shader can sum overlaps with correct perceptual brightness instead of
    // getting a "brilliance bonus" from sum-then-pow. Ushort holds the raw
    // sum so subtraction stays exact when stacked lights are removed; the
    // LightMap upload clamps to 0-255 per channel for the GPU.
    public readonly ushort[,,] BlockLightR;
    public readonly ushort[,,] BlockLightG;
    public readonly ushort[,,] BlockLightB;

    // Coarse wind-factor subgrid. 0 = sealed (no ambient wind, e.g. deep
    // cave or building interior), 255 = full ambient wind. Sampled
    // trilinearly at the listener / shader fragment so cave-mouth
    // transitions blend smoothly. Worldgen bakes it from sunlight
    // openness; uploaded to the GPU as the `wind_map` global texture.
    public readonly byte[,,] WindFactor;

    // Fog density: 0 = clear air, 255 = thickest fog. Two consumers:
    //   - LightEngine BFS uses this as extra per-step falloff so torches dim
    //     faster in fog (step 5 of the roadmap).
    //   - A Godot FogVolume binds this as its density texture so god rays /
    //     volumetric scattering carve correctly through caves vs foggy
    //     clearings (step 6 of the roadmap).
    // Chunk-local by design — an unloaded neighbor reads as 0, which is the
    // correct streaming default (no fog where there's no data).
    public readonly byte[,,] FogDensity;

    public ChunkState(Vector3I chunkCoord)
    {
        ChunkCoord = chunkCoord;
        Voxels = new VoxelType[SIZE, SIZE, SIZE];
        Shape = new byte[SIZE, SIZE, SIZE];
        KitId = new byte[SIZE, SIZE, SIZE];
        OverlayId = new byte[SIZE, SIZE, SIZE];
        DetailGroup = new byte[SIZE, SIZE, SIZE];
        DetailStrength = new byte[SIZE, SIZE, SIZE];
        Sunlight = new byte[SIZE, SIZE, SIZE];
        BlockLightR = new ushort[SIZE, SIZE, SIZE];
        BlockLightG = new ushort[SIZE, SIZE, SIZE];
        BlockLightB = new ushort[SIZE, SIZE, SIZE];
        FogDensity = new byte[SIZE, SIZE, SIZE];
        WindFactor = new byte[ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE];
        EnvTag = new byte[ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE, ENV_SUBGRID_SIZE];
    }

    public int GetWindFactor(int sx, int sy, int sz)
    {
        if (sx < 0 || sx >= ENV_SUBGRID_SIZE || sy < 0 || sy >= ENV_SUBGRID_SIZE || sz < 0 || sz >= ENV_SUBGRID_SIZE)
        {
            return 0;
        }
        return WindFactor[sx, sy, sz];
    }

    public void SetWindFactor(int sx, int sy, int sz, int factor)
    {
        if (factor < 0) { factor = 0; }
        if (factor > 255) { factor = 255; }
        WindFactor[sx, sy, sz] = (byte)factor;
    }

    // Coarse environment-tag subgrid. One byte per cell (4³ voxels per cell,
    // 64 cells per chunk). Authored in the editor (when one exists) per
    // pocket of space — Outdoor / Building / Cave / Tunnel — and trilinearly
    // sampled at the listener to drive reverb-bus blending and outdoor-layer
    // attenuation in the audio system. Worldgen seeds a default from the
    // wind/sunlight signal: open-sky cells → Outdoor, sealed cells → Cave.
    // Building/Tunnel are author-only.
    public readonly byte[,,] EnvTag;

    public EnvironmentTag GetEnvTag(int sx, int sy, int sz)
    {
        if (sx < 0 || sx >= ENV_SUBGRID_SIZE || sy < 0 || sy >= ENV_SUBGRID_SIZE || sz < 0 || sz >= ENV_SUBGRID_SIZE)
        {
            return EnvironmentTag.Outdoor;
        }
        return (EnvironmentTag)EnvTag[sx, sy, sz];
    }

    public void SetEnvTag(int sx, int sy, int sz, EnvironmentTag tag)
    {
        EnvTag[sx, sy, sz] = (byte)tag;
    }

    public VoxelType GetVoxel(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return VoxelType.Air;
        }
        return Voxels[x, y, z];
    }

    public VoxelTypeInfo.SharpAxes GetShape(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return VoxelTypeInfo.SharpAxes.None;
        }
        return (VoxelTypeInfo.SharpAxes)Shape[x, y, z];
    }

    public void SetShape(int x, int y, int z, VoxelTypeInfo.SharpAxes shape)
    {
        Shape[x, y, z] = (byte)shape;
    }

    public int GetKitId(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return KitId[x, y, z];
    }

    public void SetKitId(int x, int y, int z, int kitId)
    {
        KitId[x, y, z] = (byte)kitId;
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

    public void AddBlockLight(int x, int y, int z, int r, int g, int b)
    {
        int sr = BlockLightR[x, y, z] + r;
        int sg = BlockLightG[x, y, z] + g;
        int sb = BlockLightB[x, y, z] + b;
        BlockLightR[x, y, z] = sr > ushort.MaxValue ? ushort.MaxValue : (ushort)sr;
        BlockLightG[x, y, z] = sg > ushort.MaxValue ? ushort.MaxValue : (ushort)sg;
        BlockLightB[x, y, z] = sb > ushort.MaxValue ? ushort.MaxValue : (ushort)sb;
    }

    public void SubtractBlockLight(int x, int y, int z, int r, int g, int b)
    {
        int sr = BlockLightR[x, y, z] - r;
        int sg = BlockLightG[x, y, z] - g;
        int sb = BlockLightB[x, y, z] - b;
        BlockLightR[x, y, z] = sr < 0 ? (ushort)0 : (ushort)sr;
        BlockLightG[x, y, z] = sg < 0 ? (ushort)0 : (ushort)sg;
        BlockLightB[x, y, z] = sb < 0 ? (ushort)0 : (ushort)sb;
    }

    // Returns this chunk's fill classification. Computes on first call;
    // subsequent calls hit the cache until InvalidateFill() is called.
    // When the result is Pure, fillType holds the uniform voxel type.
    public EChunkFill GetFill(out VoxelType fillType)
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
        VoxelType first = Voxels[0, 0, 0];
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

    public int GetFog(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return FogDensity[x, y, z];
    }

    public void SetFog(int x, int y, int z, int density)
    {
        if (density < 0) { density = 0; }
        if (density > 255) { density = 255; }
        FogDensity[x, y, z] = (byte)density;
    }
}
