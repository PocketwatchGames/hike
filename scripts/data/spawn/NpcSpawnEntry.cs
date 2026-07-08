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
    [Export] public LanguageData language;
    // Branching conversation this NPC runs when talked to. Null = none (Talk
    // does nothing).
    [Export] public ConversationData conversation;

    // --- Per-individual appearance (each NPC is one unique world entity) ---
    // Rig/gender override: the model scene instanced for THIS individual (e.g. a
    // male vs female villager package). Null = the descriptor's base
    // MobData.mobScene. Passed into MobDescriptor.CreateState so it's fixed at
    // construction and serializes with the mob.
    [Export] public PackedScene scene;
    // Outfit: the modular rig's visible clothing/hair/hat mesh names (gender-
    // matched to Scene), composed with the rig's always-on base meshes at spawn.
    // Empty = the scene's authored default outfit.
    [Export] public string[] outfit = System.Array.Empty<string>();
    // Recolor applied to this individual's meshes (clothing/hair tints) so two
    // villagers in the same outfit still read as distinct. Null = no recolor.
    [Export] public MobPalette palette;
    // Idle-pose override: a clip name (e.g. "idle_happy", "idle_nervous") that
    // replaces the species' shared Idle animation for THIS individual, so
    // villagers built from one MobData each rest differently. Must name a clip
    // baked into the rig's animation library (polysplit/anims → human_anims.res).
    // Empty = the species default idle.
    [Export] public StringName idleAnimation;

    // Loyalty rewards this NPC hands back as the player gifts items.
    [Export] public Array<LoyaltyGift> loyaltyGifts = new();
    // Per-instance merchant stock.
    [Export] public MobInventoryData[] inventory = System.Array.Empty<MobInventoryData>();
    // Per-instance taste rules layered ON TOP of the species' base
    // MobData.itemPreferences — author only the delta. Composes multiplicatively
    // after the base list (ItemTagPreference.Fold), so e.g. one apothecary adds a
    // Potion x2 rule without forking MobData. Each rule is a standalone .tres so
    // it persists by path and reusable rules can be shared across merchants.
    // Empty leaves the species defaults untouched.
    [Export] public Array<ItemTagPreference> itemPreferences = new();
    // Party-member identity for a recruitable NPC: the PlayerState this
    // individual becomes when added to the party (via a RecruitToPartyAction in
    // their conversation). Authored as a standalone .tres like the starting
    // party; cloned into the roster on recruit while the NPC's mob despawns and
    // the clone stands at the active campfire. Null = not recruitable.
    [Export] public PlayerState recruitTemplate;
    // Already tamed at spawn — joins the player's side (the starter companion).
    [Export] public bool tamed;
    // Persistent (non-chunked) player-attached state, spawned once and never
    // destroyed by chunk eviction — the companion. See AddPersistentEntity.
    [Export] public bool persistent;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (descriptor == null)
        {
            return;
        }
        float rotationY = (float)(rng.NextDouble() * Mathf.Pi * 2f);
        MobSimState state = descriptor.CreateState(position, rotationY, scene);
        if (state == null)
        {
            return;
        }
        state.SpawnConditions = spawnConditions;
        if (palette != null) { state.Palette = palette; }
        if (outfit != null && outfit.Length > 0) { state.Outfit = outfit; }
        if (idleAnimation != null && (string)idleAnimation != "") { state.IdleAnimation = idleAnimation; }
        if (initialBehavior != null && (string)initialBehavior != ""
            && rng.NextDouble() < initialBehaviorChance)
        {
            state.InitialBehavior = initialBehavior;
        }
        if (language != null) { state.Language = language; }
        if (conversation != null) { state.Conversation = conversation; }
        if (loyaltyGifts != null)
        {
            foreach (LoyaltyGift gift in loyaltyGifts)
            {
                if (gift != null) { state.LoyaltyGifts.Add(gift); }
            }
        }
        if (inventory != null)
        {
            foreach (MobInventoryData entry in inventory)
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
        if (itemPreferences != null)
        {
            foreach (ItemTagPreference pref in itemPreferences)
            {
                if (pref != null) { state.ItemPreferences.Add(pref); }
            }
        }
        if (recruitTemplate != null) { state.RecruitTemplate = recruitTemplate; }
        if (tamed) { state.Tamed = true; }
        if (persistent)
        {
            ws.AddPersistentEntity(state);
        }
        else
        {
            ws.AddEntity(state);
        }
    }
}
