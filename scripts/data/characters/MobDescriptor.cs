using Godot;

// A spawn-facing composed mob: a reusable SpeciesData (base species + recolor +
// intrinsic stat modifiers + status effects + weapons) paired with a
// per-descriptor elite override. Spawn sources hold a MobDescriptor instead of a
// bare MobData so biome variants reuse one SpeciesData (and its mobScene) instead
// of a duplicated species file. This is the mob analog of ItemDescriptor
// (composition, not inheritance): a spawn entry HAS-A descriptor; it is not a
// kind of one.
//
// CreateState stamps the overrides onto the MobSimState, which already owns the
// per-instance override channel (StatusEffects, Language, …) and serializes
// it — so a composed mob survives chunk eviction and save/load without a
// bespoke species resource.
[GlobalClass]
public partial class MobDescriptor : Resource
{
    // The reusable species variant this descriptor spawns — base MobData plus
    // its recolor, stat modifiers, and intrinsic status effects. Shared across
    // descriptors so the plain and elite (and torchbearer) variants of a swamp
    // goblin all point at one goblin_swamp SpeciesData. See SpeciesData.
    [Export] public SpeciesData species;

    // Marks this as an elite variant (25% larger, crown, shared elite buff,
    // crown-trophy loot). Non-null = elite: point it at a shared elite_*.tres
    // (an EliteMobDescriptor) that carries the signature status effects + HUD
    // badge for that elite kind, so one signature is authored once and reused by
    // every species/biome elite descriptor. Null = an ordinary (non-elite) mob.
    [Export] public EliteMobDescriptor elite;

    // Base difficulty tier for mobs spawned from this descriptor. WorldGen adds
    // its per-area level field on top (see SpawnContext.MobLevel), so this is
    // a floor an authored variant can raise — e.g. a mini-boss descriptor that
    // starts at level 2 everywhere. Each level scales health, armor, and outgoing
    // damage by SimData.levelScalePerLevel (~1.5x/level) and shows as level+1 HUD
    // pips. 0 = base.
    [Export(PropertyHint.Range, "0,4,1")] public int level = 0;

    // Convenience accessor for the descriptor's base species template (null when
    // no species is set). Spawn gates and worldgen probes read this without
    // reaching through species.
    public MobData mob => species?.mob;

    // Build the runtime sim state for this composed mob at the given transform,
    // with the overrides stamped on. Returns null when the descriptor has no
    // usable species (mirrors ItemDescriptor.CreateState's null-on-unset guard).
    // sceneOverride lets a placement swap the rig per individual (e.g. a male vs
    // female villager) without forking the species — see NpcSpawnEntry.Scene.
    // Null falls back to the species' base MobData.mobScene. The scene is fixed
    // at construction (EntitySimState.Scene is readonly) and serializes with the
    // mob, so an overridden rig survives chunk eviction and save/load.
    // `levelOverride` sets the mob's difficulty tier; null uses the descriptor's
    // authored base `level`. WorldGen passes the per-area-bumped tier here (see
    // MobSpawnEntry.Spawn). The level reaches the MobSimState constructor so
    // vitals are scaled to it at creation — before the state is ever serialized —
    // rather than patched afterward. (The constructor forces non-dangerous mobs
    // back to level 0.)
    public MobSimState CreateState(Vector3 worldPosition, float rotationY, PackedScene sceneOverride = null, int? levelOverride = null, float levelScalePerLevel = 1.5f)
    {
        MobData mobData = species?.mob;
        PackedScene scene = sceneOverride ?? mobData?.mobScene;
        if (mobData == null || scene == null)
        {
            return null;
        }
        var state = new MobSimState(worldPosition, rotationY, scene, mobData, levelOverride ?? level, levelScalePerLevel);
        // The species is the mob's bestiary identity (discovery / kill-leveling
        // key) as well as the source of its recolor / loot / stat modifiers.
        state.Species = species;
        state.Palette = species.palette;
        if (species.weapons != null && species.weapons.Count > 0)
        {
            state.Weapons = species.weapons;
        }
        if (species.loot != null && species.loot.Count > 0)
        {
            state.Loot = species.loot;
        }
        // Compose this species' intrinsic status effects with the elite
        // signature's (if any) into one list so both apply at spawn.
        var effects = new Godot.Collections.Array<StatusEffectData>();
        if (species.statusEffects != null)
        {
            foreach (StatusEffectData effect in species.statusEffects)
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
