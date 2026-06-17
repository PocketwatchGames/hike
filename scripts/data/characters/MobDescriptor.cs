using Godot;

// A spawn-facing composed mob: a base MobData species paired with per-instance
// overrides (palette recolor, held weapon, elite kind). Spawn sources hold a
// MobDescriptor instead of a bare MobData so biome variants — a desert vs swamp
// goblin — reuse one MobData + one MobScene instead of a duplicated species
// file. This is the mob analog of ItemDescriptor (composition, not inheritance):
// a spawn entry HAS-A descriptor; it is not a kind of one.
//
// CreateState stamps the overrides onto the MobSimState, which already owns the
// per-instance override channel (StatusEffects, Language, …) and serializes
// it — so a composed mob survives chunk eviction and save/load without a
// bespoke species resource.
[GlobalClass]
public partial class MobDescriptor : Resource
{
    [Export] public MobData mob;

    // Recolor override. Null = fall back to the species' own MobData.palette
    // (usually none). See MobPalette / ModelAnimator.
    [Export] public MobPalette palette;

    // Weapon loadout for mobs spawned from this descriptor — the home for a
    // mob's weapons (NOT a species trait on MobData). Each WeaponData carries
    // its own action timeline, damage / continuous profiles, in-hand held model,
    // and AI engagement tuning (range / cooldown / ally gate / priority), exactly
    // like a player weapon. CreateState stamps this onto MobSimState.Weapons;
    // BehaviorAttack fires the highest-priority weapon whose gates pass and the
    // in-hand prop is the primary weapon's held model. Because weapons live here,
    // the SAME species fights differently per spawn context by authoring a second
    // descriptor (a claw goblin vs a torch-bearing camp goblin) rather than a
    // duplicate species file. Empty = a mob that never attacks.
    [Export] public Godot.Collections.Array<WeaponData> weapons = new();

    // Marks this as an elite variant (25% larger, crown, shared elite buff,
    // crown-trophy loot). Elites are authored as their own *_elite.tres
    // descriptor and given a rarer spawn entry — set this true there, pair it
    // with a signature effect in `statusEffects` and a `badge`.
    [Export] public bool elite;

    // Status effects applied to every mob spawned from this descriptor, elite or
    // not — a per-instance buff/aura channel independent of the elite signature.
    // Each is routed at spawn the same way the elite signature is: a weapon-mod
    // effect composes onto the mob's weapons, any other is added to the mob's own
    // status controller. Empty = none.
    [Export] public Godot.Collections.Array<StatusEffectData> statusEffects = new();

    // HUD badge icon for this composed mob — the marker MobHUD pins to the
    // health bar (the descriptor's analog of the elite signature's icon). Set it
    // alongside a signature effect in `statusEffects` so an authored
    // `*_elite.tres` carries its own badge instead of drawing one from the zone
    // pool. Null = no badge (MobHUD falls back to a zone-rolled elite's icon).
    [Export] public Texture2D badge;

    // Build the runtime sim state for this composed mob at the given transform,
    // with the overrides stamped on. Returns null when the descriptor has no
    // usable species (mirrors ItemDescriptor.CreateState's null-on-unset guard).
    public MobSimState CreateState(Vector3 worldPosition, float rotationY)
    {
        if (mob == null || mob.MobScene == null)
        {
            return null;
        }
        var state = new MobSimState(worldPosition, rotationY, mob.MobScene, mob);
        state.Palette = palette;
        if (weapons != null && weapons.Count > 0)
        {
            state.Weapons = weapons;
        }
        if (statusEffects != null && statusEffects.Count > 0)
        {
            state.StatusEffects = statusEffects;
        }
        state.Badge = badge;
        if (elite)
        {
            state.Elite = true;
        }
        return state;
    }
}
