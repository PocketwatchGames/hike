using Godot;
using Godot.Collections;

// A named set of animation bindings for one loadout. Two roles, same type:
//
//   - The BASE (unarmed) set — referenced by PlayerData.baseAnims — authors every
//     slot the player can play. It is the single source of truth that used to live
//     inline on PlayerData.
//   - Per-weapon override sets — referenced by WeaponData.animSet — author only the
//     slots that weapon changes (its idle / run / charge poses / attacks / block).
//     Any slot absent here falls back to the base set.
//
// Resolution (Player.AnimName): wielded weapon's override ?? base. One path for
// every animation, loop or one-shot. Each slot binds to an AnimationData — a single
// per-slot structure carrying the clip name AND its flags (affectedBySpeedMultiplier,
// hidesHeldItem) together, not split across parallel collections. The clip name must
// exist in the player's combined AnimationLibrary (swordsman_anims.res); a missing
// name falls back silently at runtime, and Validate() surfaces it at load.
//
// The flags are universal per slot, so they're read from the BASE set (PlayerData
// delegates IsAnimationSpeedAffected / AnimationHidesHeldItem to it). An override
// set only needs to fill in `name`; its flag fields are inert.
[GlobalClass]
public partial class WeaponAnimSet : Resource
{
    [Export] public Dictionary<EAnimation, AnimationData> overrides = new();

    // The per-slot binding, or null when this set doesn't define the slot.
    public AnimationData Get(EAnimation anim)
    {
        if (overrides != null && overrides.TryGetValue(anim, out AnimationData d))
        {
            return d;
        }
        return null;
    }

    // Clip name for a slot, or default (empty) when undefined — the caller
    // composes "empty => fall back to the base set / unarmed clip".
    public StringName GetOverride(EAnimation anim)
    {
        AnimationData d = Get(anim);
        return d != null ? d.name : default;
    }

    public bool IsSpeedAffected(EAnimation anim)
    {
        AnimationData d = Get(anim);
        return d != null && d.affectedBySpeedMultiplier;
    }

    // Whether `clipName` is one of this set's hides-held-item poses. Keyed by clip
    // name (not slot) so HeldItemVisual can test the animator's current clip
    // directly; the hides poses live on the base set, never weapon-overridden.
    public bool HidesHeldItemClip(StringName clipName)
    {
        if (clipName == default || overrides == null)
        {
            return false;
        }
        foreach (System.Collections.Generic.KeyValuePair<EAnimation, AnimationData> kvp in overrides)
        {
            AnimationData d = kvp.Value;
            if (d != null && d.hidesHeldItem && d.name == clipName)
            {
                return true;
            }
        }
        return false;
    }

    // Load-time check: every authored clip name must exist in the live animation
    // library. Missing names are logged (not fatal — they fall back at runtime),
    // catching the one fragile link: a clip string that doesn't match a baked clip.
    public void Validate(System.Func<StringName, bool> hasAnimation, string label)
    {
        if (overrides == null || hasAnimation == null)
        {
            return;
        }
        foreach (System.Collections.Generic.KeyValuePair<EAnimation, AnimationData> kvp in overrides)
        {
            AnimationData d = kvp.Value;
            if (d != null && d.name != default && !hasAnimation(d.name))
            {
                GD.PushError($"WeaponAnimSet '{label}': slot {kvp.Key} -> '{d.name}' has no clip in the animation library.");
            }
        }
    }
}
