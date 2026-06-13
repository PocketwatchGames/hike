using Godot;

// The authored source document for a painted world — the FIRST step in the
// authoring chain. Holds bake settings plus references to the external layer
// images (the layers ARE images, so they can be opened/inspected directly).
// A deterministic bake turns this document into a WorldState / .hike artifact;
// the .hike is never hand-edited. Mirrors the VoxelAtlasManifest pattern:
// one editor-visible resource of record + a re-runnable bake button.
//
// v1 layers: elevation (per-column float) and region (per-chunk index).
[Tool]
[GlobalClass]
public partial class WorldMapData : Resource
{
    // Supplies SimData (for the WorldState) and the zone/terrain-kit palette
    // the bake stamps. Wired as a ref per the no-hardcoded-paths rule.
    [Export] public WorldGenData GenData;

    // World extent. XZ footprint + vertical chunk range; v1 is a fixed bound
    // (streaming/tiling comes later). The elevation image is exactly
    // SizeChunks * ChunkState.SIZE texels (one per voxel column); the region
    // image is SizeChunks texels (one per chunk).
    [Export(PropertyHint.Range, "1,256,1")] public int SizeChunksX = 18;
    [Export(PropertyHint.Range, "1,256,1")] public int SizeChunksZ = 16;
    [Export(PropertyHint.Range, "-8,0,1")] public int FloorChunkY = -1;
    [Export(PropertyHint.Range, "0,32,1")] public int CeilChunkY = 4;

    // Sea level in world voxels (matches WorldGen.WATER_LEVEL). Columns whose
    // baked height sits at/below this fill with water.
    [Export] public int SeaLevel = 0;

    // World voxels of elevation that a normalized layer value of 1.0 maps to.
    [Export(PropertyHint.Range, "1,512,1")] public float MaxElevationVoxels = 64f;

    // External layer files, stored as res:// paths (globalized at load/save).
    // Elevation is a 32-bit float .exr; region is an 8-bit .png (R = index).
    [Export] public string ElevationImagePath = "";
    [Export] public string RegionImagePath = "";

    // Where BakeToWorldFile writes the packed world (res:// path; WorldFile
    // globalizes internally).
    [Export] public string OutputWorldPath = "";

    public Vector3I MinChunk => new Vector3I(-SizeChunksX / 2, FloorChunkY, -SizeChunksZ / 2);
    public Vector3I MaxChunk => new Vector3I(MinChunk.X + SizeChunksX - 1, CeilChunkY, MinChunk.Z + SizeChunksZ - 1);

    public int WorldMinX => MinChunk.X * ChunkState.SIZE;
    public int WorldMinZ => MinChunk.Z * ChunkState.SIZE;
    public int WorldMinY => MinChunk.Y * ChunkState.SIZE;
    public int WorldMaxY => MaxChunk.Y * ChunkState.SIZE + ChunkState.SIZE - 1;

    public int ImageWidth => SizeChunksX * ChunkState.SIZE;
    public int ImageHeight => SizeChunksZ * ChunkState.SIZE;

    public int RegionCount => GenData?.Regions != null ? GenData.Regions.Length : 0;

    // Map a normalized elevation value to an integer world-voxel column height.
    public int ColumnHeight(float value01)
    {
        return SeaLevel + Mathf.RoundToInt(Mathf.Clamp(value01, 0f, 1f) * MaxElevationVoxels);
    }

    // Convert a column texel (px, pz) to its owning chunk's texel in the
    // region image (px,pz are 0-based into the elevation image).
    public Vector2I ColumnTexelToRegionTexel(int px, int pz)
    {
        return new Vector2I(px / ChunkState.SIZE, pz / ChunkState.SIZE);
    }

    [ExportToolButton("Bake to .hike")]
    public Callable BakeButton => Callable.From(BakeToWorldFile);

    // Headless-capable bake: load the layer images, run the bake, write a
    // .hike. Works from the inspector button (no running game needed) because
    // WorldMapBake is pure C# + WorldFile.Write.
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
        Image elevation = LoadOrCreateElevation();
        Image region = LoadOrCreateRegion();
        WorldState ws = WorldMapBake.Build(this, elevation, region);
        WorldFile.Write(OutputWorldPath, ws);
        GD.Print($"WorldMapData: baked world to {OutputWorldPath}");
    }

    public Image LoadOrCreateElevation()
    {
        Image img = TryLoad(ElevationImagePath);
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

    public Image LoadOrCreateRegion()
    {
        Image img = TryLoad(RegionImagePath);
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

    private static Image TryLoad(string resPath)
    {
        if (string.IsNullOrEmpty(resPath))
        {
            return null;
        }
        string os = ProjectSettings.GlobalizePath(resPath);
        if (!System.IO.File.Exists(os))
        {
            return null;
        }
        return Image.LoadFromFile(os);
    }

    public void SaveElevation(Image elevation)
    {
        if (string.IsNullOrEmpty(ElevationImagePath))
        {
            GD.PrintErr("WorldMapData: ElevationImagePath not set; cannot save.");
            return;
        }
        Error e = elevation.SaveExr(ProjectSettings.GlobalizePath(ElevationImagePath));
        if (e != Error.Ok)
        {
            GD.PrintErr($"WorldMapData: SaveExr failed: {e}");
        }
    }

    public void SaveRegion(Image region)
    {
        if (string.IsNullOrEmpty(RegionImagePath))
        {
            GD.PrintErr("WorldMapData: RegionImagePath not set; cannot save.");
            return;
        }
        Error e = region.SavePng(ProjectSettings.GlobalizePath(RegionImagePath));
        if (e != Error.Ok)
        {
            GD.PrintErr($"WorldMapData: SavePng failed: {e}");
        }
    }
}
