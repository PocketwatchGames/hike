using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

// Type-tagged binary serialization for EntitySimState subclasses. Tags are
// stable wire values — append new ones, never reuse old numbers, so old world
// files keep loading after new entity types are added.
public static class EntitySerializer
{
    private enum Tag : byte
    {
        Prop = 1,
        Mob = 2,
        Door = 3,
        Torch = 4,
        Chest = 5,
        Trap = 6,
        Signpost = 7,
        FireTrap = 8,
        BerryTree = 9,
        Loot = 10,
        Campfire = 11,
        KnowledgeStone = 12,
        Well = 13,
        ClimbableTree = 14,
        Boat = 15,
        BuriedSpot = 16,
        Tent = 17,
        Forge = 18,
        Fountain = 19,
        ForageSpawner = 20,
        SafetyZone = 21,
        Roof = 22,
        Marker = 23,
        Cactus = 24,
        Trapdoor = 25,
        Lever = 26,
        PathHint = 27,
        Waterfall = 28,
        CoiledRope = 29,
    }

    // How much of the Roof payload a stream carries. Containers map their own
    // file version onto one of these and pass it to ReadList; a reader that
    // says nothing gets the current layout.
    public const int ROOF_FORMAT_ORIGINAL = 0;
    public const int ROOF_FORMAT_BROKEN = 1;
    public const int ROOF_FORMAT_FORM = 2;
    public const int ROOF_FORMAT_CURRENT = ROOF_FORMAT_FORM;

    // Legacy PropType byte values for loot. PropSimState used to cover loot
    // before LootSimState was split out; old world files still carry Tag.Prop
    // with these PropType bytes and must round-trip through the legacy reader.
    // Both bytes now route to the same unified Loot path; the historical split
    // between auto/interact pickup is decided at run time from inventory state.
    private const byte LegacyPropTypeAutoLoot = 2;
    private const byte LegacyPropTypeLoot = 3;

    // String dictionary for the list currently being written / read. Named for
    // its original and dominant contents — resource paths — but any repeated
    // per-entity string belongs in it (the variant pool tag rides it too).
    //
    // Entity payloads reference scenes and resources by index into a table at
    // the head of their list rather than repeating the path string on every
    // entity — a chunk holding 40 trees drawn from 6 scenes stores 6 paths, not
    // 40, and the path was roughly two thirds of a prop record.
    //
    // A list carries its own table by default, which keeps a standalone blob
    // (a subscene, an in-memory clone) self-describing. A container holding many
    // lists should instead open a SHARED table via BeginSharedWrite and store it
    // once — measured on the test world, per-list tables cost 203KB against
    // 411KB of original path bytes, because most chunks hold few entities and
    // the same handful of paths recur in every one of them. That overhead grows
    // with chunk count, so world files hoist one table into the header.
    // Addressability is unaffected: the header is already read up front, so any
    // chunk can still be seeked to and decoded on its own.
    //
    // [ThreadStatic] because worldgen and .hike loading both run off the main
    // thread; the alternative was threading a table argument through ~30 entity
    // cases and every nested list writer. Saved and restored around each list so
    // nesting can't clobber an outer table.
    [ThreadStatic] private static WritePathTable _writePaths;
    [ThreadStatic] private static ReadPathTable _readPaths;
    // True while a shared table is installed, meaning WriteList must not emit
    // its own — the container already wrote it.
    [ThreadStatic] private static bool _sharedWrite;
    // Which trailing fields the Roof payload being read carries — the roof is
    // the one entity whose payload has grown since it shipped. A per-payload
    // version gate can't ride ReadList's signature the way hasRotation does
    // (that one is consumed in ReadOne), so it goes here with the other
    // per-list read state.
    [ThreadStatic] private static int _roofFormat;
    // Set while reading a stream written before every resource reference moved
    // into the ref table (pre-v9 subscenes), which spelled some of them out as
    // bare path strings instead.
    [ThreadStatic] private static bool _legacyPathRefs;

    // How one ref-table slot stores its resource.
    private enum RefKind : byte
    {
        // A res:// path, re-resolved with GD.Load on read.
        Path = 0,
        // The resource's VALUE — the type to rebuild it as and its stored
        // properties. For a resource no shipped build could resolve a path to.
        Inline = 1,
    }

    public sealed class WritePathTable
    {
        public readonly List<string> Paths = new List<string>();
        // Parallel to Paths: the resource a slot stores BY VALUE, null where the
        // slot stores a path. Grows while WriteTable runs, because encoding one
        // inline resource interns whatever it references.
        public readonly List<Resource> Inline = new List<Resource>();
        // The document whose sub-resources are BAKE INPUTS rather than shipped
        // assets — the world-map painter's placements.tres, where a placement's
        // forked entry and anything that entry authors inline both live.
        public readonly string AuthoringDocument;
        private readonly Dictionary<string, int> _indices = new Dictionary<string, int>();
        private readonly Dictionary<ulong, int> _inlineIndices = new Dictionary<ulong, int>();

        public WritePathTable(string authoringDocument = null)
        {
            AuthoringDocument = authoringDocument ?? "";
        }

        public int Intern(string path)
        {
            path ??= "";
            if (_indices.TryGetValue(path, out int existing))
            {
                return existing;
            }
            int index = Paths.Count;
            Paths.Add(path);
            Inline.Add(null);
            _indices[path] = index;
            return index;
        }

        // A resource reference. Stored as a path when a shipped build can resolve
        // that path, and BY VALUE when it cannot — an in-memory resource, or a
        // sub-resource of the authoring document the world is baked from. Keyed
        // on identity, so two placements sharing one fork share one slot.
        public int Intern(Resource resource)
        {
            if (resource == null)
            {
                return Intern("");
            }
            if (IsShippable(resource.ResourcePath))
            {
                return Intern(resource.ResourcePath);
            }
            ulong id = resource.GetInstanceId();
            if (_inlineIndices.TryGetValue(id, out int existing))
            {
                return existing;
            }
            int index = Paths.Count;
            Paths.Add("");
            Inline.Add(resource);
            _inlineIndices[id] = index;
            return index;
        }

        // A "<file>::<id>" sub-resource resolves as long as <file> ships — true
        // of every authored .tres, and the reason such references are still
        // stored by path. It is NOT true of the authoring document: everything
        // else under a painted world's map/ folder is a bake input excluded from
        // an export, and a reference into one loaded as a silent null.
        private bool IsShippable(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            if (AuthoringDocument.Length == 0)
            {
                return true;
            }
            int sub = path.IndexOf("::", StringComparison.Ordinal);
            return sub <= 0 || string.CompareOrdinal(path.Substring(0, sub), AuthoringDocument) != 0;
        }
    }

    // One resource stored by value: the C# type to rebuild it as, and its stored
    // properties, each either a packed Variant or another table slot.
    internal sealed class InlineRecord
    {
        public string TypeName;
        public string[] Names;
        public byte[][] Blobs;
        // -1 where the field is a Blob, else the slot the field references.
        public int[] Refs;
    }

    public sealed class ReadPathTable
    {
        public readonly string[] Paths;
        // One GD.Load per distinct path instead of one per entity referencing it.
        // Materialized inline slots land here too, for the same reason.
        public readonly Resource[] Loaded;
        // Parallel to Paths: the value of a slot stored inline, null elsewhere.
        internal readonly InlineRecord[] Inline;

        internal ReadPathTable(string[] paths, InlineRecord[] inline = null)
        {
            Paths = paths;
            Loaded = new Resource[paths.Length];
            Inline = inline ?? new InlineRecord[paths.Length];
        }
    }

    // Installs a table spanning every list written until EndSharedWrite. The
    // caller writes the returned table with WriteTable once all its lists are
    // serialized — which means it must buffer them, since interning only
    // finishes when the last list does.
    public static WritePathTable BeginSharedWrite(string authoringDocument = null)
    {
        _writePaths = new WritePathTable(authoringDocument);
        _sharedWrite = true;
        return _writePaths;
    }

    public static void EndSharedWrite()
    {
        _writePaths = null;
        _sharedWrite = false;
    }

    // Buffered, because encoding an inline resource interns the resources IT
    // references and so appends further slots — the count is only final once the
    // last entry is out.
    public static void WriteTable(BinaryWriter w, WritePathTable table)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            for (int i = 0; i < table.Paths.Count; i++)
            {
                Resource inline = table.Inline[i];
                if (inline == null)
                {
                    bw.Write((byte)RefKind.Path);
                    bw.Write(table.Paths[i]);
                    continue;
                }
                bw.Write((byte)RefKind.Inline);
                WriteInline(bw, table, inline);
            }
        }
        w.Write7BitEncodedInt(table.Paths.Count);
        w.Write(ms.ToArray());
    }

    // `tagged` false reads a table written before a slot could hold a value -
    // every entry is a bare path string.
    public static ReadPathTable ReadTable(BinaryReader r, bool tagged = true)
    {
        int pathCount = r.Read7BitEncodedInt();
        var paths = new string[pathCount];
        var inline = new InlineRecord[pathCount];
        for (int i = 0; i < pathCount; i++)
        {
            if (tagged && (RefKind)r.ReadByte() == RefKind.Inline)
            {
                paths[i] = "";
                inline[i] = ReadInline(r);
                continue;
            }
            paths[i] = r.ReadString();
        }
        return new ReadPathTable(paths, inline);
    }

    public static void WriteList(BinaryWriter w, IReadOnlyList<EntitySimState> entities)
    {
        if (_sharedWrite)
        {
            // The container owns the table; entities go straight out.
            WriteEntities(w, entities);
            return;
        }

        // Standalone list: entities are serialized to a buffer first because the
        // table they populate has to be written ahead of them.
        WritePathTable outer = _writePaths;
        var table = new WritePathTable(outer?.AuthoringDocument);
        _writePaths = table;
        byte[] payload;
        try
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteEntities(bw, entities);
            }
            payload = ms.ToArray();
        }
        finally
        {
            _writePaths = outer;
        }

        WriteTable(w, table);
        w.Write(payload);
    }

    private static void WriteEntities(BinaryWriter w, IReadOnlyList<EntitySimState> entities)
    {
        int count = entities?.Count ?? 0;
        w.Write((uint)count);
        for (int i = 0; i < count; i++)
        {
            WriteOne(w, entities[i]);
        }
    }

    // `shared` is the container's table when the list was written under
    // BeginSharedWrite; null means the list carries its own.
    //
    // `hasRotation` false reads a list written before RotationY became a common
    // trailing field — the only such files left are pre-v3 subscenes, which load
    // with every entity at zero facing. `hasTag` false likewise reads one written
    // before the variant pool tag joined it (pre-v6 subscenes), which load
    // untagged. Both are subscene-only: the world file demands an exact version
    // match, so a stale .hike is rejected rather than read compatibly.
    // Deep-copies entities through a write/read round-trip. Clone semantics stay
    // aligned with the disk format for free — a field added to an entity
    // propagates here without anyone remembering to copy it. The clones carry no
    // RuntimeNode; whoever files them is responsible for spawning.
    public static List<EntitySimState> CloneList(IReadOnlyList<EntitySimState> source)
    {
        if (source == null || source.Count == 0)
        {
            return new List<EntitySimState>();
        }

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            WriteList(bw, source);
        }
        ms.Position = 0;
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        return ReadList(br);
    }

    public static List<EntitySimState> ReadList(BinaryReader r, ReadPathTable shared = null, bool hasRotation = true, int roofFormat = ROOF_FORMAT_CURRENT, bool hasTag = true, bool tableRefs = true, bool hasScale = true)
    {
        ReadPathTable outer = _readPaths;
        int outerRoofFormat = _roofFormat;
        bool outerLegacyRefs = _legacyPathRefs;
        _legacyPathRefs = !tableRefs;
        _readPaths = shared ?? ReadTable(r, tagged: tableRefs);
        _roofFormat = roofFormat;
        try
        {
            uint count = r.ReadUInt32();
            var list = new List<EntitySimState>((int)count);
            for (uint i = 0; i < count; i++)
            {
                list.Add(hasRotation ? ReadOne(r, hasTag, hasScale) : ReadPayload(r));
            }
            return list;
        }
        finally
        {
            _readPaths = outer;
            _roofFormat = outerRoofFormat;
            _legacyPathRefs = outerLegacyRefs;
        }
    }

    // Tag + per-kind payload, then RotationY and the variant pool tag as common
    // trailing fields — every entity carries both now, so writing them once here
    // beats threading them through 21 payloads. Trailing rather than leading
    // because the payload is what constructs the state; ReadOne assigns them
    // afterwards. The pool tag goes through the string table, so the common case
    // (a scene reusing a handful of pool names) costs one byte per entity.
    private static void WriteOne(BinaryWriter w, EntitySimState e)
    {
        WritePayload(w, e);
        w.Write(e.RotationY);
        WriteInternedString(w, e.Tag);
        w.Write(e.Scale);
    }

    private static void WritePayload(BinaryWriter w, EntitySimState e)
    {
        switch (e)
        {
            case PropSimState prop:
                w.Write((byte)Tag.Prop);
                WriteVec3(w, prop.WorldPosition);
                WriteScene(w, prop.Scene);
                w.Write((byte)prop.Type);
                // Legacy "PickedUp" byte in the Tag.Prop payload. Tree and
                // Foliage never pick up; write false to keep the wire shape
                // unchanged so existing .hike files keep loading.
                w.Write(false);
                break;

            case LootSimState loot:
                w.Write((byte)Tag.Loot);
                WriteVec3(w, loot.WorldPosition);
                WriteResource(w, loot.Data);
                w.Write(loot.PickedUp);
                break;

            case MobSimState mob:
                w.Write((byte)Tag.Mob);
                WriteVec3(w, mob.WorldPosition);
                WriteScene(w, mob.Scene);
                WriteResource(w, mob.MobData);
                // Species variant ref (bestiary identity + recolor/loot/modifier
                // source). May be null for editor-placed mobs built from a bare
                // MobData. Adding this field changed the Mob wire layout — old
                // .hike files predating it must be re-exported.
                WriteResource(w, mob.Species);
                WriteVec3(w, mob.SpawnPosition);
                w.Write(mob.SpawnRotationY);
                w.Write(mob.Alive);
                w.Write(mob.Burrowed);
                w.Write(mob.Burrowing);
                w.Write(mob.BurrowTimeMs);
                w.Write(mob.MaxHealth);
                w.Write(mob.Health);
                w.Write(mob.Armor);
                w.Write(mob.PerceptionTargets[0].perception);
                w.Write(mob.PerceptionTargets[0].triggered);
                w.Write(mob.PlayerPerception);
                w.Write(mob.MemoryTimeMs);
                w.Write((byte)mob.DiscoveryState);
                w.Write(mob.InitialBehavior != null ? mob.InitialBehavior.ToString() : "");
                w.Write((byte)mob.SpawnConditions);
                WriteResource(w, mob.Language);
                // Merchant / loyalty / conversation state — persisted so a
                // villager's per-instance stock, accumulated loyalty, and
                // remaining gift rewards survive save/load and chunk eviction.
                w.Write(mob.WillTrade);
                w.Write(mob.Loyalty);
                WriteResource(w, mob.Conversation);
                int invCount = mob.Inventory?.Count ?? 0;
                w.Write(invCount);
                for (int i = 0; i < invCount; i++)
                {
                    MobInventoryItem entry = mob.Inventory[i];
                    WriteItemState(w, entry?.item);
                    w.Write(entry?.loyaltyCost ?? 0f);
                    w.Write(entry?.secret ?? false);
                }
                int giftCount = mob.LoyaltyGifts?.Count ?? 0;
                w.Write(giftCount);
                for (int i = 0; i < giftCount; i++)
                {
                    WriteResource(w, mob.LoyaltyGifts[i]);
                }
                int giftCountsCount = mob.GiftCounts?.Count ?? 0;
                w.Write(giftCountsCount);
                if (mob.GiftCounts != null)
                {
                    foreach (KeyValuePair<ItemData, int> kvp in mob.GiftCounts)
                    {
                        WriteResource(w, kvp.Key);
                        w.Write(kvp.Value);
                    }
                }
                // Elite flag (signature effect + badge ride StatusEffects / Badge
                // below). Persisted so a reloaded elite keeps its size + crown.
                w.Write(mob.Elite);
                // Companion state — Tamed flips the mob to the player's side
                // (effective team Friendly, so the player can't friendly-fire
                // it) and re-registers it as the active companion on spawn;
                // StayCommanded is its hold/follow toggle. Persisted so a tamed
                // pet survives world-file save/load: a fresh WorldGen spawns the
                // starter companion already tamed, but a reloaded world has to
                // restore the flag or the dog comes back wild (huntable, not
                // following).
                w.Write(mob.Tamed);
                w.Write(mob.StayCommanded);
                // Per-instance overrides: palette recolor (MobDescriptor) + spawn
                // weapon loadout (MobSpawnEntry). Resource refs, may be null —
                // persisted so a reloaded variant keeps its look and equipment.
                WriteResource(w, mob.Palette);
                WriteWeaponList(w, mob.Weapons);
                // Per-instance descriptor status effects (MobDescriptor) — a
                // buff/aura channel applied regardless of Elite. Resource-ref
                // list, may be empty.
                WriteStatusEffectList(w, mob.StatusEffects);
                // HUD badge icon (MobDescriptor.badge), resource ref, may be null.
                WriteResource(w, mob.Badge);
                // Per-elite crown scene override (EliteMobDescriptor.crownScene),
                // scene ref, may be null (then SimData.EliteCrownScene is used).
                WriteScene(w, mob.EliteCrownScene);
                // Death loot (MobSimState.Loot), stamped from SpeciesData.loot
                // at spawn. Mob loot carries no permanent mods, so only item path
                // + count are persisted (mirrors the chest-loot recipe above). If
                // modded mob loot is ever added, write the descriptor's
                // statusEffects here too.
                int mobLootCount = mob.Loot?.Count ?? 0;
                w.Write(mobLootCount);
                for (int i = 0; i < mobLootCount; i++)
                {
                    ItemCount entry = mob.Loot[i];
                    WriteResource(w, entry?.descriptor?.item);
                    w.Write(entry?.count ?? 0);
                }
                // Per-instance item-preference overrides (NpcSpawnEntry):
                // resource-ref list of standalone ItemTagPreference .tres, may
                // be empty. Appended last so older world files still parse.
                int prefCount = mob.ItemPreferences?.Count ?? 0;
                w.Write(prefCount);
                for (int i = 0; i < prefCount; i++)
                {
                    WriteResource(w, mob.ItemPreferences[i]);
                }
                // Per-individual outfit override (NpcSpawnEntry.Outfit): visible-
                // mesh names for a modular humanoid. String array, may be empty.
                // Appended last so older world files still parse.
                int outfitCount = mob.Outfit?.Length ?? 0;
                w.Write(outfitCount);
                for (int i = 0; i < outfitCount; i++)
                {
                    w.Write(mob.Outfit[i] ?? string.Empty);
                }
                // Per-individual idle-pose override (NpcSpawnEntry.IdleAnimation):
                // a clip name, may be empty. Appended last so older world files
                // still parse.
                w.Write(mob.IdleAnimation != null ? mob.IdleAnimation.ToString() : "");
                // Recruitable-NPC party-member template (NpcSpawnEntry
                // .recruitTemplate): a standalone PlayerState .tres, resource
                // ref, may be null. Appended last so older world files still parse.
                WriteResource(w, mob.RecruitTemplate);
                // Difficulty tier (MobDescriptor.level + worldgen level field).
                // Scales health/armor/damage by the per-level curve, so it must
                // persist or a reloaded mob would revert to base stats. Appended last so older
                // world files still parse.
                w.Write(mob.Level);
                break;

            case DoorSimState door:
                w.Write((byte)Tag.Door);
                WriteVec3(w, door.WorldPosition);
                WriteScene(w, door.Scene);
                w.Write(door.Active);
                break;

            case BoatSimState boat:
                w.Write((byte)Tag.Boat);
                WriteVec3(w, boat.WorldPosition);
                WriteScene(w, boat.Scene);
                break;

            case TrapdoorSimState trapdoor:
                w.Write((byte)Tag.Trapdoor);
                WriteVec3(w, trapdoor.WorldPosition);
                WriteScene(w, trapdoor.Scene);
                w.Write(trapdoor.Open);
                w.Write(trapdoor.LinkTag ?? string.Empty);
                break;

            case CoiledRopeSimState rope:
                w.Write((byte)Tag.CoiledRope);
                WriteVec3(w, rope.WorldPosition);
                WriteScene(w, rope.Scene);
                w.Write(rope.Deployed);
                break;

            case LeverSimState lever:
                w.Write((byte)Tag.Lever);
                WriteVec3(w, lever.WorldPosition);
                WriteScene(w, lever.Scene);
                w.Write(lever.TargetLinkTag ?? string.Empty);
                w.Write(lever.On);
                break;

            case CampfireSimState campfire:
                w.Write((byte)Tag.Campfire);
                WriteVec3(w, campfire.WorldPosition);
                WriteScene(w, campfire.Scene);
                w.Write(campfire.Active);
                // Transient cooking state (CampfireSlots) is not serialized —
                // experimentation slot contents reset on world reload.
                // Persisting them would need stable ItemData refs first.
                break;

            case TorchSimState torch:
                w.Write((byte)Tag.Torch);
                WriteVec3(w, torch.WorldPosition);
                WriteScene(w, torch.Scene);
                w.Write(torch.Active);
                w.Write(torch.AutoLightAtNight);
                break;

            case ChestSimState chest:
                w.Write((byte)Tag.Chest);
                WriteVec3(w, chest.WorldPosition);
                WriteScene(w, chest.Scene);
                w.Write(chest.Active);
                w.Write((byte)chest.SpawnConditions);
                int chestLootCount = chest.LootItems?.Length ?? 0;
                w.Write(chestLootCount);
                for (int i = 0; i < chestLootCount; i++)
                {
                    ItemCount entry = chest.LootItems[i];
                    // Chest loot carries no permanent mods (ItemCountRange, the
                    // only producer, authors none), so only the item path + count
                    // are persisted. If modded chest loot is ever added, the
                    // descriptor's statusEffects must be written here too.
                    WriteResource(w, entry?.descriptor?.item);
                    w.Write(entry?.count ?? 0);
                }
                // Persistent slot contents (stash-style chests). Distinct
                // from the LootItems ejection recipe above.
                WriteItemList(w, chest.Contents);
                break;

            case TrapSimState trap:
                w.Write((byte)Tag.Trap);
                WriteVec3(w, trap.WorldPosition);
                WriteScene(w, trap.Scene);
                w.Write(trap.Disarmed);
                break;

            case SignpostSimState signpost:
                w.Write((byte)Tag.Signpost);
                WriteVec3(w, signpost.WorldPosition);
                WriteScene(w, signpost.Scene);
                WriteResource(w, signpost.Language);
                w.Write(signpost.Text ?? string.Empty);
                break;

            case KnowledgeStoneSimState stone:
                w.Write((byte)Tag.KnowledgeStone);
                WriteVec3(w, stone.WorldPosition);
                WriteScene(w, stone.Scene);
                // Wire shape: inscription language + a flat language-component
                // bitset that the loader synthesizes back into a single
                // LanguageTeachable concept. The full polymorphic concept
                // array isn't persisted here — authored stones with non-
                // language concepts (recipes / regions) lean on the scene's
                // _concepts override path and don't ride this slot. When the
                // editor lands and needs to author arbitrary concepts on
                // placed stones, extend the wire format with a typed concept
                // list and bump the format version.
                LanguageData wireLanguage = stone.InscriptionLanguage;
                ELanguageComponents wireComponents = ELanguageComponents.None;
                if (stone.Concepts != null)
                {
                    for (int i = 0; i < stone.Concepts.Count; i++)
                    {
                        if (stone.Concepts[i] is LanguageTeachable lt && lt.language != null)
                        {
                            wireLanguage ??= lt.language;
                            if (lt.language == wireLanguage) { wireComponents |= lt.components; }
                        }
                    }
                }
                WriteResource(w, wireLanguage);
                w.Write((int)wireComponents);
                w.Write(stone.Text ?? string.Empty);
                break;

            case FireTrapSimState fire:
                w.Write((byte)Tag.FireTrap);
                WriteVec3(w, fire.WorldPosition);
                WriteScene(w, fire.Scene);
                w.Write(fire.PhaseOffsetSeconds);
                break;

            case CactusSimState cactus:
                w.Write((byte)Tag.Cactus);
                WriteVec3(w, cactus.WorldPosition);
                WriteScene(w, cactus.Scene);
                break;

            case WellSimState well:
                w.Write((byte)Tag.Well);
                WriteVec3(w, well.WorldPosition);
                WriteScene(w, well.Scene);
                break;

            case BerryTreeSimState berry:
                w.Write((byte)Tag.BerryTree);
                WriteVec3(w, berry.WorldPosition);
                WriteScene(w, berry.Scene);
                w.Write(berry.BerryCount);
                w.Write(berry.RegrowDay);
                break;

            case ClimbableTreeSimState climbTree:
                w.Write((byte)Tag.ClimbableTree);
                WriteVec3(w, climbTree.WorldPosition);
                WriteScene(w, climbTree.Scene);
                break;

            case BuriedSpotSimState buried:
                w.Write((byte)Tag.BuriedSpot);
                WriteVec3(w, buried.WorldPosition);
                WriteScene(w, buried.Scene);
                WriteResource(w, buried.Data);
                w.Write(buried.Excavated);
                w.Write(buried.TreasureName ?? "");
                break;

            case TentSimState tent:
                w.Write((byte)Tag.Tent);
                WriteVec3(w, tent.WorldPosition);
                WriteScene(w, tent.Scene);
                break;

            case ForgeSimState forge:
                w.Write((byte)Tag.Forge);
                WriteVec3(w, forge.WorldPosition);
                WriteScene(w, forge.Scene);
                w.Write(forge.Level);
                w.Write(forge.RegrowDay);
                w.Write((int)forge.Slot);
                break;

            case FountainSimState fountain:
                w.Write((byte)Tag.Fountain);
                WriteVec3(w, fountain.WorldPosition);
                WriteScene(w, fountain.Scene);
                w.Write(fountain.RegrowDay);
                break;

            case ForageSpawnerSimState forage:
                w.Write((byte)Tag.ForageSpawner);
                WriteVec3(w, forage.WorldPosition);
                WriteScene(w, forage.Scene);
                WriteResource(w, forage.Item);
                w.Write(forage.RegrowDays);
                w.Write(forage.RegrowDay);
                break;

            case SafetyZoneSimState safety:
                w.Write((byte)Tag.SafetyZone);
                WriteVec3(w, safety.WorldPosition);
                WriteScene(w, safety.Scene);
                break;

            // No scene ref: a roof has no authored mesh, only a style to skin
            // the geometry rebuilt from these dimensions at spawn.
            case RoofSimState roof:
                w.Write((byte)Tag.Roof);
                WriteVec3(w, roof.WorldPosition);
                WriteResource(w, roof.Style);
                w.Write(roof.SizeX);
                w.Write(roof.SizeZ);
                w.Write((byte)roof.SeamAxis);
                w.Write(roof.SlopeDegrees);
                w.Write(roof.Broken);
                w.Write((byte)roof.Form);
                break;

            // The pool name it belongs to rides the common trailing Tag, so a
            // marker's payload is just where it stands (plus the editor's pin
            // scene, which is all that ever draws it).
            case MarkerSimState marker:
                w.Write((byte)Tag.Marker);
                WriteVec3(w, marker.WorldPosition);
                WriteScene(w, marker.Scene);
                break;

            // Same shape as a marker, and for the same reason: the hint name
            // rides the common trailing Tag, so the payload is just where the
            // path is meant to touch (plus the editor's pin scene).
            case PathHintSimState hint:
                w.Write((byte)Tag.PathHint);
                WriteVec3(w, hint.WorldPosition);
                WriteScene(w, hint.Scene);
                break;

            // No scene ref and no style ref: a waterfall is the edge its water
            // pours over, swept into a sheet at spawn, and everything else about
            // how it looks comes off SimData. The edge has to be stored because
            // it can't be re-derived — the drop is air, which is what a river
            // ending at a cliff looks like too.
            case WaterfallSimState waterfall:
                w.Write((byte)Tag.Waterfall);
                WriteVec3(w, waterfall.WorldPosition);
                w.Write(waterfall.TopY);
                w.Write(waterfall.BottomY);
                w.Write(waterfall.Lips.Length);
                foreach (WaterfallLip lip in waterfall.Lips)
                {
                    w.Write(lip.X);
                    w.Write(lip.Z);
                    w.Write((sbyte)lip.DirX);
                    w.Write((sbyte)lip.DirZ);
                }
                break;

            default:
                throw new InvalidOperationException($"EntitySerializer has no writer for {e.GetType().Name}");
        }
    }

    // Mirrors WriteOne: payload first, then the common trailing rotation. It has
    // to be assigned after the payload because the payload is what constructs
    // the state. A payload that returns null (an unknown tag) still consumes it,
    // so the stream stays aligned.
    private static EntitySimState ReadOne(BinaryReader r, bool hasTag, bool hasScale)
    {
        EntitySimState state = ReadPayload(r);
        float rotationY = r.ReadSingle();
        string tag = hasTag ? ReadInternedString(r) : "";
        float scale = hasScale ? r.ReadSingle() : 1f;
        if (state != null)
        {
            state.RotationY = rotationY;
            state.Tag = tag;
            state.Scale = scale;
        }
        return state;
    }

    private static EntitySimState ReadPayload(BinaryReader r)
    {
        var tag = (Tag)r.ReadByte();
        switch (tag)
        {
            case Tag.Prop:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                byte typeByte = r.ReadByte();
                bool pickedUp = r.ReadBoolean();
                // Legacy migration: pre-split PropSimState covered loot too.
                // Old world files with the retired AutoLoot/Loot PropType
                // bytes are upgraded to LootSimState on read; new code only
                // ever writes Tree/Foliage under Tag.Prop. Data is null —
                // Loot's runtime pickup probe handles the null-Data path the
                // same way it handled the legacy AutoLoot case (no item to
                // deposit, just despawn).
                if (typeByte == LegacyPropTypeAutoLoot || typeByte == LegacyPropTypeLoot)
                {
                    var loot = new LootSimState(pos, data: null);
                    loot.PickedUp = pickedUp;
                    return loot;
                }
                return new PropSimState((PropType)typeByte, pos, scene);
            }
            case Tag.Loot:
            {
                Vector3 pos = ReadVec3(r);
                var data = ReadResource<ItemData>(r);
                bool pickedUp = r.ReadBoolean();
                var loot = new LootSimState(pos, data);
                loot.PickedUp = pickedUp;
                return loot;
            }
            case Tag.Mob:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                var mobData = ReadResource<MobData>(r);
                var species = ReadResource<SpeciesData>(r);
                Vector3 spawnPos = ReadVec3(r);
                float spawnRotationY = r.ReadSingle();
                bool alive = r.ReadBoolean();
                bool burrowed = r.ReadBoolean();
                bool burrowing = r.ReadBoolean();
                ulong burrowTimeMs = r.ReadUInt64();
                float maxHealth = r.ReadSingle();
                float health = r.ReadSingle();
                float armor = r.ReadSingle();
                float targetPerception = r.ReadSingle();
                bool targetTriggered = r.ReadBoolean();
                float playerPerception = r.ReadSingle();
                ulong memoryTimeMs = r.ReadUInt64();
                var perceptionState = (EPlayerPerceptionState)r.ReadByte();
                string initialBehavior = r.ReadString();
                var spawnConditions = (ESpawnConditions)r.ReadByte();
                var language = ReadResource<LanguageData>(r);
                bool willTrade = r.ReadBoolean();
                float loyalty = r.ReadSingle();
                var conversation = ReadResource<ConversationData>(r);
                int invCount = r.ReadInt32();
                var inventory = new List<MobInventoryItem>(invCount);
                for (int i = 0; i < invCount; i++)
                {
                    ItemState invItem = ReadItemState(r);
                    float loyaltyCost = r.ReadSingle();
                    bool secret = r.ReadBoolean();
                    inventory.Add(new MobInventoryItem
                    {
                        item = invItem,
                        loyaltyCost = loyaltyCost,
                        secret = secret,
                    });
                }
                int giftCount = r.ReadInt32();
                var loyaltyGifts = new List<LoyaltyGift>(giftCount);
                for (int i = 0; i < giftCount; i++)
                {
                    loyaltyGifts.Add(ReadResource<LoyaltyGift>(r));
                }
                int giftCountsCount = r.ReadInt32();
                var giftCounts = new Dictionary<ItemData, int>(giftCountsCount);
                for (int i = 0; i < giftCountsCount; i++)
                {
                    var key = ReadResource<ItemData>(r);
                    int val = r.ReadInt32();
                    if (key != null)
                    {
                        giftCounts[key] = val;
                    }
                }
                bool elite = r.ReadBoolean();
                bool tamed = r.ReadBoolean();
                bool stayCommanded = r.ReadBoolean();
                var palette = ReadResource<MobPalette>(r);
                var weapons = ReadWeaponList(r);
                var statusEffects = ReadStatusEffectList(r);
                var badge = ReadResource<Texture2D>(r);
                var eliteCrownScene = ReadScene(r);
                int mobLootCount = r.ReadInt32();
                Godot.Collections.Array<ItemCount> loot = mobLootCount > 0 ? new Godot.Collections.Array<ItemCount>() : null;
                for (int i = 0; i < mobLootCount; i++)
                {
                    ItemData item = ReadResource<ItemData>(r);
                    int count = r.ReadInt32();
                    loot.Add(new ItemCount { descriptor = new ItemDescriptor { item = item }, count = count });
                }
                int prefCount = r.ReadInt32();
                var itemPreferences = new List<ItemTagPreference>(prefCount);
                for (int i = 0; i < prefCount; i++)
                {
                    itemPreferences.Add(ReadResource<ItemTagPreference>(r));
                }
                int outfitCount = r.ReadInt32();
                var outfit = new string[outfitCount];
                for (int i = 0; i < outfitCount; i++)
                {
                    outfit[i] = r.ReadString();
                }
                string idleAnimation = r.ReadString();
                var recruitTemplate = ReadResource<PlayerState>(r);

                // Live facing arrives in the common trailing field (ReadOne
                // assigns it); only the authored spawn facing is in the payload.
                var mob = new MobSimState(pos, rotationY: 0f, spawnPos, spawnRotationY, scene, mobData);
                mob.Species = species;
                mob.RestoredFromSave = true;
                mob.Language = language;
                if (!string.IsNullOrEmpty(initialBehavior))
                {
                    mob.InitialBehavior = initialBehavior;
                }
                mob.SpawnConditions = spawnConditions;
                mob.Alive = alive;
                mob.Burrowed = burrowed;
                mob.Burrowing = burrowing;
                mob.BurrowTimeMs = burrowTimeMs;
                mob.MaxHealth = maxHealth;
                mob.Health = health;
                mob.Armor = armor;
                mob.PerceptionTargets[0].perception = targetPerception;
                mob.PerceptionTargets[0].triggered = targetTriggered;
                mob.PlayerPerception = playerPerception;
                mob.MemoryTimeMs = memoryTimeMs;
                mob.DiscoveryState = perceptionState;
                mob.WillTrade = willTrade;
                mob.Loyalty = loyalty;
                mob.Conversation = conversation;
                mob.Inventory = inventory;
                mob.LoyaltyGifts = loyaltyGifts;
                mob.GiftCounts = giftCounts;
                mob.Elite = elite;
                mob.Tamed = tamed;
                mob.StayCommanded = stayCommanded;
                mob.Palette = palette;
                mob.Weapons = weapons;
                mob.StatusEffects = statusEffects;
                mob.Badge = badge;
                mob.EliteCrownScene = eliteCrownScene;
                mob.Loot = loot;
                mob.ItemPreferences = itemPreferences;
                mob.Outfit = outfit;
                if (!string.IsNullOrEmpty(idleAnimation))
                {
                    mob.IdleAnimation = idleAnimation;
                }
                mob.RecruitTemplate = recruitTemplate;
                mob.Level = r.ReadInt32();
                return mob;
            }
            case Tag.Door:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                bool active = r.ReadBoolean();
                var door = new DoorSimState(pos, rotationY: 0f, scene);
                door.Active = active;
                return door;
            }
            case Tag.Boat:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                return new BoatSimState(pos, rotationY: 0f, scene);
            }
            case Tag.Trapdoor:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                bool open = r.ReadBoolean();
                string linkTag = r.ReadString();
                var trapdoor = new TrapdoorSimState(pos, rotationY: 0f, scene);
                trapdoor.Open = open;
                trapdoor.LinkTag = linkTag;
                return trapdoor;
            }
            case Tag.CoiledRope:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                bool deployed = r.ReadBoolean();
                var rope = new CoiledRopeSimState(pos, rotationY: 0f, scene);
                rope.Deployed = deployed;
                return rope;
            }
            case Tag.Lever:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                string targetLinkTag = r.ReadString();
                bool on = r.ReadBoolean();
                var lever = new LeverSimState(pos, rotationY: 0f, scene);
                lever.TargetLinkTag = targetLinkTag;
                lever.On = on;
                return lever;
            }
            case Tag.Torch:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                bool active = r.ReadBoolean();
                bool autoLightAtNight = r.ReadBoolean();
                var torch = new TorchSimState(pos, scene);
                torch.Active = active;
                torch.AutoLightAtNight = autoLightAtNight;
                return torch;
            }
            case Tag.Campfire:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                bool active = r.ReadBoolean();
                var campfire = new CampfireSimState(pos, scene);
                campfire.Active = active;
                // Per-type constant (not serialized) so disk-loaded campfires
                // still project their mob-avoidance hazard zone.
                campfire.HazardRadius = CampfireSimState.DefaultHazardRadius;
                return campfire;
            }
            case Tag.Chest:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                bool active = r.ReadBoolean();
                var spawnConditions = (ESpawnConditions)r.ReadByte();
                int n = r.ReadInt32();
                ItemCount[] lootItems = n > 0 ? new ItemCount[n] : null;
                for (int i = 0; i < n; i++)
                {
                    ItemData item = ReadResource<ItemData>(r);
                    int count = r.ReadInt32();
                    lootItems[i] = new ItemCount { descriptor = new ItemDescriptor { item = item }, count = count };
                }
                var chest = new ChestSimState(pos, scene)
                {
                    Active = active,
                    SpawnConditions = spawnConditions,
                    LootItems = lootItems,
                };
                List<ItemState> contents = ReadItemList(r);
                for (int i = 0; i < contents.Count; i++)
                {
                    chest.Contents.Add(contents[i]);
                }
                return chest;
            }
            case Tag.Trap:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                bool disarmed = r.ReadBoolean();
                var trap = new TrapSimState(pos, scene);
                trap.Disarmed = disarmed;
                trap.HazardRadius = TrapSimState.DefaultHazardRadius;
                return trap;
            }
            case Tag.Signpost:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                var languageData = ReadResource<LanguageData>(r);
                string text = r.ReadString();
                return new SignpostSimState(pos, scene, text, languageData);
            }
            case Tag.KnowledgeStone:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                var languageData = ReadResource<LanguageData>(r);
                var components = (ELanguageComponents)r.ReadInt32();
                string text = r.ReadString();
                // Reconstruct the SimState concept override list from the
                // wire's language + components bitset — old .hike files
                // taught one language component bundle per stone, and that's
                // still the shape this Tag.KnowledgeStone wire encodes. A
                // None components value means "no override" — leave Concepts
                // null so KnowledgeStone.Create falls back to the scene's
                // authored _concepts array.
                Godot.Collections.Array<TeachableConcept> concepts = null;
                if (languageData != null && components != ELanguageComponents.None)
                {
                    concepts = new() { new LanguageTeachable { language = languageData, components = components } };
                }
                return new KnowledgeStoneSimState(pos, scene, text, languageData, concepts);
            }
            case Tag.FireTrap:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                float phaseOffset = r.ReadSingle();
                var fire = new FireTrapSimState(pos, scene);
                fire.PhaseOffsetSeconds = phaseOffset;
                fire.HazardRadius = FireTrapSimState.DefaultHazardRadius;
                return fire;
            }
            case Tag.Cactus:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                var cactus = new CactusSimState(pos, scene);
                cactus.HazardRadius = CactusSimState.DefaultHazardRadius;
                return cactus;
            }
            case Tag.Well:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                return new WellSimState(pos, scene);
            }
            case Tag.BerryTree:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                int berryCount = r.ReadInt32();
                int berryRegrowDay = r.ReadInt32();
                var berry = new BerryTreeSimState(pos, scene, berryCount);
                berry.RegrowDay = berryRegrowDay;
                return berry;
            }
            case Tag.ClimbableTree:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                return new ClimbableTreeSimState(pos, scene);
            }
            case Tag.BuriedSpot:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                var data = ReadResource<BuriedSpotData>(r);
                bool excavated = r.ReadBoolean();
                var buried = new BuriedSpotSimState(pos, scene, data);
                buried.Excavated = excavated;
                buried.TreasureName = r.ReadString();
                return buried;
            }
            case Tag.Tent:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                return new TentSimState(pos, scene);
            }
            case Tag.Forge:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                int level = r.ReadInt32();
                int reactivateDay = r.ReadInt32();
                var slot = (EUpgradeSlot)r.ReadInt32();
                var forge = new ForgeSimState(pos, scene, level, slot);
                forge.RegrowDay = reactivateDay;
                return forge;
            }
            case Tag.Fountain:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                int reactivateDay = r.ReadInt32();
                var fountain = new FountainSimState(pos, scene);
                fountain.RegrowDay = reactivateDay;
                return fountain;
            }
            case Tag.ForageSpawner:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                var item = ReadResource<ItemData>(r);
                int regrowDays = r.ReadInt32();
                int regrowDay = r.ReadInt32();
                var forage = new ForageSpawnerSimState(pos, scene, item, regrowDays);
                forage.RegrowDay = regrowDay;
                return forage;
            }
            case Tag.SafetyZone:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                return new SafetyZoneSimState(pos, scene);
            }
            case Tag.Roof:
            {
                Vector3 pos = ReadVec3(r);
                var style = ReadResource<RoofStyleData>(r);
                float sizeX = r.ReadSingle();
                float sizeZ = r.ReadSingle();
                var seamAxis = (ERoofSeamAxis)r.ReadByte();
                float slopeDegrees = r.ReadSingle();
                // Subscenes written before these fields existed stop short;
                // reading one anyway would eat the next entity's tag byte and
                // derail the whole list.
                float broken = _roofFormat >= ROOF_FORMAT_BROKEN ? r.ReadSingle() : 0f;
                var form = _roofFormat >= ROOF_FORMAT_FORM ? (ERoofForm)r.ReadByte() : ERoofForm.Gable;
                return new RoofSimState(pos, style, sizeX, sizeZ, seamAxis, form, slopeDegrees, broken);
            }
            case Tag.Marker:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                // Pool tag comes from the common trailing field ReadOne applies.
                return new MarkerSimState(pos, "", scene);
            }
            case Tag.PathHint:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                // Hint name comes from the common trailing field, as above.
                return new PathHintSimState(pos, "", scene);
            }
            case Tag.Waterfall:
            {
                Vector3 pos = ReadVec3(r);
                float topY = r.ReadSingle();
                float bottomY = r.ReadSingle();
                int count = r.ReadInt32();
                var lips = new WaterfallLip[count];
                for (int i = 0; i < count; i++)
                {
                    int x = r.ReadInt32();
                    int z = r.ReadInt32();
                    lips[i] = new WaterfallLip(x, z, r.ReadSByte(), r.ReadSByte());
                }
                return new WaterfallSimState(pos, topY, bottomY, lips);
            }
            default:
                throw new InvalidOperationException($"Unknown entity tag {(byte)tag}");
        }
    }

    private static void WriteVec3(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3 ReadVec3(BinaryReader r)
    {
        float x = r.ReadSingle();
        float y = r.ReadSingle();
        float z = r.ReadSingle();
        return new Vector3(x, y, z);
    }

    private static void WriteScene(BinaryWriter w, PackedScene scene)
    {
        WriteResource(w, scene);
    }

    private static PackedScene ReadScene(BinaryReader r)
    {
        return ReadPathRef<PackedScene>(r);
    }

    private static void WriteResource(BinaryWriter w, Resource resource)
    {
        w.Write7BitEncodedInt(_writePaths.Intern(resource));
    }

    private static T ReadResource<T>(BinaryReader r) where T : Resource
    {
        return ReadPathRef<T>(r);
    }

    // Plain strings share the resource-path table: a pool tag repeats across
    // every entity in its pool, which is exactly what the table is good at.
    // Read back as text rather than resolved to a Resource.
    private static void WriteInternedString(BinaryWriter w, string value)
    {
        w.Write7BitEncodedInt(_writePaths.Intern(value ?? ""));
    }

    private static string ReadInternedString(BinaryReader r)
    {
        int index = r.Read7BitEncodedInt();
        ReadPathTable table = _readPaths;
        if (index < 0 || index >= table.Paths.Length)
        {
            throw new InvalidDataException($"Entity string index {index} outside the list's table of {table.Paths.Length}.");
        }
        return table.Paths[index] ?? "";
    }

    private static T ReadPathRef<T>(BinaryReader r) where T : Resource
    {
        return Resolve(r.Read7BitEncodedInt()) as T;
    }

    private static Resource Resolve(int index)
    {
        ReadPathTable table = _readPaths;
        if (index < 0 || index >= table.Paths.Length)
        {
            throw new InvalidDataException($"Entity path index {index} outside the list's table of {table.Paths.Length}.");
        }
        if (table.Loaded[index] != null)
        {
            return table.Loaded[index];
        }
        if (table.Inline[index] != null)
        {
            return Materialize(index);
        }
        if (string.IsNullOrEmpty(table.Paths[index]))
        {
            return null;
        }
        return table.Loaded[index] = LoadRef<Resource>(table.Paths[index]);
    }

    // True when the slot holds nothing at all, which is what a null reference
    // interns to. Distinct from a load FAILURE, which also resolves to null but
    // still names a path.
    private static bool IsNullRef(int index)
    {
        return _readPaths.Inline[index] == null && string.IsNullOrEmpty(_readPaths.Paths[index]);
    }

    // A resource BY VALUE. Nested references go back through the table, so a
    // shipped asset an inline resource points at is still stored as a path.
    //
    // Flat records only: a non-empty Array or Dictionary property is REFUSED
    // rather than guessed at. A resource with structure of its own has earned a
    // .tres of its own, and half-writing one is the silent data loss this path
    // exists to remove.
    private static void WriteInline(BinaryWriter w, WritePathTable table, Resource resource)
    {
        w.Write(resource.GetType().FullName ?? "");
        var names = new List<string>();
        var blobs = new List<byte[]>();
        var refs = new List<int>();
        foreach (Godot.Collections.Dictionary property in resource.GetPropertyList())
        {
            if (((PropertyUsageFlags)property["usage"].AsInt64() & PropertyUsageFlags.Storage) == 0)
            {
                continue;
            }
            string name = property["name"].AsString();
            // Engine bookkeeping. resource_path especially must not round-trip:
            // the value is stored here precisely because that path resolves to
            // nothing, and restoring it would re-register the rebuilt copy under
            // a path pointing at a document the build does not ship.
            if (name == "script" || name.StartsWith("resource_", StringComparison.Ordinal))
            {
                continue;
            }
            Variant value = resource.Get(name);
            if (value.VariantType == Variant.Type.Nil)
            {
                continue;
            }
            if (value.VariantType == Variant.Type.Object)
            {
                names.Add(name);
                blobs.Add(null);
                refs.Add(table.Intern(value.As<Resource>()));
                continue;
            }
            if (value.VariantType == Variant.Type.Array || value.VariantType == Variant.Type.Dictionary)
            {
                bool empty = value.VariantType == Variant.Type.Array
                    ? value.AsGodotArray().Count == 0 : value.AsGodotDictionary().Count == 0;
                if (!empty)
                {
                    GD.PushError($"EntitySerializer: '{resource.GetType().Name}.{name}' is a non-empty collection on a resource "
                        + $"with no shippable path ('{resource.ResourcePath}'), so it cannot be baked by value. Give that "
                        + "resource its own .tres. The field is being dropped.");
                }
                continue;
            }
            names.Add(name);
            blobs.Add(GD.VarToBytes(value));
            refs.Add(-1);
        }

        w.Write7BitEncodedInt(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            w.Write(names[i]);
            w.Write(refs[i] >= 0);
            if (refs[i] >= 0)
            {
                w.Write7BitEncodedInt(refs[i]);
                continue;
            }
            w.Write7BitEncodedInt(blobs[i].Length);
            w.Write(blobs[i]);
        }
    }

    private static InlineRecord ReadInline(BinaryReader r)
    {
        var record = new InlineRecord { TypeName = r.ReadString() };
        int count = r.Read7BitEncodedInt();
        record.Names = new string[count];
        record.Blobs = new byte[count][];
        record.Refs = new int[count];
        for (int i = 0; i < count; i++)
        {
            record.Names[i] = r.ReadString();
            if (r.ReadBoolean())
            {
                record.Refs[i] = r.Read7BitEncodedInt();
                continue;
            }
            record.Refs[i] = -1;
            record.Blobs[i] = r.ReadBytes(r.Read7BitEncodedInt());
        }
        return record;
    }

    // Rebuilt by C# type name rather than by attaching the script: a resource
    // the editor materializes as a bare Godot.Resource cannot be cast back to
    // the type whose field it is about to be assigned to. Registered in Loaded
    // BEFORE its fields are set, so a cycle between two inline resources ends.
    private static Resource Materialize(int index)
    {
        ReadPathTable table = _readPaths;
        InlineRecord record = table.Inline[index];
        Type type = Type.GetType(record.TypeName);
        if (type == null || Activator.CreateInstance(type) is not Resource resource)
        {
            GD.PushError($"EntitySerializer: no resource type '{record.TypeName}' to rebuild an inline reference as.");
            return null;
        }
        table.Loaded[index] = resource;
        for (int i = 0; i < record.Names.Length; i++)
        {
            resource.Set(record.Names[i], record.Refs[i] >= 0
                ? Variant.From(Resolve(record.Refs[i]))
                : GD.BytesToVar(record.Blobs[i]));
        }
        return resource;
    }

    // A resource embedded in another document (a MobDescriptor's StatusEffectData,
    // an NPC appearance's MobPalette) has a "<file>::<id>" path, and GD.Load only
    // resolves that form from the resource cache. Loading the outer document
    // first registers its sub-resources, so the second load hits the cache.
    // Only reached for a document the build SHIPS — a sub-resource of the world's
    // authoring document is stored by value instead (WritePathTable.Intern).
    private static T LoadRef<T>(string path) where T : Resource
    {
        int sub = path.IndexOf("::", System.StringComparison.Ordinal);
        if (sub > 0)
        {
            GD.Load<Resource>(path.Substring(0, sub));
        }
        return GD.Load<T>(path);
    }

    // A reference spelled out as a bare path — the shape the three lists below
    // used before every reference moved into the ref table.
    private static T LegacyRef<T>(BinaryReader r) where T : Resource
    {
        string path = r.ReadString();
        return string.IsNullOrEmpty(path) ? null : LoadRef<T>(path);
    }

    // Weapon loadout (MobSimState.Weapons), stamped from SpeciesData.weapons at
    // spawn: count + each WeaponData resource path. Null/empty writes a 0 count
    // and reads back as null (a mob that never attacks).
    private static void WriteWeaponList(BinaryWriter w, Godot.Collections.Array<WeaponData> weapons)
    {
        int count = weapons?.Count ?? 0;
        w.Write(count);
        for (int i = 0; i < count; i++)
        {
            WriteResource(w, weapons[i]);
        }
    }

    private static Godot.Collections.Array<WeaponData> ReadWeaponList(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count <= 0)
        {
            return null;
        }
        var weapons = new Godot.Collections.Array<WeaponData>();
        for (int i = 0; i < count; i++)
        {
            weapons.Add(_legacyPathRefs ? LegacyRef<WeaponData>(r) : ReadResource<WeaponData>(r));
        }
        return weapons;
    }

    // Per-instance descriptor status effects (MobSimState.StatusEffects): count +
    // each StatusEffectData resource path. Null/empty writes a 0 count and reads
    // back as null.
    private static void WriteStatusEffectList(BinaryWriter w, Godot.Collections.Array<StatusEffectData> effects)
    {
        int count = effects?.Count ?? 0;
        w.Write(count);
        for (int i = 0; i < count; i++)
        {
            WriteResource(w, effects[i]);
        }
    }

    private static Godot.Collections.Array<StatusEffectData> ReadStatusEffectList(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count <= 0)
        {
            return null;
        }
        var effects = new Godot.Collections.Array<StatusEffectData>();
        for (int i = 0; i < count; i++)
        {
            effects.Add(_legacyPathRefs ? LegacyRef<StatusEffectData>(r) : ReadResource<StatusEffectData>(r));
        }
        return effects;
    }

    // ItemState wire format: ItemData resource path + the base ItemState fields.
    // The stack's units are stored as spoil cohorts — a count and each cohort's
    // (units, removeOnDay) pair — so per-batch spoilage survives save/load; then
    // cooldownExpireMs, cooldownDurationMs, touched, whole-item removeOnDay, level.
    // Polymorphic subclass fields (WeaponState.ammo, LanternState.isActive) are
    // not preserved — items round-trip through ItemData.CreateState() which resets
    // them to authored defaults. Extend this when player Inventory persistence
    // lands and subclass state needs to survive save/load.
    private static void WriteItemState(BinaryWriter w, ItemState item)
    {
        if (item == null || item.data == null)
        {
            WriteResource(w, null);
            return;
        }
        WriteResource(w, item.data);
        w.Write(item.CohortCount);
        for (int i = 0; i < item.CohortCount; i++)
        {
            item.GetCohort(i, out int count, out int removeOnDay);
            w.Write(count);
            w.Write(removeOnDay);
        }
        w.Write(item.cooldownExpireMs);
        w.Write(item.cooldownDurationMs);
        w.Write(item.touched);
        w.Write(item.removeOnDay);
        w.Write(item.level);
    }

    private static ItemState ReadItemState(BinaryReader r)
    {
        // An absent item writes the null reference and nothing else, so the
        // trailing fields are only there when the reference names something.
        ItemData data;
        if (_legacyPathRefs)
        {
            string path = r.ReadString();
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            data = LoadRef<ItemData>(path);
        }
        else
        {
            int slot = r.Read7BitEncodedInt();
            if (IsNullRef(slot))
            {
                return null;
            }
            // Read all trailing fields unconditionally to keep the stream aligned
            // even if the resource itself has been renamed/removed since the file
            // was written — a missing item silently drops the slot, but the
            // following entries still parse correctly.
            data = Resolve(slot) as ItemData;
        }
        int cohortCount = r.ReadInt32();
        var cohorts = new (int count, int removeOnDay)[System.Math.Max(0, cohortCount)];
        for (int i = 0; i < cohortCount; i++)
        {
            cohorts[i] = (r.ReadInt32(), r.ReadInt32());
        }
        ulong cooldownExpireMs = r.ReadUInt64();
        ulong cooldownDurationMs = r.ReadUInt64();
        bool touched = r.ReadBoolean();
        int removeOnDay = r.ReadInt32();
        int level = r.ReadInt32();
        if (data == null)
        {
            return null;
        }
        ItemState state = data.CreateState();
        // Rebuild the ledger from the persisted cohorts (AddUnits merges same-day
        // entries, so the stack matches what was written).
        state.ClearCohorts();
        for (int i = 0; i < cohorts.Length; i++)
        {
            state.AddUnits(cohorts[i].count, cohorts[i].removeOnDay);
        }
        state.cooldownExpireMs = cooldownExpireMs;
        state.cooldownDurationMs = cooldownDurationMs;
        state.touched = touched;
        state.removeOnDay = removeOnDay;
        state.level = level;
        return state;
    }

    private static void WriteItemList(BinaryWriter w, IReadOnlyList<ItemState> items)
    {
        int count = items?.Count ?? 0;
        w.Write(count);
        for (int i = 0; i < count; i++)
        {
            WriteItemState(w, items[i]);
        }
    }

    private static List<ItemState> ReadItemList(BinaryReader r)
    {
        int count = r.ReadInt32();
        var list = new List<ItemState>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(ReadItemState(r));
        }
        return list;
    }
}
