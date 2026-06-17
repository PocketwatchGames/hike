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

    // Held weapon prop override. Null = fall back to the held model of the mob's
    // primary weapon (MobData.weapons). Set this to give a variant a different
    // in-hand prop than its weapon would otherwise show.
    [Export] public PackedScene heldWeaponScene;
    [Export] public EHand heldWeaponHand = EHand.Right;

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
        state.HeldWeaponScene = heldWeaponScene;
        state.HeldWeaponHand = heldWeaponHand;
        if (elite)
        {
            state.Elite = true;
            state.EliteStatusEffect = eliteStatusEffect;
        }
        return state;
    }
}
