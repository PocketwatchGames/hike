using System;
using System.Collections.Generic;
using System.IO;
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
    }

    // Legacy PropType byte values for loot. PropSimState used to cover loot
    // before LootSimState was split out; old world files still carry Tag.Prop
    // with these PropType bytes and must round-trip through the legacy reader.
    // Both bytes now route to the same unified Loot path; the historical split
    // between auto/interact pickup is decided at run time from inventory state.
    private const byte LegacyPropTypeAutoLoot = 2;
    private const byte LegacyPropTypeLoot = 3;

    public static void WriteList(BinaryWriter w, IReadOnlyList<EntitySimState> entities)
    {
        if (entities == null)
        {
            w.Write((uint)0);
            return;
        }

        w.Write((uint)entities.Count);
        foreach (EntitySimState e in entities)
        {
            WriteOne(w, e);
        }
    }

    public static List<EntitySimState> ReadList(BinaryReader r)
    {
        uint count = r.ReadUInt32();
        var list = new List<EntitySimState>((int)count);
        for (uint i = 0; i < count; i++)
        {
            list.Add(ReadOne(r));
        }
        return list;
    }

    private static void WriteOne(BinaryWriter w, EntitySimState e)
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
                w.Write(prop.RotationY);
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
                w.Write(mob.RotationY);
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
                // Scales health/armor/damage by 2^Level, so it must persist or a
                // reloaded mob would revert to base stats. Appended last so older
                // world files still parse.
                w.Write(mob.Level);
                break;

            case DoorSimState door:
                w.Write((byte)Tag.Door);
                WriteVec3(w, door.WorldPosition);
                WriteScene(w, door.Scene);
                w.Write(door.RotationY);
                w.Write(door.Active);
                break;

            case BoatSimState boat:
                w.Write((byte)Tag.Boat);
                WriteVec3(w, boat.WorldPosition);
                WriteScene(w, boat.Scene);
                w.Write(boat.RotationY);
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

            default:
                throw new InvalidOperationException($"EntitySerializer has no writer for {e.GetType().Name}");
        }
    }

    private static EntitySimState ReadOne(BinaryReader r)
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
                float rotationY = r.ReadSingle();
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
                return new PropSimState((PropType)typeByte, pos, scene) { RotationY = rotationY };
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
                float rotationY = r.ReadSingle();
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

                var mob = new MobSimState(pos, rotationY, spawnPos, spawnRotationY, scene, mobData);
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
                float rotationY = r.ReadSingle();
                bool active = r.ReadBoolean();
                var door = new DoorSimState(pos, rotationY, scene);
                door.Active = active;
                return door;
            }
            case Tag.Boat:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                float rotationY = r.ReadSingle();
                return new BoatSimState(pos, rotationY, scene);
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
        w.Write(scene != null ? scene.ResourcePath : "");
    }

    private static PackedScene ReadScene(BinaryReader r)
    {
        string path = r.ReadString();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        return GD.Load<PackedScene>(path);
    }

    private static void WriteResource(BinaryWriter w, Resource resource)
    {
        w.Write(resource != null ? resource.ResourcePath : "");
    }

    private static T ReadResource<T>(BinaryReader r) where T : Resource
    {
        string path = r.ReadString();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        return GD.Load<T>(path);
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
            WeaponData wd = weapons[i];
            w.Write(wd != null ? wd.ResourcePath : "");
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
            string path = r.ReadString();
            weapons.Add(string.IsNullOrEmpty(path) ? null : GD.Load<WeaponData>(path));
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
            StatusEffectData e = effects[i];
            w.Write(e != null ? e.ResourcePath : "");
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
            string path = r.ReadString();
            effects.Add(string.IsNullOrEmpty(path) ? null : GD.Load<StatusEffectData>(path));
        }
        return effects;
    }

    // ItemState wire format: ItemData resource path + the base ItemState fields.
    // The stack's units are stored as spoil cohorts — a count and each cohort's
    // (units, removeOnDay) pair — so per-batch spoilage survives save/load; then
    // cooldownExpireMs, cooldownDurationMs, touched, whole-item removeOnDay, level.
    // Polymorphic subclass fields (WeaponState.ammo, ConsumableState.isActive) are
    // not preserved — items round-trip through ItemData.CreateState() which resets
    // them to authored defaults. Extend this when player Inventory persistence
    // lands and subclass state needs to survive save/load.
    private static void WriteItemState(BinaryWriter w, ItemState item)
    {
        if (item == null || item.data == null)
        {
            w.Write("");
            return;
        }
        w.Write(item.data.ResourcePath);
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
        string path = r.ReadString();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        // Read all trailing fields unconditionally to keep the stream aligned
        // even if the resource itself has been renamed/removed since the file
        // was written — a missing item silently drops the slot, but the
        // following entries still parse correctly.
        ItemData data = GD.Load<ItemData>(path);
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
