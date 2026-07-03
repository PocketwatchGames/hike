shader_type spatial;
// Alpha-tested opaque, NOT alpha-blended:
//   - depth_prepass_alpha runs an alpha-tested depth prepass so the depth
//     buffer correctly reflects the cut-out sprite shapes before the main
//     color pass. With MultiMesh we can't reorder instances per-frame for
//     transparent-pass back-to-front sorting; rendering in the opaque pass
//     with depth-write makes z-sort an automatic consequence of the depth
//     buffer instead.
//   - depth_draw_opaque writes depth in the color pass too, so subsequent
//     opaque draws (other detail-sprite chunks, sprite_lit, props) sort
//     correctly via the depth buffer.
// Cap occlusion is handled by an off-screen mask render rather than the
// stencil pipeline, so detail_sprite no longer needs to participate in
// any alpha-queue ordering for the cap to work.
render_mode depth_prepass_alpha, depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform;
// No stencil write. Previously stamped stencil=8 so the player's X-ray
// pass would discard at grass-blade pixels (preventing a "bright
// silhouette flash through the grass" when a blade landed between camera
// and player). With X-ray now a subtle 20%-alpha black overlay, the
// remaining flicker behind near grass is barely visible — and keeping
// the stencil block meant grass on a hill in front of the player ALSO
// blocked X-ray, hiding the player when they were genuinely behind
// terrain. Accepting the minor near-grass flicker to get the far-grass
// fix is the better trade.

// MultiMesh-instanced detail sprite (grass, flowers, twigs, etc.).
//
// Author the mesh as a quad in the XY plane with its base at Y=0 (so VERTEX.y
// reads 0 at the bottom and increases upward). Wind and player push key off
// VERTEX.y, so taller sprites sway proportionally more — no per-mesh height
// uniform needed.

// filter_linear is required by the subpixel_aa path: the fragment snaps the
// sample point to the texel center except within a ~1-screen-pixel band at
// each texel boundary, where linear filtering blends into the neighbor. With
// filter_nearest the snap is a no-op. When subpixel_aa is off, the code
// snaps to seam = texel center, so linear sampling degenerates to exactly
// the nearest texel value — visually identical to the old filter_nearest.
uniform sampler2D sprite_texture : source_color, filter_linear;

// Atlas region in normalized UV space: xy = origin, zw = size. Default
// (0, 0, 1, 1) samples the full bound texture. When a DetailEntry points at
// an AtlasTexture, DetailEntry.GetMaterial binds the underlying atlas to
// sprite_texture and packs the atlas region into this uniform so shader UV
// 0..1 still maps to the sub-region. textureSize(sprite_texture, 0) returns
// the full atlas size, so anything that needs the *sprite's* pixel size
// (e.g. ground_fade column hashing) multiplies it by uv_region.zw.
uniform vec4 uv_region = vec4(0.0, 0.0, 1.0, 1.0);

// Per-entry tint map (a data/mask texture, NOT source_color — import the PNG
// as non-sRGB / "Linear" so its channel values read as raw weights). Per
// texel: R = pull weight toward tint_color_a, G = pull weight toward the
// per-instance ground color (COLOR.rgb), B = pull weight toward tint_color_b;
// a black texel keeps the source texture color. Bound per entry by
// DetailEntry.GetMaterial. Defaults to black (hint_default_black) so entries
// with no tint map render straight from sprite_texture. filter_nearest: the
// mask is sampled at snapped texel centers and must not bleed between regions.
uniform sampler2D tint_map : hint_default_black, filter_nearest;
// Atlas region for tint_map, same xy=origin / zw=size packing as uv_region.
// Identity (0,0,1,1) when the tint map is a standalone (non-atlas) texture.
uniform vec4 tint_uv_region = vec4(0.0, 0.0, 1.0, 1.0);
// The two authored tint targets the tint_map's R and B channels pull toward.
// source_color so the authored sRGB colors linearize to match the albedo.
uniform vec3 tint_color_a : source_color = vec3(1.0);
uniform vec3 tint_color_b : source_color = vec3(1.0);

// --- Normal pipeline ---
// Sprites light off the per-instance terrain normal that ChunkDetailScatter
// packs into INSTANCE_CUSTOM.xyz (the same vector the sprite uses for its
// slope lean), so each sprite inherits the lighting profile of the voxel
// beneath it exactly and never reads as a separate object from the ground.
// No per-pixel perturbation: the world normal IS the terrain normal.

// Subpixel-perfect pixel-art filter (toggle for A/B).
//   On:  fragment snaps the sample UV to the nearest texel center except in
//        a ~1-screen-pixel-wide band at each texel boundary, where linear
//        filtering crossfades to the neighbor. This kills the sparkle /
//        missing-pixel artifacts when the sprite is bent/sheared by wind or
//        rolled off-axis: screen coverage of a source texel varies smoothly
//        instead of snapping between whole texels. At native 1:1 scale or
//        minification (dudv >= 1) the clamp is inactive and it degenerates
//        to plain bilinear — still correct, just no longer snappy.
//   Off: snap target collapses to the texel center every fragment, so
//        linear filtering returns exactly the nearest texel value (matches
//        the prior filter_nearest look exactly).
// Note on alpha fringing: linear filtering reads color from transparent
// neighbors at texel boundaries. If you see dark halos, re-import the
// source textures with an alpha-bleed / "premultiply alpha" step so
// transparent texels carry a sensible RGB.
uniform bool subpixel_aa = true;

// --- Lighting globals (shared with sprite_lit / voxel_clip) ---
global uniform sampler3D light_map : filter_linear, repeat_enable;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
global uniform vec3 sun_world_dir;
// Lingering surface wetness in [0, 1]. Driven by SkyController from recent
// rain / fog / humidity. Same value the terrain shader reads, so wet grass
// and wet stone darken together. Modulated per-fragment by sun_mask (sprites
// under cover stay dry) and by world_normal.y (upward-facing blades darken,
// downward-facing leaves don't).
global uniform float wetness_level;
// Shared wet-darken tunable — same value the terrain shader reads so wet grass
// darkens in lockstep with wet stone. Grass uses ONLY this darken for its wet
// look; the terrain's specular / sky reflection / pooling are deliberately not
// shared (see fragment()).
global uniform float wet_albedo_floor;
// Shared tint globals (voxel_clip uses the same pair). Two off-axis fill
// directions and their colors. Surfaces facing away from a fill are
// multiplied by that fill's color; surfaces facing toward it by 1. Shading
// off the terrain normal gives sprites the same directional read as the
// terrain underneath them.
global uniform vec3 fill_a_world_dir;
global uniform vec3 fill_b_world_dir;
global uniform vec3 fill_a_color;
global uniform vec3 fill_b_color;
global uniform vec2 cloud_offset;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"

global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
global uniform vec3 camera_clip_growth_center;
global uniform float camera_clip_growth_max_radius;
global uniform float camera_clip_growth_edge_softness;
#include "res://shaders/clip_dither.gdshaderinc"
// Dark-adaptation ("night eyes") tone curve. Driven by the player sim
// (Player.EyeDilation) via GameClient; exact no-op when eye_adaptation == 0.
#include "res://shaders/eye_adaptation.gdshaderinc"
// Debug toggles — shared with voxel_clip so terrain and sprites behave
// identically under each flag and you can isolate shading divergences.
global uniform bool debug_normals;        // RGB = world_normal * 0.5 + 0.5
global uniform bool debug_unlit;          // raw tex only, no lighting
global uniform bool debug_no_tint;        // skip fill_a_tint / fill_b_tint
global uniform bool debug_no_cloud;       // force cloud_dim = 1
// debug_no_detail_normal: read in voxel_clip only, listed here so both
// shaders declare the same global set.
global uniform bool debug_no_detail_normal;
// Lighting-only debug view shared with voxel_clip + sprite_lit. When true
// the grass tex.rgb is replaced with white so the output reflects only the
// lighting path, for direct comparison against terrain / sprites.
global uniform bool debug_white_albedo;
global uniform bool debug_wet_spec;
// 1 / cos(camera_pitch). Stretches sprite Y so a row of source pixels projects
// to one screen-pixel row regardless of the iso camera's tilt — same trick
// sprite_lit uses; without it, detail sprites read as foreshortened relative
// to authored 2D sprites in the same scene.
global uniform float sprite_stretch;

// --- Ground stain projector (shared with voxel_clip) ---
// Grass samples the SAME top-down stain RT the terrain does so a mark (the
// player's grounding-shadow blob, scorch, footprints) darkens the blade in
// lockstep with the ground it stands on — no bright-grass-over-dark-ground
// seam. Sampled once at the blade ROOT (see fragment()), not per-fragment, so a
// swaying blade doesn't slide the mark across itself; mirrors the single-anchor
// light_map sample. Globals already declared/seeded for voxel_clip + the
// GroundStainProjector — redeclared here only because each shader owns its set.
global uniform sampler2D ground_stain_tex : filter_linear;
global uniform vec3 ground_stain_origin;
global uniform vec3 ground_stain_right;
global uniform vec3 ground_stain_up;
global uniform float ground_stain_size;
global uniform float ground_stain_strength;
global uniform bool ground_stain_enabled;
#include "res://shaders/ground_stain.gdshaderinc"

// --- Wind / interaction globals ---
// wind_dir.xz = horizontal direction; .y unused (kept vec3 for cheap mix with
// world up if we ever want vertical sway). amplitude scales the displacement
// in world units at the *top* of a 1m-tall sprite.
global uniform vec3 wind_dir;
global uniform float wind_amplitude;
// Pre-integrated sway phase (SkyController accumulates wind_frequency*dt each
// frame). Passing a phase instead of a frequency avoids the sin-speed ramp you
// get when weather lerps wind_frequency while the shader is still multiplying
// by TIME — the phase integrates the current frequency; past cycles stay put.
global uniform float wind_phase;

// player_pos is the player's world-space foot position. Sprites within
// player_radius push their tops away radially in the XZ plane; falloff is
// smoothstep so the boundary is soft.
global uniform vec3 player_pos;
global uniform float player_radius;
global uniform float player_strength;

// Per-entry scalar that multiplies BOTH wind sway and player push. 1.0 = full
// motion (grass, flowers); 0.0 = locked rigid (cave pebbles, bones, anything
// that shouldn't bend). Stamped into the material by DetailEntry.GetMaterial
// from the [Export] WindStrength field on the entry resource.
uniform float wind_strength : hint_range(0.0, 1.0) = 1.0;

// Per-entry response to surface wetness. Scales the wet_factor that drives the
// wet darken — grass's only wet effect (no specular / reflection / pooling).
// 1.0 = darkens alongside the terrain when wet (grass, leaves — default);
// 0.0 = ignores wetness entirely (dry twigs, bones, pebbles). Stamped into the
// material by DetailEntry.GetMaterial from the [Export] WetStrength field.
uniform float wet_strength : hint_range(0.0, 1.0) = 1.0;

// Baked-AO darkening strength, mirrors voxel_clip's `ao_strength` so grass
// shelter-darkens in lockstep with the ground beneath it. The per-instance
// occlusion (0 = open, 1 = sheltered) is computed at scatter time and carried
// in INSTANCE_COLOR.a; this scales how strongly it darkens the lit blade.
// Stamped by DetailEntry.GetMaterial from the `ao_strength` CVar (the same
// value the terrain material reads) so the two stay matched.
uniform float ao_strength : hint_range(0.0, 1.0) = 1.0;

varying vec3 world_origin;
// Fragment->light pass for cloud shadow composition (same trick sprite_lit
// uses so a cloud + terrain shadow read as one shadow, not stacked).
varying float cloud_dim;
// Per-fragment world position. Sampled for cloud shadow so the sprite's
// cloud state matches the fog shader's Y-dependent shaft sampling at the
// same sun ray. See the cloud_shadow call in fragment().
varying vec3 frag_world_pos;
// Per-instance terrain normal in world space — the sprite's lighting
// normal. Inherited from INSTANCE_CUSTOM.xyz (same source as the slope
// lean), so sprites match the lighting profile of the voxel they sit on.
varying vec3 terrain_normal_ws;
// Per-instance ground porosity (INSTANCE_CUSTOM.w), the BlockData.Porosity
// of the tile under this sprite. Scales the wet darken so a wet blade
// darkens by the same fraction as the ground it's rooted in — the terrain
// shader gates its wet_dark by this same porosity. Set by ChunkDetailScatter
// / WorldDetailScatter.
varying float ground_porosity;

// Cheap 2D hash for per-instance wind phase. Derived from instance origin so
// adjacent blades have different sway phases and a field doesn't sway in
// lock-step.
float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;

	// Random horizontal mirror per instance — picks a different hash offset
	// from the sway phase so mirror doesn't correlate with wind timing. Only
	// the texture UV is flipped; with the sprite lit by the flat terrain
	// normal there's no tangent frame that needs to agree with the flip.
	float mirror = step(0.5, hash12(world_origin.xz + vec2(7.31, 13.71)));
	UV.x = mix(UV.x, 1.0 - UV.x, mirror);

	// Two-stage billboard:
	//   1) Yaw: rotate the quad around world Y so its plane faces the camera
	//      horizontally. sprite_right is horizontal, sprite_up is world Y.
	//      The plane never pitches into/out of screen.
	//   2) Roll: rotate within the sprite plane around the camera-facing
	//      forward axis by an angle derived from the per-instance terrain
	//      normal projected onto the plane. The sprite leans visibly when
	//      viewed across the slope and reads as upright when viewed along
	//      the slope (the projected normal collapses to world-up there).
	// Then stretch only the world-Y component of each rolled axis by
	// sprite_stretch (= 1/cos(camera_pitch)). Foreshortening only affects
	// vertical world distance under iso pitch; horizontal world distance is
	// already in the screen plane and needs no compensation.
	// Non-uniform per-instance scale: the scatter packs texture width and
	// height (in world units) into MODEL_MATRIX columns 0 and 1 respectively,
	// so sprites natively sit at their texture's pixel aspect without any
	// authored mesh-size tuning. ScaleMin/ScaleMax multiplier is folded in
	// uniformly on top.
	float scale_x = length(MODEL_MATRIX[0].xyz);
	float scale_y = length(MODEL_MATRIX[1].xyz);
	vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right_yaw = normalize(cross(vec3(0.0, 1.0, 0.0), cam_back));
	vec3 sprite_up_yaw = vec3(0.0, 1.0, 0.0);
	// sprite_forward is horizontal (perpendicular to world up and to
	// sprite_right_yaw) — the axis we roll around.
	vec3 sprite_forward = cross(sprite_right_yaw, sprite_up_yaw);

	// Project the per-instance terrain normal into the sprite plane and
	// resolve the roll angle as its tilt off world-up within that plane.
	// Flat ground (normal = world Y) → roll = 0, sprite stays upright. The
	// sprite's local up axis ends up aligned with the projected normal, so
	// the top of the sprite leans in the same direction the terrain normal
	// points (i.e. downslope for an outward-facing surface).
	ground_porosity = INSTANCE_CUSTOM.w;
	vec3 n = INSTANCE_CUSTOM.xyz;
	vec3 n_in_plane = n - dot(n, sprite_forward) * sprite_forward;
	float right_comp = dot(n_in_plane, sprite_right_yaw);
	float up_comp = dot(n_in_plane, sprite_up_yaw);
	float roll = atan(right_comp, up_comp);
	float c = cos(roll);
	float s = sin(roll);

	// Rotate (right, up) in the sprite plane by `roll` around sprite_forward.
	// Choosing sprite_up_rolled = sin*right + cos*up makes the rotated up
	// vector equal to the projected-normal direction in the (right, up)
	// basis — so the lean direction matches the normal, not its mirror.
	vec3 sprite_right_rolled = c * sprite_right_yaw - s * sprite_up_yaw;
	vec3 sprite_up_rolled = s * sprite_right_yaw + c * sprite_up_yaw;

	// Pass the per-instance terrain normal to the fragment stage as the
	// sprite's lighting normal. (The rolled tangent axes below are used only
	// to orient the quad geometry; the stretch is a screen-space correction
	// for camera pitch and must not touch the lighting normal.)
	terrain_normal_ws = normalize(n);

	// Stretch only the world-Y component — the only component that gets
	// foreshortened by camera pitch. Horizontal components are already in
	// the screen plane.
	sprite_right_rolled.y *= sprite_stretch;
	sprite_up_rolled.y *= sprite_stretch;

	vec3 vertex_world = world_origin
		+ VERTEX.x * scale_x * sprite_right_rolled
		+ VERTEX.y * scale_y * sprite_up_rolled;

	// Bend reference height in *unstretched* world units so wind amplitude
	// doesn't grow when the camera tilts more.
	float h = max(VERTEX.y * scale_y, 0.0);
	float bend = h * h;

	// Per-sprite phase from the instance origin so a field doesn't sway in
	// lock-step. Two octaves at coprime frequencies for non-repeating motion.
	float phase = hash12(world_origin.xz) * 6.2831853;
	float t = wind_phase;
	float sway = sin(t + phase) * 0.7 + sin(t * 1.7 + phase * 1.3) * 0.3;

	vec3 wind_offset = wind_dir * (sway * wind_amplitude * bend * wind_strength);

	// Player push: radial in XZ, falloff to 0 at player_radius.
	vec3 to_sprite = world_origin - player_pos;
	to_sprite.y = 0.0;
	float dist = length(to_sprite);
	vec3 push_offset = vec3(0.0);
	if (player_radius > 0.0 && dist < player_radius && dist > 1e-4) {
		vec3 push_dir = to_sprite / dist;
		float falloff = 1.0 - smoothstep(0.0, player_radius, dist);
		push_offset = push_dir * (falloff * player_strength * bend * wind_strength);
	}

	vertex_world += wind_offset + push_offset;
	frag_world_pos = vertex_world;

	vec4 view_pos = VIEW_MATRIX * vec4(vertex_world, 1.0);
	POSITION = PROJECTION_MATRIX * view_pos;
	// Hand the built-in (Environment) depth fog a correct view-space VERTEX.
	// render_mode skip_vertex_transform leaves VERTEX in the quad's local space
	// (used above for the billboard + UV), which Godot's fog stage would read as
	// ~0 fragment depth — so detail sprites escape the distance fog that fogs the
	// terrain and pop against the fogged-dark far terrain at the top of the view.
	// Writing the real view-space position makes grass fog identically to the
	// ground beneath it. (Same gap exists in the other skip_vertex_transform
	// sprite shaders if their geometry ever needs to fog.)
	VERTEX = view_pos.xyz;
}

void fragment() {
	if (clip_dither_should_discard(world_origin, FRAGCOORD.xy)) {
		discard;
	}

	vec2 atlas_uv = uv_region.xy + UV * uv_region.zw;

	// Subpixel-perfect snap in atlas-pixel space. Operating on the full atlas
	// size (not the uv_region sub-size) keeps the snap aligned with the
	// underlying texel grid that filter_linear interpolates against. See the
	// subpixel_aa uniform comment for the full rationale.
	vec2 full_size = vec2(textureSize(sprite_texture, 0));
	vec2 px = atlas_uv * full_size;
	vec2 seam = floor(px) + 0.5;
	vec2 px_aa;
	if (subpixel_aa) {
		// dudv = source texels covered by one screen pixel. Magnified (<1):
		// clamp bites, sample stays near the center for most of the texel
		// with a thin transition band. At 1:1 the clamp is a no-op
		// (px - seam is already in [-0.5, 0.5]) and we read exact bilinear.
		// Minified (>1): same formula still returns a valid in-quad sample;
		// mipmaps would be the next step but are intentionally off for
		// pixel-art.
		vec2 dp = max(fwidth(px), vec2(1e-6));
		px_aa = seam + clamp((px - seam) / dp, -0.5, 0.5);
	} else {
		px_aa = seam;
	}
	// Clamp to this sprite's own texel region so filter_linear can't bleed the
	// outer texels into the wrapped opposite edge (default repeat) or an
	// adjacent atlas cell.
	vec2 region_min = uv_region.xy * full_size;
	vec2 region_max = (uv_region.xy + uv_region.zw) * full_size;
	px_aa = clamp(px_aa, region_min + vec2(0.5), region_max - vec2(0.5));
	vec2 sprite_uv = px_aa / full_size;
	vec4 tex = texture(sprite_texture, sprite_uv);
	if (tex.a < 0.5) {
		discard;
	}

	// Tint-map recolor. The per-entry tint_map carries, per texel, three pull
	// weights: R toward tint_color_a, G toward the per-instance ground color
	// (COLOR.rgb — the GroundTint of the voxel under this sprite, set by
	// ChunkDetailScatter), B toward tint_color_b. A black texel leaves the
	// source texture color untouched. The three pulls blend as a normalized
	// weighted average with the texture holding the remaining
	// (1 - r - g - b) weight, so overlapping channels never oversaturate and
	// the order of the pulls doesn't matter. The denominator is
	// max(1, r+g+b) >= 1, so there's no divide-by-zero and no scaling when the
	// weights already sum to <= 1. Sampled at the same snapped sprite-space UV
	// as the albedo (remapped into the tint atlas region) so mask and texture
	// stay texel-aligned. Replaces the old procedural bottom-pixels "drip"
	// rooting — authors now paint the ground-pull falloff into the G channel.
	vec2 snapped_uv01 = (sprite_uv - uv_region.xy) / uv_region.zw;
	vec2 tint_uv = tint_uv_region.xy + snapped_uv01 * tint_uv_region.zw;
	vec3 tmap = texture(tint_map, tint_uv).rgb;
	float w_tex = max(0.0, 1.0 - tmap.r - tmap.g - tmap.b);
	float inv_total = 1.0 / (w_tex + tmap.r + tmap.g + tmap.b);
	tex.rgb = (tex.rgb * w_tex
		+ tint_color_a * tmap.r
		+ COLOR.rgb * tmap.g
		+ tint_color_b * tmap.b) * inv_total;

	// The sprite's lighting normal is the per-instance terrain normal under
	// it (passed through from the vertex stage), so it shades exactly like
	// the voxel it sits on. Re-normalize because varying interpolation can
	// shorten the vector across the quad.
	vec3 world_normal = normalize(terrain_normal_ws);

	// Sample the light map at the sprite's anchor (root). Per-fragment
	// sampling would shimmer as the sprite sways; a single anchor sample
	// is correct because light_map is voxel-resolution. Done before the
	// tint so sun_mask can weight the NdotL fade.
	vec3 light_sample_pos = world_origin + vec3(0.0, 0.5, 0.0);
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = textureLod(light_map, uvw, 0.0);
	float sun_mask = pow(lm.r, light_falloff_exp);
	// Wetness gate: only sky-exposed (sun_mask) and upward-facing (positive
	// Y normal) sprites wet out. Since the sprite normal is the terrain
	// normal, grass on flat/upward ground picks up wetness while sprites on
	// steep faces stay drier. Same curve and exponents as voxel_clip so wet
	// grass and wet terrain behave consistently.
	float wet_slope = pow(max(world_normal.y, 0.0), 3.0);
	// Grass shares the terrain's wetness INPUT (wetness_level) and the same
	// sky-exposure × upward-slope gate, but NOT the terrain's wet look — no
	// specular glint, sky reflection, or pooling. The only wet effect is the
	// slight albedo darken applied below, so blades don't read dry/grey over
	// wet ground. Per-entry wet_strength scales how strongly this sprite darkens.
	// ground_porosity is the porosity of the tile under the sprite (the same
	// BlockData.Porosity the terrain shader folds into its wet_dark): porous
	// soil darkens a lot, non-porous rock barely. Folding it in here keeps the
	// sprite's darken in lockstep with the ground rather than always darkening
	// at full strength (which read too dark over the same wet terrain).
	float wet_factor = wetness_level * sun_mask * wet_slope * wet_strength * ground_porosity;
	// Same cloud_shadow_ground call as terrain and regular sprites —
	// samples the cloud noise at world XZ with no sun-direction projection,
	// so grass, terrain, and sprite shadows stay in lockstep and all drift
	// at wind-rate regardless of sun elevation. See sprite_lit.gdshader's
	// matching call for the full rationale.
	float cloud = cloud_shadow_ground(frag_world_pos, TIME);
	cloud_dim = debug_no_cloud ? 1.0 : 1.0 - cloud * cloud_shadow_strength;
	// NdotL lift on sun-facing sprite normals — matches voxel_clip so terrain
	// and detail sprites pop into bloom at matching angles. See voxel_clip.
	float sun_ndl = max(0.0, dot(world_normal, -sun_world_dir));
	vec3 sun_lit = sun_mask * sun_intensity * sun_color * (0.9 + 0.4 * sun_ndl);
	vec3 block_lit = lm.gba;

	// Reverse directional shading — same pair voxel_clip uses so detail
	// sprites and terrain shade under the same key/fill at matching angles.
	// Faces facing a fill get multiplied by 1 (no tint); faces facing away
	// get multiplied by the fill's color. Both fills are off-axis from the
	// sun, so we don't fade by sun_mask — keeping the tints active outdoors
	// is what gives sunlit slopes their directional read.
	float fill_a_facing = max(0.0, dot(world_normal, -fill_a_world_dir));
	float fill_b_facing = max(0.0, dot(world_normal, -fill_b_world_dir));
	vec3 fill_a_tint = mix(fill_a_color, vec3(1.0), fill_a_facing);
	vec3 fill_b_tint = mix(fill_b_color, vec3(1.0), fill_b_facing);
	if (debug_no_tint) {
		fill_a_tint = vec3(1.0);
		fill_b_tint = vec3(1.0);
	}
	if (debug_white_albedo) {
		tex.rgb = vec3(0.0);
	}
	// Wet sprites darken the same way terrain does — floor tuned via
	// SkyController's wet_albedo_floor.
	tex.rgb *= mix(1.0, wet_albedo_floor, wet_factor);
	vec3 tinted = tex.rgb * fill_a_tint * fill_b_tint;

	// Ground stain composited into the albedo BEFORE the ALBEDO/EMISSION split,
	// exactly as voxel_clip does for terrain, so the mark survives the always-on
	// EMISSION term. Sampled at the blade ROOT (world_origin) — the whole sprite
	// darkens by the stain at its base, matching the ground it's rooted in.
	// world_normal.y (the terrain normal under the sprite) feeds the same
	// verticality gate the terrain uses, so a mark doesn't climb a cliff the
	// grass clings to.
	tinted = apply_ground_stain(tinted, world_origin, world_normal.y);

	NORMAL = world_normal;
	// Baked AO: multiplicative shelter/contact darkening on the lit blade, the
	// same factor voxel_clip applies to terrain (ao=0 open → no change, ao=1
	// sheltered → scaled down by ao_strength). The per-instance occlusion is in
	// INSTANCE_COLOR.a (COLOR.a), computed at scatter time to match the ground.
	// Without this, grass under canopy / against cliffs reads brighter than the
	// shaded ground it sits on.
	float ao_factor = 1.0 - COLOR.a * ao_strength;
	// Same split as sprite_lit / voxel_clip so detail sprites and terrain
	// shadow / get torchlit consistently:
	//   ALBEDO   = direct sun color (ATTENUATION-scaled in light())
	//   EMISSION = ambient sky bounce + block light (never shadowed)
	ALBEDO = tinted * sun_lit * (1.0 - sun_ambient) * ao_factor;
	EMISSION = tinted * (block_lit + sun_lit * sun_ambient * cloud_dim) * ao_factor;

	// Eye adaptation (eye_adaptation.gdshaderinc): lift in low local light when
	// dark-adapted, push toward blowout in bright. light_est = area illuminance
	// (sun + block, pre-shadow, not albedo). Before the debug blocks so
	// diagnostics stay un-adapted. No-op when eye_adaptation == 0.
	float ea_gain = eye_adaptation_gain(dot(sun_lit + block_lit, vec3(0.299, 0.587, 0.114)));
	ALBEDO *= ea_gain;
	EMISSION *= ea_gain;

	if (debug_normals) {
		ALBEDO = vec3(0.0);
		EMISSION = world_normal * 0.5 + 0.5;
	}
	if (debug_unlit) {
		ALBEDO = vec3(0.0);
		EMISSION = tex.rgb;
	}
	if (debug_wet_spec) {
		// Grass has no wet specular; show the shared wet gate it darkens by:
		//   R = wetness_level (global), G = sun_mask * wet_slope (per-fragment).
		ALBEDO = vec3(0.0);
		EMISSION = vec3(wetness_level, sun_mask * wet_slope, 0.0);
	}
}

void light() {
	// ATTENUATION (Godot's directional shadow atlas) and cloud_dim compose as
	// min() so a cloud + cast shadow read as one shadow, not stacked.
	// Per-source energy gate — see voxel_clip.gdshader's light() for the
	// full rationale. LIGHT_COLOR has light_energy * PI baked in; extract
	// the scalar via LIGHT_COLOR.r * INV_PI (lights are authored white).
	const float INV_PI = 0.31830989;
	float light_energy = LIGHT_COLOR.r * INV_PI;
	float light_gate = light_energy * min(ATTENUATION, cloud_dim);
	DIFFUSE_LIGHT += ALBEDO * light_gate;
}
