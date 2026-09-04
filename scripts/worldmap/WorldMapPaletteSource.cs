using System;
using System.Collections.Generic;
using Godot;

// WHERE EVERY PAINTER PALETTE COMES FROM. One table, and it is the only place a
// palette is declared.
//
// Nothing is registered by hand. A palette names the directories it is made of
// (or the block catalog it filters) and every resource of the right type found
// there is offered, so adding a zone, a ground set, a prop set or a placeable
// entity is putting the file in the directory and nothing else. The step this
// replaces — "and now find which array on WorldMapData to append it to" — is
// the one that silently costs you the resource: a torch entry authored months
// ago was simply never added, and nothing anywhere reported it missing.
//
// Two kinds of palette, and the difference is the whole reason a ledger exists:
//
//   - INDEXED. A painted raster stores a slot number, so the slot a file
//     occupies is a wire format. Discovery APPENDS to the document's
//     WorldMapPalettes ledger and never reorders it; the ledger is what fixes a
//     slot to a file forever.
//   - FREE. Nothing stores an index — an EntityPlacement holds its entry by
//     reference, and a preset is a brush that is never written down — so the
//     list is just what is on disk in name order, with no ledger at all.
//
// Directories are scanned NON-RECURSIVELY, and that is load-bearing rather than
// incidental: `spawn_entries/mobs/` holds the composite entry an author places
// (goblin.tres, which offers all thirteen goblins as variants) while
// `spawn_entries/mobs/variants/` holds the leaves the generator's spawn lists
// name. Both are MobSpawnEntry, so nothing but the directory can tell them
// apart — which makes "which folder is it in" the authoring decision, visible
// in the file browser instead of buried in an array.
public sealed class WorldMapPaletteSource
{
    // Stable key into WorldMapPalettes. Persisted, so it never changes once a
    // document has been saved with it.
    public string Id;

    // What the tool row calls this palette.
    public string Label;

    // The resource type discovered. Subclasses count: SpawnEntryData finds
    // every MobSpawnEntry, ChestSpawnEntry and TorchSpawnEntry there is.
    public Type Type;

    // Directories scanned, non-recursively. Null for a catalog-backed palette.
    public string[] Roots;

    // Catalog-backed palettes filter the game's block list rather than a
    // directory: a block is authored into BlockCatalog, which is already the
    // single registry for the per-voxel byte, so a second copy of that list
    // under resources/ would be a registration step with nothing to add.
    public Func<BlockData, bool> Blocks;

    // Does a painted raster store this palette's index? Indexed palettes get a
    // ledger; free ones do not.
    public bool Indexed;

    private WorldMapPaletteSource(string id, string label, Type type, bool indexed,
        string[] roots = null, Func<BlockData, bool> blocks = null)
    {
        Id = id;
        Label = label;
        Type = type;
        Indexed = indexed;
        Roots = roots;
        Blocks = blocks;
    }

    private const string AUTHORING = "res://resources/data/world_authoring/";
    private const string SHARED = "res://resources/data/worlds/shared/";

    public const string Zones = "zones";
    public const string Regions = "regions";
    public const string GroundSets = "ground_sets";
    public const string PropLists = "prop_lists";
    public const string MobSets = "mob_sets";
    public const string WaterTypes = "water_types";
    public const string PavingBlocks = "paving_blocks";
    public const string Entities = "entities";
    public const string Presets = "presets";

    // ADD A PALETTE HERE. Nothing else in the painter needs to know it exists —
    // WorldMapState resolves whatever this table declares.
    public static readonly WorldMapPaletteSource[] Table =
    {
        new(Zones, "Zones", typeof(ZoneData), indexed: true,
            roots: new[] { AUTHORING + "zones/" }),

        new(Regions, "Regions", typeof(RegionData), indexed: true,
            roots: new[] { SHARED + "regions/" }),

        new(GroundSets, "Ground", typeof(GroundSetData), indexed: true,
            roots: new[] { AUTHORING + "ground_sets/" }),

        // ONE palette for both prop layers. What a list is FOR — a barrier you
        // cannot pass or one you can break — is which layer it was painted on,
        // not a property of the list, so splitting this in two would only stop
        // a boulder field being used as either.
        new(PropLists, "Props", typeof(PropListData), indexed: true,
            roots: new[] { AUTHORING + "prop_lists/" }),

        new(MobSets, "Mobs", typeof(SpawnSetData), indexed: true,
            roots: new[] { AUTHORING + "mob_sets/" }),

        // Every block the mesher draws as water, which is the same question
        // Blocks.IsWater asks — so a water type added later is paintable the
        // moment it is in the catalog. Slot 0 of the RASTER is still "whatever
        // the zone says"; these are the explicit overrides.
        new(WaterTypes, "Water", typeof(BlockData), indexed: true,
            blocks: b => b.render == EBlockRender.Water),

        // Anything solid with a top face. That excludes air and openings (not
        // solid) and the barrier (solid, but textureless — it is never meant to
        // be seen), and it needs no new authored flag to say so.
        new(PavingBlocks, "Paving", typeof(BlockData), indexed: true,
            blocks: b => b.solid && b.top != null && b.render != EBlockRender.Water),

        // FREE: EntityPlacement holds its entry by reference, so this list may
        // be reordered by a rename with no consequence at all.
        new(Entities, "Entities", typeof(SpawnEntryData), indexed: false,
            roots: new[]
            {
                AUTHORING + "spawn_entries/",
                AUTHORING + "spawn_entries/mobs/",
                SHARED + "spawn_entries/",
                SHARED + "spawn_entries/npcs/",
            }),

        // FREE: a preset is a composite brush stroke. It writes ground, props
        // and zone and is itself never recorded.
        new(Presets, "Presets", typeof(PaintPresetData), indexed: false,
            roots: new[] { AUTHORING + "presets/" }),
    };

    public static WorldMapPaletteSource Find(string id)
    {
        foreach (WorldMapPaletteSource source in Table)
        {
            if (source.Id == id)
            {
                return source;
            }
        }
        return null;
    }

    // Everything this palette currently offers, by resource path, in name order.
    // Order matters only for a FREE palette; an indexed one takes its order from
    // the ledger and uses this purely as the set of candidates.
    public string[] Discover()
    {
        if (Blocks != null)
        {
            var found = new List<string>();
            foreach (BlockData block in BlockCatalog.Active.blocks ?? Array.Empty<BlockData>())
            {
                if (block != null && !string.IsNullOrEmpty(block.ResourcePath) && Blocks(block))
                {
                    found.Add(block.ResourcePath);
                }
            }
            found.Sort(StringComparer.Ordinal);
            return found.ToArray();
        }
        return ResourceTypeIndex.In(Type, Roots);
    }

    // The palette, resolved to live resources.
    //
    // An INDEXED palette is the ledger in ledger order, extended with whatever
    // discovery found that it does not already list. A slot whose file is gone
    // resolves to null and KEEPS ITS INDEX — the columns painted with it are
    // still out there, so collapsing the hole would re-point every slot after
    // it. A FREE palette ignores the ledger entirely.
    public Resource[] Resolve(WorldMapPalettes palettes)
    {
        string[] paths = Discover();
        if (Indexed)
        {
            WorldMapPaletteLedger ledger = palettes.For(Id);
            var slots = new List<string>(ledger.slots ?? Array.Empty<string>());
            foreach (string path in paths)
            {
                if (!slots.Contains(path))
                {
                    slots.Add(path);
                }
            }
            ledger.slots = slots.ToArray();
            paths = ledger.slots;
        }
        var resolved = new Resource[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            // A serialized path table, not a hardcoded one: the ledger IS the
            // document's record of what slot i means, exactly as WorldFile's
            // ref table is for a baked world.
            resolved[i] = ResourceLoader.Exists(paths[i]) ? ResourceLoader.Load<Resource>(paths[i]) : null;
            if (resolved[i] == null)
            {
                GD.PushWarning($"WorldMapPaletteSource: {Id} slot {i} is missing ({paths[i]}); "
                    + "the slot is kept so the columns painted with it do not re-point.");
            }
        }
        return resolved;
    }

    // Resolve and narrow in one step. A slot that failed to load, or that loaded
    // as the wrong type, stays as a null hole rather than shifting its
    // neighbours.
    public static T[] Resolve<T>(string id, WorldMapPalettes palettes) where T : Resource
    {
        WorldMapPaletteSource source = Find(id);
        if (source == null)
        {
            GD.PushError($"WorldMapPaletteSource: no palette named '{id}'.");
            return Array.Empty<T>();
        }
        Resource[] loaded = source.Resolve(palettes);
        var typed = new T[loaded.Length];
        for (int i = 0; i < loaded.Length; i++)
        {
            typed[i] = loaded[i] as T;
        }
        return typed;
    }
}
