using Godot;

// The authored source document for a painted world — the FIRST step in the
// authoring chain. Holds bake settings plus references to the external layer
// files (the layers ARE images / data files, openable directly). A
// deterministic bake (WorldMapState) turns this into a WorldState / .hike;
// the .hike is never hand-edited. Mirrors the VoxelAtlasManifest pattern.
//
// Layers: elevation (per-column height), water (per-column water surface),
// region (per-chunk index), zone (per-chunk index), tunnels (per-voxel carve).
[Tool]
[GlobalClass]
public partial class WorldMapData : Resource
{
    [Export] public WorldGenData GenData;

    // World extent (XZ footprint + vertical chunk range). Per-column images are
    // SizeChunks * ChunkState.SIZE texels; per-chunk images are SizeChunks.
    [Export(PropertyHint.Range, "1,256,1")] public int SizeChunksX = 18;
    [Export(PropertyHint.Range, "1,256,1")] public int SizeChunksZ = 16;
    [Export(PropertyHint.Range, "-8,0,1")] public int FloorChunkY = -1;
    [Export(PropertyHint.Range, "0,32,1")] public int CeilChunkY = 4;

    // Default ocean elevation in world voxels (the elevation tool tweaks this
    // live; matches WorldGen.WATER_LEVEL).
    [Export] public int SeaLevel = 0;

    // World voxels of elevation a normalized layer value of 1.0 maps to. Shared
    // by the elevation and water layers (same column-height encoding).
    [Export(PropertyHint.Range, "1,512,1")] public float MaxElevationVoxels = 64f;

    // External layer files, stored as res:// paths (globalized at load/save).
    [Export] public string ElevationImagePath = "";   // .exr, Rf, per column
    [Export] public string WaterImagePath = "";        // .exr, Rf, per column
    [Export] public string RegionImagePath = "";       // .png, R8, per chunk
    [Export] public string ZoneImagePath = "";         // .png, R8, per chunk
    [Export] public string ScatterImagePath = "";      // .png, Rgba8, per column (R=kind, G=density)
    [Export] public string TunnelMaskPath = "";        // .bin, per voxel carve mask

    // Where BakeToWorldFile writes the packed world (res:// path).
    [Export] public string OutputWorldPath = "";

    public Vector3I MinChunk => new Vector3I(-SizeChunksX / 2, FloorChunkY, -SizeChunksZ / 2);
    public Vector3I MaxChunk => new Vector3I(MinChunk.X + SizeChunksX - 1, CeilChunkY, MinChunk.Z + SizeChunksZ - 1);

    public int WorldMinX => MinChunk.X * ChunkState.SIZE;
    public int WorldMinZ => MinChunk.Z * ChunkState.SIZE;
    public int WorldMinY => MinChunk.Y * ChunkState.SIZE;
    public int WorldMaxY => MaxChunk.Y * ChunkState.SIZE + ChunkState.SIZE - 1;

    public int ImageWidth => SizeChunksX * ChunkState.SIZE;
    public int ImageHeight => SizeChunksZ * ChunkState.SIZE;
    public int VoxelHeight => WorldMaxY - WorldMinY + 1;

    public int RegionCount => GenData?.Regions != null ? GenData.Regions.Length : 0;
    public int ZoneCount => GenData?.Zones != null ? GenData.Zones.Length : 0;

    // Column texel -> owning chunk's texel (shared by region + zone images).
    public Vector2I ColumnTexelToChunkTexel(int px, int pz)
    {
        return new Vector2I(px / ChunkState.SIZE, pz / ChunkState.SIZE);
    }

    [ExportToolButton("Bake to .hike")]
    public Callable BakeButton => Callable.From(BakeToWorldFile);

    // Headless bake (no running game): build a transient state from the layer
    // files and write the world. WorldMapState + WorldFile.Write are pure C#.
    public void BakeToWorldFile()
    {
        if (GenData == null)
        {
            GD.PrintErr("WorldMapData: GenData not set.");
            return;
        }
        if (string.IsNullOrEmpty(OutputWorldPath))
        {
            GD.PrintErr("WorldMapData: OutputWorldPath not set.");
            return;
        }
        var state = new WorldMapState(this);
        WorldState ws = state.BuildWorld();
        WorldFile.Write(OutputWorldPath, ws);
        GD.Print($"WorldMapData: baked world to {OutputWorldPath}");
    }

    // ---- Layer load / create / save -------------------------------------

    public Image LoadOrCreateElevation()
    {
        return LoadOrCreateColumnImage(ElevationImagePath);
    }

    public Image LoadOrCreateWater()
    {
        return LoadOrCreateColumnImage(WaterImagePath);
    }

    public Image LoadOrCreateRegion()
    {
        return LoadOrCreateChunkImage(RegionImagePath);
    }

    public Image LoadOrCreateZone()
    {
        return LoadOrCreateChunkImage(ZoneImagePath);
    }

    // Scatter is a per-column RGBA8 image: R = kind id (0 = none), G = density.
    public Image LoadOrCreateScatter()
    {
        Image img = TryLoad(ScatterImagePath);
        if (img != null)
        {
            if (img.GetWidth() != ImageWidth || img.GetHeight() != ImageHeight)
            {
                img.Resize(ImageWidth, ImageHeight, Image.Interpolation.Nearest);
            }
            if (img.GetFormat() != Image.Format.Rgba8)
            {
                img.Convert(Image.Format.Rgba8);
            }
            return img;
        }
        Image blank = Image.CreateEmpty(ImageWidth, ImageHeight, false, Image.Format.Rgba8);
        blank.Fill(new Color(0f, 0f, 0f, 1f));
        return blank;
    }

    private Image LoadOrCreateColumnImage(string path)
    {
        Image img = TryLoad(path);
        if (img != null)
        {
            if (img.GetWidth() != ImageWidth || img.GetHeight() != ImageHeight)
            {
                img.Resize(ImageWidth, ImageHeight);
            }
            if (img.GetFormat() != Image.Format.Rf)
            {
                img.Convert(Image.Format.Rf);
            }
            return img;
        }
        Image blank = Image.CreateEmpty(ImageWidth, ImageHeight, false, Image.Format.Rf);
        blank.Fill(new Color(0f, 0f, 0f, 1f));
        return blank;
    }

    private Image LoadOrCreateChunkImage(string path)
    {
        Image img = TryLoad(path);
        if (img != null)
        {
            if (img.GetWidth() != SizeChunksX || img.GetHeight() != SizeChunksZ)
            {
                img.Resize(SizeChunksX, SizeChunksZ, Image.Interpolation.Nearest);
            }
            if (img.GetFormat() != Image.Format.R8)
            {
                img.Convert(Image.Format.R8);
            }
            return img;
        }
        Image blank = Image.CreateEmpty(SizeChunksX, SizeChunksZ, false, Image.Format.R8);
        blank.Fill(new Color(0f, 0f, 0f, 1f));
        return blank;
    }

    // Per-voxel tunnel carve mask, indexed [px, ly, pz] (ly = wy - WorldMinY).
    // Stored as a tiny raw .bin (dims header + bytes) — too sparse/3D to be a
    // useful image, and the carved result is captured in the baked .hike anyway.
    public byte[,,] LoadOrCreateTunnels()
    {
        int nx = ImageWidth;
        int ny = VoxelHeight;
        int nz = ImageHeight;
        if (!string.IsNullOrEmpty(TunnelMaskPath))
        {
            string os = ProjectSettings.GlobalizePath(TunnelMaskPath);
            if (System.IO.File.Exists(os))
            {
                try
                {
                    using var fs = System.IO.File.OpenRead(os);
                    using var br = new System.IO.BinaryReader(fs);
                    int fx = br.ReadInt32();
                    int fy = br.ReadInt32();
                    int fz = br.ReadInt32();
                    if (fx == nx && fy == ny && fz == nz)
                    {
                        var arr = new byte[nx, ny, nz];
                        byte[] buf = br.ReadBytes(nx * ny * nz);
                        System.Buffer.BlockCopy(buf, 0, arr, 0, buf.Length);
                        return arr;
                    }
                }
                catch (System.Exception e)
                {
                    GD.PrintErr($"WorldMapData: tunnel load failed: {e.Message}");
                }
            }
        }
        return new byte[nx, ny, nz];
    }

    public void SaveElevation(Image img)
    {
        SaveExr(ElevationImagePath, img, "elevation");
    }

    public void SaveWater(Image img)
    {
        SaveExr(WaterImagePath, img, "water");
    }

    public void SaveRegion(Image img)
    {
        SavePng(RegionImagePath, img, "region");
    }

    public void SaveZone(Image img)
    {
        SavePng(ZoneImagePath, img, "zone");
    }

    public void SaveScatter(Image img)
    {
        SavePng(ScatterImagePath, img, "scatter");
    }

    public void SaveTunnels(byte[,,] tunnels)
    {
        if (string.IsNullOrEmpty(TunnelMaskPath))
        {
            return;
        }
        try
        {
            int nx = tunnels.GetLength(0);
            int ny = tunnels.GetLength(1);
            int nz = tunnels.GetLength(2);
            byte[] buf = new byte[tunnels.Length];
            System.Buffer.BlockCopy(tunnels, 0, buf, 0, buf.Length);
            using var fs = System.IO.File.Create(ProjectSettings.GlobalizePath(TunnelMaskPath));
            using var bw = new System.IO.BinaryWriter(fs);
            bw.Write(nx);
            bw.Write(ny);
            bw.Write(nz);
            bw.Write(buf);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"WorldMapData: tunnel save failed: {e.Message}");
        }
    }

    private static Image TryLoad(string resPath)
    {
        if (string.IsNullOrEmpty(resPath))
        {
            return null;
        }
        string os = ProjectSettings.GlobalizePath(resPath);
        return System.IO.File.Exists(os) ? Image.LoadFromFile(os) : null;
    }

    private static void SaveExr(string resPath, Image img, string label)
    {
        if (string.IsNullOrEmpty(resPath))
        {
            GD.PrintErr($"WorldMapData: {label} path not set; cannot save.");
            return;
        }
        Error e = img.SaveExr(ProjectSettings.GlobalizePath(resPath));
        if (e != Error.Ok)
        {
            GD.PrintErr($"WorldMapData: {label} SaveExr failed: {e}");
        }
    }

    private static void SavePng(string resPath, Image img, string label)
    {
        if (string.IsNullOrEmpty(resPath))
        {
            GD.PrintErr($"WorldMapData: {label} path not set; cannot save.");
            return;
        }
        Error e = img.SavePng(ProjectSettings.GlobalizePath(resPath));
        if (e != Error.Ok)
        {
            GD.PrintErr($"WorldMapData: {label} SavePng failed: {e}");
        }
    }
}
