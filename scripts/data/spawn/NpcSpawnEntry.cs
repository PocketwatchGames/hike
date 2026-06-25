using System;
using Godot;
using Godot.Collections;

// A named NPC: a single world entity spawned with the overrides that describe
// THAT individual rather than its species — a branching Conversation, a spoken
// Language, merchant Inventory, LoyaltyGifts (rewards), taste rules, and the
// Tamed/Persistent companion flags. Kept as its own entry type (not folded into
// MobSpawnEntry) so standard mobs aren't cluttered with NPC-only fields, and
// kept off the shared MobDescriptor/SpeciesData so those stay reusable species
// templates — every placement is its own entity with its own dialogue and stock.
// Covers both the friendly villager (merchant + conversation) and the starter
// companion (Tamed + Persistent). See MobSimState's LoyaltyGifts / Inventory
// rationale.
[GlobalClass]
public partial class NpcSpawnEntry : MobSpawnEntry
{
    // Spoken language (scrambles dialogue until the player learns it). Null
    // leaves MobData.language.
    [Export] public LanguageData Language;
    // Branching conversation this NPC runs when talked to. Null = none (Talk
    // does nothing).
    [Export] public ConversationData Conversation;

    // --- Per-individual appearance (each NPC is one unique world entity) ---
    // Rig/gender override: the model scene instanced for THIS individual (e.g. a
    // male vs female villager package). Null = the descriptor's base
    // MobData.MobScene. Passed into MobDescriptor.CreateState so it's fixed at
    // construction and serializes with the mob.
    [Export] public PackedScene Scene;
    // Outfit: the modular rig's visible clothing/hair/hat mesh names (gender-
    // matched to Scene), composed with the rig's always-on base meshes at spawn.
    // Empty = the scene's authored default outfit.
    [Export] public string[] Outfit = System.Array.Empty<string>();
    // Recolor applied to this individual's meshes (clothing/hair tints) so two
    // villagers in the same outfit still read as distinct. Null = no recolor.
    [Export] public MobPalette Palette;

    // Loyalty rewards this NPC hands back as the player gifts items.
    [Export] public Array<LoyaltyGift> LoyaltyGifts = new();
    // Per-instance merchant stock.
    [Export] public MobInventoryData[] Inventory = System.Array.Empty<MobInventoryData>();
    // Per-instance taste rules layered ON TOP of the species' base
    // MobData.itemPreferences — author only the delta. Composes multiplicatively
    // after the base list (ItemTagPreference.Fold), so e.g. one apothecary adds a
    // Potion x2 rule without forking MobData. Each rule is a standalone .tres so
    // it persists by path and reusable rules can be shared across merchants.
    // Empty leaves the species defaults untouched.
    [Export] public Array<ItemTagPreference> ItemPreferences = new();
    // Already tamed at spawn — joins the player's side (the starter companion).
    [Export] public bool Tamed;
    // Persistent (non-chunked) player-attached state, spawned once and never
    // destroyed by chunk eviction — the companion. See AddPersistentEntity.
    [Export] public bool Persistent;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Descriptor == null)
        {
            return;
        }
        float rotationY = (float)(rng.NextDouble() * Mathf.Pi * 2f);
        MobSimState state = Descriptor.CreateState(position, rotationY, Scene);
        if (state == null)
        {
            return;
        }
        state.SpawnConditions = spawnConditions;
        if (Palette != null) { state.Palette = Palette; }
        if (Outfit != null && Outfit.Length > 0) { state.Outfit = Outfit; }
        if (InitialBehavior != null && (string)InitialBehavior != ""
            && rng.NextDouble() < InitialBehaviorChance)
        {
            state.InitialBehavior = InitialBehavior;
        }
        if (Language != null) { state.Language = Language; }
        if (Conversation != null) { state.Conversation = Conversation; }
        if (LoyaltyGifts != null)
        {
            foreach (LoyaltyGift gift in LoyaltyGifts)
            {
                if (gift != null) { state.LoyaltyGifts.Add(gift); }
            }
        }
        if (Inventory != null)
        {
            foreach (MobInventoryData entry in Inventory)
            {
                if (entry == null || entry.item == null) { continue; }
                ItemState itemState = entry.item.CreateState();
                itemState.stackCount = Mathf.Max(1, entry.count);
                state.Inventory.Add(new MobInventoryItem
                {
                    item = itemState,
                    loyaltyCost = entry.loyaltyCost,
                    secret = entry.secret,
                });
            }
        }
        if (ItemPreferences != null)
        {
            foreach (ItemTagPreference pref in ItemPreferences)
            {
                if (pref != null) { state.ItemPreferences.Add(pref); }
            }
        }
        if (Tamed) { state.Tamed = true; }
        if (Persistent)
        {
            ws.AddPersistentEntity(state);
        }
        else
        {
            ws.AddEntity(state);
        }
    }
}
