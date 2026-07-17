using Godot;
using Godot.Collections;

// A shared "ability tell": the consistent accent color a combat ability stamps
// onto its wielder's body so a mob READS as what it can do, regardless of which
// region it spawned in. Authored once per theme as a shared .tres and referenced
// from the ability data itself (WeaponData.abilityTell / StatusEffectData
// .abilityTell), so every species that fires the same ability shows the same
// accent — a fire drake and a torch goblin share one fire tell. This is why mob
// color is keyed on abilities, not regions: two regional variants with the same
// loadout resolve to the same base palette + the same tell, hence the same look.
//
// Applied at spawn as a shift of the mob's base palette toward accentColor (see
// ModelAnimator.ApplyPalette) — the species keeps its base identity while the
// accent signals the ability.
// [Tool] so the editor instantiates it as its real type when embedded under a
// [Tool] parent (e.g. StatusEffectData) — see the sub-resource convention in
// CLAUDE.md. Normally referenced as a shared .tres, not embedded.
[Tool]
[GlobalClass]
public partial class AbilityTellData : Resource
{
    // Body color this ability shifts the mob toward. Authored in sRGB.
    [Export] public Color accentColor = Colors.White;

    // How far the base palette lerps toward accentColor: 0 = base shows as-is,
    // 1 = the accent fully replaces the base tone. Mid values keep the species'
    // base identity legible while still reading the tell.
    [Export(PropertyHint.Range, "0,1,0.01")] public float strength = 0.5f;

    // When a mob has more than one telling ability (a venomous torchbearer),
    // only the highest-priority tell is shown — "one accent for the standout
    // ability". Ties keep the first found (weapons scanned before status effects).
    [Export] public int priority = 0;

    // The single strongest tell across a mob's weapons and intrinsic status
    // effects, or null when none carry one.
    public static AbilityTellData Select(Array<WeaponData> weapons, Array<StatusEffectData> statusEffects)
    {
        AbilityTellData best = null;
        if (weapons != null)
        {
            foreach (WeaponData weapon in weapons)
            {
                best = Stronger(best, weapon?.abilityTell);
            }
        }
        if (statusEffects != null)
        {
            foreach (StatusEffectData effect in statusEffects)
            {
                best = Stronger(best, effect?.abilityTell);
            }
        }
        return best;
    }

    private static AbilityTellData Stronger(AbilityTellData current, AbilityTellData candidate)
    {
        if (candidate == null)
        {
            return current;
        }
        if (current == null || candidate.priority > current.priority)
        {
            return candidate;
        }
        return current;
    }
}
