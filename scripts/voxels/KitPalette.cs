using System.Collections.Generic;
using Godot;

// What role a kit slot plays. NOT authored on the kit itself — derived from how
// this world's zones reference it (surfaceKit / caveKit / submergedKit /
// shoreKit), so a kit does not have to repeat what the zone already says. The
// worldgen passes that gate on "is this voxel on a surface kit?" (dirt overlays,
// the scatter noise pick, road suppression) read it back through KitPalette.
public enum EKitPurpose
{
    // Explicit values because this is SERIALIZED now — it is an [Export] on
    // TerrainKitData, so inserting a member without a value would renumber the
    // rest and silently re-purpose every authored kit.
    None = 0,
    Surface = 1,
    Cave = 2,
    Submerged = 3,
    Shore = 4,
}

// One world's resolved kit palette — slot <-> kit, slot -> block, slot ->
// purpose, plus the detail-group palette derived from it.
//
// **Owned by the WorldState it belongs to** (`WorldState.Kits`), not by the
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
// WorldState in hand — `ws.SetBlockWorld(wx, wy, wz, KitBlocks.ForKit(kitId))`
// was the shape of most of them — so `ws.Kits.BlockFor(kitId)` costs one field
// read more than a static did.
//
// The lookups stay flat arrays for the same reason `Blocks` does: they are asked
// per voxel per chunk build.
public sealed class KitPalette
{
    // The kit channel is a byte, so it addresses 0..255. Sized by the CHANNEL,
    // never by BlockCatalog.MAX_BLOCKS — that is a different id space which
    // merely happened to be bigger, and past 64 kits it silently dropped the
    // rest to the fallback ground.
    public const int MAX_KITS = 256;

    // A world with no palette at all — a fresh editor scratch world, a test.
    // Every accessor answers the fallback, so `ws.Kits` is never null and no
    // caller needs a null check on the hot path.
    public static readonly KitPalette Empty = new(System.Array.Empty<TerrainKitData>());

    public TerrainKitData[] Kits { get; }

    // Deduplicated defaultDetail groups, in palette order. Per-voxel
    // DetailGroup bytes are 1-BASED indices into this (0 = no detail).
    public DetailGroupData[] DetailGroups { get; }

    private readonly Dictionary<TerrainKitData, byte> _index = new();
    private readonly Dictionary<DetailGroupData, byte> _detailIndex = new();
    private readonly int[] _blockByKit = new int[MAX_KITS];
    private readonly byte[] _purposes;
    private readonly HashSet<int> _kitGroundBlocks = new();

    // Purpose comes off each KIT (TerrainKitData.purpose), not from how some
    // zone list happens to reference it. It used to be derived by walking a
    // WorldGenData's ZoneGens, which made it a side effect of zone PLACEMENT:
    // a kit no listed zone referenced was classified None, so `IsSurfaceKit`
    // answered false for it and the passes gated on that skipped it. Four kits
    // in this project were in exactly that state — appended to the palette for
    // the painter, referenced only by zone-gen files the default world does not
    // list — and it was harmless only by luck, because all four are surface
    // kits and None happens to fall through to the surface branch.
    public static KitPalette Build(KitPaletteData authored)
    {
        return new KitPalette(authored?.kits ?? System.Array.Empty<TerrainKitData>());
    }

    private KitPalette(TerrainKitData[] kits)
    {
        if (kits.Length > MAX_KITS)
        {
            GD.PushError($"KitPalette: {kits.Length} kits exceeds the {MAX_KITS} a TerrainId byte can "
                + "address; the excess renders as the default ground.");
        }
        Kits = kits;
        _purposes = new byte[kits.Length];

        int fallback = Blocks.GroundId;
        for (int i = 0; i < _blockByKit.Length; i++)
        {
            _blockByKit[i] = fallback;
        }

        var details = new List<DetailGroupData>();
        for (int i = 0; i < kits.Length && i < MAX_KITS; i++)
        {
            TerrainKitData kit = kits[i];
            if (kit == null)
            {
                continue;
            }
            _purposes[i] = (byte)kit.purpose;
            // A kit named twice would make SlotOf's answer depend on iteration
            // order, and it wastes a slot in a 256-wide channel.
            if (!_index.TryAdd(kit, (byte)i))
            {
                GD.PushWarning($"KitPalette: '{kit.ResourcePath}' appears in more than one slot "
                    + $"({_index[kit]} and {i}); the first wins.");
            }
            if (kit.block == null)
            {
                GD.PushWarning($"KitPalette: slot {i} ('{kit.ResourcePath}') names no block; "
                    + "using the default ground.");
            }
            else
            {
                _blockByKit[i] = kit.block.blockId;
                _kitGroundBlocks.Add(kit.block.blockId);
            }
            if (kit.defaultDetail != null && !_detailIndex.ContainsKey(kit.defaultDetail))
            {
                _detailIndex[kit.defaultDetail] = (byte)details.Count;
                details.Add(kit.defaultDetail);
            }
        }
        DetailGroups = details.ToArray();
    }

    // Slot for a kit, 0 when it has none. Slot 0 is a real kit, so a caller that
    // must distinguish "not in this palette" uses TryGetSlot.
    public byte SlotOf(TerrainKitData kit)
    {
        return kit != null && _index.TryGetValue(kit, out byte i) ? i : (byte)0;
    }

    // For authoring tools that stamp a chosen kit (the editor's Terrain brush):
    // false means the kit has no slot in THIS world, so the caller can warn
    // rather than silently paint slot 0.
    public bool TryGetSlot(TerrainKitData kit, out byte slot)
    {
        slot = 0;
        return kit != null && _index.TryGetValue(kit, out slot);
    }

    // Stored TerrainId byte -> its kit, for passes reading defaultDetail /
    // detailNoise* / forest* / tree scenes.
    public TerrainKitData KitAt(int terrainId)
    {
        return (uint)terrainId < (uint)Kits.Length ? Kits[terrainId] : null;
    }

    // The block a kit slot's ground is made of.
    public int BlockFor(int terrainId)
    {
        return (uint)terrainId < (uint)_blockByKit.Length ? _blockByKit[terrainId] : _blockByKit[0];
    }

    // Is this block SOME kit's ground in this world?
    //
    // The question a subscene stamp asks before re-texturing a voxel: kit ground
    // is a biome statement and the scene has no biome, so it adopts the one it
    // lands in, while anything else — a stone wall, a plank floor, cobbles, a
    // dirt path — is a deliberate material that survives the journey.
    //
    // Deliberately NOT BlockData.naturalGround, which answers "may the road pass
    // grade across this?" and is true of Road and Dirt — using it re-textured a
    // town square's paths into grass.
    public bool IsKitGround(int blockId)
    {
        return _kitGroundBlocks.Contains(blockId);
    }

    // Was this slot classified Surface at build time? Gates the passes that ask
    // "is this voxel walkable above-water ground?" — dirt overlay stamping, the
    // surface scatter noise pick, road suppression on the scatter pass.
    public bool IsSurfaceKit(int terrainId)
    {
        return (uint)terrainId < (uint)_purposes.Length
            && _purposes[terrainId] == (byte)EKitPurpose.Surface;
    }

    public bool IsCaveKit(int terrainId)
    {
        return (uint)terrainId < (uint)_purposes.Length
            && _purposes[terrainId] == (byte)EKitPurpose.Cave;
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
        var names = new string[Kits.Length];
        for (int i = 0; i < Kits.Length; i++)
        {
            names[i] = Kits[i]?.ResourcePath ?? "";
        }
        return names;
    }

    // The same, for the DETAIL palette. Recorded separately because it has the
    // same hazard through a different door: DetailGroup bytes are 1-based
    // indices into it, and it is DERIVED from the kits' defaultDetail — so it
    // can be reordered by an edit that leaves the kit palette untouched
    // (repointing one kit's defaultDetail), which the kit check would pass.
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
