using Godot;
using Godot.Collections;

// Per-weapon-type animation overrides, wired onto WeaponData.animSet and
// consulted by Player.UpdateAnimation while THIS weapon is the one in hand.
//
// A single SPARSE map over EAnimation slots: author only the slots this weapon
// changes (its own idle / run / sneak / attack / charge poses / block); any slot
// absent here falls back to the player's unarmed clip (PlayerData.animations).
// This is the one and only place per-weapon clips live — every animation, loop
// or one-shot, resolves through the same chokepoint (Player.AnimName), so there
// is exactly one path from "what the player is doing" to "which clip plays".
//
// The weapon-specific poses (charge tiers, block) are just EAnimation slots like
// any other — Charge1Idle/Walk/Run, Charge2Idle/Walk/Run, Block, Attack, Attack2
// — selected by UpdateAnimation from runner state and resolved here. Nothing is
// keyed by a bespoke field anymore.
//
// Clip names resolve against the player's combined AnimationLibrary
// (swordsman_anims.res); clips are added through PlayerAnimManifest (drop the
// FBX in the anims folder named to match, rebuild). An unmapped slot, or a name
// the active animator doesn't have, falls back cleanly — so this set is inert
// until the matching art exists.
[GlobalClass]
public partial class WeaponAnimSet : Resource
{
    [Export] public Dictionary<EAnimation, StringName> overrides = new();

    // Override clip for a slot, or default (empty) when this weapon doesn't
    // change it — the caller composes "empty => unarmed fallback".
    public StringName GetOverride(EAnimation anim)
    {
        if (overrides != null && overrides.TryGetValue(anim, out StringName name))
        {
            return name;
        }
        return default;
    }
}
