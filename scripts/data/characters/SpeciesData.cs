using Godot;

// A reusable species variant: a base MobData paired with the per-variant
// recolor, intrinsic status effects, and loot that define one species (a swamp
// goblin vs a desert goblin). Factored out of MobDescriptor so the SAME variant
// is authored once and shared by every descriptor that uses it — e.g. one
// goblin_swamp SpeciesData feeds both the plain and the elite swamp-goblin
// descriptor instead of duplicating the mob+palette+venom triple in each.
//
// This is the unit of bestiary identity: a runtime mob IS-A species
// (MobSimState.Species), and discovery / kill-leveling key on it (see
// SimState.DiscoveredSpecies). The base MobData (reached via `mob`) is the
// shared template AND the bestiary "page" that groups a type's species; this
// SpeciesData is one "row" on that page. A MobDescriptor HAS-A SpeciesData plus
// its own elite override (the species-side analog of EliteMobDescriptor). A
// pure loadout variant (a claw goblin vs a torch-bearing one) is its own
// SpeciesData, since weapons are a species trait — and thus its own bestiary row.
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
    // player weapon. MobDescriptor.CreateState stamps this onto
    // MobSimState.Weapons; BehaviorAttack fires the highest-priority weapon whose
    // gates pass and the in-hand prop is the primary weapon's held model. Because
    // weapons are a species trait, a loadout variant (a claw goblin vs a
    // torch-bearing one) is authored as its own SpeciesData — a distinct bestiary
    // row. Empty = a mob that never attacks.
    [Export] public Godot.Collections.Array<WeaponData> weapons = new();

    // Loot ejected from the mob's body when it dies (was a MobData field; lives
    // here so each zone variant drops its own spoils — e.g. a forest kun-kun
    // drops kun_kun_forest_meat, a desert one kun_kun_desert_meat, both parented
    // to the shared kun_kun_meat so "needs kun-kun meat" recipes still match).
    // Each entry spawns `count` Loot instances of its descriptor, fired outward
    // on the same upward arc chests use. MobDescriptor.CreateState stamps this
    // onto MobSimState.Loot (read by Mob.EjectLoot); empty = no drops.
    [Export] public Godot.Collections.Array<ItemCount> loot = new();
}
