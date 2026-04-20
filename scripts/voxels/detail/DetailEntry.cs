using Godot;

// One scatterable sprite inside a DetailGroupData. Add a new grass blade /
// flower / pebble by creating a .tres with this script, wiring its texture,
// and dropping it into the parent group's Entries array.
//
// Geometry is always a shared unit QuadMesh; the scatter sizes each instance
// to (Texture.Width / PixelsPerUnit, Texture.Height / PixelsPerUnit) in
// world units and folds ScaleMin/ScaleMax in as a uniform multiplier. This
// way trimming a source PNG doesn't change the sprite's visible size and
// there's no per-entry mesh to keep in sync with texture aspect.
//
// Material handling: the sprite Texture lives as an export on this resource
// — NOT as a shader parameter on a per-entry material .tres. Reason:
// detail_sprite.gdshader uses globals (light_map, player_pos, etc.) that
// only exist at runtime, so the editor can't compile the shader and the
// material inspector can't surface its parameters; an authored .tres would
// silently lose its `shader_parameter/sprite_texture` line on resave. At
// scatter time, ChunkDetailScatter calls GetMaterial(), which lazily clones
// the shared detail_sprite.tres template and stamps Texture onto it.
[GlobalClass]
public partial class DetailEntry : Resource
{
    private const string MaterialTemplatePath = "res://resources/materials/detail_sprite.tres";

    // Sprite albedo. Wired through to the shader's `sprite_texture` uniform
    // when the runtime material is built. Its pixel dimensions (divided by
    // ChunkDetailScatter.PIXELS_PER_UNIT) drive the sprite's base world size.
    [Export] public Texture2D Texture;

    // Sampling weight within the parent group. The group picks an entry by
    // weighted choice — entries with weight 2.0 appear twice as often as
    // entries with weight 1.0. Weights are not normalized; they're relative.
    [Export] public float Weight = 1.0f;

    // Per-instance uniform scale jitter (multiplied onto the texture-derived
    // world size). 1.0..1.0 = constant size; 0.9..1.1 = ±10%.
    [Export] public float ScaleMin = 0.9f;
    [Export] public float ScaleMax = 1.1f;

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
        _materialCache = mat;
        return mat;
    }
}
