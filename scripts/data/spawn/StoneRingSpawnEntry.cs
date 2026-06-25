using System;
using Godot;

// Bespoke fixture: a geometric ring of stone props centered on the anchor,
// used to bound the swamp starting village. Stones are spaced by arc length
// around a circle of radius Radius. SelfPlaces so a SpawnGroupData hands it the
// anchor directly (the grassy scatter sampler would otherwise re-place it off
// center) — each stone then resolves its own column surface height so the ring
// follows the ground.
[GlobalClass]
public partial class StoneRingSpawnEntry : SpawnEntryData
{
    // Stone prop scenes; one is chosen at random per stone for variety. Placed
    // as PropType.Tree — the same path the kit rock scatter uses, so they pick
    // up the tree collider's path-blocking footprint.
    [Export] public PackedScene[] Scenes = System.Array.Empty<PackedScene>();

    // Ring radius and target arc spacing between adjacent stones, in meters.
    // Stone count = round(circumference / Spacing), clamped to >= 3.
    [Export] public float Radius = 15f;
    [Export] public float Spacing = 5f;

    // Per-stone random radial jitter (meters) so the ring doesn't read as a
    // machined circle. 0 = exact circle.
    [Export] public float RadiusJitter = 0f;

    // Added to each stone's resolved surface height. Defaults to 1.5 to match
    // the kit rock scatter: the mesher smooths a flat column's visible top to
    // 0.5 above the voxel-grid top, so +1 would bury the stone's base.
    [Export] public float GroundYOffset = 1.5f;

    public override bool SelfPlaces => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scenes == null || Scenes.Length == 0 || Spacing <= 0f || Radius <= 0f)
        {
            return;
        }
        int count = Mathf.Max(3, Mathf.RoundToInt(Mathf.Tau * Radius / Spacing));
        for (int i = 0; i < count; i++)
        {
            float angle = (float)i / count * Mathf.Tau;
            float r = Radius + ((float)rng.NextDouble() * 2f - 1f) * RadiusJitter;
            int wx = Mathf.FloorToInt(position.X + r * Mathf.Cos(angle));
            int wz = Mathf.FloorToInt(position.Z + r * Mathf.Sin(angle));

            float y = context?.SurfaceYAt != null
                ? context.SurfaceYAt(wx, wz) + GroundYOffset
                : position.Y + (GroundYOffset - 1f);

            PackedScene scene = Scenes[rng.Next(Scenes.Length)];
            if (scene == null)
            {
                continue;
            }
            ws.AddEntity(new PropSimState(PropType.Tree,
                new Vector3(wx + 0.5f, y, wz + 0.5f),
                scene)
            {
                RotationY = (float)rng.NextDouble() * Mathf.Tau,
            });
        }
    }
}
