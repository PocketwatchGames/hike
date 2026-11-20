using Godot;
using Godot.Collections;

// The after-dark threat a habitat carries: which mobs NightMobSpawner raises in
// the dark around the player, and how the danger scalar that drives it builds.
// Read live from the habitat under the player (HabitatData.nightSpawn), so
// retuning needs no rebake. No mobs (or no NightSpawnData at all) = nothing
// spawns there, neither by night nor from darkness dwell.
[GlobalClass]
public partial class NightSpawnData : Resource
{
    // Species materialized in dark spots around the player, one picked at random
    // per spawn. TRANSIENT (not persisted to WorldState) — the live population
    // near the player IS the whole mechanic. Empty = dormant.
    [Export] public Array<SpeciesData> mobs = new();

    // Live population the spawner drives toward at full danger. Measured against
    // currently-loaded mobs of `mobs` near the player.
    [Export(PropertyHint.Range, "0,120,1")] public int maxPopulation = 18;

    // The whole mechanic runs off ONE danger scalar = max(time-of-day term,
    // darkness dwell), driving spawn rate, population cap and level together.
    // The player's own light is NOT a spawn input — it's the separate
    // concealment axis (slime vision, MobData.darkness*). See Sim.DarknessDwell.

    // Shapes the TIME term: pow(nightProgress, this), where nightProgress is 0 at
    // sunset → 1 at midnight → 0 at dawn (and 0 all day). >1 keeps the evening
    // and the small hours calm and peaks hard around midnight; 1 = linear.
    [Export(PropertyHint.Range, "0.25,6,0.05")] public float timeDangerCurve = 2.5f;

    // DARKNESS term. Perceived light [0,1] at the player at/below which the spot
    // counts as fully dark (dwell targets 1); ramps linearly to 0 as light reaches
    // this. Keep below a windowed-but-roofed interior (~0.13-0.17) so only
    // near-black spaces accumulate — night itself is the time term's job.
    [Export(PropertyHint.Range, "0,1,0.01")] public float darkThreshold = 0.1f;
    // Seconds for the dwell to charge 0 → full in pitch black.
    [Export(PropertyHint.Range, "5,300,1")] public float darkRiseSeconds = 90f;
    // Seconds for the dwell to drain to 0 in full light.
    [Export(PropertyHint.Range, "1,120,1")] public float darkFallSeconds = 15f;

    // Spawn interval (s) at full danger (fast) and near-zero danger (slow),
    // lerped by danger.
    [Export(PropertyHint.Range, "0.5,30,0.5")] public float intervalSeconds = 4f;
    [Export(PropertyHint.Range, "0.5,120,0.5")] public float slowIntervalSeconds = 20f;

    // Cap on new mobs per interval, so a large deficit fills in over several
    // intervals rather than as a sudden wall.
    [Export(PropertyHint.Range, "1,20,1")] public int maxPerSweep = 3;

    // Spawn ring around the player (m). Max must stay inside the loaded entity
    // radius, and also sizes the nav-grid window scanned each cycle.
    [Export(PropertyHint.Range, "4,80,1")] public float minRadius = 8f;
    [Export(PropertyHint.Range, "4,120,1")] public float maxRadius = 24f;

    // A candidate spawns only where BLOCK light (torches, campfires, lanterns —
    // peak channel) is at or below this, so mobs never appear inside firelight.
    [Export(PropertyHint.Range, "0,1,0.01")] public float maxBlockLight = 0.28f;
    // Weighting of the pick toward darker candidates: pow(darkness, this).
    // 0 = uniform; higher = strongly favor the darkest spot.
    [Export(PropertyHint.Range, "0,6,0.05")] public float darknessBias = 2f;

    // Level at full danger: a spawn's level is round(danger × this). Already-
    // spawned mobs keep the level they arrived at.
    [Export(PropertyHint.Range, "0,4,1")] public int maxLevel = 4;

    public bool SpawnsAnything()
    {
        return mobs != null && mobs.Count > 0;
    }
}
