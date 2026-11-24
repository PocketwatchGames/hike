using Godot;

// The authored source document for a painted world — the FIRST step in the
// authoring chain. Holds bake settings plus references to the external layer
// files (the layers ARE images / data files, openable directly). A
// deterministic bake (WorldMapState) turns this into a WorldState / .hike;
// the .hike is never hand-edited. Mirrors the VoxelAtlasManifest pattern.
//
// Layers: elevation (per-column height in voxels relative to seaLevel, signed),
// region (per-chunk index), zone (per-chunk index), and the per-voxel layer
// (tunnels.bin: carves, builds, repaving, water, terrain kit overrides).
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
    [Export] public TerrainPaletteData terrainPalette;
    [Export] public SimData simData;

    // NO PALETTE ARRAYS. What a document can paint — its zones, regions, ground
    // sets, prop sets, mob sets, water types, paving blocks, placeable entities
    // and presets — is DISCOVERED from disk by AuthoringPaletteSource.Table, and
    // the slot each one occupies is recorded in the WorldMapPalettes ledger
    // beside the layer images. Nothing is registered by hand, so a resource
    // becomes paintable by existing in the right directory.
    //
    // They were fields here, and the cost was silent: an authored entry the
    // author forgot to append simply did not exist in the tool, with nothing
    // anywhere reporting it. The ledger also belongs beside the layers rather
    // than here because the painter WRITES it every session, and this file is
    // one the Godot editor may have open (see the [Tool] note above).

    // How many danger levels the ramp has, and so what the painted scalar
    // layer's 0..1 MEANS: a column stores a fraction and decodes to
    // 0..mobLevelCount-1. Five matches the 0-4 band worldgen uses
    // (mobLevelMin/Max, mobLevelCap). World data, not a tool setting — change it
    // and every already-painted column reads as a different level. How those
    // levels are COLOURED is WorldMapInkData.mobLevelColors. At most 15: a
    // passage stores its level in four bits of the tunnel mask, one of them
    // "the surface's" (WorldMapState.MaxPassageLevel).
    [Export(PropertyHint.Range, "1,15,1")] public int mobLevelCount = 5;

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

    // Ground for columns with none painted. Replaces "inherit the zone's terrains",
    // which only worked while zones still carried terrains — and which quietly tied
    // the two layers together.
    [Export] public TerrainKitData defaultGround;


    // World extent (XZ footprint + vertical chunk range). Per-column images are
    // SizeChunks * ChunkState.SIZE texels; per-chunk images are SizeChunks.
    [Export(PropertyHint.Range, "1,256,1")] public int sizeChunksX = 18;
    [Export(PropertyHint.Range, "1,256,1")] public int sizeChunksZ = 16;
    [Export(PropertyHint.Range, "-8,0,1")] public int floorChunkY = -1;
    [Export(PropertyHint.Range, "0,32,1")] public int ceilChunkY = 4;

    // World voxel Y that 0 means in both height layers (matches
    // TerrainMath.SEA_LEVEL). It is the ORIGIN of the signed encoding and the
    // level a new document is prefilled with water up to — not a waterline
    // rule: nothing asks "is this ground below the sea", the voxel layer's water
    // alone says where water is.
    [Export] public int seaLevel = 0;

    // Vertical range BOTH height layers can express, in voxels relative to
    // seaLevel. Negative is seabed — digging below the sea is how ocean and lake
    // beds are made, so this must stay negative. Deepening it past the floor
    // chunk does nothing: SnapVoxels also clamps to the world extent.
    [Export(PropertyHint.Range, "-512,0,1")] public float minElevationVoxels = -16f;
    [Export(PropertyHint.Range, "1,512,1")] public float maxElevationVoxels = 64f;

    // How the painted terrain picks a zone terrain. A column at or within
    // shoreBandVoxels above THE WATER BESIDE IT is shore; anything the water
    // stands over is submerged; the rest is surface. Measured from the water
    // rather than from seaLevel, so a drained basin below zero is not sand and a
    // mountain lake gets a beach. Below the top surfaceDepthVoxels
    // the column switches to the zone's cave terrain, so a tunnel bored through a
    // hillside has rock walls rather than a cross-section of grass.
    [Export(PropertyHint.Range, "0,16,1")] public int shoreBandVoxels = 2;
    [Export(PropertyHint.Range, "1,16,1")] public int surfaceDepthVoxels = 2;

    // Authoring lattice: every painted height snaps to a multiple of this, and
    // the map colours one visibly distinct band per step. 1 means every voxel is
    // its own step — heights are still whole metres, there is just no coarser
    // lattice on top. Raise it to force terracing, mirroring the plateau lattice
    // the terrain generator snaps enclosed space to.
    [Export(PropertyHint.Range, "1,16,1")] public int elevationStepVoxels = 1;

    // A DOCUMENT IS ITS FOLDER: this .tres, its layers.bin, placements.tres and
    // palettes.tres, at fixed names beside it — nothing to author, so copying a
    // world's folder copies a world that edits only its own files. Don't
    // reintroduce authored paths: a copied document then silently paints and
    // bakes over the world it was copied from.


    // World m/s the wind layer's full strength paints. The layer stores a
    // normalized strength, so this is the only place the authored range lives;
    // WindGen.DEFAULT_BASE_SPEED (5 m/s) is the calm-day magnitude an unpainted
    // chunk inherits from its zone, so a max several times that is what makes
    // painting a gale worth doing.
    [Export(PropertyHint.Range, "0,40,0.5")] public float windPaintMaxSpeed = 20f;

    // The folder this document was loaded from. Empty for a document that has
    // no file, which then has no layers to load and nowhere to save them.
    public string DocumentDir => string.IsNullOrEmpty(ResourcePath) ? "" : ResourcePath.GetBaseDir();

    private string LayerPath(string file)
    {
        string dir = DocumentDir;
        return string.IsNullOrEmpty(dir) ? "" : dir.PathJoin(file);
    }

    // The world this document authors — the worlds/<name>/ it sits in — which
    // decides what the painter's pickers offer (WorldScope). Null for a
    // document outside worlds/.
    public string World => WorldScope.Of(ResourcePath);

    // Where a bake writes the packed world: worlds/<world>/<world>.hike, beside
    // the world's start content and OUTSIDE the document's folder, because it is
    // the one thing in a world a build ships that the document produces. Empty
    // for a document with no world (NoWorldMessage says why). worldmap_bake can
    // redirect a single bake (BakeToWorldFile's argument).
    public string OutputWorldPath => World == null
        ? ""
        : WorldScope.RootOf(World) + World + "." + WorldFile.FILE_EXTENSION;

    public string NoWorldMessage()
    {
        return $"'{ResourcePath}' is not inside a world folder (res://resources/data/worlds/<name>/), "
            + "so it has no world to bake. Move its folder into one.";
    }

    public Vector3I MinChunk => new Vector3I(-sizeChunksX / 2, floorChunkY, -sizeChunksZ / 2);
    public Vector3I MaxChunk => new Vector3I(MinChunk.X + sizeChunksX - 1, ceilChunkY, MinChunk.Z + sizeChunksZ - 1);

    public int WorldMinX => MinChunk.X * ChunkState.SIZE;
    public int WorldMinZ => MinChunk.Z * ChunkState.SIZE;
    public int WorldMinY => MinChunk.Y * ChunkState.SIZE;
    public int WorldMaxY => MaxChunk.Y * ChunkState.SIZE + ChunkState.SIZE - 1;

    public int ImageWidth => sizeChunksX * ChunkState.SIZE;
    public int ImageHeight => sizeChunksZ * ChunkState.SIZE;
    public int VoxelHeight => WorldMaxY - WorldMinY + 1;


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
    // never wrote. outputPath redirects this one bake (a test bake that must not
    // overwrite the real world); empty writes the document's own .hike.
    public bool BakeToWorldFile(string outputPath = "", System.Action<float, string> progress = null)
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            outputPath = OutputWorldPath;
        }
        if (string.IsNullOrEmpty(outputPath))
        {
            GD.PrintErr($"WorldMapData: {NoWorldMessage()}");
            return false;
        }
        // WorldMapBake.Bake, not BuildWorld + Write: the sun flood and the
        // canopy stamp between them are part of a bake, because nothing relights
        // a world on load. It must run on the main thread (the canopy stamp
        // instantiates scenes).
        var state = new WorldMapState(this);
        // Every bake fills from the collision as it is on disk NOW, whichever
        // path reached it. Free in a fresh process; the point is that the rule
        // has no exception to remember.
        state.RefreshPropAssets();
        return new WorldMapBake(state, outputPath).Bake(progress);
    }

    // ---- The layer file ---------------------------------------------------
    //
    // Every raster layer of the document and the voxel layer, in ONE binary file
    // beside the .tres (layers.bin), cut into one SECTION per chunk column — the
    // whole of what the document says about those 16 x 16 columns:
    //
    //   magic, version, nx, ny, nz, section size (16)
    //   the image layer table: name, format, per-chunk flag, in stored order
    //   section count, each section's byte length (7-bit varint), the sections
    //
    // A section is zlib over, in the table's order, each image layer's slice —
    // a per-column layer's 16 x 16 texels as byte PLANES (all first bytes, then
    // all second bytes: a float's slow high bytes then form runs), a per-chunk
    // layer's one texel — followed by the voxel columns, px-major then pz, each
    // run-length encoded bottom to top as (ushort count, uint cell).
    //
    // One rule for the whole file, and an edit with ANY tool changes only the
    // sections of the chunk columns it touched: the encoding is deterministic,
    // so every other section saves byte-identical and git deltas one version of
    // the file against the last. It is also the unit a partial load would read.
    //
    // The layers are held in memory as Images and the voxel array; only how they
    // reach the disk is here. Exact-version like .hike: another version, or a
    // layer table that is not exactly this code's, is refused by name.

    public const string LAYERS_FILE = "layers.bin";
    public const string PLACEMENTS_FILE = "placements.tres";     // WorldMapPlacements
    public const string PALETTES_FILE = "palettes.tres";         // WorldMapPalettes (palette slot ledgers)

    // Every file a document owns beside itself — what makes a folder taken.
    public static readonly string[] LAYER_FILES = { LAYERS_FILE, PLACEMENTS_FILE, PALETTES_FILE };

    public string LayersPath => LayerPath(LAYERS_FILE);
    public string PlacementsPath => LayerPath(PLACEMENTS_FILE);
    public string PalettesPath => LayerPath(PALETTES_FILE);

    // Image layer names, as stored. See WorldMapState for what each holds.
    public const string ElevationLayer = "elevation";        // Rf, per column (voxels rel. sea, signed)
    public const string RegionLayer = "region";              // R8, per chunk
    public const string ZoneLayer = "zone";                  // R8, per chunk
    public const string HabitatLayer = "habitat";            // R8, per chunk (habitat + 1, 0 = none)
    // R = compass angle (0..255 a full turn), G = strength (0 = UNPAINTED,
    // inherit the zone's; 1..255 calm through gale).
    public const string WindLayer = "wind";                  // Rgba8, per chunk
    // The painted region IS the barrier: which list furnishes it, no density.
    public const string BlockingPropsLayer = "props_blocking"; // R8, per column (prop list + 1)
    public const string GroundLayer = "ground";              // R8, per column (surface terrain kit + 1)
    public const string PavingLayer = "paving";              // R8, per column (surface paving block + 1)
    public const string MobsLayer = "mobs";                  // Rgba8, per column (R = mob set + 1, G = density)
    public const string ScalarsLayer = "scalars";            // Rgba8, per column (R = mob level, G = climb, B = roughness)
    // Per voxel: the edit and its payload, the water, the terrain kit override.
    public const string VoxelLayer = "voxels";

    private static readonly (string Name, Image.Format Format, bool PerChunk)[] IMAGE_LAYERS =
    {
        (ElevationLayer, Image.Format.Rf, false),
        (RegionLayer, Image.Format.R8, true),
        (ZoneLayer, Image.Format.R8, true),
        (HabitatLayer, Image.Format.R8, true),
        (WindLayer, Image.Format.Rgba8, true),
        (BlockingPropsLayer, Image.Format.R8, false),
        (GroundLayer, Image.Format.R8, false),
        (PavingLayer, Image.Format.R8, false),
        (MobsLayer, Image.Format.Rgba8, false),
        (ScalarsLayer, Image.Format.Rgba8, false),
    };

    // The document's layers, as loaded or about to be saved.
    public sealed class LayerSet
    {
        public readonly System.Collections.Generic.Dictionary<string, Image> Images = new();
        public uint[,,] Voxels;
    }

    private const int LAYERS_MAGIC = 0x504D4B48; // "HKMP"
    private const int LAYERS_VERSION = 2;
    private const int SECTION = ChunkState.SIZE;

    // A document with no layer file yet gets blank layers, and its voxel layer is
    // prefilled with the zone's water up to seaLevel: the world starts as sea,
    // and land hides the water it stands above until it is carved away.
    public LayerSet LoadLayers()
    {
        string os = string.IsNullOrEmpty(LayersPath) ? "" : ProjectSettings.GlobalizePath(LayersPath);
        if (os == "" || !System.IO.File.Exists(os))
        {
            return BlankLayers();
        }
        // A file that cannot be read THROWS rather than coming back blank: the
        // painter saves every layer together, so a blank one here is the whole
        // document deleted at the next Ctrl+S.
        using var fs = System.IO.File.OpenRead(os);
        using var br = new System.IO.BinaryReader(fs);
        int magic = br.ReadInt32();
        int version = br.ReadInt32();
        if (magic != LAYERS_MAGIC || version != LAYERS_VERSION)
        {
            throw new System.IO.InvalidDataException(
                $"{LayersPath}: not a v{LAYERS_VERSION} layer file (magic {magic:X8}, version {version})");
        }
        int nx = br.ReadInt32();
        int ny = br.ReadInt32();
        int nz = br.ReadInt32();
        int section = br.ReadInt32();
        if (nx != ImageWidth || ny != VoxelHeight || nz != ImageHeight || section != SECTION)
        {
            throw new System.IO.InvalidDataException(
                $"{LayersPath}: layers are {nx}x{ny}x{nz} in {section}-column sections, "
                + $"document is {ImageWidth}x{VoxelHeight}x{ImageHeight} in {SECTION}");
        }
        int layerCount = br.ReadInt32();
        if (layerCount != IMAGE_LAYERS.Length)
        {
            throw new System.IO.InvalidDataException(
                $"{LayersPath}: {layerCount} image layers, this code has {IMAGE_LAYERS.Length}");
        }
        for (int i = 0; i < layerCount; i++)
        {
            string name = br.ReadString();
            var format = (Image.Format)br.ReadInt32();
            bool perChunk = br.ReadBoolean();
            (string Name, Image.Format Format, bool PerChunk) spec = IMAGE_LAYERS[i];
            if (name != spec.Name || format != spec.Format || perChunk != spec.PerChunk)
            {
                throw new System.IO.InvalidDataException(
                    $"{LayersPath}: layer {i} is '{name}' {format}{(perChunk ? " per chunk" : "")}, "
                    + $"this code expects '{spec.Name}' {spec.Format}{(spec.PerChunk ? " per chunk" : "")}");
            }
        }

        int sx = nx / SECTION;
        int sz = nz / SECTION;
        int count = br.ReadInt32();
        if (count != sx * sz)
        {
            throw new System.IO.InvalidDataException($"{LayersPath}: {count} sections, expected {sx * sz}");
        }
        var lengths = new int[count];
        for (int i = 0; i < count; i++)
        {
            lengths[i] = br.Read7BitEncodedInt();
        }
        var packed = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            packed[i] = br.ReadBytes(lengths[i]);
            if (packed[i].Length != lengths[i])
            {
                throw new System.IO.InvalidDataException($"{LayersPath}: truncated at section {i}");
            }
        }

        byte[][] images = new byte[IMAGE_LAYERS.Length][];
        for (int l = 0; l < IMAGE_LAYERS.Length; l++)
        {
            images[l] = new byte[LayerWidth(l) * LayerHeight(l) * BytesPerPixel(IMAGE_LAYERS[l].Format)];
        }
        var voxels = new uint[nx, ny, nz];
        string bad = null;
        // Sections write disjoint texels and columns, so they decode in parallel.
        System.Threading.Tasks.Parallel.For(0, count, i =>
        {
            try
            {
                DecodeSection(packed[i], i / sz, i % sz, images, voxels, ny);
            }
            catch (System.Exception e)
            {
                bad = $"section {i}: {e.Message}";
            }
        });
        if (bad != null)
        {
            throw new System.IO.InvalidDataException($"{LayersPath}: {bad}");
        }

        var set = new LayerSet { Voxels = voxels };
        for (int l = 0; l < IMAGE_LAYERS.Length; l++)
        {
            set.Images[IMAGE_LAYERS[l].Name] = Image.CreateFromData(
                LayerWidth(l), LayerHeight(l), false, IMAGE_LAYERS[l].Format, images[l]);
        }
        return set;
    }

    public LayerSet BlankLayers()
    {
        var set = new LayerSet();
        for (int l = 0; l < IMAGE_LAYERS.Length; l++)
        {
            Image blank = Image.CreateEmpty(LayerWidth(l), LayerHeight(l), false, IMAGE_LAYERS[l].Format);
            blank.Fill(new Color(0f, 0f, 0f, 1f));
            set.Images[IMAGE_LAYERS[l].Name] = blank;
        }
        set.Voxels = new uint[ImageWidth, VoxelHeight, ImageHeight];
        FillSea(set.Voxels);
        return set;
    }

    public void SaveLayers(LayerSet set)
    {
        if (string.IsNullOrEmpty(LayersPath))
        {
            GD.PrintErr("WorldMapData: document has no folder; cannot save its layers.");
            return;
        }
        try
        {
            byte[][] images = new byte[IMAGE_LAYERS.Length][];
            for (int l = 0; l < IMAGE_LAYERS.Length; l++)
            {
                Image img = set.Images[IMAGE_LAYERS[l].Name];
                if (img.GetFormat() != IMAGE_LAYERS[l].Format)
                {
                    img.Convert(IMAGE_LAYERS[l].Format);
                }
                if (img.GetWidth() != LayerWidth(l) || img.GetHeight() != LayerHeight(l))
                {
                    throw new System.ArgumentException(
                        $"layer '{IMAGE_LAYERS[l].Name}' is {img.GetWidth()}x{img.GetHeight()}, "
                        + $"document wants {LayerWidth(l)}x{LayerHeight(l)}");
                }
                images[l] = img.GetData();
            }
            uint[,,] voxels = set.Voxels;
            int ny = voxels.GetLength(1);
            int sx = ImageWidth / SECTION;
            int sz = ImageHeight / SECTION;
            var sections = new byte[sx * sz][];
            System.Threading.Tasks.Parallel.For(0, sections.Length, i =>
            {
                sections[i] = EncodeSection(i / sz, i % sz, images, voxels, ny);
            });

            // Encoded in full before the file is opened, so a failure part-way
            // leaves the previous file intact.
            using var ms = new System.IO.MemoryStream();
            using (var bw = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(LAYERS_MAGIC);
                bw.Write(LAYERS_VERSION);
                bw.Write(ImageWidth);
                bw.Write(ny);
                bw.Write(ImageHeight);
                bw.Write(SECTION);
                bw.Write(IMAGE_LAYERS.Length);
                foreach ((string name, Image.Format format, bool perChunk) in IMAGE_LAYERS)
                {
                    bw.Write(name);
                    bw.Write((int)format);
                    bw.Write(perChunk);
                }
                bw.Write(sections.Length);
                foreach (byte[] sec in sections)
                {
                    bw.Write7BitEncodedInt(sec.Length);
                }
                foreach (byte[] sec in sections)
                {
                    bw.Write(sec);
                }
            }
            System.IO.File.WriteAllBytes(ProjectSettings.GlobalizePath(LayersPath), ms.ToArray());
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"WorldMapData: layer save failed: {e.Message}");
        }
    }

    private int LayerWidth(int layer) => IMAGE_LAYERS[layer].PerChunk ? sizeChunksX : ImageWidth;
    private int LayerHeight(int layer) => IMAGE_LAYERS[layer].PerChunk ? sizeChunksZ : ImageHeight;

    private static int BytesPerPixel(Image.Format format)
    {
        return format switch
        {
            Image.Format.R8 => 1,
            Image.Format.Rgba8 => 4,
            Image.Format.Rf => 4,
            _ => throw new System.ArgumentException($"no layer is stored as {format}"),
        };
    }

    // Chunk column (cx, cz): every image layer's slice in table order, then the
    // voxel columns. See the format comment above.
    private byte[] EncodeSection(int cx, int cz, byte[][] images, uint[,,] voxels, int ny)
    {
        using var ms = new System.IO.MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new System.IO.BinaryWriter(z))
        {
            for (int l = 0; l < IMAGE_LAYERS.Length; l++)
            {
                int bpp = BytesPerPixel(IMAGE_LAYERS[l].Format);
                byte[] data = images[l];
                if (IMAGE_LAYERS[l].PerChunk)
                {
                    w.Write(data, (cz * sizeChunksX + cx) * bpp, bpp);
                    continue;
                }
                for (int b = 0; b < bpp; b++)
                {
                    for (int pz = cz * SECTION; pz < cz * SECTION + SECTION; pz++)
                    {
                        for (int px = cx * SECTION; px < cx * SECTION + SECTION; px++)
                        {
                            w.Write(data[(pz * ImageWidth + px) * bpp + b]);
                        }
                    }
                }
            }
            for (int px = cx * SECTION; px < cx * SECTION + SECTION; px++)
            {
                for (int pz = cz * SECTION; pz < cz * SECTION + SECTION; pz++)
                {
                    int y = 0;
                    while (y < ny)
                    {
                        uint cell = voxels[px, y, pz];
                        int run = 1;
                        while (y + run < ny && voxels[px, y + run, pz] == cell)
                        {
                            run++;
                        }
                        w.Write((ushort)run);
                        w.Write(cell);
                        y += run;
                    }
                }
            }
        }
        return ms.ToArray();
    }

    private void DecodeSection(byte[] packed, int cx, int cz, byte[][] images, uint[,,] voxels, int ny)
    {
        using var ms = new System.IO.MemoryStream(packed);
        using var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var r = new System.IO.BinaryReader(z);
        for (int l = 0; l < IMAGE_LAYERS.Length; l++)
        {
            int bpp = BytesPerPixel(IMAGE_LAYERS[l].Format);
            byte[] data = images[l];
            if (IMAGE_LAYERS[l].PerChunk)
            {
                for (int b = 0; b < bpp; b++)
                {
                    data[(cz * sizeChunksX + cx) * bpp + b] = r.ReadByte();
                }
                continue;
            }
            for (int b = 0; b < bpp; b++)
            {
                for (int pz = cz * SECTION; pz < cz * SECTION + SECTION; pz++)
                {
                    for (int px = cx * SECTION; px < cx * SECTION + SECTION; px++)
                    {
                        data[(pz * ImageWidth + px) * bpp + b] = r.ReadByte();
                    }
                }
            }
        }
        for (int px = cx * SECTION; px < cx * SECTION + SECTION; px++)
        {
            for (int pz = cz * SECTION; pz < cz * SECTION + SECTION; pz++)
            {
                int y = 0;
                while (y < ny)
                {
                    int run = r.ReadUInt16();
                    uint cell = r.ReadUInt32();
                    if (run == 0 || y + run > ny)
                    {
                        throw new System.IO.InvalidDataException($"bad voxel run in column ({px}, {pz})");
                    }
                    for (int k = 0; k < run; k++)
                    {
                        voxels[px, y + k, pz] = cell;
                    }
                    y += run;
                }
            }
        }
    }

    // The zone's water in every voxel at or below seaLevel, whole world.
    public void FillSea(uint[,,] arr)
    {
        FillSea(arr, 0, 0, arr.GetLength(0), arr.GetLength(2));
    }

    // Same, over columns [x0, x1) x [z0, z1) only — a canvas grown at its edge.
    public void FillSea(uint[,,] arr, int x0, int z0, int x1, int z1)
    {
        int top = Mathf.Min(seaLevel - WorldMinY, arr.GetLength(1) - 1);
        for (int px = x0; px < x1; px++)
        {
            for (int ly = 0; ly <= top; ly++)
            {
                for (int pz = z0; pz < z1; pz++)
                {
                    arr[px, ly, pz] |= WorldMapState.ZoneWaterBits;
                }
            }
        }
    }
}
