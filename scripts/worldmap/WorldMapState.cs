using System.Collections.Generic;
using Godot;

// Runtime document + bake for the world-map painter. Owns every layer's mutable
// data, the column/region/zone/tunnel queries the tools and views read, and the
// deterministic bake (BuildWorld) that turns the painted layers into a WorldState
// / .hike. The painter edits only the 2D layer images; no live voxel world is
// kept — the WorldState is materialized on demand at bake/save time.
//
// The elevation + water images REPLACE WorldGen's noise height/water; the rest
// of WorldGen's per-column logic (ramps, shore, kit blending) is out of scope,
// so this is a clean focused stamp rather than a fork of the 3100-line WorldGen.
public class WorldMapState
{
    // Surface kit = palette index 0 (BuildKitPalette puts zone 0's SurfaceKit
    // first), which is also the per-voxel TerrainId default.
    public readonly WorldMapData Data;

    public WorldState WorldState;  // baked voxels (built on demand at bake time)

    public Image Elevation;        // Rf, per column (normalized height, truth)
    public Image Water;            // Rf, per column (water surface height)
    public Image Region;           // R8, per chunk (region index)
    public Image Zone;             // R8, per chunk (zone index)
    public Image Scatter;          // Rgba8, per column (R = kind id, G = density)
    public byte[,,] Tunnels;       // [px, ly, pz] carve mask (1 = carved air)

    // Live ocean elevation in world voxels (the elevation tool edits this).
    public int SeaLevel;

    // Scatter entity bookkeeping during a bake: the EntitySimState placed for
    // each column, so a re-scatter of the same column removes the old one first.
    private readonly System.Collections.Generic.Dictionary<Vector2I, EntitySimState> _scatterEntities = new();

    public WorldMapState(WorldMapData data)
    {
        Data = data;
        SeaLevel = data.SeaLevel;
        Elevation = data.LoadOrCreateElevation();
        Water = data.LoadOrCreateWater();
        Region = data.LoadOrCreateRegion();
        Zone = data.LoadOrCreateZone();
        Scatter = data.LoadOrCreateScatter();
        Tunnels = data.LoadOrCreateTunnels();
    }

    public int RegionCount => Data.RegionCount;
    public int ZoneCount => Data.ZoneCount;

    // ---- Queries --------------------------------------------------------

    public int ColumnHeight(float v01)
    {
        return SeaLevel + Mathf.RoundToInt(Mathf.Clamp(v01, 0f, 1f) * Data.MaxElevationVoxels);
    }

    public float Elevation01(int px, int pz)
    {
        return Elevation.GetPixel(ClampX(px), ClampZ(pz)).R;
    }

    public float Water01(int px, int pz)
    {
        return Water.GetPixel(ClampX(px), ClampZ(pz)).R;
    }

    // Topmost solid voxel of the painted terrain column.
    public int TerrainHeight(int px, int pz)
    {
        return ColumnHeight(Elevation01(px, pz));
    }

    // Effective water surface: the deeper of the global ocean and any painted
    // water body. (Water01 == 0 maps to SeaLevel, so this is >= SeaLevel.)
    public int WaterSurface(int px, int pz)
    {
        return Mathf.Max(SeaLevel, ColumnHeight(Water01(px, pz)));
    }

    // Column has water standing above its terrain.
    public bool Underwater(int px, int pz)
    {
        return WaterSurface(px, pz) > TerrainHeight(px, pz);
    }

    // Terrain top sits below the ocean (open-sea floor).
    public bool Ocean(int px, int pz)
    {
        return TerrainHeight(px, pz) < SeaLevel;
    }

    public bool IsTunnel(int px, int pz, int wy)
    {
        int ly = wy - Data.WorldMinY;
        if (px < 0 || px >= Data.ImageWidth || pz < 0 || pz >= Data.ImageHeight
            || ly < 0 || ly >= Data.VoxelHeight)
        {
            return false;
        }
        return Tunnels[px, ly, pz] != 0;
    }

    public void SetTunnel(int px, int pz, int wy, bool carved)
    {
        int ly = wy - Data.WorldMinY;
        if (px < 0 || px >= Data.ImageWidth || pz < 0 || pz >= Data.ImageHeight
            || ly < 0 || ly >= Data.VoxelHeight)
        {
            return;
        }
        Tunnels[px, ly, pz] = (byte)(carved ? 1 : 0);
    }

    // Solid land (not carved) at a given Y — used by the tunnel view.
    public bool SolidAt(int px, int pz, int wy)
    {
        return wy <= TerrainHeight(px, pz) && !IsTunnel(px, pz, wy);
    }

    private int ClampX(int px) => Mathf.Clamp(px, 0, Data.ImageWidth - 1);
    private int ClampZ(int pz) => Mathf.Clamp(pz, 0, Data.ImageHeight - 1);

    // ---- Bake -----------------------------------------------------------

    // Full build from the current layers: create the WorldState + every chunk,
    // stamp regions/zones, stamp all columns, propagate sunlight.
    public WorldState BuildWorld()
    {
        var ws = new WorldState(Data.MinChunk, Data.MaxChunk, Data.GenData.SimData);

        ZoneGenData[] zones = Data.GenData.Zones ?? [];
        ws.Zones = new ZoneState[zones.Length];
        for (int i = 0; i < zones.Length; i++)
        {
            ws.Zones[i] = new ZoneState
            {
                Data = zones[i]?.Zone,
                WindDirection = new Vector3(0.7f, 0f, 0.7f),
                Elevation = 0f,
            };
        }
        RegionData[] regions = Data.GenData.Regions ?? [];
        ws.Regions = new RegionState[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            ws.Regions[i] = new RegionState { Data = regions[i] };
        }

        for (int cx = Data.MinChunk.X; cx <= Data.MaxChunk.X; cx++)
        {
            for (int cy = Data.MinChunk.Y; cy <= Data.MaxChunk.Y; cy++)
            {
                for (int cz = Data.MinChunk.Z; cz <= Data.MaxChunk.Z; cz++)
                {
                    var coord = new Vector3I(cx, cy, cz);
                    var chunk = new ChunkState(coord);
                    chunk.RegionIndex = SampleChunkIndex(Region, cx, cz, RegionCount);
                    chunk.ZoneIndex = SampleChunkIndex(Zone, cx, cz, ZoneCount);
                    ws._chunks[coord] = chunk;
                }
            }
        }

        WorldState = ws;
        StampColumns(new Rect2I(0, 0, Data.ImageWidth, Data.ImageHeight), null);

        // Scatter props/interactives into the fresh WorldState (World is null
        // here, so this only adds sim states — the painter's initial entity
        // load spawns the nodes).
        RescatterColumns(new Rect2I(0, 0, Data.ImageWidth, Data.ImageHeight));

        int spawnH = TerrainHeight(-Data.WorldMinX, -Data.WorldMinZ);
        ws.Spawn = new Vector3(0.5f, spawnH + 2f, 0.5f);

        LightEngine.ComputeSunlight(ws);
        return ws;
    }

    // Re-stamp every column under a texel rect, recording changed voxels.
    public void StampColumns(Rect2I texelRect, List<Vector3I> changed)
    {
        int x0 = Mathf.Max(0, texelRect.Position.X);
        int z0 = Mathf.Max(0, texelRect.Position.Y);
        int x1 = Mathf.Min(Data.ImageWidth, texelRect.Position.X + texelRect.Size.X);
        int z1 = Mathf.Min(Data.ImageHeight, texelRect.Position.Y + texelRect.Size.Y);
        for (int px = x0; px < x1; px++)
        {
            for (int pz = z0; pz < z1; pz++)
            {
                StampColumn(px, pz, changed);
            }
        }
    }

    private void StampColumn(int px, int pz, List<Vector3I> changed)
    {
        int wx = Data.WorldMinX + px;
        int wz = Data.WorldMinZ + pz;
        int th = TerrainHeight(px, pz);
        int wsurf = WaterSurface(px, pz);

        for (int wy = Data.WorldMinY; wy <= Data.WorldMaxY; wy++)
        {
            VoxelType desired;
            if (IsTunnel(px, pz, wy))
            {
                desired = VoxelType.Air;   // carve wins (air pocket, no flood sim)
            }
            else if (wy <= th)
            {
                desired = VoxelType.Terrain;
            }
            else if (wy <= wsurf)
            {
                desired = VoxelType.Water;
            }
            else
            {
                desired = VoxelType.Air;
            }

            if (changed != null)
            {
                if (WorldState.GetVoxelWorld(wx, wy, wz) == desired)
                {
                    continue;
                }
                changed.Add(new Vector3I(wx, wy, wz));
            }

            if (desired == VoxelType.Terrain)
            {
                WorldState.SetVoxelWorld(wx, wy, wz, VoxelType.Terrain, VoxelTypeInfo.SharpAxes.Y);
            }
            else
            {
                WorldState.SetVoxelWorld(wx, wy, wz, desired);
            }
        }
    }

    // Re-evaluate scatter for every column under a texel rect: drop the old
    // entity (if any), then place a new one when the cell has a kind + the
    // hash roll falls under its density, on dry land. Adds/removes sim states on
    // WorldState during the bake.
    public void RescatterColumns(Rect2I texelRect)
    {
        int x0 = Mathf.Max(0, texelRect.Position.X);
        int z0 = Mathf.Max(0, texelRect.Position.Y);
        int x1 = Mathf.Min(Data.ImageWidth, texelRect.Position.X + texelRect.Size.X);
        int z1 = Mathf.Min(Data.ImageHeight, texelRect.Position.Y + texelRect.Size.Y);
        for (int px = x0; px < x1; px++)
        {
            for (int pz = z0; pz < z1; pz++)
            {
                RescatterColumn(px, pz);
            }
        }
    }

    private void RescatterColumn(int px, int pz)
    {
        var col = new Vector2I(px, pz);
        if (_scatterEntities.TryGetValue(col, out EntitySimState old))
        {
            WorldState.RemoveEntity(old);
            _scatterEntities.Remove(col);
        }

        Color cell = Scatter.GetPixel(px, pz);
        int kindId = Mathf.RoundToInt(cell.R * 255f);
        float density = cell.G;
        if (kindId <= 0 || density <= 0f)
        {
            return;
        }
        // Dry land only — no scatter underwater or on the open-sea floor.
        if (Underwater(px, pz) || TerrainHeight(px, pz) < SeaLevel)
        {
            return;
        }
        uint h = Hash(px, pz);
        if (ToFloat01(h) >= density)
        {
            return;
        }

        int surfaceY = TerrainHeight(px, pz);
        var pos = new Vector3(Data.WorldMinX + px + 0.5f, surfaceY + 1f, Data.WorldMinZ + pz + 0.5f);
        EntitySimState sim = ScatterFactory.Create((EScatterKind)(kindId - 1), Data, ZoneIndexAt(px, pz), pos, h);
        if (sim == null)
        {
            return;
        }
        WorldState.AddEntity(sim);
        _scatterEntities[col] = sim;
    }

    private int ZoneIndexAt(int px, int pz)
    {
        Vector2I ct = Data.ColumnTexelToChunkTexel(px, pz);
        int idx = Mathf.RoundToInt(Zone.GetPixel(ct.X, ct.Y).R * 255f);
        return ZoneCount > 0 ? Mathf.Clamp(idx, 0, ZoneCount - 1) : 0;
    }

    private static uint Hash(int x, int z)
    {
        unchecked
        {
            uint h = (uint)x * 0x9E3779B1u;
            h ^= (uint)z * 0x85EBCA77u;
            h = ((h >> 16) ^ h) * 0x045D9F3Bu;
            h = ((h >> 16) ^ h) * 0x045D9F3Bu;
            return (h >> 16) ^ h;
        }
    }

    private static float ToFloat01(uint h) => (h & 0xFFFFFFu) / 16777216f;

    // Save the layer images, then bake a fresh WorldState from them and write the
    // .hike. The bake is materialized on demand here (and in WorldMapData's
    // headless "Bake to .hike" button) — the painter never holds a live world.
    public void Save()
    {
        Data.SaveElevation(Elevation);
        Data.SaveWater(Water);
        Data.SaveRegion(Region);
        Data.SaveZone(Zone);
        Data.SaveScatter(Scatter);
        Data.SaveTunnels(Tunnels);
        if (!string.IsNullOrEmpty(Data.OutputWorldPath))
        {
            try
            {
                BuildWorld();
                WorldFile.Write(Data.OutputWorldPath, WorldState);
                GD.Print($"WorldMapState: saved layers + baked world to {Data.OutputWorldPath}");
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"WorldMapState: world export failed: {e.Message}");
            }
        }
        else
        {
            GD.Print("WorldMapState: saved layers (no OutputWorldPath set, skipped .hike export)");
        }
    }

    private byte SampleChunkIndex(Image img, int cx, int cz, int count)
    {
        int lcx = cx - Data.MinChunk.X;
        int lcz = cz - Data.MinChunk.Z;
        if (lcx < 0 || lcx >= img.GetWidth() || lcz < 0 || lcz >= img.GetHeight())
        {
            return 0;
        }
        return ClampIndex((byte)Mathf.RoundToInt(img.GetPixel(lcx, lcz).R * 255f), count);
    }

    private static byte ClampIndex(byte idx, int count)
    {
        if (count <= 0)
        {
            return 0;
        }
        return idx >= count ? (byte)(count - 1) : idx;
    }

    // ---- Shared palette / colours (used by the views) -------------------

    public static Color RegionColor(int idx)
    {
        if (idx <= 0)
        {
            return new Color(0.22f, 0.22f, 0.24f);
        }
        return Color.FromHsv((idx * 0.61803398875f) % 1f, 0.55f, 0.85f);
    }

    public static Color ZoneColor(int idx)
    {
        return Color.FromHsv((idx * 0.61803398875f + 0.13f) % 1f, 0.45f, 0.9f);
    }

    // Hypsometric land ramp: green lowland -> brown -> white peaks.
    public static Color Hypsometric(float v)
    {
        Color low = new Color(0.27f, 0.5f, 0.22f);
        Color mid = new Color(0.5f, 0.4f, 0.26f);
        Color high = new Color(0.95f, 0.95f, 0.95f);
        return v < 0.5f ? low.Lerp(mid, v / 0.5f) : mid.Lerp(high, (v - 0.5f) / 0.5f);
    }
}
