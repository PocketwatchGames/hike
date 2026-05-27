using System;

// Bitmask of circumstances that must ALL hold for a spawn entry's node to
// materialize when its chunk activates. Each set bit is a REQUIRED condition;
// an unset bit means "don't care". None (0) spawns unconditionally.
//
// This is a one-way SPAWN gate, not a presence gate: it is evaluated only when
// the chunk activates (and at the day↔night edge for night entries). Once a
// node has spawned it persists until its chunk evicts, even if conditions
// later change — a goblin caught out at dawn keeps hunting; a sparrow already
// in flight when rain starts is not yanked from the sky.
//
// Day + Night both set is contradictory (never spawns) — authors pick one.
//
// Wire values are stable — append new bits, never reassign existing ones, so
// existing .tres files and world saves keep loading.
[Flags]
public enum ESpawnConditions
{
    None        = 0,
    Day         = 1 << 0, // only when it is NOT night
    Night       = 1 << 1, // only at night
    Clear       = 1 << 2, // only when not raining (rain below RainSpawnThreshold)
    NotHeavyRain = 1 << 3, // only when rain is below SimData.HeavyRainSpawnThreshold
}
