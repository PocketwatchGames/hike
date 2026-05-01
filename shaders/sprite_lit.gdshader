shader_type spatial;
// Opaque queue (no blend_mix): the alpha cut in fragment() (discard if
// tex.a < 0.5) gives alpha-cut silhouettes without putting the sprite into
// the transparent queue. Transparent queue requires per-frame back-to-front
// sorting which prevents Godot from batching draws across spatially-spread
// sprites; opaque queue lets sprites sharing the same material batch into
// one draw call. The dither-fade in fragment() also uses discard, not
// partial alpha, so visibility stippling still works in the opaque queue.
// All current art is alpha-cut compatible — if a future sprite genuinely
// needs soft-alpha edges it'll need to opt into a separate blend_mix variant.
render_mode depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform;

// Y-aligned, camera-facing pixel-art sprite.
//
// The quad is authored on a Sprite3D with centered = false, pixel_size = 1.0,
// billboard = 0, offset = (-W/2, H). VERTEX therefore spans
//   x in [-W/2, W/2], y in [0, H]
// in source-pixel units, with VERTEX = (0, 0) at the node origin (the anchor,
// by convention the center-bottom of the sprite where it touches the ground).
//
// The vertex stage places each vertex in world space on a Y-aligned billboard
// that rotates around Y to face the camera (but does not tilt — it is not a
// full billboard). Vertical offsets are stretched by 1/cos(pitch) so a row of
// source pixels projects to one screen-pixel row despite the camera's tilt.
//
// The fragment stage computes the source texel from FRAGCOORD relative to the
// snapped screen-space projection of the anchor, so every fragment samples an
// exact integer source pixel — no UV interpolation drift.

// filter_linear is required by the subpixel_aa path: the fragment snaps the
// sample point to the texel center except within a ~1-screen-pixel band at
// each texel boundary, where linear filtering blends into the neighbor. When
// subpixel_aa is off the snap collapses to the texel center and linear
// sampling returns exactly the nearest texel value — matches the prior
// filter_nearest / texelFetch behavior visually.
uniform sampler2D sprite_texture : source_color, filter_linear;
// Subpixel-perfect pixel-art filter (toggle for A/B). See the matching block
// in detail_sprite.gdshader for the full rationale; same math, applied to
// the screen-basis-inverted source pixel position `src_px`. Only affects
// visibly rotated (AlignToTerrain > 0) or non-integer-scaled (ScaleMin !=
// ScaleMax) sprites — upright 1:1 sprites were already pixel-perfect via
// texelFetch. Atlas-bleed note: linear sampling would pull in neighboring
// atlas cells at the sprite's outer texels; we clamp px_aa into
// [0.5, sprite_size - 0.5] so the sample stays inside the sprite region.
uniform bool subpixel_aa = true;
// RGBA8 light map: R = sun mask, GBA = post-pow block light per RGB channel.
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
// Cloud occlusion parameters shared with the sky + fog + voxel + water
// shaders (registered by SkyController). cloud_shadow.gdshaderinc uses
// these to project cloud shadows along the sun direction. sun_world_dir
// must also be declared; add it before the include.
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_offset;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
// Lighting-only debug view shared with voxel_clip + detail_sprite. When
// true the sprite's tex.rgb is replaced with white so ALBEDO/EMISSION
// reflect only the lighting path (sun_lit * cloud * shadow + block_lit),
// letting you directly compare sprite vs terrain lighting with source
// textures out of the equation.
global uniform bool debug_white_albedo;
#include "res://shaders/cloud_shadow.gdshaderinc"
// Per-sprite uniforms moved to `instance uniform` so all LitSprites sharing
// a texture can share one ShaderMaterial — Godot batches their draws into
// one call. Per-instance values live in render-instance data, set via
// RenderingServer.InstanceGeometrySetShaderParameter from LitSprite.cs.
// `sprite_texture` (sampler2D) cannot be an instance uniform in Godot 4,
// so each unique texture still needs its own shared material.
instance uniform ivec2 sprite_size = ivec2(1, 1);
instance uniform ivec2 sprite_region_origin = ivec2(0, 0);
// Per-instance horizontal mirror. The sprite_lit shader samples the texture
// via texelFetch on a screen-space coordinate, so Sprite3D's FlipH (which
// only flips mesh UVs) has no effect — this uniform flips the computed
// tex_coord.x instead. LitSprite.Mirror seeds a 50% coin flip per instance.
instance uniform bool sprite_mirror = false;
// Blend between upright billboarding (0) and terrain-aligned billboarding
// (1). Upright = yaw-to-camera with world-up as the sprite's up axis.
// Terrain-aligned = yaw-to-camera + roll within the sprite plane so the
// sprite's up axis follows terrain_normal (same roll math detail_sprite.gdshader
// uses for scattered grass). Fractional values lerp the roll angle.
// terrain_normal is the per-instance normal of the surface under the sprite
// anchor in world space; ignored when align_to_terrain is 0. Defaults keep
// the upright behaviour so materials that don't set either uniform render
// identically to the pre-alignment shader.
instance uniform float align_to_terrain : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 terrain_normal = vec3(0.0, 1.0, 0.0);

// Push the sprite rendering toward the camera (in meters) along the
// horizontal billboard-forward axis. Recomputed from cam_back every frame,
// so it follows camera yaw — unlike a static node Z offset, which bakes a
// fixed world-space vector into MODEL_MATRIX and only reads as "forward"
// from one angle. Used to park the sprite at the front edge of a cylinder
// collider so the art lines up visually with the walkable footprint; collider
// still sits at the node origin, only the rendered sprite shifts.
instance uniform float forward_offset = 0.0;

// Dither-fade amount. 1 = fully opaque, 0 = fully discarded. Compared
// against a screen-space 4x4 Bayer threshold in fragment(); fractional
// values produce an ordered-dither stipple that reads as pop-in / pop-out
// at the chunky sprite scale. Driven by Mob's discovery fade so a mob
// transitions from invisible → dithered-in → solid over ~0.1s.
instance uniform float visibility : hint_range(0.0, 1.0) = 1.0;
// Silhouette tint blend. 0 = normal lit color, 1 = fully flat-tinted
// (lighting is bypassed; fragment outputs silhouette_tint directly). The
// mix happens POST-lighting so a silhouetted sprite reads as a constant
// flat shape regardless of the lightmap/cloud/shadow state under it —
// the "you remember this thing is here" cue for discovered-but-unseen mobs.
instance uniform float silhouette_amount : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 silhouette_tint : source_color = vec3(0.0);

global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
#include "res://shaders/clip_dither.gdshaderinc"
global uniform float sprite_chunky;
global uniform float sprite_stretch;

// Block-light shadow projector — see shaders/block_light_shadow.gdshaderinc.
// Sampled in fragment() at the sprite's anchor world_origin; the returned
// factor dims block_lit (lm.gba) so a sprite standing above this sprite
// (e.g. a tree shading a smaller sprite below it) reduces the torch/lamp
// contribution here without affecting unshadowed regions.
global uniform sampler2D block_light_shadow_tex : filter_linear;
global uniform vec3 block_light_shadow_origin;
global uniform vec3 block_light_shadow_right;
global uniform vec3 block_light_shadow_up;
global uniform float block_light_shadow_size;
global uniform float block_light_shadow_strength;
global uniform float block_light_shadow_blur_uv;
global uniform bool block_light_shadow_enabled;
#include "res://shaders/block_light_shadow.gdshaderinc"

varying flat vec2 anchor_px;
varying flat vec3 world_origin;
// Uniform scale extracted from MODEL_MATRIX in vertex(). The custom billboard
// math below ignores the rotation/scale part of MODEL_MATRIX, so we pull the
// X-column length out explicitly and apply it — matches Node3D.Scale on any
// ancestor of this sprite. Assumes uniform scale (LitSprite.ScaleMin/Max).
varying flat float instance_scale;
// Screen-pixel delta per one source-pixel step along the rolled sprite axes
// (sprite_right_rolled and sprite_up_rolled after camera-pitch stretch).
// For align_to_terrain=0 these collapse to (1, 0) and (0, -1) — i.e. the
// upright 1-source-pixel-per-screen-pixel mapping the old fragment relied on.
// For rolled sprites the fragment stage inverts this 2x2 basis to recover
// the source pixel for each screen fragment, keeping texelFetch exact.
varying flat vec2 sprite_right_screen;
varying flat vec2 sprite_up_screen;
// Fragment→light varying. 1 = unclouded, 0 = fully overcast.
// Written in fragment(), read in light() so cloud shadow composes with
// the directional shadow atlas as min() rather than multiply — see light().
varying float cloud_dim;
// Per-fragment world position (interpolated billboard point). Used for
// per-fragment cloud sampling so the sprite's cloud shadow stays coherent
// with the fog shader's shaft inscatter: both sample along the same sun
// ray at the fragment's own Y, so a sprite in a cloud gap sees the same
// bright sky that the shaft above it sees, and a sprite in a shadow sees
// the same dim air. Sampling at world_origin (Y-independent) would let
// the sprite land in shadow while the air above is in a gap, which makes
// dark sprites read as gray silhouettes when bright fog composites on
// top of their crushed color.
varying vec3 frag_world_pos;

float hash13(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash13(i); float n100 = hash13(i + vec3(1,0,0));
	float n010 = hash13(i + vec3(0,1,0)); float n110 = hash13(i + vec3(1,1,0));
	float n001 = hash13(i + vec3(0,0,1)); float n101 = hash13(i + vec3(1,0,1));
	float n011 = hash13(i + vec3(0,1,1)); float n111 = hash13(i + vec3(1,1,1));
	return mix(mix(mix(n000,n100,f.x), mix(n010,n110,f.x), f.y),
	           mix(mix(n001,n101,f.x), mix(n011,n111,f.x), f.y), f.z);
}

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;
	// Column length = scale along that axis (for uniform-scale + rotation).
	instance_scale = length(MODEL_MATRIX[0].xyz);

	// Two-stage billboard identical to detail_sprite.gdshader's:
	//   1) Yaw — rotate the sprite plane around world Y so it faces the
	//      camera. sprite_right_yaw is horizontal, world_up is the plane's
	//      authored up axis. The plane never pitches into/out of screen.
	//   2) Roll — rotate within the plane around sprite_forward (the
	//      out-of-plane axis) so the sprite's up axis follows terrain_normal.
	//      The angle is lerped by align_to_terrain, so 0 keeps the original
	//      upright behaviour and 1 fully rolls to match the slope.
	// Flat ground (terrain_normal = world-up) makes roll = 0 regardless of
	// align_to_terrain, so it's safe to leave terrain_normal at its default
	// when a caller doesn't know the slope.
	vec3 world_up = vec3(0.0, 1.0, 0.0);
	vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right_yaw = normalize(cross(world_up, cam_back));
	vec3 sprite_forward = cross(sprite_right_yaw, world_up);

	// Shift the sprite toward the camera along the billboard-forward axis.
	// Must happen after sprite_forward is resolved and before world_origin is
	// consumed by the billboard expansion + clip-space projection below, so
	// vertices, anchor_px, and the fragment's light-sample position all share
	// the shifted origin. sprite_forward = cross(sprite_right_yaw, world_up)
	// evaluates to the horizontal projection of cam_back, which points FROM
	// the sprite TOWARD the camera (cam_back is the world direction of the
	// camera's +Z / "behind-the-lens" axis, which from any point in the
	// scene reads as "toward the camera"). So we add.
	world_origin += sprite_forward * forward_offset;

	vec3 n = normalize(terrain_normal);
	vec3 n_in_plane = n - dot(n, sprite_forward) * sprite_forward;
	float right_comp = dot(n_in_plane, sprite_right_yaw);
	float up_comp = dot(n_in_plane, world_up);
	float roll = atan(right_comp, up_comp) * clamp(align_to_terrain, 0.0, 1.0);
	float cr = cos(roll);
	float sr = sin(roll);

	vec3 sprite_right_rolled = cr * sprite_right_yaw - sr * world_up;
	vec3 sprite_up_rolled = sr * sprite_right_yaw + cr * world_up;

	float stretch = max(sprite_stretch, 1e-4);
	float chunky = max(sprite_chunky, 1e-6);

	// Camera-pitch compensation: stretch only the world-Y component of each
	// rolled axis so that whichever axis ends up vertical in world space
	// (the upright-case up axis, or a partly-vertical axis of a rolled
	// sprite) projects 1 source pixel to 1 screen pixel under iso pitch.
	// Horizontal components pass through unchanged — they're already in the
	// screen plane.
	vec3 right_stretched = vec3(sprite_right_rolled.x, sprite_right_rolled.y * stretch, sprite_right_rolled.z);
	vec3 up_stretched = vec3(sprite_up_rolled.x, sprite_up_rolled.y * stretch, sprite_up_rolled.z);

	vec3 vertex_world = world_origin
		+ VERTEX.x * chunky * instance_scale * right_stretched
		+ VERTEX.y * chunky * instance_scale * up_stretched;
	frag_world_pos = vertex_world;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

	// Project origin + a 1-source-pixel step along each rolled axis into
	// pixel space, so the fragment stage can invert the basis to recover
	// the source pixel for each screen fragment. This replaces the upright-
	// only d.x/d.y → tex_coord mapping; for align_to_terrain = 0 the basis
	// collapses to (1, 0) / (0, -1) and reproduces the old mapping exactly.
	vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin, 1.0);
	vec4 right_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + chunky * instance_scale * right_stretched, 1.0);
	vec4 up_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + chunky * instance_scale * up_stretched, 1.0);
	vec2 origin_ndc = origin_clip.xy / origin_clip.w;
	vec2 right_ndc = right_clip.xy / right_clip.w;
	vec2 up_ndc = up_clip.xy / up_clip.w;
	anchor_px = floor((origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE + 0.5);
	sprite_right_screen = (right_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
	sprite_up_screen = (up_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
}

void fragment() {
	if (clip_dither_should_discard(world_origin.y, FRAGCOORD.xy)) {
		discard;
	}

	// Invert the screen-space basis (sprite_right_screen, sprite_up_screen)
	// to recover the source-pixel offset (source_x, source_y) of this
	// fragment from the anchor. The basis already bakes in chunky and
	// instance_scale, so sxy comes out in source-pixel units — no extra
	// scale division needed. For align_to_terrain=0 the basis is (1, 0) /
	// (0, -1) and this collapses to the old (d.x, -d.y) mapping.
	vec2 d = FRAGCOORD.xy - anchor_px;
	float basis_det = sprite_right_screen.x * sprite_up_screen.y - sprite_up_screen.x * sprite_right_screen.y;
	float inv_det = 1.0 / (abs(basis_det) < 1e-6 ? 1e-6 : basis_det);
	vec2 sxy = vec2(
		(sprite_up_screen.y * d.x - sprite_up_screen.x * d.y) * inv_det,
		(sprite_right_screen.x * d.y - sprite_right_screen.y * d.x) * inv_det
	);
	// Continuous source-pixel position (pre-mirror). The half-width offset
	// uses integer division + float cast to preserve the old integer
	// texelFetch semantics for odd sprite_size.x — a plain `* 0.5` would
	// shift odd-width sprites by half a texel relative to the previous
	// behavior. sprite_size.y never uses the division so a plain cast is fine.
	float half_w = float(sprite_size.x / 2);
	vec2 src_px = vec2(sxy.x + half_w, -sxy.y + float(sprite_size.y));

	// Bounds check on the integer floor — matches the old tex_coord bounds
	// exactly so we don't widen or shrink the sprite silhouette compared to
	// the pre-subpixel-aa path.
	ivec2 tex_coord = ivec2(int(floor(src_px.x)), int(floor(src_px.y)));
	if (sprite_mirror) {
		tex_coord.x = sprite_size.x - 1 - tex_coord.x;
	}
	if (tex_coord.x < 0 || tex_coord.x >= sprite_size.x
		|| tex_coord.y < 0 || tex_coord.y >= sprite_size.y) {
		discard;
	}

	vec2 px_aa;
	if (subpixel_aa) {
		// Snap into the texel center for most of each texel, with a
		// ~1-screen-pixel transition band at the boundary where linear
		// filtering blends into the neighbor. fwidth(src_px) is in source
		// texels per screen pixel, so the clamp width scales correctly with
		// whatever per-instance scale / camera zoom / roll happens to be.
		vec2 seam = floor(src_px) + 0.5;
		vec2 dp = max(fwidth(src_px), vec2(1e-6));
		px_aa = seam + clamp((src_px - seam) / dp, -0.5, 0.5);
	} else {
		px_aa = floor(src_px) + 0.5;
	}
	// Clamp into [0.5, sprite_size - 0.5] so the outer texels of the sprite
	// region don't blend with whatever lives next to them in the atlas.
	// Loses the subpixel AA on the outermost half-texel, which is fine —
	// authored sprites tend to fade alpha to 0 there anyway.
	px_aa = clamp(px_aa, vec2(0.5), vec2(sprite_size) - vec2(0.5));
	// Mirror the continuous coordinate the same way the integer fetch did:
	// tex_coord.x = sprite_size.x - 1 - tex_coord.x collapses at a texel
	// center to sprite_size.x - (center), so the continuous form is
	// sprite_size.x - px_aa.x.
	if (sprite_mirror) {
		px_aa.x = float(sprite_size.x) - px_aa.x;
	}

	vec2 atlas_size = vec2(textureSize(sprite_texture, 0));
	vec2 atlas_uv = (vec2(sprite_region_origin) + px_aa) / atlas_size;
	vec4 tex = texture(sprite_texture, atlas_uv);
	if (tex.a < 0.5) {
		discard;
	}

	// Dither fade: 4x4 Bayer threshold keyed off integer FRAGCOORD so the
	// pattern is stable at the screen-pixel level (no sub-pixel shimmer as
	// the camera pans). `visibility >= 1` skips the compare entirely so
	// static props pay nothing for this path.
	if (visibility < 1.0) {
		const mat4 bayer = mat4(
			vec4( 0.0,  8.0,  2.0, 10.0),
			vec4(12.0,  4.0, 14.0,  6.0),
			vec4( 3.0, 11.0,  1.0,  9.0),
			vec4(15.0,  7.0, 13.0,  5.0)
		) * (1.0 / 16.0);
		ivec2 fp = ivec2(FRAGCOORD.xy) % 4;
		if (bayer[fp.y][fp.x] >= visibility) {
			discard;
		}
	}

	// Sprite anchor sits on the ground — i.e. at the top face of a solid
	// voxel — so sampling at world_origin lands inside that solid voxel and
	// reads 0 light. Push up half a voxel into the air above so we read the
	// adjacent air-voxel's light. (Same trick voxel_clip uses with normal
	// offset, but for a sprite "up" is always the relevant direction.)
	vec3 light_sample_pos = world_origin + vec3(0.0, 0.5, 0.0);
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = textureLod(light_map, uvw, 0.0);
	float sun_mask = pow(lm.r, light_falloff_exp);
	// Cloud shadow: pass to light() via cloud_dim so it composes with
	// ATTENUATION as min() (darkest occluder wins) rather than multiplying
	// onto the direct-sun term. Ambient sky-bounce (EMISSION half) is still
	// cloud-dimmed below.
	//
	// Matches the terrain: cloud_shadow_ground samples the cloud noise at
	// world XZ only (no sun-direction projection), giving a 2D pattern
	// draped across the world that drifts at a constant wind-rate regardless
	// of sun elevation. Using the same sample here keeps sprite and terrain
	// shadows in lockstep — a pine's cloud state always matches the ground
	// right under it. The tradeoff: sprite cloud state may briefly disagree
	// with the fog shader's sun-projected shaft sampling during day, which
	// in rare cases reads as a sprite in cloud shadow under a bright shaft.
	// Acceptable compared to terrain / sprite mismatch visible everywhere.
	float cloud = cloud_shadow_ground(frag_world_pos, TIME);
	cloud_dim = 1.0 - cloud * cloud_shadow_strength;
	vec3 sun_lit = sun_mask * sun_intensity * sun_color;
	// Per-sprite block_lit dim. Sample the projector at the anchor (one
	// position for the whole sprite) — the silhouette read should be
	// per-sprite, not per-fragment, so a sprite either reads as "stood
	// under another sprite" or doesn't, with no cross-fading inside its
	// own footprint.
	vec3 block_lit = lm.gba * block_light_shadow_factor(world_origin);
	vec3 light = min(vec3(1.0), sun_lit + block_lit);

	NORMAL = vec3(0.0, 1.0, 0.0);
	if (debug_white_albedo) {
		tex.rgb = vec3(0.0);
	}
	// Match voxel_clip's split so sprites and terrain shadow consistently:
	//   ALBEDO   = direct sun color (scaled by ATTENUATION in light())
	//   EMISSION = ambient sky bounce + block light (never shadowed)
	// Final pixel = ALBEDO * ATTENUATION + EMISSION (ambient_light_disabled).
	// Silhouette blends the lit result toward a flat tint: ALBEDO fades to
	// zero (so light() stops adding sun) while EMISSION fades to the tint,
	// yielding `tint` at silhouette_amount=1 regardless of lighting state.
	vec3 lit_albedo = tex.rgb * sun_lit * (1.0 - sun_ambient);
	vec3 lit_emission = tex.rgb * (block_lit + sun_lit * sun_ambient * cloud_dim);
	ALBEDO = mix(lit_albedo, vec3(0.0), silhouette_amount);
	EMISSION = mix(lit_emission, silhouette_tint, silhouette_amount);
}

void light() {
	// ATTENUATION is Godot's directional shadow atlas — terrain + sprite
	// shadow proxies, already composed correctly in the atlas. No LIGHT_COLOR
	// / NdotL so sprites' billboarded fake normal doesn't introduce a fake
	// lambert term.
	//
	// Cloud shadow (cloud_dim from fragment()) and the shadow atlas both
	// occlude direct sun. Compose as min(): whichever is darker wins, not
	// product — so a cast shadow + cloud shadow read as one shadow, not two
	// stacked. Ambient sky-bounce is still cloud-dimmed in EMISSION.
	//
	// Per-source energy gate — see voxel_clip.gdshader's light() for the
	// full rationale. LIGHT_COLOR = light_color * light_energy * PI in
	// Godot 4; both directional lights are white so LIGHT_COLOR.r * INV_PI
	// gives the scalar energy factor we need.
	const float INV_PI = 0.31830989;
	float light_energy = LIGHT_COLOR.r * INV_PI;
	DIFFUSE_LIGHT += ALBEDO * light_energy * min(ATTENUATION, cloud_dim);
}
