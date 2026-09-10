using System.Collections.Generic;
using Godot;

// What role a terrain slot plays. NOT authored on the terrain itself — derived from how
// this world's zones reference it (surfaceTerrain / caveTerrain / submergedTerrain /
// shoreTerrain), so a terrain does not have to repeat what the zone already says. The
// worldgen passes that gate on "is this voxel on a surface terrain?" (dirt overlays,
// the scatter noise pick, road suppression) read it back through TerrainPalette.
public enum ETerrainPurpose
{
    // Explicit values because this is SERIALIZED now — it is an [Export] on
    // TerrainData, so inserting a member without a value would renumber the
    // rest and silently re-purpose every authored terrain.
    None = 0,
    Surface = 1,
    Cave = 2,
    Submerged = 3,
    Shore = 4,
}

// One world's resolved terrain palette — slot <-> terrain, slot -> block, slot ->
// purpose, plus the detail-group palette derived from it.
//
// **Owned by the WorldState it belongs to** (`WorldState.Terrains`), not by the
// process. It used to be a set of statics on WorldGen, bound by whichever of six
// call sites ran last, and that was wrong in three ways worth remembering:
//
//   - The palette is not generation scratch. It has to stay valid for the whole
//     session and is read by code with nothing to do with generation (the
//     mesher, SubsceneStamper, the editor, the map painter), so it is world
//     state that merely happened to live in the generator.
//   - It is the `.hike`'s wire format, and nothing tied a baked file to the
//     palette it was baked against. See WorldFile, which now records the slot
//     names and checks them.
//   - The world-map painter's bake writes it from a BACKGROUND thread while the
//     painter is live, which is why "one bake at a time" had to be a rule.
//
// Nothing was lost by moving it: every hot-path reader already had the
// WorldState in hand — `ws.SetBlockWorld(wx, wy, wz, KitBlocks.ForTerrain(terrainId))`
// was the shape of most of them — so `ws.Terrains.BlockFor(terrainId)` costs one field
// read more than a static did.
//
// The lookups stay flat arrays for the same reason `Blocks` does: they are asked
// per voxel per chunk build.
public sealed class TerrainPalette
{
    // The terrain channel is a byte, so it addresses 0..255. Sized by the CHANNEL,
    // never by BlockCatalog.MAX_BLOCKS — that is a different id space which
    // merely happened to be bigger, and past 64 terrains it silently dropped the
    // rest to the fallback ground.
    public const int MAX_KITS = 256;

    // A world with no palette at all — a fresh editor scratch world, a test.
    // Every accessor answers the fallback, so `ws.Terrains` is never null and no
    // caller needs a null check on the hot path.
    public static readonly TerrainPalette Empty = new(System.Array.Empty<TerrainData>());

    public TerrainData[] Terrains { get; }

    // Deduplicated defaultDetail groups, in palette order. Per-voxel
    // DetailGroup bytes are 1-BASED indices into this (0 = no detail).
    public DetailGroupData[] DetailGroups { get; }

    private readonly Dictionary<TerrainData, byte> _index = new();
    private readonly Dictionary<DetailGroupData, byte> _detailIndex = new();
    private readonly int[] _blockByTerrain = new int[MAX_KITS];
    private readonly byte[] _purposes;
    private readonly HashSet<int> _terrainGroundBlocks = new();

    // Purpose comes off each KIT (TerrainData.purpose), not from how some
    // zone list happens to reference it. It used to be derived by walking a
    // WorldGenData's ZoneGens, which made it a side effect of zone PLACEMENT:
    // a terrain no listed zone referenced was classified None, so `IsSurfaceTerrain`
    // answered false for it and the passes gated on that skipped it. Four terrains
    // in this project were in exactly that state — appended to the palette for
    // the painter, referenced only by zone-gen files the default world does not
    // list — and it was harmless only by luck, because all four are surface
    // terrains and None happens to fall through to the surface branch.
    public static TerrainPalette Build(TerrainPaletteData authored)
    {
        return new TerrainPalette(authored?.terrains ?? System.Array.Empty<TerrainData>());
    }

    private TerrainPalette(TerrainData[] terrains)
    {
        if (terrains.Length > MAX_KITS)
        {
            GD.PushError($"TerrainPalette: {terrains.Length} terrains exceeds the {MAX_KITS} a TerrainId byte can "
                + "address; the excess renders as the default ground.");
        }
        Terrains = terrains;
        _purposes = new byte[terrains.Length];

        int fallback = Blocks.GroundId;
        for (int i = 0; i < _blockByTerrain.Length; i++)
        {
            _blockByTerrain[i] = fallback;
        }

        var details = new List<DetailGroupData>();
        for (int i = 0; i < terrains.Length && i < MAX_KITS; i++)
        {
            TerrainData terrain = terrains[i];
            if (terrain == null)
            {
                continue;
            }
            _purposes[i] = (byte)terrain.purpose;
            // A terrain named twice would make SlotOf's answer depend on iteration
            // order, and it wastes a slot in a 256-wide channel.
            if (!_index.TryAdd(terrain, (byte)i))
            {
                GD.PushWarning($"TerrainPalette: '{terrain.ResourcePath}' appears in more than one slot "
                    + $"({_index[terrain]} and {i}); the first wins.");
            }
            if (terrain.block == null)
            {
                GD.PushWarning($"TerrainPalette: slot {i} ('{terrain.ResourcePath}') names no block; "
                    + "using the default ground.");
            }
            else
            {
                _blockByTerrain[i] = terrain.block.blockId;
                _terrainGroundBlocks.Add(terrain.block.blockId);
            }
            if (terrain.defaultDetail != null && !_detailIndex.ContainsKey(terrain.defaultDetail))
            {
                _detailIndex[terrain.defaultDetail] = (byte)details.Count;
                details.Add(terrain.defaultDetail);
            }
        }
        DetailGroups = details.ToArray();
    }

    // Slot for a terrain, 0 when it has none. Slot 0 is a real terrain, so a caller that
    // must distinguish "not in this palette" uses TryGetSlot.
    public byte SlotOf(TerrainData terrain)
    {
        return terrain != null && _index.TryGetValue(terrain, out byte i) ? i : (byte)0;
    }

    // For authoring tools that stamp a chosen terrain (the editor's Terrain brush):
    // false means the terrain has no slot in THIS world, so the caller can warn
    // rather than silently paint slot 0.
    public bool TryGetSlot(TerrainData terrain, out byte slot)
    {
        slot = 0;
        return terrain != null && _index.TryGetValue(terrain, out slot);
    }

    // Stored TerrainId byte -> its terrain, for passes reading defaultDetail /
    // detailNoise* / forest* / tree scenes.
    public TerrainData TerrainAt(int terrainId)
    {
        return (uint)terrainId < (uint)Terrains.Length ? Terrains[terrainId] : null;
    }

    // The block a terrain slot's ground is made of.
    public int BlockFor(int terrainId)
    {
        return (uint)terrainId < (uint)_blockByTerrain.Length ? _blockByTerrain[terrainId] : _blockByTerrain[0];
    }

    // Is this block SOME terrain's ground in this world?
    //
    // The question a subscene stamp asks before re-texturing a voxel: terrain ground
    // is a biome statement and the scene has no biome, so it adopts the one it
    // lands in, while anything else — a stone wall, a plank floor, cobbles, a
    // dirt path — is a deliberate material that survives the journey.
    //
    // Deliberately NOT BlockData.naturalGround, which answers "may the road pass
    // grade across this?" and is true of Road and Dirt — using it re-textured a
    // town square's paths into grass.
    public bool IsTerrainGround(int blockId)
    {
        return _terrainGroundBlocks.Contains(blockId);
    }

    // Was this slot classified Surface at build time? Gates the passes that ask
    // "is this voxel walkable above-water ground?" — dirt overlay stamping, the
    // surface scatter noise pick, road suppression on the scatter pass.
    public bool IsSurfaceTerrain(int terrainId)
    {
        return (uint)terrainId < (uint)_purposes.Length
            && _purposes[terrainId] == (byte)ETerrainPurpose.Surface;
    }

    public bool IsCaveTerrain(int terrainId)
    {
        return (uint)terrainId < (uint)_purposes.Length
            && _purposes[terrainId] == (byte)ETerrainPurpose.Cave;
    }

    // Detail group -> its 1-based stamp value for ChunkState.DetailGroup.
    // 0 means "no detail" — the group is null or not in this palette.
    public byte DetailSlotOf(DetailGroupData group)
    {
        return group != null && _detailIndex.TryGetValue(group, out byte i) ? (byte)(i + 1) : (byte)0;
    }

    // Resource paths of every slot, in order — what a .hike records so a later
    // load can prove its TerrainId bytes still mean what they meant at bake.
    public string[] SlotNames()
    {
        var names = new string[Terrains.Length];
        for (int i = 0; i < Terrains.Length; i++)
        {
            names[i] = Terrains[i]?.ResourcePath ?? "";
        }
        return names;
    }

    // The same, for the DETAIL palette. Recorded separately because it has the
    // same hazard through a different door: DetailGroup bytes are 1-based
    // indices into it, and it is DERIVED from the terrains' defaultDetail — so it
    // can be reordered by an edit that leaves the terrain palette untouched
    // (repointing one terrain's defaultDetail), which the terrain check would pass.
    public string[] DetailSlotNames()
    {
        var names = new string[DetailGroups.Length];
        for (int i = 0; i < DetailGroups.Length; i++)
        {
            names[i] = DetailGroups[i]?.ResourcePath ?? "";
        }
        return names;
    }

    // Does a stored slot list still describe this palette? Returns the first
    // slot that moved, or -1 when they agree.
    //
    // Extra slots APPENDED since the bake are fine and deliberately allowed: the
    // stored bytes still mean what they meant, which is the whole reason the
    // palette is append-only. Anything else — a shorter palette, a renamed or
    // reordered slot — means the world's voxels now say something the author
    // never wrote.
    public int FirstMismatch(string[] storedNames)
    {
        return FirstMismatch(storedNames, SlotNames());
    }

    public int FirstDetailMismatch(string[] storedNames)
    {
        return FirstMismatch(storedNames, DetailSlotNames());
    }

    private static int FirstMismatch(string[] stored, string[] live)
    {
        if (stored == null)
        {
            return -1;
        }
        if (stored.Length > live.Length)
        {
            return live.Length;
        }
        for (int i = 0; i < stored.Length; i++)
        {
            if (stored[i] != live[i])
            {
                return i;
            }
        }
        return -1;
    }
}
