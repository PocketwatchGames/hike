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
// Atlas support: Texture may be either a standalone Texture2D or an
// AtlasTexture pointing at a region inside a shared decor atlas. AtlasTexture
// is detected in GetMaterial() — we bind the underlying atlas to the shader's
// sprite_texture sampler and pack the region (normalized) into the uv_region
// uniform so shader UV 0..1 still maps to the sub-region. AtlasTexture.
// GetWidth / GetHeight already return the region size, so the scatter's
// per-instance world size math needs no special case.
//
// Material handling: the sprite Texture lives as an export on this resource
// — NOT as a shader parameter on a per-entry material .tres. Reason:
// detail_sprite.gdshader uses globals (light_map, player_pos, etc.) that
// only exist at runtime, so the editor can't compile the shader and the
// material inspector can't surface its parameters; an authored .tres would
// silently lose its `shader_parameter/sprite_texture` line on resave. At
// scatter time, ChunkDetailScatter calls GetMaterial(), which lazily clones
// the shared detail_sprite.tres template and stamps Texture, TintMap, and the
// tint colors onto it.
[GlobalClass]
public partial class DetailEntry : Resource
{
    private const string MaterialTemplatePath = "res://resources/materials/detail_sprite.tres";

    // Sprite albedo. Wired through to the shader's `sprite_texture` uniform
    // when the runtime material is built. Its pixel dimensions (divided by
    // ChunkDetailScatter.PIXELS_PER_UNIT) drive the sprite's base world size.
    [Export] public Texture2D Texture;

    // Per-texel recolor mask wired through to the shader's `tint_map` uniform.
    // R = pull toward TintColorA, G = pull toward the ground color of the voxel
    // beneath the sprite, B = pull toward TintColorB; a black texel keeps the
    // source Texture color. Authored at the same pixel dimensions as Texture
    // and may be a standalone Texture2D or an AtlasTexture (same atlas handling
    // as Texture). Import the PNG as a non-sRGB / "Linear" data texture so its
    // channels read as raw weights. Leave null to render straight from Texture
    // (the shader's tint_map defaults to black).
    [Export] public Texture2D TintMap;

    // The two colors the tint map's R and B channels pull toward. White leaves
    // the masked region looking like the texture under the tint (a neutral
    // default); pick the actual recolor targets per entry.
    [Export] public Color TintColorA = Colors.White;
    [Export] public Color TintColorB = Colors.White;

    // Sampling weight within the parent group. The group picks an entry by
    // weighted choice — entries with weight 2.0 appear twice as often as
    // entries with weight 1.0. Weights are not normalized; they're relative.
    [Export] public float Weight = 1.0f;

    // Per-instance uniform scale jitter (multiplied onto the texture-derived
    // world size). 1.0..1.0 = constant size; 0.9..1.1 = ±10%.
    [Export] public float ScaleMin = 0.9f;
    [Export] public float ScaleMax = 1.1f;

    // Multiplier on wind sway AND player push for this entry. 1.0 = full
    // motion (grass, flowers — default); 0.0 = locked rigid (cave pebbles,
    // bones, anything that should sit still). Stamped into the cloned shader
    // material's `wind_strength` parameter by GetMaterial().
    [Export(PropertyHint.Range, "0,1,0.01")] public float WindStrength = 1.0f;

    // Per-entry response to surface wetness. Scales BOTH the wet darken and
    // the wet sheen proportionally (it multiplies the shader's wet_factor, the
    // shared term both effects derive from). 1.0 = wets out fully alongside the
    // terrain (grass, leafy plants — default); 0.0 = stays dry-looking under
    // any rain (dead twigs, bones, cave pebbles). Values in between let a moss
    // or a waxy leaf wet out more or less than the ground beneath it. Stamped
    // into the cloned shader material's `wet_strength` parameter by
    // GetMaterial(); the global wetness level/floor/spec tunables still gate
    // the overall look, this is just the per-entry weight on top.
    [Export(PropertyHint.Range, "0,1,0.01")] public float WetStrength = 1.0f;

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
            ResolveAtlas(Texture, out Texture2D spriteTex, out Vector4 spriteRegion);
            mat.SetShaderParameter("sprite_texture", spriteTex);
            mat.SetShaderParameter("uv_region", spriteRegion);
        }
        if (TintMap != null)
        {
            ResolveAtlas(TintMap, out Texture2D tintTex, out Vector4 tintRegion);
            mat.SetShaderParameter("tint_map", tintTex);
            mat.SetShaderParameter("tint_uv_region", tintRegion);
        }
        mat.SetShaderParameter("tint_color_a", TintColorA);
        mat.SetShaderParameter("tint_color_b", TintColorB);
        mat.SetShaderParameter("wind_strength", WindStrength);
        mat.SetShaderParameter("wet_strength", WetStrength);
        // Match the terrain's baked-AO darkening strength so sheltered grass
        // darkens in lockstep with the ground (CVars.aoStrength also feeds the
        // terrain material via ChunkMesh.SetAoStrength). Read at material-build
        // time — a live CVar change re-tints terrain immediately but grass only
        // after a re-scatter (chunk reload), which is fine for this tuning knob.
        mat.SetShaderParameter("ao_strength", CVars.aoStrength.Value);
        _materialCache = mat;
        return mat;
    }

    // Unwraps AtlasTexture to (underlying atlas texture, normalized region).
    // For a plain Texture2D the atlas is the texture itself and the region is
    // the identity rect (0,0,1,1), matching the shader's uv_region default so
    // non-atlas entries sample full 0..1 UV unchanged. Region normalization
    // divides by the atlas pixel size.
    private static void ResolveAtlas(Texture2D tex, out Texture2D atlas, out Vector4 region)
    {
        if (tex is AtlasTexture at && at.Atlas != null)
        {
            atlas = at.Atlas;
            float atlasW = at.Atlas.GetWidth();
            float atlasH = at.Atlas.GetHeight();
            if (atlasW > 0f && atlasH > 0f)
            {
                Rect2 r = at.Region;
                region = new Vector4(r.Position.X / atlasW, r.Position.Y / atlasH, r.Size.X / atlasW, r.Size.Y / atlasH);
                return;
            }
        }
        atlas = tex;
        region = new Vector4(0f, 0f, 1f, 1f);
    }
}
