using Godot;

// Lightweight authoring marker placed in a tree (prop) scene to mark where
// wind-blown leaves shed from. Holds NO live particle system — it just
// registers its world position + chosen leaf emitter scene with
// WindParticleManager, which leases a pooled GpuParticles3D to nearby sources.
// This keeps the live particle-system count flat even with thousands of
// batch-rendered trees resident (see WorldPropScatter / MultimeshPropSprite).
// The node's transform is the authored data — the emission anchor (e.g. the
// canopy center).
[GlobalClass]
public partial class WindEmitterSource : Node3D
{
    // The leaf particle scene that sheds here (root = GPUParticles3D). The
    // color/look lives entirely in the scene's particle_lit material — pick a
    // different scene (green / yellow / red) per tree. Null = inert.
    [Export] public PackedScene EmitterScene { get; set; }

    public override void _Ready()
    {
        // Manager may not exist yet during a headless/editor load; null-safe
        // like MultimeshPropSprite guarding on Sim.Current.PropScatter.
        WindParticleManager.Current?.RegisterSource(this);
    }

    public override void _ExitTree()
    {
        // Chunk eviction / scene teardown cascades here, keeping the manager's
        // registry consistent with the resident entity set.
        WindParticleManager.Current?.UnregisterSource(this);
    }
}
