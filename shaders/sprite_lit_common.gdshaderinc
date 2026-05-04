// Shared uniforms + varyings + fragment() + light() for every lit-sprite
// variant: sprite_lit (upright props), sprite_lit_character (upright +
// stencil for x-ray), sprite_lit_flat (flat-on-ground). Each variant's
// body file:
//
//   1. #include this file
//   2. defines its own vertex() — that's the only thing that differs.
//
// The fragment stage is variant-agnostic because it works off the screen-
// space basis (sprite_right_screen, sprite_up_screen, anchor_px) the
// vertex stage hands it via varyings. The flat variant produces the same
// (1, 0) / (0, -1) basis for upright-equivalent yaw + sin(pitch) stretch
// math, so the texelFetch-style source-pixel recovery in fragment() works
// identically for upright and flat.

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
// Per-sprite uniforms moved to `instance uniform` so all sprites sharing
// a texture can share one ShaderMaterial — Godot batches their draws into
// one call. Per-instance values live in render-instance data, set via
// RenderingServer.InstanceGeometrySetShaderParameter from SpriteBase.cs
// and its subclasses. `sprite_texture` (sampler2D) cannot be an instance
// uniform in Godot 4, so each unique texture still needs its own shared
// material.
instance uniform ivec2 sprite_size = ivec2(1, 1);
instance uniform ivec2 sprite_region_origin = ivec2(0, 0);
// Per-instance horizontal mirror. Sprite shaders sample the texture
// via texelFetch on a screen-space coordinate, so Sprite3D's FlipH (which
// only flips mesh UVs) has no effect — this uniform flips the computed
// tex_coord.x instead. SpriteBase.Mirror seeds a 50% coin flip per instance.
instance uniform bool sprite_mirror = false;
// Upright-only uniforms (align_to_terrain / terrain_normal / forward_offset)
// are declared here even though only the upright body uses them. The
// reason is index alignment: sprite_xray.gdshader (used as the character
// next_pass) declares its instance uniforms in this exact order, and
// Godot warns "More than one material in instance export the same
// instance shader uniform with different indices" if the next_pass and
// the base pass disagree on uniform position. Keeping the same name+order
// across all sprite_lit-family shaders avoids that warning. Flat sprites
// never push these names; defaults render identically to "upright on
// flat ground" but the flat vertex stage ignores them anyway.
instance uniform float align_to_terrain : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 terrain_normal = vec3(0.0, 1.0, 0.0);
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
// math in each variant ignores the rotation/scale part of MODEL_MATRIX, so
// the X-column length is pulled out explicitly and applied — matches
// Node3D.Scale on any ancestor of this sprite. Assumes uniform scale
// (SpriteBase.ScaleMin/Max).
varying flat float instance_scale;
// Screen-pixel delta per one source-pixel step along the sprite's right /
// up axes after camera-pitch stretch. The vertex stage produces these in
// every variant; the fragment stage inverts the basis to recover the source
// pixel for each screen fragment, keeping texelFetch exact.
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

void fragment() {
	if (clip_dither_should_discard(world_origin.y, FRAGCOORD.xy)) {
		discard;
	}

	// Invert the screen-space basis (sprite_right_screen, sprite_up_screen)
	// to recover the source-pixel offset (source_x, source_y) of this
	// fragment from the anchor. The basis already bakes in chunky and
	// instance_scale, so sxy comes out in source-pixel units — no extra
	// scale division needed. Whichever vertex variant we ran, it's
	// calibrated so an axis-aligned upright/flat sprite produces basis
	// (1, 0) / (0, -1) and this collapses to the (d.x, -d.y) mapping.
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
	//
	// The Y origin depends on which body included this file:
	//   - Upright (sprite_lit_body): VERTEX.y ∈ [0, H], anchor at bottom.
	//     `src_px_y_origin_factor` = 1.0 → src_px.y = -sxy.y + H, so
	//     VERTEX.y = 0 maps to source row H-1 (bottom of source art).
	//   - Flat (sprite_lit_flat_body): VERTEX.y ∈ [-H/2, H/2], anchor at
	//     center. `src_px_y_origin_factor` = 0.5 → src_px.y = -sxy.y + H/2,
	//     so VERTEX.y = 0 maps to source row H/2 (center of source art).
	float half_w = float(sprite_size.x / 2);
	vec2 src_px = vec2(sxy.x + half_w, -sxy.y + float(sprite_size.y) * src_px_y_origin_factor);

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
