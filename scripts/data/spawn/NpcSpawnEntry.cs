using System;
using Godot;
using Godot.Collections;

// A named NPC: a mob spawned with per-instance overrides that are inherently
// not shareable across a species template — a branching Conversation, a spoken
// Language, merchant Inventory, and LoyaltyGifts (rewards). Covers both the
// friendly villager (merchant + conversation) and the starter companion
// (Tamed + Persistent). These overrides live on the placement entry, not on the
// shared MobData, so the species .tres stays generic — see MobSimState's
// LoyaltyGifts / Inventory rationale.
[GlobalClass]
public partial class NpcSpawnEntry : MobSpawnEntry
{
    // Spoken language (scrambles dialogue until the player learns it). Null
    // leaves the descriptor/species default.
    [Export] public LanguageData Language;
    // Branching conversation attached per-instance. Null leaves the default.
    [Export] public ConversationData Conversation;
    // Loyalty rewards this NPC hands back as the player gifts items.
    [Export] public Array<LoyaltyGift> LoyaltyGifts = new();
    // Per-instance merchant stock.
    [Export] public MobInventoryData[] Inventory = System.Array.Empty<MobInventoryData>();
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
        MobSimState state = Descriptor.CreateState(position, rotationY);
        if (state == null)
        {
            return;
        }
        state.SpawnConditions = spawnConditions;
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
