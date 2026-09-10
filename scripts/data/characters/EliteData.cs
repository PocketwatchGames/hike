using Godot;

// The signature package for an elite mob: the status effects, HUD badge and
// crown that make one "kind" of elite (a lightning elite, a stoneskin one).
// Authored once and shared by every spawn that raises a mob to it — change the
// signature here and all elites of that kind follow.
//
// **Elite is a property of a SPAWN, not of a species.** A spawn source turns its
// mob elite by pointing MobSpawnEntry.elite at one of these (non-null = elite),
// and SpeciesData.CreateState composes these effects on top of the species' own
// and stamps the badge onto the spawned MobSimState. So an elite goblin stays
// the same SpeciesData — one bestiary row, one discovery, one kill-quest target
// — as a plain one, and no species is forked to make an elite of it.
//
// [Tool] so the editor instantiates it as the real type when referenced from a
// [Tool] context.
[Tool]
[GlobalClass]
public partial class EliteData : Resource
{
    // The elite's signature status effects, applied to every mob spawned elite
    // through this signature — composed alongside (not replacing) the species'
    // own statusEffects. Each is routed at spawn the same way a species effect
    // is: a weapon-mod effect composes onto the mob's weapons, any other onto
    // the mob's status controller (see Mob.ApplySpawnStatusEffect).
    [Export] public Godot.Collections.Array<StatusEffectData> statusEffects = new();

    // HUD badge icon for elites of this kind — the marker MobHUD pins to the
    // health bar. Null = no badge.
    [Export] public Texture2D badge;

    // The spinning halo/crown instanced over elites of this kind (a scene on
    // EliteCrown). Lets a signature carry its own marker — e.g. a lightning crown
    // distinct from a fire one. Null = fall back to the shared SimData.EliteCrownScene.
    [Export] public PackedScene crownScene;
}
