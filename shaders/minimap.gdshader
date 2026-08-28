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
//   A = collidable-prop stamp (0 = none; non-zero paints the Prop colour)
shader_type canvas_item;

// Per-state A textures.
uniform sampler2D surface_texture_a : filter_nearest;
uniform sampler2D surface_texture_below1_a : filter_nearest;
uniform sampler2D surface_texture_below2_a : filter_nearest;
uniform sampler2D exploration_texture_a : filter_nearest;
uniform sampler2D exploration_texture_below1_a : filter_nearest;
uniform sampler2D exploration_texture_below2_a : filter_nearest;
uniform vec2 world_origin_xz_a = vec2(0.0);
uniform vec2 world_extent_pixels_a = vec2(1.0);
uniform float meters_per_pixel_a = 2.0;
uniform float reference_elevation_a = 0.0;

// Per-state B textures.
uniform sampler2D surface_texture_b : filter_nearest;
uniform sampler2D surface_texture_below1_b : filter_nearest;
uniform sampler2D surface_texture_below2_b : filter_nearest;
uniform sampler2D exploration_texture_b : filter_nearest;
uniform sampler2D exploration_texture_below1_b : filter_nearest;
uniform sampler2D exploration_texture_below2_b : filter_nearest;
uniform vec2 world_origin_xz_b = vec2(0.0);
uniform vec2 world_extent_pixels_b = vec2(1.0);
uniform float meters_per_pixel_b = 2.0;
uniform float reference_elevation_b = 0.0;

// 0 = pure A, 1 = pure B. Lerps to 1 each frame; resets to 0 on mode /
// slice change so changes fade in.
uniform float state_transition : hint_range(0.0, 1.0) = 1.0;

// Shared between states.
uniform sampler2D tile_lut : filter_nearest;
uniform float below1_brightness : hint_range(0.0, 1.0) = 0.5;
uniform float below2_brightness : hint_range(0.0, 1.0) = 0.25;
uniform vec2 player_world_xz = vec2(0.0);
// Runtime-driven by Hud each frame from Minimap.ActiveViewRadiusMeters
// (= player vision × multiplier, scaled by indoor-zoom in indoor mode).
// Don't author in the .tres — any value set there gets clobbered.
uniform float view_radius_meters = 32.0;

// Rotates the world sampling around player_world_xz so a chosen world
// direction lands "up" on screen. 0 = world +X right / +Z down (the HUD
// minimap, which instead rotates its whole TextureRect to the camera yaw).
// The world map sets −π/4 so game-north (−X,−Z) points to the top — see
// WorldMapScreen. Off-map corners fall out of bounds and read as void.
uniform float map_rotation = 0.0;


// Elevation shading. The signed delta from the player's own elevation, in
// metres, normalised to `range` and curved by `gamma` — gamma BELOW 1
// emphasises the small deltas a player can actually act on. `above` / `below`
// are the brightness multipliers the curve reaches at the full range.
uniform float elevation_shade_range : hint_range(1.0, 64.0) = 12.0;
uniform float elevation_shade_gamma : hint_range(0.1, 2.0) = 0.5;
uniform float elevation_shade_above : hint_range(1.0, 2.0) = 1.35;
uniform float elevation_shade_below : hint_range(0.0, 1.0) = 0.6;

// ---- elevation step lines ----
// A line is drawn on any voxel edge where the surface steps, INTO the higher
// cell (so it reads as the rim of its plateau, not a fence between two cells) —
// the same rule the world-map painter's outline pass uses. Bucketed by the size
// of the step: 1 m, 2 m, 3 m-or-more — a traversal legend (walk up / mantle /
// wall), which is why they measure stand_height and not the drawn surface.
// Alpha rides in the colour.
uniform vec4 step_ink_1m : source_color = vec4(0.0, 0.0, 0.0, 0.25);
uniform vec4 step_ink_2m : source_color = vec4(0.0, 0.0, 0.0, 0.75);
uniform vec4 step_ink_3m : source_color = vec4(1.0, 1.0, 1.0, 0.75);
// Line thickness in OUTPUT PIXELS, not in cells. A cell fraction cannot serve
// both consumers: a voxel is ~3 px on the HUD minimap and 12 px on the zoomed-in
// world map, so one fraction is a hairline on one and a slab on the other.
// Capped as a fraction of a cell so a line can never swallow the cell it edges.
uniform float step_line_pixels : hint_range(0.5, 8.0) = 2.0;
uniform float step_edge_max_fraction : hint_range(0.05, 0.5) = 0.34;
// Lines fade out once an output pixel covers more than this many world metres —
// past that there is no cell left to draw an edge inside. So the whole-world
// overview (5 m/texel) gets none of this and the detail view and HUD minimap
// get all of it, decided per fragment with no help from the C# side.
uniform float step_fade_start_mpp : hint_range(0.1, 8.0) = 0.6;
uniform float step_fade_end_mpp : hint_range(0.1, 8.0) = 1.2;

uniform vec4 void_color : source_color = vec4(0.04, 0.04, 0.06, 1.0);
uniform float mask_radius : hint_range(0.0, 0.7071) = 0.5;
uniform float mask_softness : hint_range(0.0, 0.1) = 0.015;

// Floor on the exploration (fog-of-war) reveal. 0 = normal fog (unexplored
// terrain stays dark). 1 = terrain always fully shown regardless of what the
// party has charted — a treasure map depicts land the player may never have
// visited (WorldMapScreen sets this in treasure-map mode).
uniform float min_reveal : hint_range(0.0, 1.0) = 0.0;

// Resolve one source-texel's TERRAIN color (tile LUT only — foliage is
// applied separately below so its edges stay sharp).
vec3 fetch_terrain_color(sampler2D surf_tex, ivec2 coord) {
    vec4 s = texelFetch(surf_tex, coord, 0);
    float tile_id = s.b * 255.0;
    return texture(tile_lut, vec2((tile_id + 0.5) / 64.0, 0.5)).rgb;
}

// Height as stored in the surface texture: R low byte + G high byte, in metres
// above the world's biased floor. 0 is the no-content sentinel.
// Category indices written into the tile channel by MinimapData — must match
// EMinimapCategory. Only these two are named here; the rest arrive in the
// texture and are never compared against.
const float PROP_CATEGORY = 4.0;
const float WATER_CATEGORY = 2.0;
// Metres a body sits below a water surface; mirrors ClimbLedgeMarker
// .WaterStandDrop and WorldMapState.WaterStandDrop.
const float WATER_STAND_DROP = 1.0;

vec3 fetch_category_color(float category) {
    return texture(tile_lut, vec2((category + 0.5) / 64.0, 0.5)).rgb;
}

float decode_height(vec4 texel) {
    return texel.r * 255.0 + texel.g * 255.0 * 256.0;
}

// The height a BODY meets, which is what the step lines are a legend for. Water
// is stored at its free surface, but a body stands or floats one metre into it —
// the convention WalkabilityGrid uses and the mantle is measured against. It
// holds at any depth, because wading and swimming differ in how a body is held
// up, not in where the surface holding it sits. Without the drop a bank one
// voxel proud of a lake — a real mantle — inks as a walk-up. Guarded so a column
// at the very floor of the biased range cannot decay into the 0 no-content
// sentinel.
float stand_height(vec4 texel) {
    float h = decode_height(texel);
    float category = texel.b * 255.0;
    float wet = step(WATER_CATEGORY - 0.5, category) * step(category, WATER_CATEGORY + 0.5);
    return max(h - wet * WATER_STAND_DROP, min(h, 1.0));
}

// How far THIS cell stands above the neighbour, in metres, or 0 if it does not
// (or if either side has no content, which is the world edge — a step against
// nothing is not a cliff).
float step_drop(float h_here, float h_there) {
    float both = step(0.5, h_here) * step(0.5, h_there);
    return max(h_here - h_there, 0.0) * both;
}

// Resolve ONE already-fetched source texel all the way to its lit colour:
// category, prop override, elevation shade. Split out so a minifying fragment
// can average several of them — see sample_layer.
vec3 resolve_texel(vec4 t, float reference_elev) {
    vec3 c = fetch_category_color(t.b * 255.0);
    // A collidable prop REPLACES the ground's category colour rather than
    // darkening it: the map draws five categories and a prop is one of them, so
    // "there is a tree here" must not come out as a shade of the soil under it.
    c = mix(c, fetch_category_color(PROP_CATEGORY), step(0.5, t.a * 255.0));

    // Elevation shading, CONTINUOUS and relative to the player. Not banded: the
    // old form compared floor(h / plateau_height) against the player's band, so
    // the boundaries sat at absolute multiples of 4 m in WORLD space. And not
    // linear in the delta, because the deltas that matter are the small ones —
    // the metre step you can walk up versus the cliff you cannot. Gamma below 1
    // is what emphasises them; past the range it clamps so a distant mountain
    // does not run away to white.
    float d = clamp((decode_height(t) - reference_elev) / max(elevation_shade_range, 0.001), -1.0, 1.0);
    float shaped = sign(d) * pow(abs(d), max(elevation_shade_gamma, 0.001));
    float shade = shaped >= 0.0
        ? mix(1.0, elevation_shade_above, shaped)
        : mix(1.0, elevation_shade_below, -shaped);
    return c * shade;
}

// Decode one (surface, exploration) pair at `uv` with the given reference
// elevation. Returns the lit color, a has_content coverage in 0..1, and the
// nearest texel's raw height.
//
// MAGNIFYING fragments take ONE nearest tap and stay pixel-crisp. MINIFYING
// ones box-average the source texels they actually cover, and they have to:
// the zoomed-out level draws 1.25 m of world per texel from a 1 m source, so a
// single tap keeps one voxel and discards the rest — and because map_rotation
// spins the sample grid 45° against the axis-aligned voxel grid, that is a
// moiré that crawls as you pan. The aliasing was baked INTO the render target,
// so no amount of care at display time could have removed it.
//
// The average is over RESOLVED COLOURS, never over the texture. The surface
// texture is packed INDEX data — R/G height bytes, B the category enum, A the
// prop flag — so a linear sampler is not merely blurry but wrong: averaging
// category 1 (Stone) and 3 (Road) yields 2, which is Water.
//
// Tap count follows the real footprint (`fwidth` of the source-texel position),
// so it is exactly 1 wherever the map is magnifying — the detail layer and the
// HUD minimap pay nothing for this.
const int MAX_MINIFY_TAPS = 4;

void sample_layer(
    sampler2D surf_tex, sampler2D expl_tex, vec2 uv, float reference_elev,
    out vec3 out_color, out float out_has_content, out float out_height
) {
    ivec2 itex_size = textureSize(surf_tex, 0);
    vec2 fsz = vec2(itex_size);
    ivec2 maxc = itex_size - 1;
    vec2 pp = uv * fsz;
    ivec2 nearest_coord = clamp(ivec2(pp), ivec2(0), maxc);
    vec4 surf_nearest = texelFetch(surf_tex, nearest_coord, 0);

    // The step lines and the caller want THIS cell's height, not a blend of its
    // neighbours', whatever the tap count.
    out_height = decode_height(surf_nearest);

    // Source texels covered by one output pixel. Must be outside any branch.
    vec2 span2 = vec2(fwidth(pp.x), fwidth(pp.y));
    float span = max(span2.x, span2.y);
    int taps = clamp(int(ceil(span)), 1, MAX_MINIFY_TAPS);

    vec3 color;
    if (taps <= 1) {
        color = resolve_texel(surf_nearest, reference_elev);
        out_has_content = step(0.5, out_height);
    } else {
        vec3 sum = vec3(0.0);
        float hits = 0.0;
        float inv = 1.0 / float(taps);
        for (int j = 0; j < MAX_MINIFY_TAPS; j++) {
            if (j >= taps) { break; }
            for (int i = 0; i < MAX_MINIFY_TAPS; i++) {
                if (i >= taps) { break; }
                vec2 off = ((vec2(float(i), float(j)) + 0.5) * inv - 0.5) * span;
                vec4 t = texelFetch(surf_tex, clamp(ivec2(pp + off), ivec2(0), maxc), 0);
                // Empty texels are skipped rather than averaged in: their height
                // is the 0 sentinel, which the shade would read as a cliff and
                // darken the coastline with. Coverage becomes the fraction that
                // DID hit content, so the world edge fades instead of stepping.
                if (decode_height(t) < 0.5) { continue; }
                sum += resolve_texel(t, reference_elev);
                hits += 1.0;
            }
        }
        out_has_content = hits / float(taps * taps);
        color = hits > 0.0 ? sum / hits : vec3(0.0);
    }

    color *= max(texture(expl_tex, uv).r, min_reveal);
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
    // Rotate the screen offset into world space so map_rotation radians of
    // spin put the desired world direction at the top of the panel.
    float map_cos = cos(map_rotation);
    float map_sin = sin(map_rotation);
    vec2 rot_centered = vec2(
        uv_centered.x * map_cos - uv_centered.y * map_sin,
        uv_centered.x * map_sin + uv_centered.y * map_cos);
    vec2 world_xz = player_world_xz + rot_centered * (view_radius_meters * 2.0);
    vec2 voxel_extent = extent_pixels * meters_per_pix;
    vec2 tex_uv = (world_xz - world_origin) / voxel_extent;
    bool out_of_bounds = tex_uv.x < 0.0 || tex_uv.x > 1.0 || tex_uv.y < 0.0 || tex_uv.y > 1.0;
    vec2 safe_uv = clamp(tex_uv, vec2(0.0), vec2(1.0));

    vec3 c0; float has0; float h0;
    vec3 c1; float has1; float h1_unused;
    vec3 c2; float has2; float h2_unused;
    sample_layer(surf,    expl,    safe_uv, reference_elev, c0, has0, h0);
    sample_layer(surf_b1, expl_b1, safe_uv, reference_elev, c1, has1, h1_unused);
    sample_layer(surf_b2, expl_b2, safe_uv, reference_elev, c2, has2, h2_unused);
    c1 *= below1_brightness;
    c2 *= below2_brightness;

    vec3 composed = c2 * has2;
    composed = mix(composed, c1, has1);
    composed = mix(composed, c0, has0);

    // ---- elevation step lines ----
    // The surface texture is one texel per voxel column, so the texel grid IS
    // the voxel grid: a fragment's fractional position inside its texel is its
    // position inside the metre cell, which is what lets a line be a third of a
    // cell wide rather than a whole cell.
    //
    // Each of the four edges is tested from the perspective of THIS cell, and
    // only when this cell is the higher of the pair — so the line lands on the
    // rim of the plateau, and the neighbouring fragment across the boundary
    // draws nothing. A corner takes the largest step of the edges that apply.
    ivec2 tsz = textureSize(surf, 0);
    ivec2 tmax = tsz - 1;
    vec2 cellpp = safe_uv * vec2(tsz);
    ivec2 cell = clamp(ivec2(floor(cellpp)), ivec2(0), tmax);
    vec2 cell_frac = fract(cellpp);

    float h_here = stand_height(texelFetch(surf, cell, 0));
    float h_negx = stand_height(texelFetch(surf, clamp(cell + ivec2(-1, 0), ivec2(0), tmax), 0));
    float h_posx = stand_height(texelFetch(surf, clamp(cell + ivec2( 1, 0), ivec2(0), tmax), 0));
    float h_negy = stand_height(texelFetch(surf, clamp(cell + ivec2(0, -1), ivec2(0), tmax), 0));
    float h_posy = stand_height(texelFetch(surf, clamp(cell + ivec2(0,  1), ivec2(0), tmax), 0));

    // Metres per output pixel — needed BEFORE the edge tests now, since the
    // line width is expressed in pixels and converted into cells here.
    vec2 world_deriv = vec2(fwidth(world_xz.x), fwidth(world_xz.y));
    float mpp = max(world_deriv.x, world_deriv.y);
    float w = min(step_line_pixels * mpp, step_edge_max_fraction);
    float drop = 0.0;
    drop = max(drop, step_drop(h_here, h_negx) * step(cell_frac.x, w));
    drop = max(drop, step_drop(h_here, h_posx) * step(1.0 - w, cell_frac.x));
    drop = max(drop, step_drop(h_here, h_negy) * step(cell_frac.y, w));
    drop = max(drop, step_drop(h_here, h_posy) * step(1.0 - w, cell_frac.y));

    // Past ~1 metre per output pixel there is no cell left to draw an edge
    // inside, so the whole-world overview fades them out on its own.
    float line_fade = 1.0 - smoothstep(step_fade_start_mpp, step_fade_end_mpp, mpp);

    vec4 step_ink = drop >= 2.5 ? step_ink_3m : (drop >= 1.5 ? step_ink_2m : step_ink_1m);
    // Gated on reveal like the ground under it — a contour in unexplored fog
    // would chart terrain the party has never seen.
    float line_reveal = max(texelFetch(expl, cell, 0).r, min_reveal);
    float line_on = step(0.5, drop) * line_fade * line_reveal * has0;
    composed = mix(composed, step_ink.rgb, step_ink.a * line_on);

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
