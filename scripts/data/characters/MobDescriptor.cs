using Godot;

// A spawn-facing composed mob: a base MobData species paired with per-instance
// overrides (palette recolor, held weapon, elite kind). Spawn sources hold a
// MobDescriptor instead of a bare MobData so biome variants — a desert vs swamp
// goblin — reuse one MobData + one MobScene instead of a duplicated species
// file. This is the mob analog of ItemDescriptor (composition, not inheritance):
// a spawn entry HAS-A descriptor; it is not a kind of one.
//
// CreateState stamps the overrides onto the MobSimState, which already owns the
// per-instance override channel (EliteStatusEffect, Language, …) and serializes
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

    // Force this spawn to be an elite carrying `eliteStatusEffect` as its
    // signature. Leave `elite` false to let the spawn entry roll it
    // (MobSpawnEntry.EliteChance), which draws a signature from the zone pool.
    // A forced elite with a null effect is still elite (size only / zone draw).
    [Export] public bool elite;
    [Export] public StatusEffectData eliteStatusEffect;

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
        if (elite)
        {
            state.Elite = true;
            state.EliteStatusEffect = eliteStatusEffect;
        }
        return state;
    }
}
