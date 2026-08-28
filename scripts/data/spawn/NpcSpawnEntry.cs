using System;
using Godot;
using Godot.Collections;

// A named NPC: a single world entity spawned with the overrides that describe
// THAT individual rather than its species — a branching Conversation, a spoken
// Language, merchant Inventory, LoyaltyGifts (rewards) and taste rules. Kept as
// its own entry type (not folded into MobSpawnEntry) so standard mobs aren't
// cluttered with NPC-only fields, and kept off the shared
// MobDescriptor/SpeciesData so those stay reusable species templates — every
// placement is its own entity with its own dialogue and stock. See MobSimState's
// LoyaltyGifts / Inventory rationale.
//
// **An NPC always spawns chunk-streamed and untamed.** Becoming a companion is a
// RUNTIME transition and owns both halves of itself: Mob.Tame flips
// MobSimState.Tamed once loyalty crosses MobData.tameLoyalty, and
// Sim.PromoteCompanionToPersistent moves the mob out of its chunk bucket into the
// persistent store at that same moment. The spawn-time `tamed` / `persistent`
// pair that used to short-circuit that was the starter companion, and nothing
// authored either flag — reintroducing one would mean a second way in, which is
// how the two halves come apart.
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

    // The clips baked into the rig this individual is drawn with — its own
    // `scene` override, else the species' model scene.
    //
    // Read off the PackedScene's STATE rather than by instantiating it: the
    // rig names its AnimationLibrary as a plain ext_resource, so the clip list
    // is reachable without building a node tree (and without running _Ready on
    // scripts that expect a live Sim, which the painter has none of).
    //
    // Returns null — a free-text box — where the library is not reachable that
    // way. A rig whose AnimationPlayer lives inside an INSTANCED sub-scene keeps
    // its properties in that sub-scene's state, not this one's, so the walk
    // below finds nothing and the author types the name as before.
    public override string[] NameCandidates(StringName property)
    {
        if (property != PropertyName.idleAnimation)
        {
            return base.NameCandidates(property);
        }
        PackedScene rig = scene ?? descriptor?.mob?.mobScene;
        SceneState state = rig?.GetState();
        if (state == null)
        {
            return null;
        }
        var names = new System.Collections.Generic.List<string>();
        int nodes = state.GetNodeCount();
        for (int n = 0; n < nodes; n++)
        {
            int properties = state.GetNodePropertyCount(n);
            for (int i = 0; i < properties; i++)
            {
                // "libraries/<key>" — the key is the animation-name prefix and
                // is empty for the single-library rigs here, which is why the
                // clips come back bare.
                string name = state.GetNodePropertyName(n, i);
                if (!name.StartsWith("libraries/", System.StringComparison.Ordinal))
                {
                    continue;
                }
                if (state.GetNodePropertyValue(n, i).As<AnimationLibrary>() is not AnimationLibrary library)
                {
                    continue;
                }
                string prefix = name["libraries/".Length..];
                foreach (StringName clip in library.GetAnimationList())
                {
                    names.Add(prefix.Length > 0 ? $"{prefix}/{clip}" : clip.ToString());
                }
            }
        }
        if (names.Count == 0)
        {
            return null;
        }
        // Idle poses first, then everything else alphabetically. The rig carries
        // ~55 clips and only a handful are rest poses, but the rest are not
        // INVALID — a pose could reasonably be named "sit" or "lean" — so they
        // are ordered down rather than filtered out. Hiding them would make the
        // list a rule about naming that nothing else enforces.
        names.Sort((a, b) =>
        {
            bool aIdle = a.StartsWith("idle", System.StringComparison.OrdinalIgnoreCase);
            bool bIdle = b.StartsWith("idle", System.StringComparison.OrdinalIgnoreCase);
            return aIdle == bIdle
                ? string.CompareOrdinal(a, b)
                : aIdle ? -1 : 1;
        });
        return names.ToArray();
    }

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (descriptor == null)
        {
            return;
        }
        float rotationY = context?.FacingY ?? (float)(rng.NextDouble() * Mathf.Pi * 2f);
        MobSimState state = descriptor.CreateState(position, rotationY, scene);
        if (state == null)
        {
            return;
        }
        state.SpawnConditions = spawnConditions;
        if (palette != null) { state.Palette = palette; }
        if (outfit != null && outfit.Length > 0) { state.Outfit = outfit; }
        if (idleAnimation != null && (string)idleAnimation != "") { state.IdleAnimation = idleAnimation; }
        // The chance is a POPULATION fraction ("a quarter of spawned goblins
        // start in Wander"), so it has nothing to be a fraction of when someone
        // placed this one by hand — an authored placement always takes the
        // behaviour it names.
        if (initialBehavior != null && (string)initialBehavior != ""
            && (context?.AuthoredPosition == true
                || rng.NextDouble() < initialBehaviorChance))
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
                itemState.SetCount(Mathf.Max(1, entry.count));
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
        ws.AddEntity(state);
    }
}
