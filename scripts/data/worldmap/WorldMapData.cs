using Godot;

// The authored source document for a painted world — the FIRST step in the
// authoring chain. Holds bake settings plus references to the external layer
// files (the layers ARE images / data files, openable directly). A
// deterministic bake (WorldMapState) turns this into a WorldState / .hike;
// the .hike is never hand-edited. Mirrors the VoxelAtlasManifest pattern.
//
// Layers: elevation (per-column height in voxels relative to seaLevel, signed),
// water (per-column water surface, same encoding; below the range = none),
// region (per-chunk index), zone (per-chunk index), tunnels (per-voxel carve).
//
// NOT [Tool], and it must stay that way. Nothing it references is [Tool], so
// under the rule in the root CLAUDE.md a [Tool] class here would materialise
// each typed reference as a base Resource, throw from the typed setter, read
// the field empty in the inspector, and write this .tres back WITHOUT it on the
// next editor save. That is silent data loss, and it happened here twice in one
// session while this held a WorldGenData. Tagging those closures instead would
// mean ~174 classes. The cost of staying un-[Tool] is that [ExportToolButton]
// cannot run, so the bake is driven from the painter (Ctrl+S) instead.
//
// It holds NO WorldGenData. A painted document authors its own terrain, so
// depending on the generator's authoring asset only ever made two editable
// pointers at one file with nothing checking they agreed.
[GlobalClass]
public partial class WorldMapData : Resource
{
    // What a painted world needs that is NOT about generating one. Each is
    // already its own resource, and the generator holds the same four — but as
    // its OWN references, so neither side reaches through the other.
    //
    // startContent is what makes a painted world able to begin differently — its
    // own quests, party and starting knowledge, instead of inheriting whichever
    // set the generator authors. finish carries maxGradeStep, which is the last
    // thing that used to be borrowed from a terrain approach.
    [Export] public WorldStartData startContent;
    [Export] public WorldFinishData finish;
    [Export] public KitPaletteData kitPalette;
    [Export] public SimData simData;

    // Named regions this document can paint, the mirror of `zones`. RegionData
    // rather than worldgen's RegionGenData wrapper: the painter only ever read
    // `.region` off each, and placement — which is all the wrapper adds — is
    // what painting IS.
    [Export] public RegionData[] regions = System.Array.Empty<RegionData>();

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

    // How many danger levels the ramp has, and so what the painted scalar
    // layer's 0..1 MEANS: a column stores a fraction and decodes to
    // 0..mobLevelCount-1. Five matches the 0-4 band worldgen uses
    // (mobLevelMin/Max, mobLevelCap). World data, not a tool setting — change it
    // and every already-painted column reads as a different level. How those
    // levels are COLOURED is WorldMapInkData.mobLevelColors.
    [Export(PropertyHint.Range, "1,16,1")] public int mobLevelCount = 5;

    // Roughen: the shortest step it treats as a cliff, and the band of wall that
    // always survives. A cliff of height h has h - band voxels of erosion to
    // give: at 4m that is a single voxel, at 5m two, and so on. The band is what
    // keeps an eroded cliff a cliff.
    [Export(PropertyHint.Range, "2,32,1")] public int roughenMinCliffVoxels = 4;
    [Export(PropertyHint.Range, "0,16,1")] public int roughenKeepBandVoxels = 3;

    // Metres of run per voxel of talus. 1 is a 45-degree scree cone; higher
    // spreads the same rubble further out. At least 1 is what keeps every new
    // step to a single voxel: erosion piled into ONE column is a 2m step, and a
    // 2m step is something the player can mantle.
    [Export(PropertyHint.Range, "1,8,1")] public int roughenTalusRunPerVoxel = 1;

    // How far weathering may reach from a cliff, in metres. Bounds the cost of
    // the spread on a very tall wall.
    [Export(PropertyHint.Range, "1,32,1")] public int roughenMaxSpreadVoxels = 8;

    // Splits that budget between the lip and the foot — 1 puts it all in talus
    // at the base, 0 takes it all off the top. Sampled at world coordinates from
    // a fixed seed, so the same cliff always weathers the same way and two
    // people opening the document see the same thing.
    [Export] public int roughenNoiseSeed = 8891;
    [Export(PropertyHint.Range, "0.01,4,0.001")] public float roughenNoiseFrequency = 0.6f;

    // Shortest wall a route may be painted on. THREE: 3m is the shortest wall
    // that can be MARKED climbable, and it is also the band weathering leaves
    // standing, so an eroded cliff stays markable.
    // Deliberately NOT WorldGenData.climbMinCliffHeight, which is the minimum for
    // the PROCEDURAL pass and has no say over a wall someone drew a route on.
    [Export(PropertyHint.Range, "2,32,1")] public int climbRouteMinWallVoxels = 3;

    // Ground for columns with none painted. Replaces "inherit the zone's kits",
    // which only worked while zones still carried kits — and which quietly tied
    // the two layers together.
    [Export] public GroundSetData defaultGround;

    // Water types this document can paint, as the blocks themselves. A column
    // with none painted takes whatever its ZONE authors, which is the identity
    // and what every document had before this layer existed.
    //
    // Typed BlockData references are safe here because WorldMapData is
    // deliberately not [Tool], so nothing it holds is subject to the [Tool]
    // closure rule — BlockData could never satisfy it anyway, since digItem
    // opens ItemData's ~90-class closure.
    [Export] public BlockData[] waterTypes = System.Array.Empty<BlockData>();

    // Ground sets this document can paint. A column with no ground painted falls
    // back to defaultGround above.
    [Export] public GroundSetData[] groundSets = System.Array.Empty<GroundSetData>();

    // Prop sets this document can paint. The scatter layer stores an index into
    // this palette plus a density multiplier, so a set is defined once and
    // painted anywhere — the whole point of pulling "pine stand" out of the kit
    // it used to be inlined in.
    [Export] public SpawnSetData[] propSets = System.Array.Empty<SpawnSetData>();

    // Entries the entity tool can place one at a time. The same
    // `SpawnEntryData` the scatter layers use, so one palette covers props,
    // mobs, chests, loot and NPCs, and a hand-placed chest spawns through
    // exactly the path a scattered one does.
    [Export] public SpawnEntryData[] entityPalette = System.Array.Empty<SpawnEntryData>();

    // Blocks this document can pave a column's surface with — roads, plazas,
    // building floors. A BlockData directly, not a kit or a surface: appearance
    // IS the block now, and a paved column wants the block's material properties
    // too (footstep sound, speed multiplier, dig yield), which the overlay skin
    // worldgen's road pass uses cannot carry.
    [Export] public BlockData[] pavingBlocks = System.Array.Empty<BlockData>();

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

    // World voxel Y that 0 means in both height layers (matches
    // TerrainMath.SEA_LEVEL). It is the ORIGIN of the signed encoding and the
    // level an unpainted water column reads as — not a waterline rule: nothing
    // asks "is this ground below the sea", the water layer alone says where
    // water is. Which also makes it the elevation the world is prefilled with
    // water at, since a blank layer is zeros.
    [Export] public int seaLevel = 0;

    // Vertical range BOTH height layers can express, in voxels relative to
    // seaLevel. Negative is seabed — digging below the sea is how ocean and lake
    // beds are made, so this must stay negative. Deepening it past the floor
    // chunk does nothing: SnapVoxels also clamps to the world extent. One value
    // below the floor is reserved as the water layer's "no water" sentinel
    // (WorldMapState.NoWaterVoxels), so the range is what an author can paint.
    [Export(PropertyHint.Range, "-512,0,1")] public float minElevationVoxels = -16f;
    [Export(PropertyHint.Range, "1,512,1")] public float maxElevationVoxels = 64f;

    // How the painted terrain picks a zone kit. A column at or within
    // shoreBandVoxels above THE WATER BESIDE IT is shore; anything the water
    // stands over is submerged; the rest is surface. Measured from the water
    // rather than from seaLevel, so a drained basin below zero is not sand and a
    // mountain lake gets a beach. Below the top surfaceDepthVoxels
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

    // External layer files, stored as res:// paths (globalized at load/save).
    [Export] public string elevationImagePath = "";   // .exr, Rf, per column (voxels rel. sea, signed)
    [Export] public string waterImagePath = "";        // .exr, Rf, per column (voxels rel. sea, signed)
    [Export] public string regionImagePath = "";       // .png, R8, per chunk
    [Export] public string zoneImagePath = "";         // .png, R8, per chunk
    // Prevailing wind, per CHUNK — the same grid the zone index is painted on,
    // because that is the granularity the bake seeds ChunkState's wind velocity
    // subgrid at. R = compass angle (0..255 spans a full turn), G = strength
    // (0 = UNPAINTED, inherit the zone's prevailing direction; 1..255 = calm
    // through gale). The strength byte doubling as the painted-mask is what
    // makes an unpainted document behave exactly as it did before the layer
    // existed.
    [Export] public string windImagePath = "";         // .png, Rgba8, per chunk (R=angle, G=strength+1)

    // World m/s the wind layer's full strength paints. The layer stores a
    // normalized strength, so this is the only place the authored range lives;
    // WindGen.DEFAULT_BASE_SPEED (5 m/s) is the calm-day magnitude an unpainted
    // chunk inherits from its zone, so a max several times that is what makes
    // painting a gale worth doing.
    [Export(PropertyHint.Range, "0,40,0.5")] public float windPaintMaxSpeed = 20f;
    [Export] public string scatterImagePath = "";      // .png, Rgba8, per column (R=set+1, G=density)
    [Export] public string groundImagePath = "";       // .png, R8, per column (ground set + 1, 0 = default)
    [Export] public string waterTypeImagePath = "";    // .png, R8, per column (waterTypes index + 1, 0 = the zone's)
    // .png, Rgba8, per column: R = paving block + 1 (0 = none), G/B = the world
    // Y it is laid at + 1 (0 = seated on the column's own surface).
    [Export] public string pavingImagePath = "";
    [Export] public string placementsPath = "";        // .tres, WorldMapPlacements (subscene stamps)
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

    public int RegionCount => regions?.Length ?? 0;
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
    // Returns whether the world actually reached disk. A bake can fail late —
    // the .hike is written last, and the file is commonly held open by a running
    // game or editor — so a caller that assumes success will report a world it
    // never wrote.
    public bool BakeToWorldFile()
    {
        if (string.IsNullOrEmpty(outputWorldPath))
        {
            GD.PrintErr("WorldMapData: OutputWorldPath not set.");
            return false;
        }
        // WorldMapState.Bake, not BuildWorld + Write: the sun flood and the
        // canopy stamp between them are part of a bake now, because nothing
        // relights a world on load. Straight-line here — this caller is already
        // the main thread and has no UI to keep alive.
        var state = new WorldMapState(this);
        return new WorldMapBake(state).Bake();
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
        return LoadOrCreateIndexImage(groundImagePath);
    }

    public Image LoadOrCreateWaterType()
    {
        return LoadOrCreateIndexImage(waterTypeImagePath);
    }

    // A per-column INDEX layer: R8, storing a palette index + 1 so 0 can mean
    // "nothing painted".
    private Image LoadOrCreateIndexImage(string path)
    {
        Image img = TryLoad(path);
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

    // The paving layer is a per-column RGBA8 rather than a plain index image:
    // R = block index + 1 (0 = none), G/B = the world Y it is laid at, plus one,
    // low byte first, with 0 meaning "on whatever surface is under it". Two
    // channels because a document may span more than 255 voxels of height.
    //
    // A layer written before levels existed is single-channel and converts with
    // G/B zero, which is exactly the surface-seated it always meant.
    public Image LoadOrCreatePaving()
    {
        Image img = TryLoad(pavingImagePath);
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

    public void SavePaving(Image img)
    {
        SavePng(pavingImagePath, img, "paving");
    }

    public void SaveGround(Image img)
    {
        SavePng(groundImagePath, img, "ground");
    }

    public void SaveWaterType(Image img)
    {
        SavePng(waterTypeImagePath, img, "water type");
    }

    public Image LoadOrCreateRegion()
    {
        return LoadOrCreateChunkImage(regionImagePath);
    }

    public Image LoadOrCreateZone()
    {
        return LoadOrCreateChunkImage(zoneImagePath);
    }

    public Image LoadOrCreateWind()
    {
        return LoadOrCreateChunkRgbaImage(windImagePath);
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

    // Per-chunk again, but four channels: a layer that needs to carry a value AND
    // a painted-or-not flag cannot fit in the R8 index images.
    private Image LoadOrCreateChunkRgbaImage(string path)
    {
        Image img = TryLoad(path);
        if (img != null)
        {
            if (img.GetWidth() != sizeChunksX || img.GetHeight() != sizeChunksZ)
            {
                img.Resize(sizeChunksX, sizeChunksZ, Image.Interpolation.Nearest);
            }
            if (img.GetFormat() != Image.Format.Rgba8)
            {
                img.Convert(Image.Format.Rgba8);
            }
            return img;
        }
        Image blank = Image.CreateEmpty(sizeChunksX, sizeChunksZ, false, Image.Format.Rgba8);
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

    public void SaveWind(Image img)
    {
        SavePng(windImagePath, img, "wind");
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
