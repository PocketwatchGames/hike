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
    // The bundled look — rig, outfit and recolor as ONE authored choice. Set,
    // it wins over the raw trio below; null falls back to them.
    //
    // Two paths rather than one because they are authored by different things.
    // Worldgen's house lists name the three fields inline and always have. A
    // hand placement picks a bundle, because the map's property panel can only
    // offer a single pick per row and three independent rows cannot enforce that
    // an outfit's meshes exist in the rig it is worn on — see NpcAppearanceData.
    [Export] public NpcAppearanceData appearance;

    // The appearances THIS entry may be given, the way MobSpawnEntry.variants
    // constrains a descriptor: one npc palette entry, with the villager picked
    // per placement, so selecting it highlights every NPC on the map. Empty
    // leaves the row offering every authored appearance.
    [Export] public NpcAppearanceData[] appearances = System.Array.Empty<NpcAppearanceData>();

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

    // The three appearance channels, resolved once so the bundle and the raw
    // trio cannot disagree between the spawn path and the animation picker.
    public PackedScene Rig => appearance?.scene ?? scene;

    public string[] Outfit => appearance != null && appearance.outfit is { Length: > 0 }
        ? appearance.outfit
        : outfit;

    public MobPalette Recolor => appearance?.palette ?? palette;

    // Which villager of its family this one is. The appearance is what a hand
    // placement varies, so it names the individual; a worldgen entry authored
    // before appearances existed falls back to its descriptor.
    public override string VariantName()
    {
        if (appearance != null && !string.IsNullOrEmpty(appearance.ResourcePath))
        {
            return appearance.ResourcePath.GetFile().GetBaseName();
        }
        return base.VariantName();
    }

    // What an author actually sets on a villager, in the order they set it:
    // who it looks like, how it stands, what it says, what tongue it says it in,
    // and whether it can join the party.
    private static readonly StringName[] Order =
    {
        PropertyName.appearance, PropertyName.idleAnimation,
        PropertyName.conversation, PropertyName.language,
        PropertyName.recruitTemplate,
    };

    public override StringName[] PropertyOrder => Order;

    // Three rows an NPC does not want, each for its own reason:
    //
    // `descriptor` — the two humanoid descriptors resolve to the SAME MobData
    // and differ only in a bestiary displayName, so picking one changes nothing
    // an author can see. Which individual this is was already decided by the
    // appearance and the conversation.
    // `levelOverride` — a difficulty tier for a villager standing in a doorway
    // is meaningless; the field belongs to the fighting mobs it was added for.
    // `initialBehavior` — an NPC runs its conversation and its idle pose, not a
    // combat brain's entry state.
    public override bool ShowsProperty(StringName name)
    {
        return name != PropertyName.descriptor
            && name != PropertyName.levelOverride
            && name != PropertyName.initialBehavior
            && base.ShowsProperty(name);
    }

    public override Resource[] ResourceCandidates(StringName property)
    {
        if (property == PropertyName.appearance && appearances is { Length: > 0 })
        {
            return appearances;
        }
        return base.ResourceCandidates(property);
    }

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
        PackedScene rig = Rig ?? descriptor?.mob?.mobScene;
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

    // Aimable: which way a villager standing in a doorway looks is the whole
    // point of placing that one by hand.
    public override bool UsesFacing => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (descriptor == null)
        {
            return;
        }
        float rotationY = context?.FacingY ?? (float)(rng.NextDouble() * Mathf.Pi * 2f);
        MobSimState state = descriptor.CreateState(position, rotationY, Rig);
        if (state == null)
        {
            return;
        }
        state.SpawnConditions = context?.SpawnConditions ?? ESpawnConditions.None;
        MobPalette recolor = Recolor;
        string[] worn = Outfit;
        if (recolor != null) { state.Palette = recolor; }
        if (worn != null && worn.Length > 0) { state.Outfit = worn; }
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
