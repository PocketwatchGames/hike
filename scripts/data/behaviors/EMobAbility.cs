using System;

// What a species can do that a shared brain may wire a node for. A brain is
// authored as the full set of behaviors; a node naming abilities in
// BehaviorNode.requiredAbilities is dropped, with every edge into it, from any
// mob whose SpeciesData.abilities lacks one (Mob.InitBehaviors). Stored in
// .tres as an int: append new bits, never reassign existing ones.
[Flags]
public enum EMobAbility
{
    None = 0,
    // Sidesteps an incoming projectile (the Attack -> Dodge reaction).
    Dodge = 1 << 0,
    // Digs into the ground to hide from a distant threat.
    Burrow = 1 << 1,
    // Registers a dead body at all; without it the mob also skips its corpse scan.
    InspectCorpses = 1 << 2,
}
