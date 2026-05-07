// Minimap fragment shader. Renders two render-state snapshots (A = old,
// B = live) and crossfades between them so mode toggles (outdoor↔indoor)
// and slice-level crossings transition smoothly instead of snapping.
//
// Each state has its own surface + exploration textures, world extent /
// origin, meters_per_pixel, and reference elevation — those properties
// differ between the outdoor heightmap and an indoor slice atlas, so each
// state needs its own UV math. `state_transition` lerps 0 → 1 (A → B).
//
// Within each state, three slice layers are sampled (active + below1 +
// below2) and composited so the player can see partial content of slices
// below them through holes in the active slice. Outdoor mode binds the
// same surface texture to all three so the composite collapses to one.
//
// Surface texture channel layout (RGBA8):
//   R = height low byte    \  combined: world Y of top face,
//   G = height high byte   /            range 0..65535
//   B = resolved tile id   (0..63, indexes tile_lut by id+0.5)
//   A = foliage id         (0 = none, indexes foliage_lut)
shader_type canvas_item;

// Per-state A textures.
uniform sampler2D surface_texture_a : filter_nearest;
uniform sampler2D surface_texture_below1_a : filter_nearest;
uniform sampler2D surface_texture_below2_a : filter_nearest;
uniform sampler2D exploration_texture_a : filter_linear;
uniform sampler2D exploration_texture_below1_a : filter_linear;
uniform sampler2D exploration_texture_below2_a : filter_linear;
uniform vec2 world_origin_xz_a = vec2(0.0);
uniform vec2 world_extent_pixels_a = vec2(1.0);
uniform float meters_per_pixel_a = 2.0;
uniform float reference_elevation_a = 0.0;

// Per-state B textures.
uniform sampler2D surface_texture_b : filter_nearest;
uniform sampler2D surface_texture_below1_b : filter_nearest;
uniform sampler2D surface_texture_below2_b : filter_nearest;
uniform sampler2D exploration_texture_b : filter_linear;
uniform sampler2D exploration_texture_below1_b : filter_linear;
uniform sampler2D exploration_texture_below2_b : filter_linear;
uniform vec2 world_origin_xz_b = vec2(0.0);
uniform vec2 world_extent_pixels_b = vec2(1.0);
uniform float meters_per_pixel_b = 2.0;
uniform float reference_elevation_b = 0.0;

// 0 = pure A, 1 = pure B. Lerps to 1 each frame; resets to 0 on mode /
// slice change so changes fade in.
uniform float state_transition : hint_range(0.0, 1.0) = 1.0;

// Shared between states.
uniform sampler2D tile_lut : filter_nearest;
uniform sampler2D foliage_lut : filter_nearest;
uniform float below1_brightness : hint_range(0.0, 1.0) = 0.5;
uniform float below2_brightness : hint_range(0.0, 1.0) = 0.25;
uniform vec2 player_world_xz = vec2(0.0);
// Runtime-driven by Hud each frame from Minimap.ActiveViewRadiusMeters
// (= player vision × multiplier, scaled by indoor-zoom in indoor mode).
// Don't author in the .tres — any value set there gets clobbered.
uniform float view_radius_meters = 32.0;

// Plateau height (meters per elevation band). Must match
// MinimapData.PlateauHeight on the C# side.
uniform float plateau_height = 4.0;

// Flat above/below brightness + saturation values. Pixels in the same
// plateau as the reference: 1.0/1.0 (unmodified). Pixels in any plateau
// above: above_*. Pixels in any plateau below: below_*.
uniform float above_brightness : hint_range(0.0, 2.0) = 1.2;
uniform float above_saturation : hint_range(0.0, 1.0) = 0.7;
uniform float below_brightness : hint_range(0.0, 2.0) = 0.55;
uniform float below_saturation : hint_range(0.0, 1.0) = 0.4;

// Per-side dynamic-range compression for off-plateau pixels. 1.0 leaves
// color values alone; 0.0 collapses every channel to middle gray (0.5).
// Each scales channel deviation from 0.5 — bright values get less bright
// AND dark values get less dark — so the range squeezes inward without
// pushing toward pure white or black. Above/below tunable separately so
// e.g. underground (below) can read more compressed than higher ground
// the player can climb to (above), or vice versa.
uniform float above_contrast : hint_range(0.0, 1.0) = 1.0;
uniform float below_contrast : hint_range(0.0, 1.0) = 1.0;

uniform vec4 void_color : source_color = vec4(0.04, 0.04, 0.06, 1.0);
uniform float mask_radius : hint_range(0.0, 0.7071) = 0.5;
uniform float mask_softness : hint_range(0.0, 0.1) = 0.015;

// Decode one (surface, exploration) pair at `uv` with the given reference
// elevation. Returns the lit color and a 0/1 has_content flag.
void sample_layer(
    sampler2D surf_tex, sampler2D expl_tex, vec2 uv, float reference_elev,
    out vec3 out_color, out float out_has_content
) {
    vec4 surf = texture(surf_tex, uv);
    float height = surf.r * 255.0 + surf.g * 255.0 * 256.0;
    out_has_content = step(0.5, height);

    float tile_id = surf.b * 255.0;
    float foliage_id = surf.a * 255.0;
    vec3 color = texture(tile_lut, vec2((tile_id + 0.5) / 64.0, 0.5)).rgb;
    vec4 foli = texture(foliage_lut, vec2((foliage_id + 0.5) / 256.0, 0.5));
    color = mix(color, foli.rgb, step(0.5, foliage_id) * foli.a);

    // Plateau-banded above/below treatment.
    float pixel_plateau = floor(height / plateau_height);
    float ref_plateau = floor(reference_elev / plateau_height);
    float plateau_diff = pixel_plateau - ref_plateau;
    float t_above = step(0.5, plateau_diff);
    float t_below = step(0.5, -plateau_diff);
    float brightness = 1.0
        + (above_brightness - 1.0) * t_above
        + (below_brightness - 1.0) * t_below;
    float saturation = 1.0
        + (above_saturation - 1.0) * t_above
        + (below_saturation - 1.0) * t_below;
    color *= brightness;
    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma), color, saturation);

    // Off-plateau dynamic-range compression: bright values lose brightness
    // while dark values gain it, both sliding toward middle gray. Doesn't
    // push any channel past 0.5 — just shrinks the spread. Above and below
    // sides use their own contrast knobs so each side can be tuned
    // independently.
    float compress = (1.0 - above_contrast) * t_above
                   + (1.0 - below_contrast) * t_below;
    color = mix(color, vec3(0.5), compress);

    float explored = texture(expl_tex, uv).r;
    color *= explored;
    out_color = color;
}

// Render one state — UV calc, three-layer composite, void-color masking
// — and return both the composed color and an "any content found" flag
// so the caller can mix to void where neither state has data.
void render_state(
    vec2 frag_uv,
    sampler2D surf, sampler2D surf_b1, sampler2D surf_b2,
    sampler2D expl, sampler2D expl_b1, sampler2D expl_b2,
    vec2 world_origin, vec2 extent_pixels, float meters_per_pix,
    float reference_elev,
    out vec3 out_color, out float out_has_any
) {
    vec2 uv_centered = frag_uv - vec2(0.5);
    vec2 world_xz = player_world_xz + uv_centered * (view_radius_meters * 2.0);
    vec2 voxel_extent = extent_pixels * meters_per_pix;
    vec2 tex_uv = (world_xz - world_origin) / voxel_extent;
    bool out_of_bounds = tex_uv.x < 0.0 || tex_uv.x > 1.0 || tex_uv.y < 0.0 || tex_uv.y > 1.0;
    vec2 safe_uv = clamp(tex_uv, vec2(0.0), vec2(1.0));

    vec3 c0; float has0;
    vec3 c1; float has1;
    vec3 c2; float has2;
    sample_layer(surf,    expl,    safe_uv, reference_elev, c0, has0);
    sample_layer(surf_b1, expl_b1, safe_uv, reference_elev, c1, has1);
    sample_layer(surf_b2, expl_b2, safe_uv, reference_elev, c2, has2);
    c1 *= below1_brightness;
    c2 *= below2_brightness;

    vec3 composed = c2 * has2;
    composed = mix(composed, c1, has1);
    composed = mix(composed, c0, has0);

    float any = max(has0, max(has1, has2));
    out_has_any = any * (1.0 - float(out_of_bounds));
    out_color = composed;
}

void fragment() {
    vec3 color_a; float has_a;
    vec3 color_b; float has_b;
    render_state(
        UV,
        surface_texture_a, surface_texture_below1_a, surface_texture_below2_a,
        exploration_texture_a, exploration_texture_below1_a, exploration_texture_below2_a,
        world_origin_xz_a, world_extent_pixels_a, meters_per_pixel_a, reference_elevation_a,
        color_a, has_a
    );
    render_state(
        UV,
        surface_texture_b, surface_texture_below1_b, surface_texture_below2_b,
        exploration_texture_b, exploration_texture_below1_b, exploration_texture_below2_b,
        world_origin_xz_b, world_extent_pixels_b, meters_per_pixel_b, reference_elevation_b,
        color_b, has_b
    );

    // Crossfade between states. Wherever a state has no content, treat its
    // color as void so the other state's content can fade through cleanly.
    vec3 void_rgb = void_color.rgb;
    vec3 a_rgb = mix(void_rgb, color_a, has_a);
    vec3 b_rgb = mix(void_rgb, color_b, has_b);
    vec3 composed = mix(a_rgb, b_rgb, state_transition);

    // Either state having content keeps the pixel non-void during the
    // crossfade so nothing pops to void mid-transition.
    float any_content = max(has_a, has_b);
    vec4 rgb_out = mix(void_color, vec4(composed, 1.0), any_content);

    // Circular alpha mask — fades to transparent past mask_radius so the
    // square TextureRect reads as a round minimap.
    float dist = length(UV - vec2(0.5));
    float alpha = 1.0 - smoothstep(mask_radius - mask_softness, mask_radius, dist);
    rgb_out.a *= alpha;

    COLOR = rgb_out;
}
