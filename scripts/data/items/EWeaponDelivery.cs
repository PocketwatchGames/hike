using System;

// How a weapon delivers its attacks — used to gate which weapon mods may attach.
// On WeaponData it's a CAPABILITY set (a weapon can deliver several ways, e.g. a
// melee weapon with a charged throw); on WeaponModData.requiredDelivery it's a
// REQUIREMENT mask. A mod attaches when its requirement is None (no restriction)
// or shares any bit with the weapon's capabilities. Most mods (Vampiric, Flaming,
// Knockback) apply to anything that lands a hit and leave the requirement None;
// only delivery-specific mods (Fragile detonate-on-contact, Charged Pierce) set it.
// Wire values are stable — append new bits, never reassign existing ones.
[Flags]
public enum EWeaponDelivery
{
    None = 0,
    Melee = 1 << 0,
    Shot = 1 << 1,
    Thrown = 1 << 2,
}
