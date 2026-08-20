using Godot;

// The authored source document for a painted world — the FIRST step in the
// authoring chain. Holds bake settings plus references to the external layer
// files (the layers ARE images / data files, openable directly). A
// deterministic bake (WorldMapState) turns this into a WorldState / .hike;
// the .hike is never hand-edited. Mirrors the VoxelAtlasManifest pattern.
//
// Layers: elevation (per-column height in voxels relative to seaLevel, signed),
// water (per-column water surface, same encoding),
// region (per-chunk index), zone (per-chunk index), tunnels (per-voxel carve).
//
// NOT [Tool], and it must stay that way. WorldGenData is not [Tool], so under
// the rule in the root CLAUDE.md a [Tool] class cannot hold a typed reference to
// one: the editor materialises `genData` as a base Resource, the typed setter
// throws, the field reads empty in the inspector, and the next editor save
// writes this .tres back WITHOUT it. That is silent data loss, and it happened
// here twice in one session — each time the next bake died on a null genData.
// Marking WorldGenData [Tool] instead would mean tagging its whole ~174-class
// transitive closure. The cost of staying un-[Tool] is that [ExportToolButton]
// cannot run, so the bake is driven from the painter (Ctrl+S) instead.
[GlobalClass]
public partial class WorldMapData : Resource
{
    [Export] public WorldGenData genData;

    // The zones this document can paint — ZoneData, the theme and weather
    // profile, and nothing else.
    //
    // It used to paint ZoneGenData, which bundles kits, spawn lists, difficulty
    // bands, fixtures and terrain tuning alongside the theme. Every one of those
    // is now either its own painted layer or irrelevant to a painter (painting
    // IS the placement, so a zone's bounds, fixtures and terrain tuning have no
    // meaning here). What was left was one dereference to `.zone`, so the
    // indirection went and the theme is painted directly.
    [Export] public ZoneData[] zones = System.Array.Empty<ZoneData>();

    // Stops of the danger ramp, and the length of this array IS the number of
    // levels the tool offers. Five matches the 0-4 band worldgen uses
    // (mobLevelMin/Max, mobLevelCap). The field between them is continuous and
    // the map lerps these linearly, so a soft brush edge reads as a gradient
    // rather than as a staircase.
    [Export] public Color[] mobLevelColors =
    {
        new Color(0.35f, 0.55f, 0.35f),
        new Color(0.65f, 0.70f, 0.30f),
        new Color(0.80f, 0.55f, 0.20f),
        new Color(0.75f, 0.30f, 0.20f),
        new Color(0.50f, 0.18f, 0.45f),
    };

    // A painted climbing route, inked over the step outline in place of its
    // height ink — so a route reads as the white edge you clicked turning
    // magenta, and nothing else about the map changes.
    [Export] public Color climbInk = new Color(1f, 0.2f, 0.9f, 1f);

    // Shortest wall a route may be painted on. Matches the ">2m" bucket the
    // outline pass draws in edgeInkOver2m, because "paint the white edges" is the
    // rule the tool is teaching — deliberately NOT WorldGenData.climbMinCliffHeight,
    // which is the minimum for the PROCEDURAL pass and has no say over a wall
    // someone drew a route on.
    [Export(PropertyHint.Range, "2,32,1")] public int climbRouteMinWallVoxels = 3;

    // Ground for columns with none painted. Replaces "inherit the zone's kits",
    // which only worked while zones still carried kits — and which quietly tied
    // the two layers together.
    [Export] public GroundSetData defaultGround;

    // Ground sets this document can paint. A column with no ground painted falls
    // back to defaultGround above.
    [Export] public GroundSetData[] groundSets = System.Array.Empty<GroundSetData>();

    // Prop sets this document can paint. The scatter layer stores an index into
    // this palette plus a density multiplier, so a set is defined once and
    // painted anywhere — the whole point of pulling "pine stand" out of the kit
    // it used to be inlined in.
    [Export] public SpawnSetData[] propSets = System.Array.Empty<SpawnSetData>();

    // Mob sets this document can paint. The SAME resource type as propSets —
    // "a weighted set of things placed at an area rate" describes both — but a
    // separate palette and a separate raster, because one set per column means
    // painting wolves would otherwise erase the pine stand under them. Two
    // palettes also stop the mob brush offering you trees.
    [Export] public SpawnSetData[] mobSets = System.Array.Empty<SpawnSetData>();

    // Composite brushes: one stroke writing ground + props + zone together.
    [Export] public PaintPresetData[] presets = System.Array.Empty<PaintPresetData>();

    // World extent (XZ footprint + vertical chunk range). Per-column images are
    // SizeChunks * ChunkState.SIZE texels; per-chunk images are SizeChunks.
    [Export(PropertyHint.Range, "1,256,1")] public int sizeChunksX = 18;
    [Export(PropertyHint.Range, "1,256,1")] public int sizeChunksZ = 16;
    [Export(PropertyHint.Range, "-8,0,1")] public int floorChunkY = -1;
    [Export(PropertyHint.Range, "0,32,1")] public int ceilChunkY = 4;

    // World voxel Y of the waterline (matches WorldGen.WATER_LEVEL). A document
    // constant, not a live knob: the elevation layer is measured RELATIVE to it,
    // so terrain is raised and lowered past the shore without moving the sea.
    [Export] public int seaLevel = 0;

    // Vertical range the elevation layer can express, in voxels relative to
    // seaLevel. Negative is seabed — painting below the waterline is how oceans
    // and lake beds are dug, so this must stay negative. Deepening it past the
    // floor chunk does nothing: SnapVoxels also clamps to the world extent.
    [Export(PropertyHint.Range, "-512,0,1")] public float minElevationVoxels = -16f;
    [Export(PropertyHint.Range, "1,512,1")] public float maxElevationVoxels = 64f;

    // How the painted terrain picks a zone kit. A column at or within
    // shoreBandVoxels above the waterline is shore; anything the water stands
    // over is submerged; the rest is surface. Below the top surfaceDepthVoxels
    // the column switches to the zone's cave kit, so a tunnel bored through a
    // hillside has rock walls rather than a cross-section of grass.
    [Export(PropertyHint.Range, "0,16,1")] public int shoreBandVoxels = 2;
    [Export(PropertyHint.Range, "1,16,1")] public int surfaceDepthVoxels = 2;

    // Authoring lattice: every painted height snaps to a multiple of this, and
    // the map colours one visibly distinct band per step. 1 means every voxel is
    // its own step — heights are still whole metres, there is just no coarser
    // lattice on top. Raise it to force terracing, mirroring the plateau lattice
    // the terrain generator snaps enclosed space to.
    [Export(PropertyHint.Range, "1,16,1")] public int elevationStepVoxels = 1;

    // Elevation palette. Height is read as bands of `metersPerBand`: the band
    // picks a colour from this cycle, and the metre within the band lifts it
    // toward white, so a step is always visible — a lift inside a band, a hue
    // change across one — without any contour trickery.
    //
    // These are BASE colours, the darkest metre of their band, so they are
    // authored at part value: a fully saturated base has no headroom left to
    // lift into and its four metres would be indistinguishable. The cycle is the
    // six primaries/secondaries then the same wheel offset by half a step;
    // append more to delay the repeat.
    [Export] public Color[] elevationBandHues =
    {
        new Color(0.4f, 0.4f, 0f), new Color(0f, 0.4f, 0f), new Color(0f, 0.4f, 0.4f),
        new Color(0f, 0f, 0.4f), new Color(0.4f, 0f, 0.4f), new Color(0.4f, 0f, 0f),
        new Color(0.2f, 0.4f, 0f), new Color(0f, 0.4f, 0.2f), new Color(0f, 0.2f, 0.4f),
        new Color(0.2f, 0f, 0.4f), new Color(0.4f, 0f, 0.2f), new Color(0.4f, 0.2f, 0f),
    };

    [Export(PropertyHint.Range, "1,16,1")] public int metersPerBand = 4;

    // Submerged ground is drawn as flat water, NOT as a tinted seabed: depth and
    // height would otherwise speak the same colour language and the eye cannot
    // separate "low" from "underwater". Two shades only — down to
    // shallowWaterDepth under the surface, then plain blue however deep it gets.
    // Step outlines still draw over both, so the bed's shape is readable without
    // its height being legible.
    [Export] public Color shallowWaterColor = new Color(0.35f, 0.60f, 0.90f);
    [Export] public Color deepWaterColor = new Color(0.10f, 0.20f, 0.60f);
    [Export(PropertyHint.Range, "1,8,1")] public int shallowWaterDepth = 1;

    // Ink for the outline drawn on a voxel edge where the height changes, by how
    // big that change is: under 2m, exactly 2m, over 2m. ALPHA IS PART OF THE
    // COLOUR — a step reads louder by being both stronger and more opaque, and
    // splitting the two across a colour and a float only invites them to drift.
    //
    // edgeInkSub2m is drawn ONLY on views whose colour does not already encode
    // elevation (see IWorldMapView.ColorShowsElevation): on the elevation map it
    // would run a line along every metre of every slope, saying nothing the
    // bands have not already said.
    [Export] public Color edgeInkSub2m = new Color(0f, 0f, 0f, 0.0902f);
    [Export] public Color edgeInk2m = new Color(0f, 0f, 0f, 0.5961f);
    [Export] public Color edgeInkOver2m = new Color(1f, 1f, 1f, 0.8314f);

    // External layer files, stored as res:// paths (globalized at load/save).
    [Export] public string elevationImagePath = "";   // .exr, Rf, per column (voxels rel. sea, signed)
    [Export] public string waterImagePath = "";        // .exr, Rf, per column (voxels rel. sea, signed)
    [Export] public string regionImagePath = "";       // .png, R8, per chunk
    [Export] public string zoneImagePath = "";         // .png, R8, per chunk
    [Export] public string scatterImagePath = "";      // .png, Rgba8, per column (R=set+1, G=density)
    [Export] public string groundImagePath = "";       // .png, R8, per column (ground set + 1, 0 = default)
    [Export] public string mobImagePath = "";          // .png, Rgba8, per column (R=mob set+1, G=density)

    // Per-column SCALAR layers, packed one per channel of a single image rather
    // than a file each: R = mob level, G = climb coverage (not yet painted),
    // B/A spare.
    [Export] public string scalarImagePath = "";       // .png, Rgba8, per column
    [Export] public string tunnelMaskPath = "";        // .bin, per voxel carve mask

    // Where BakeToWorldFile writes the packed world (res:// path).
    [Export] public string outputWorldPath = "";

    public Vector3I MinChunk => new Vector3I(-sizeChunksX / 2, floorChunkY, -sizeChunksZ / 2);
    public Vector3I MaxChunk => new Vector3I(MinChunk.X + sizeChunksX - 1, ceilChunkY, MinChunk.Z + sizeChunksZ - 1);

    public int WorldMinX => MinChunk.X * ChunkState.SIZE;
    public int WorldMinZ => MinChunk.Z * ChunkState.SIZE;
    public int WorldMinY => MinChunk.Y * ChunkState.SIZE;
    public int WorldMaxY => MaxChunk.Y * ChunkState.SIZE + ChunkState.SIZE - 1;

    public int ImageWidth => sizeChunksX * ChunkState.SIZE;
    public int ImageHeight => sizeChunksZ * ChunkState.SIZE;
    public int VoxelHeight => WorldMaxY - WorldMinY + 1;

    public int RegionCount => genData?.regions != null ? genData.regions.Length : 0;
    public ZoneData[] PaintableZones => zones ?? System.Array.Empty<ZoneData>();

    public int ZoneCount => PaintableZones.Length;

    // Column texel -> owning chunk's texel (shared by region + zone images).
    public Vector2I ColumnTexelToChunkTexel(int px, int pz)
    {
        return new Vector2I(px / ChunkState.SIZE, pz / ChunkState.SIZE);
    }

    // Headless bake (no running game): build a transient state from the layer
    // files and write the world. WorldMapState + WorldFile.Write are pure C#, so
    // this is callable from a console command or a CLI hook. It is deliberately
    // NOT an [ExportToolButton] — see the [Tool] note on the class.
    public void BakeToWorldFile()
    {
        if (genData == null)
        {
            GD.PrintErr("WorldMapData: GenData not set.");
            return;
        }
        if (string.IsNullOrEmpty(outputWorldPath))
        {
            GD.PrintErr("WorldMapData: OutputWorldPath not set.");
            return;
        }
        var state = new WorldMapState(this);
        WorldState ws = state.BuildWorld();
        WorldFile.Write(outputWorldPath, ws);
        GD.Print($"WorldMapData: baked world to {outputWorldPath}");
    }

    // ---- Layer load / create / save -------------------------------------

    public Image LoadOrCreateElevation()
    {
        return LoadOrCreateColumnImage(elevationImagePath);
    }

    public Image LoadOrCreateWater()
    {
        return LoadOrCreateColumnImage(waterImagePath);
    }

    // Per-column index layer (R8 at column resolution) — unlike region/zone,
    // which are per chunk.
    public Image LoadOrCreateScalars()
    {
        return LoadOrCreateSpawnImage(scalarImagePath);
    }

    public void SaveScalars(Image img)
    {
        SavePng(scalarImagePath, img, "scalars");
    }

    public Image LoadOrCreateMobs()
    {
        return LoadOrCreateSpawnImage(mobImagePath);
    }

    public void SaveMobs(Image img)
    {
        SavePng(mobImagePath, img, "mobs");
    }

    public Image LoadOrCreateGround()
    {
        Image img = TryLoad(groundImagePath);
        if (img != null)
        {
            if (img.GetWidth() != ImageWidth || img.GetHeight() != ImageHeight)
            {
                img.Resize(ImageWidth, ImageHeight, Image.Interpolation.Nearest);
            }
            if (img.GetFormat() != Image.Format.R8)
            {
                img.Convert(Image.Format.R8);
            }
            return img;
        }
        Image blank = Image.CreateEmpty(ImageWidth, ImageHeight, false, Image.Format.R8);
        blank.Fill(new Color(0f, 0f, 0f, 1f));
        return blank;
    }

    public void SaveGround(Image img)
    {
        SavePng(groundImagePath, img, "ground");
    }

    public Image LoadOrCreateRegion()
    {
        return LoadOrCreateChunkImage(regionImagePath);
    }

    public Image LoadOrCreateZone()
    {
        return LoadOrCreateChunkImage(zoneImagePath);
    }

    public Image LoadOrCreateScatter()
    {
        return LoadOrCreateSpawnImage(scatterImagePath);
    }

    // A spawn layer is a per-column RGBA8: R = set index + 1 (0 = none),
    // G = density multiplier. Props and mobs are the same shape.
    private Image LoadOrCreateSpawnImage(string path)
    {
        Image img = TryLoad(path);
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
            if (img.GetWidth() != sizeChunksX || img.GetHeight() != sizeChunksZ)
            {
                img.Resize(sizeChunksX, sizeChunksZ, Image.Interpolation.Nearest);
            }
            if (img.GetFormat() != Image.Format.R8)
            {
                img.Convert(Image.Format.R8);
            }
            return img;
        }
        Image blank = Image.CreateEmpty(sizeChunksX, sizeChunksZ, false, Image.Format.R8);
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
        if (!string.IsNullOrEmpty(tunnelMaskPath))
        {
            string os = ProjectSettings.GlobalizePath(tunnelMaskPath);
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
        SaveExr(elevationImagePath, img, "elevation");
    }

    public void SaveWater(Image img)
    {
        SaveExr(waterImagePath, img, "water");
    }

    public void SaveRegion(Image img)
    {
        SavePng(regionImagePath, img, "region");
    }

    public void SaveZone(Image img)
    {
        SavePng(zoneImagePath, img, "zone");
    }

    public void SaveScatter(Image img)
    {
        SavePng(scatterImagePath, img, "scatter");
    }

    public void SaveTunnels(byte[,,] tunnels)
    {
        if (string.IsNullOrEmpty(tunnelMaskPath))
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
            using var fs = System.IO.File.Create(ProjectSettings.GlobalizePath(tunnelMaskPath));
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
