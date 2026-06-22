using Godot;

// A reusable species variant: a base MobData paired with the per-variant
// recolor and intrinsic status effects that define a sub-species (a swamp goblin
// vs a desert goblin). Factored out of MobDescriptor so the SAME variant is
// authored once and shared by every descriptor that uses it — e.g. one
// goblin_swamp SubSpeciesData feeds both the plain and the elite swamp-goblin
// descriptor instead of duplicating the mob+palette+venom triple in each. This
// is the species-side analog of EliteMobDescriptor (the elite signature shared
// across descriptors); a MobDescriptor HAS-A SubSpeciesData plus its own weapons
// and elite override.
[GlobalClass]
public partial class SubSpeciesData : Resource
{
    // Base species template for this variant.
    [Export] public MobData mob;

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

    // Loot ejected from the mob's body when it dies (was a MobData field; lives
    // here so each zone variant drops its own spoils — e.g. a forest kun-kun
    // drops kun_kun_forest_meat, a desert one kun_kun_desert_meat, both parented
    // to the shared kun_kun_meat so "needs kun-kun meat" recipes still match).
    // Each entry spawns `count` Loot instances of its descriptor, fired outward
    // on the same upward arc chests use. MobDescriptor.CreateState stamps this
    // onto MobSimState.Loot (read by Mob.EjectLoot); empty = no drops.
    [Export] public Godot.Collections.Array<ItemCount> loot = new();
}
