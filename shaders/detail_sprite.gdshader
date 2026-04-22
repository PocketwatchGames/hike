shader_type spatial;
// Alpha-tested opaque, NOT alpha-blended:
//   - depth_prepass_alpha runs an alpha-tested depth prepass so the depth
//     buffer correctly reflects the cut-out sprite shapes before the main
//     color pass. With MultiMesh we can't reorder instances per-frame for
//     transparent-pass back-to-front sorting; rendering in the opaque pass
//     with depth-write makes z-sort an automatic consequence of the depth
//     buffer instead.
//   - depth_draw_opaque writes depth in the color pass too, so subsequent
//     opaque draws (including other detail-sprite chunks) sort correctly.
// blend_mix is omitted — the sprite is fully opaque or discarded; nothing
// to blend.
render_mode depth_prepass_alpha, depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform;

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

// --- Normal pipeline ---
// Terrain-anchored perturbation model. The BASE world normal for every
// sprite is the per-instance terrain normal that ChunkDetailScatter packs
// into INSTANCE_CUSTOM.xyz (same vector the sprite uses for its slope
// lean), so sprites inherit the lighting profile of the voxel beneath
// them. A small tangent-space perturbation (from an authored normal map
// or the synthetic dome below) is projected into world space via the
// sprite's horizontal (sprite_right) + vertical (sprite_up) axes and
// added on top, scaled by dome_strength. sprite_forward (the billboard
// out-of-plane direction, which rotates with the camera) is NOT used —
// that's what made the previous model swing darkness with camera angle.
// Net effect: sprites light like the terrain below them + a subtle
// per-pixel wiggle that catches specular highlights without dominating.
uniform sampler2D normal_map : hint_normal, filter_nearest;
uniform bool use_normal_map = false;
// Synthetic dome tangent-normal when no normal_map is bound:
//   tangent.x = sign(2*UV.x-1) * sqrt(|2*UV.x-1|)  (horizontal lean)
//   tangent.y = 1 - UV.y^2                         (vertical — flat at
//                                                   root UV.y=1, strong
//                                                   at tip UV.y=0)
//   tangent.z = unused in terrain-anchored mode
// dome_strength scales the perturbation. 0 = pure terrain normal (matches
// ground exactly). ~0.25 = subtle dynamic variation. 1 = maximum wiggle.
uniform float dome_strength : hint_range(0.0, 1.0) = 0.25;

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

// Ground-rooting: each texel column of the sprite picks a random fade
// height in [0, ground_fade_pixels] pixels, then smoothstep-lerps that
// band toward COLOR.rgb (per-instance ground tint set by
// ChunkDetailScatter). The per-column hash produces an irregular
// pixel-art boundary (some columns have no tint, others have the full
// N pixels) rather than a clean horizontal band.
uniform float ground_fade_pixels : hint_range(0.0, 8.0) = 3.0;

// --- Lighting globals (shared with sprite_lit / voxel_clip) ---
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
global uniform vec3 sun_world_dir;
// Shared tint globals (voxel_clip uses the same pair). Two off-axis fill
// directions and their colors. Surfaces facing away from a fill are
// multiplied by that fill's color; surfaces facing toward it by 1. With
// a dome or authored normal this gives the sprite free directional shading.
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
// 1 / cos(camera_pitch). Stretches sprite Y so a row of source pixels projects
// to one screen-pixel row regardless of the iso camera's tilt — same trick
// sprite_lit uses; without it, detail sprites read as foreshortened relative
// to authored 2D sprites in the same scene.
global uniform float sprite_stretch;

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

varying vec3 world_origin;
// Fragment->light pass for cloud shadow composition (same trick sprite_lit
// uses so a cloud + terrain shadow read as one shadow, not stacked).
varying float cloud_dim;
// Per-fragment world position. Sampled for cloud shadow so the sprite's
// cloud state matches the fog shader's Y-dependent shaft sampling at the
// same sun ray. See the cloud_shadow call in fragment().
varying vec3 frag_world_pos;
// Sprite tangent axes in world space (un-stretched rolled vectors). The
// terrain-anchored normal model uses sprite_right and sprite_up only —
// sprite_forward (the billboard out-of-plane axis) rotates with camera
// and is deliberately excluded from the perturbation projection.
varying vec3 sprite_right_ws;
varying vec3 sprite_up_ws;
// Per-instance terrain normal in world space — the base normal for
// lighting. Inherited from INSTANCE_CUSTOM.xyz (same source as the slope
// lean), so sprites match the lighting profile of the voxel they sit on.
varying vec3 terrain_normal_ws;

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
	// from the sway phase so mirror doesn't correlate with wind timing.
	// Computed up front because both the UV flip (texture sample) and the
	// tangent frame X flip (normal sampling) need to agree. Without the
	// tangent flip, normal-mapped / dome-synthesised nx stays in its
	// authored orientation while UV is mirrored, so adjacent instances end
	// up with dark sides swapped.
	float mirror = step(0.5, hash12(world_origin.xz + vec2(7.31, 13.71)));
	float mirror_sign = 1.0 - 2.0 * mirror;
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

	// Pass the unstretched tangent frame to the fragment stage. The stretch
	// below is a screen-space correction for camera pitch (it distorts the
	// quad geometry so source rows project one-to-one), but the sprite's
	// actual tangent-space axes in world space are the un-stretched rolled
	// vectors. Using the stretched versions would shear normals and make
	// lighting read wrong at high camera tilt.
	//
	// For mirrored instances we negate the right axis so that sampling the
	// normal source (map or dome synthesis) from flipped UV and then
	// multiplying by sprite_right_ws produces the same world-space normal
	// an unmirrored instance would produce at the equivalent art location.
	sprite_right_ws = sprite_right_rolled * mirror_sign;
	sprite_up_ws = sprite_up_rolled;
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

	vec3 wind_offset = wind_dir * (sway * wind_amplitude * bend);

	// Player push: radial in XZ, falloff to 0 at player_radius.
	vec3 to_sprite = world_origin - player_pos;
	to_sprite.y = 0.0;
	float dist = length(to_sprite);
	vec3 push_offset = vec3(0.0);
	if (player_radius > 0.0 && dist < player_radius && dist > 1e-4) {
		vec3 push_dir = to_sprite / dist;
		float falloff = 1.0 - smoothstep(0.0, player_radius, dist);
		push_offset = push_dir * (falloff * player_strength * bend);
	}

	vertex_world += wind_offset + push_offset;
	frag_world_pos = vertex_world;

	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);
}

void fragment() {
	if (world_origin.y > camera_clip) {
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
	vec2 sprite_uv = px_aa / full_size;
	vec4 tex = texture(sprite_texture, sprite_uv);
	if (tex.a < 0.5) {
		discard;
	}

	// Ground root blend — smoothly lerp the bottom N texels of the sprite
	// toward the per-instance ground tint (COLOR, set by ChunkDetailScatter).
	// Per texel-column hash offsets the fade threshold so the boundary is
	// irregular (pixel-art "drip"), while smoothstep keeps the interior
	// gradient reading as real lerp rather than a flat color block.
	// Quantizing the hash input to the texel grid (not raw UV.x) keeps the
	// boundary stable under filter_nearest — all screen fragments within one
	// texel agree on threshold, so there's no sub-texel shimmer. Per-instance
	// offset decorrelates adjacent blades.
	vec2 tex_size = vec2(textureSize(sprite_texture, 0)) * uv_region.zw;
	float texel_col = floor(UV.x * tex_size.x);
	float col_hash = hash12(vec2(texel_col, world_origin.x * 17.0 + world_origin.z * 23.0));
	// Per-column fade height in [0, ground_fade_pixels] texels. Columns
	// that roll near 0 stay fully untinted; columns that roll near N are
	// tinted all the way up to N texels from the root. Smoothstep within
	// the column gives a gradient from pure texture at the band's top to
	// full ground tint at UV.y = 1.
	float fade_px = col_hash * ground_fade_pixels;
	float threshold = 1.0 - fade_px / tex_size.y;
	float ground_blend = smoothstep(threshold, 1.0, UV.y);
	tex.rgb = mix(tex.rgb, COLOR.rgb, ground_blend);

	// Terrain-anchored normal: base = terrain normal under the sprite,
	// plus a small tangent-space perturbation. We extract only the (x, y)
	// components of the tangent normal (horizontal + "up-in-sprite") and
	// project them through sprite_right / sprite_up — sprite_forward is
	// skipped because it rotates with the camera. Scaled by dome_strength
	// so 0 = pure terrain normal (matches ground lighting exactly) and
	// larger values introduce progressively more per-pixel variation.
	float raw_nx;
	float raw_ny;
	if (use_normal_map) {
		vec3 m = texture(normal_map, atlas_uv).xyz * 2.0 - 1.0;
		raw_nx = m.x;
		raw_ny = m.y;
	} else {
		float u = 2.0 * UV.x - 1.0;
		raw_nx = sign(u) * sqrt(abs(u));
		// Godot QuadMesh UV.y=0 at top, UV.y=1 at bottom. (1 - UV.y²)
		// reads 0 at the root and ≈1 at the tip, so the body bias adds
		// a subtle "tip leans up" without fighting the terrain normal.
		raw_ny = 1.0 - UV.y * UV.y;
	}
	vec3 perturb_ws = raw_nx * sprite_right_ws + raw_ny * sprite_up_ws;
	vec3 world_normal = normalize(terrain_normal_ws + perturb_ws * dome_strength);

	// Sample the light map at the sprite's anchor (root). Per-fragment
	// sampling would shimmer as the sprite sways; a single anchor sample
	// is correct because light_map is voxel-resolution. Done before the
	// tint so sun_mask can weight the NdotL fade.
	vec3 light_sample_pos = world_origin + vec3(0.0, 0.5, 0.0);
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = textureLod(light_map, uvw, 0.0);
	float sun_mask = pow(lm.r, light_falloff_exp);
	// Y-dependent per-fragment sampling — keeps the grass's cloud state in
	// agreement with the fog shader's shaft sampling above it (both project
	// along the sun ray at the fragment's own Y). See the matching call in
	// sprite_lit.gdshader for the full rationale. Grass is short so the
	// Y-dep / Y-indep divergence is small, but keeping the formulation
	// identical across billboard shaders prevents future drift.
	float cloud = cloud_shadow(frag_world_pos, TIME);
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
	vec3 tinted = tex.rgb * fill_a_tint * fill_b_tint;

	NORMAL = world_normal;
	// Same split as sprite_lit / voxel_clip so detail sprites and terrain
	// shadow / get torchlit consistently:
	//   ALBEDO   = direct sun color (ATTENUATION-scaled in light())
	//   EMISSION = ambient sky bounce + block light (never shadowed)
	ALBEDO = tinted * sun_lit * (1.0 - sun_ambient);
	EMISSION = tinted * (block_lit + sun_lit * sun_ambient * cloud_dim);

	if (debug_normals) {
		ALBEDO = vec3(0.0);
		EMISSION = world_normal * 0.5 + 0.5;
	}
	if (debug_unlit) {
		ALBEDO = vec3(0.0);
		EMISSION = tex.rgb;
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
	DIFFUSE_LIGHT += ALBEDO * light_energy * min(ATTENUATION, cloud_dim);
}
