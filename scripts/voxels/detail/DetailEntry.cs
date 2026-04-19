using Godot;

// One scatterable mesh inside a DetailGroupData. Authored as a .tres alongside
// the sprite texture, so adding a new grass blade or flower means: create the
// PlaneMesh + ShaderMaterial in the editor, then reference the mesh from a new
// DetailEntry sub-resource on the group.
//
// Per-instance variation (position jitter, yaw, scale, ground-color tint) is
// computed at scatter time and packed into the MultiMesh's per-instance
// transform + custom data. The shader does the wind/explosion/player vertex
// displacement so animation costs are constant regardless of instance count.
[GlobalClass]
public partial class DetailEntry : Resource
{
    // The instanced quad. Author as a PlaneMesh oriented in the XY plane with
    // its base at Y=0 (so the bottom of the sprite sits on the ground when
    // placed). The mesh's surface-0 material must be the detail_sprite
    // ShaderMaterial with the sprite's albedo texture wired in.
    [Export] public Mesh Mesh;

    // Sampling weight within the parent group. The group picks an entry by
    // weighted choice — entries with weight 2.0 appear twice as often as
    // entries with weight 1.0. Weights are not normalized; they're relative.
    [Export] public float Weight = 1.0f;

    // Per-instance uniform scale jitter (multiplied onto the mesh's authored
    // size). 1.0 = no jitter; 1.0..1.0 = constant size; 0.9..1.1 = ±10%.
    [Export] public float ScaleMin = 0.9f;
    [Export] public float ScaleMax = 1.1f;

    // Per-instance random yaw rotation. Currently a no-op — detail_sprite.
    // gdshader Y-billboards in the vertex stage and ignores the per-instance
    // basis rotation entirely. Kept on the resource for future shaders that
    // render non-billboarded meshes (e.g. modelled flowers seen from any
    // angle), where uniform yaw across instances would tell.
    [Export] public bool RandomYaw = false;
}
