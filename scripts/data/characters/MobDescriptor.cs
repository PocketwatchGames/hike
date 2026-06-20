using Godot;

// A spawn-facing composed mob: a reusable SubSpeciesData (base species + recolor
// + intrinsic status effects) paired with per-descriptor overrides (held
// weapons, elite kind). Spawn sources hold a MobDescriptor instead of a bare
// MobData so biome/loadout variants — a claw goblin vs a torch-bearing camp
// goblin — reuse one SubSpeciesData (and its MobScene) instead of a duplicated
// species file. This is the mob analog of ItemDescriptor (composition, not
// inheritance): a spawn entry HAS-A descriptor; it is not a kind of one.
//
// CreateState stamps the overrides onto the MobSimState, which already owns the
// per-instance override channel (StatusEffects, Language, …) and serializes
// it — so a composed mob survives chunk eviction and save/load without a
// bespoke species resource.
[GlobalClass]
public partial class MobDescriptor : Resource
{
    // The reusable species variant this descriptor spawns — base MobData plus
    // its recolor and intrinsic status effects. Shared across descriptors so the
    // plain and elite (and torchbearer) variants of a swamp goblin all point at
    // one goblin_swamp SubSpeciesData. See SubSpeciesData.
    [Export] public SubSpeciesData subSpecies;

    // Weapon loadout for mobs spawned from this descriptor — the home for a
    // mob's weapons (NOT a species trait on MobData). Each WeaponData carries
    // its own action timeline, damage / continuous profiles, in-hand held model,
    // and AI engagement tuning (range / cooldown / ally gate / priority), exactly
    // like a player weapon. CreateState stamps this onto MobSimState.Weapons;
    // BehaviorAttack fires the highest-priority weapon whose gates pass and the
    // in-hand prop is the primary weapon's held model. Because weapons live here,
    // the SAME subspecies fights differently per spawn context by authoring a
    // second descriptor (a claw goblin vs a torch-bearing camp goblin) rather
    // than a duplicate species file. Empty = a mob that never attacks.
    [Export] public Godot.Collections.Array<WeaponData> weapons = new();

    // Marks this as an elite variant (25% larger, crown, shared elite buff,
    // crown-trophy loot). Non-null = elite: point it at a shared elite_*.tres
    // (an EliteMobDescriptor) that carries the signature status effects + HUD
    // badge for that elite kind, so one signature is authored once and reused by
    // every species/biome elite descriptor. Null = an ordinary (non-elite) mob.
    [Export] public EliteMobDescriptor elite;

    // Convenience accessor for the descriptor's base species (null when no
    // subSpecies is set). Spawn gates and worldgen probes read this without
    // reaching through subSpecies.
    public MobData mob => subSpecies?.mob;

    // Build the runtime sim state for this composed mob at the given transform,
    // with the overrides stamped on. Returns null when the descriptor has no
    // usable species (mirrors ItemDescriptor.CreateState's null-on-unset guard).
    public MobSimState CreateState(Vector3 worldPosition, float rotationY)
    {
        MobData mobData = subSpecies?.mob;
        if (mobData == null || mobData.MobScene == null)
        {
            return null;
        }
        var state = new MobSimState(worldPosition, rotationY, mobData.MobScene, mobData);
        state.Palette = subSpecies.palette;
        if (weapons != null && weapons.Count > 0)
        {
            state.Weapons = weapons;
        }
        // Compose this subspecies' intrinsic status effects with the elite
        // signature's (if any) into one list so both apply at spawn.
        var effects = new Godot.Collections.Array<StatusEffectData>();
        if (subSpecies.statusEffects != null)
        {
            foreach (StatusEffectData effect in subSpecies.statusEffects)
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
