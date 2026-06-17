using Godot;

// The signature package for an elite mob variant, factored out of MobDescriptor
// so the SAME elite kind (e.g. "lightning elite") is authored once and shared by
// every species/biome elite descriptor (desert, swamp, torchbearer goblins) via
// one elite_lightning.tres reference — change the signature in one place and all
// elites of that kind follow.
//
// A MobDescriptor turns elite by pointing its `elite` field at one of these
// (non-null = elite). MobDescriptor.CreateState composes this descriptor's
// StatusEffects on top of its own per-instance statusEffects, and stamps the
// badge onto the spawned MobSimState. [Tool] so the editor instantiates it as
// the real type when referenced from a [Tool] context.
[Tool]
[GlobalClass]
public partial class EliteMobDescriptor : Resource
{
    // The elite's signature status effects, applied to every mob spawned from a
    // descriptor that references this elite — composed alongside (not replacing)
    // the descriptor's own statusEffects. Each is routed at spawn the same way a
    // descriptor effect is: a weapon-mod effect composes onto the mob's weapons,
    // any other onto the mob's status controller (see Mob.ApplySpawnStatusEffect).
    [Export] public Godot.Collections.Array<StatusEffectData> statusEffects = new();

    // HUD badge icon for elites of this kind — the marker MobHUD pins to the
    // health bar. Null = no badge (MobHUD falls back to a zone-rolled elite's icon).
    [Export] public Texture2D badge;

    // The spinning halo/crown instanced over elites of this kind (a scene on
    // EliteCrown). Lets a signature carry its own marker — e.g. a lightning crown
    // distinct from a fire one. Null = fall back to the shared SimData.EliteCrownScene.
    [Export] public PackedScene crownScene;
}
