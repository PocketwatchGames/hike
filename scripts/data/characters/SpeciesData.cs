using Godot;

// A species: a base MobData paired with the per-variant recolor, intrinsic
// status effects, weapons and loot that define one creature (a swamp goblin vs
// a desert goblin). This is what a spawn source names, and CreateState builds
// the runtime MobSimState from it.
//
// It is the unit of bestiary identity: a runtime mob IS-A species
// (MobSimState.Species), and discovery / kill-leveling key on it (see
// SimState.DiscoveredSpecies). The base MobData (reached via `mob`) is the
// shared template AND the bestiary "page" that groups a type's species; this
// SpeciesData is one "row" on that page. A pure loadout variant (a claw goblin
// vs a torch-bearing one) is its own SpeciesData, since weapons are a species
// trait — and thus its own bestiary row.
//
// **Being elite is NOT a species trait** — it is a property of one spawn, so it
// rides on the spawn source (MobSpawnEntry.elite) and is passed into CreateState
// rather than authored here. That keeps an elite goblin the same bestiary row,
// discovery and kill-quest target as a plain one, and spares every elite a
// forked copy of the seven fields below.
[GlobalClass]
public partial class SpeciesData : Resource
{
    // Base species template for this variant — the shared MobData (scene, brain,
    // stats, animations) and the bestiary page this species is listed under.
    [Export] public MobData mob;

    // Bestiary row label for this species (e.g. "Forest Spider"). Null/empty
    // falls back to the base mob's displayName (the page title).
    [Export] public StringName displayName;

    // Small bestiary row portrait for this species. Null falls back to the base
    // mob's bestiaryPortrait (the page portrait). Distinct from MobData
    // .bestiaryPortrait so a recolored variant can show its own tint.
    [Export] public Texture2D portrait;

    // Per-variant inherent stat modifiers, mirroring MobData.modifiers but
    // scoped to this species — folded into the mob's stat composition at runtime
    // (see Mob.ComposeStat) ON TOP OF the base mob's modifiers, so a swamp
    // variant can be tankier / a forest one stealthier without forking MobData.
    // Also the source the bestiary lists per row (StatList.Modifiers). Empty =
    // identical to the base species' stats.
    [Export] public Godot.Collections.Array<StatModifier> modifiers = new();
    // Managed read-mirror of `modifiers` — see MobData.ModifiersFlat.
    private StatModifier[] _modifiersFlat;
    public StatModifier[] ModifiersFlat => _modifiersFlat ??= StatModifierUtil.Flatten(modifiers);

    // Recolor override. Null = fall back to the species' own MobData.palette
    // (usually none). See MobPalette / ModelAnimator.
    [Export] public MobPalette palette;

    // Status effects intrinsic to this variant, applied to every mob spawned
    // from a descriptor that uses it — a per-variant buff/aura channel composed
    // alongside (not replacing) the elite signature at spawn. Each is routed the
    // same way at spawn: a weapon-mod effect composes onto the mob's weapons, any
    // other onto the mob's status controller (see Mob.ApplySpawnStatusEffect).
    // Empty = none.
    [Export] public Godot.Collections.Array<StatusEffectData> statusEffects = new();

    // Weapon loadout for mobs of this species — the home for a mob's weapons
    // (NOT a base trait on MobData). Each WeaponData carries its own action
    // timeline, damage / continuous profiles, in-hand held model, and AI
    // engagement tuning (range / cooldown / ally gate / priority), exactly like a
    // player weapon. CreateState stamps this onto MobSimState.Weapons;
    // BehaviorAttack fires the highest-priority weapon whose
    // gates pass and the in-hand prop is the primary weapon's held model. Because
    // weapons are a species trait, a loadout variant (a claw goblin vs a
    // torch-bearing one) is authored as its own SpeciesData — a distinct bestiary
    // row. Empty = a mob that never attacks.
    [Export] public Godot.Collections.Array<WeaponData> weapons = new();

    // Loot ejected from the mob's body when it dies (was a MobData field; lives
    // here so each zone variant sets its own spoils — all kun-kun variants drop
    // the shared kun_kun_meat, all goblins goblin_meat, etc.).
    // Each entry spawns `count` Loot instances of its descriptor, fired outward
    // on the same upward arc chests use. CreateState stamps this onto
    // MobSimState.Loot (read by Mob.EjectLoot); empty = no drops.
    [Export] public Godot.Collections.Array<ItemCount> loot = new();

    // Build the runtime sim state for a mob of this species at the given
    // transform. Returns null when the species has no usable MobData or scene.
    //
    // `elite` is the spawn's elite signature (null = an ordinary mob): it stamps
    // the crown / badge onto the state and composes its signature effects on top
    // of this species' intrinsic ones. `sceneOverride` swaps the rig per
    // individual (a male vs female villager) without forking the species — see
    // NpcSpawnEntry.Scene; null falls back to MobData.mobScene. The scene is
    // fixed at construction (EntitySimState.Scene is readonly) and serializes
    // with the mob, so an overridden rig survives chunk eviction and save/load.
    //
    // `level` is the mob's difficulty tier, resolved by the caller (an authored
    // floor plus the per-area worldgen field — see SpawnContext.MobLevel). It
    // reaches the MobSimState constructor so vitals are scaled to it at creation,
    // before the state is ever serialized, rather than patched afterward. (The
    // constructor forces non-dangerous mobs back to level 0.)
    public MobSimState CreateState(Vector3 worldPosition, float rotationY, EliteData elite = null,
        PackedScene sceneOverride = null, int level = 0, float levelScalePerLevel = 1.5f)
    {
        PackedScene scene = sceneOverride ?? mob?.mobScene;
        if (mob == null || scene == null)
        {
            return null;
        }
        var state = new MobSimState(worldPosition, rotationY, scene, mob, level, levelScalePerLevel);
        // The species is the mob's bestiary identity (discovery / kill-leveling
        // key) as well as the source of its recolor / loot / stat modifiers.
        state.Species = this;
        state.Palette = palette;
        if (weapons != null && weapons.Count > 0)
        {
            state.Weapons = weapons;
        }
        if (loot != null && loot.Count > 0)
        {
            state.Loot = loot;
        }
        // Compose this species' intrinsic status effects with the elite
        // signature's (if any) into one list so both apply at spawn.
        var effects = new Godot.Collections.Array<StatusEffectData>();
        if (statusEffects != null)
        {
            foreach (StatusEffectData effect in statusEffects)
            {
                effects.Add(effect);
            }
        }
        if (elite != null)
        {
            state.Elite = true;
            state.Badge = elite.badge;
            state.EliteCrownScene = elite.crownScene;
            if (elite.statusEffects != null)
            {
                foreach (StatusEffectData effect in elite.statusEffects)
                {
                    effects.Add(effect);
                }
            }
        }
        if (effects.Count > 0)
        {
            state.StatusEffects = effects;
        }
        return state;
    }
}
