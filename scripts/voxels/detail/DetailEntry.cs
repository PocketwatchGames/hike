using Godot;

// One scatterable sprite inside a DetailGroupData. Add a new grass blade /
// flower / pebble by creating a .tres with this script, wiring its mesh +
// texture, and dropping it into the parent group's Entries array.
//
// Per-instance variation (position jitter, yaw, scale, ground-color tint) is
// computed at scatter time and packed into the MultiMesh's per-instance
// transform + custom data. The shader does the wind/explosion/player vertex
// displacement so animation costs are constant regardless of instance count.
//
// Material handling: the sprite Texture + FadeHeight live as exports on this
// resource — NOT as shader parameters on a per-entry material .tres. Reason:
// detail_sprite.gdshader uses globals (light_map, player_pos, etc.) that
// only exist at runtime, so the editor can't compile the shader and the
// material inspector can't surface its parameters; an authored .tres would
// silently lose its `shader_parameter/sprite_texture` line on resave. At
// scatter time, ChunkDetailScatter calls GetMaterial(), which lazily clones
// the shared detail_sprite.tres template and stamps these exports onto it.
// Same pattern LitSprite uses for the existing sprite_lit shader.
[GlobalClass]
public partial class DetailEntry : Resource
{
    private const string MaterialTemplatePath = "res://resources/materials/detail_sprite.tres";

    // The instanced quad. Author as a QuadMesh in the XY plane with its base
    // at Y=0 (use center_offset = (0, height/2, 0)). The mesh's own surface
    // material is ignored — ChunkDetailScatter sets material_override per
    // MultiMeshInstance3D from this entry's GetMaterial().
    [Export] public Mesh Mesh;

    // Sprite albedo. Wired through to the shader's `sprite_texture` uniform
    // when the runtime material is built.
    [Export] public Texture2D Texture;

    // Bottom-fade band in world units. The bottom `FadeHeight` of the sprite
    // blends from Texture into the per-instance ground color (baked at
    // scatter time). 0.625 ≈ 10 source pixels at 16 px/unit; 0.738 ≈ 10
    // source pixels at the project's 0.0738 m/px sprite scale.
    [Export] public float FadeHeight = 0.625f;

    // Sampling weight within the parent group. The group picks an entry by
    // weighted choice — entries with weight 2.0 appear twice as often as
    // entries with weight 1.0. Weights are not normalized; they're relative.
    [Export] public float Weight = 1.0f;

    // Per-instance uniform scale jitter (multiplied onto the mesh's authored
    // size). 1.0..1.0 = constant size; 0.9..1.1 = ±10%.
    [Export] public float ScaleMin = 0.9f;
    [Export] public float ScaleMax = 1.1f;

    // Per-instance random yaw rotation. Currently a no-op — detail_sprite.
    // gdshader Y-billboards in the vertex stage and ignores the per-instance
    // basis rotation entirely. Kept on the resource for future shaders that
    // render non-billboarded meshes (e.g. modelled flowers seen from any
    // angle), where uniform yaw across instances would tell.
    [Export] public bool RandomYaw = false;

    // Lazily-built ShaderMaterial cache. Built once per DetailEntry instance
    // (Godot caches loaded resources, so the same entry shared across many
    // chunks reuses the same material — one shader compile, one GPU upload).
    // Not [Export]; reset whenever the resource is reloaded from disk.
    private ShaderMaterial _materialCache;

    public ShaderMaterial GetMaterial()
    {
        if (_materialCache != null)
        {
            return _materialCache;
        }
        var template = GD.Load<ShaderMaterial>(MaterialTemplatePath);
        if (template == null)
        {
            GD.PushError($"DetailEntry: could not load material template at {MaterialTemplatePath}");
            return null;
        }
        var mat = (ShaderMaterial)template.Duplicate();
        if (Texture != null)
        {
            mat.SetShaderParameter("sprite_texture", Texture);
        }
        mat.SetShaderParameter("fade_height", FadeHeight);
        _materialCache = mat;
        return mat;
    }
}
