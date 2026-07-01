// Shared body for the lit 3D skinned-character / prop shaders. Two thin
// wrappers include this and differ only by their stencil_mode line:
//   - model_lit.gdshader           : props / trees / static meshes (no stencil)
//   - model_lit_character.gdshader : mobs / discoverable meshes that want the
//                                    X-ray next_pass — writes stencil = 8 so the
//                                    sprite/model X-ray passes skip character
//                                    pixels (same contract as sprite_lit_character).
//
// Samples the global `light_map` so the mesh darkens in caves and brightens in
// sun like the world around it, with a wrap-lambert form term from the real
// surface normal. See the per-uniform comments below for the AO and lighting
// split. Beyond lighting, this carries the discovery presentation that mobs
// (and discoverable props) drive — a Bayer dither pop-in (`visibility`) and a
// flat silhouette tint for remembered-but-unseen targets (`silhouette_amount`)
// — matching sprite_lit_common so 3D and sprite visuals read identically.

// Character albedo atlas. Nearest filter + no mip to keep the flat, pixel-art-
// adjacent low-poly look consistent with the rest of the game.
uniform sampler2D albedo_tex : source_color, filter_nearest;

// RGBA8 light map: R = sun mask, GBA = post-pow block light per RGB channel.
global uniform sampler3D light_map : filter_linear, repeat_enable;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
// Cloud occlusion + sun direction (owned by SkyController). sun_world_dir,
// cloud_offset, cloud_threshold, cloud_sharpness, cloud_noise_tex and
// cloud_altitude are required by cloud_shadow.gdshaderinc and must be declared
// before the include.
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_offset;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"

// Dark-adaptation ("night eyes") tone curve. Driven by the player sim
// (Player.EyeDilation) via GameClient; exact no-op when eye_adaptation == 0.
#include "res://shaders/eye_adaptation.gdshaderinc"

// Ceiling cutaway: discard the mesh above the animating camera clip so model
// mobs / props vanish under a cutaway like sprites do. Unlike terrain
// (voxel_clip, per-fragment), this tests the mesh ORIGIN so the whole model
// dithers/culls as a unit rather than being sliced at the plane — see the
// model_origin varying. Globals owned by GameCamera; declare before the include.
global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
global uniform vec3 camera_clip_growth_center;
global uniform float camera_clip_growth_max_radius;
global uniform float camera_clip_growth_edge_softness;
#include "res://shaders/clip_dither.gdshaderinc"

// How strongly the wrap-lambert form term darkens surfaces facing away from
// the sun. 0 = flat (lit purely by the volumetric light map, like a sprite);
// 1 = full wrap-lambert. A middle value keeps the world-consistent lightmap
// look while giving the mesh enough shading to read as 3D.
uniform float form_strength : hint_range(0.0, 1.0) = 0.55;

// Per-material multiplicative tint applied to the sampled albedo BEFORE the
// lighting math. Default (1,1,1) leaves the texture untouched (back-compatible
// for character materials). Used by rock_lit.tres to brighten/warm the Synty
// rock palette (which UV-samples a flat mid-grey region) into a stone tone
// that reads at desert biome ambient without changing the source atlas.
uniform vec3 color_tint : source_color = vec3(1.0, 1.0, 1.0);

// Optional self-illumination, independent of the light map (so it glows even
// in the dark / when no fire light reaches it). Default strength 0 is inert so
// every existing material (trees, rocks, signs, chests, characters) renders
// exactly as before. Modulated by the sampled albedo (`base`) rather than added
// flat, so the glow FOLLOWS the texture — bright ember texels glow hot, dark
// charred wood stays dim — instead of washing the whole surface a flat color.
// Tint warm-shifts the glow; strength scales it (can exceed 1 for a hot bloom).
// Used by the lit campfire LOG material; the doused variant (and the stone ring,
// a separate mesh) leave strength at 0.
uniform vec3 emission_tint : source_color = vec3(0.0);
uniform float emission_strength = 0.0;

// --- Discovery presentation (instance uniforms) -----------------------------
// Per-instance so every mesh that shares one model material can still fade /
// silhouette independently — driven via GeometryInstance3D.SetInstanceShader
// Parameter from Mob (model mobs) and Discoverable (mesh props). Defaults are
// inert (fully visible, no silhouette) so every existing static prop / tree /
// player that never pushes them renders exactly as before. Names + order match
// model_xray.gdshader so a material that pairs this base with the X-ray
// next_pass keeps instance-uniform indices aligned across both passes (Godot
// warns otherwise — see sprite_lit_common for the same constraint).
//
// Dither-fade amount. 1 = fully opaque, 0 = fully discarded. Compared against a
// screen-space 4x4 Bayer threshold so fractional values stipple in/out, reading
// as discovery pop-in at the chunky pixel scale.
instance uniform float visibility : hint_range(0.0, 1.0) = 1.0;
// Silhouette tint blend. 0 = normal lit color, 1 = flat silhouette_tint
// (lighting bypassed). Applied post-lighting so a silhouetted mesh reads as a
// constant flat shape regardless of the lightmap under it — the "you remember
// this is here" cue for discovered-but-unseen mobs.
instance uniform float silhouette_amount : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 silhouette_tint : source_color = vec3(0.0);

// --- Palette recolor (instance uniforms) ------------------------------------
// Flat per-instance recolor for modular character parts (skin tone, hair
// color). A MeshInstance3D that should take a palette color sets `recolor` to
// that color and `recolor_amount` to 1; the sampled atlas color is replaced by
// the flat color BEFORE lighting, so form/AO/lightmap still shade it but the
// hue is the chosen palette tone — an arbitrary color (incl. lighter than the
// baked swatch), unlike a multiply that can only darken. Default amount 0
// leaves every other mesh (props, armor, mobs) untouched. Driven via
// SetInstanceShaderParameter from Player (see ModelAnimator.SetMeshRecolor /
// PlayerArmorVisual). Declared AFTER the discovery names so the shared
// instance-uniform indices stay aligned with model_xray.gdshader (which mirrors
// these two as placeholders for the same reason).
instance uniform vec3 recolor : source_color = vec3(1.0);
instance uniform float recolor_amount : hint_range(0.0, 1.0) = 0.0;

// --- Ambient occlusion ------------------------------------------------------
// AO is a multiplicative darken on the diffuse lighting (the same place the
// terrain applies its baked AO — voxel_clip). Two independent sources feed it,
// because different model_lit users have different geometry available:
//
//   1. Baked vertex AO (`vertex_ao_strength`) — for meshes that bake real
//      occlusion into vertex COLOR at construction. The procedural tree trunks
//      do this (TreeTrunk.BakeSkeleton): the full branch skeleton is in hand, so
//      we compute genuine concavity — fork crotches where limbs meet, plus root-
//      ground contact — instead of approximating. COLOR.r carries BRIGHTNESS
//      (1 = open, <1 = occluded), so a mesh with no vertex-color array reads as
//      vec4(1.0) = fully open and is unaffected. Off (0) by default so imported
//      meshes that happen to ship stray FBX vertex colors (some Synty rocks do)
//      are ignored unless a material opts in.
//   2. Contact AO (`ao_contact_strength`) — for imported meshes we CAN'T bake
//      (rocks, chests, statues): fragments near the model's base (object-space Y
//      near `ao_base_y`, fading over `ao_height`) darken, faking the ground
//      contact shadow. Pure position ramp, no geometry needed.
//
// We deliberately DON'T fake a downward-facing/hemisphere AO term: the lighting
// here isn't a uniform ambient dome but directional sky + bounced block lights,
// so "faces down → darker" doesn't correspond to anything physical. Concavity is
// the thing worth darkening — baked where we can compute it, contact-approximated
// where we can't.
//
// Runtime gate `model_ao_strength` (global uniform, CVar `model_ao`) scales BOTH
// sources: 0 = off (pre-AO look), 1 = authored, >1 exaggerates to confirm where
// it lands — mirrors the terrain `ao_strength` CVar.
global uniform float model_ao_strength;
// Strength of the baked vertex-COLOR occlusion in [0,1]. 0 = ignore vertex color
// (default — safe for any mesh). Trunk bark materials set this to 1.
uniform float vertex_ao_strength : hint_range(0.0, 1.0) = 0.0;
// Object-space Y of the contact point (model base). 0 works for props whose
// pivot sits on the ground — the convention for the FBX rocks.
uniform float ao_base_y = 0.0;
// Height (object-space units ≈ meters) over which contact AO fades from full at
// the base to none above. ~1.5 grounds a rock without creeping up the model.
uniform float ao_height = 1.5;
// Strength of the contact darken in [0,1]. Authored per material — set to 0 on
// meshes that bake their own AO (the trunks) so the base isn't double-darkened.
uniform float ao_contact_strength : hint_range(0.0, 1.0) = 0.55;

varying vec3 world_pos;
varying vec3 world_normal;
// Object-space Y forwarded for the contact-AO height ramp in fragment().
varying float object_y;
// Baked per-vertex occlusion brightness (COLOR.r): 1 = open, <1 = occluded.
varying float vert_brightness;
// Mesh origin (world space) — flat, so the ceiling-clip test resolves once
// per instance instead of per fragment. Keeps a mesh from being sliced at the
// clip plane: the whole model dithers/culls as a unit, matching sprites (which
// clip against their flat per-sprite anchor). See fragment().
varying flat vec3 model_origin;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	model_origin = MODEL_MATRIX[3].xyz;
	object_y = VERTEX.y;
	vert_brightness = COLOR.r;
	// Uniform-scale rig, so transforming the normal by MODEL_MATRIX (without
	// the inverse-transpose) is correct.
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	// Ceiling cutaway: test the whole mesh against the clip by its origin (a
	// flat varying), NOT the per-fragment world_pos — otherwise a mesh
	// straddling the clip gets sliced at the plane. A prop should either fully
	// dither out or stay, as a unit, like sprites do (which clip against their
	// flat anchor). Runs before any other work.
	if (clip_dither_should_discard(model_origin, FRAGCOORD.xy)) {
		discard;
	}

	// Discovery dither: 4x4 Bayer threshold keyed off integer FRAGCOORD so the
	// pattern is stable at the screen-pixel level. `visibility >= 1` skips the
	// compare entirely so static props pay nothing. Matches sprite_lit_common.
	// Inlined (not a helper) because Godot only exposes FRAGCOORD / discard
	// directly inside fragment().
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

	vec4 tex = texture(albedo_tex, UV);
	vec3 base = tex.rgb * color_tint;
	// Palette recolor: flat-replace the sampled hue with the chosen tone before
	// lighting (inert at recolor_amount 0, the default for every non-skin/hair
	// mesh). See the instance-uniform comment above.
	base = mix(base, recolor, recolor_amount);

	// Editor-preview fallback: light globals aren't registered until the game
	// starts, so a standalone .tres / editor preview gets vec3(0) for
	// light_map_inv_size. Fall back to "full bright" so authoring sees the
	// model's atlas color without a cave-dimmed read. Mirrors particle_lit.
	// (Godot disallows `return` in fragment(), so this is an if/else.)
	bool globals_ready = length(light_map_inv_size) > 1e-6;
	vec3 out_albedo;
	vec3 out_emission;
	if (!globals_ready) {
		out_albedo = base;
		out_emission = vec3(0.0);
	} else {
		// Push the sample point up half a voxel into the air above the surface
		// so we read the adjacent air voxel's light instead of zero inside a
		// solid (same trick the sprite / particle shaders use).
		vec3 light_sample_pos = world_pos + vec3(0.0, 0.5, 0.0);
		vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
		vec4 lm = textureLod(light_map, uvw, 0.0);
		float sun_mask = pow(lm.r, light_falloff_exp);

		// Wrap-lambert form term from the real surface normal. sun_world_dir
		// points FROM the sun, so the surface-to-sun vector is -sun_world_dir.
		// The wrap (ndl*0.5+0.5) keeps back faces softly lit rather than
		// crushed to black, since the lightmap already carries world occlusion.
		vec3 to_sun = -normalize(sun_world_dir);
		float ndl = dot(normalize(world_normal), to_sun);
		float wrap = ndl * 0.5 + 0.5;
		float form = mix(1.0, wrap, form_strength);

		float cloud = cloud_shadow_ground(world_pos, TIME);
		float cloud_dim = 1.0 - cloud * cloud_shadow_strength;

		vec3 sun_lit = sun_mask * sun_intensity * sun_color * form;
		vec3 block_lit = lm.gba;

		// AO (see uniforms above): baked vertex occlusion (real concavity, where a
		// mesh provides it) plus the contact-height ramp (for un-baked meshes).
		// Summed and clamped — for any given material typically only one source is
		// non-zero. Gated by the runtime model_ao_strength for toggle / verify.
		float baked_ao = (1.0 - vert_brightness) * vertex_ao_strength;
		float contact_ao = (1.0 - smoothstep(ao_base_y, ao_base_y + ao_height, object_y)) * ao_contact_strength;
		float ao = clamp(baked_ao + contact_ao, 0.0, 1.0);
		float ao_factor = 1.0 - ao * model_ao_strength;

		// Same split as sprite_lit_common: ALBEDO carries direct sun (light()
		// scales it by the shadow atlas), EMISSION carries ambient + block.
		// AO multiplies both diffuse halves (matches voxel_clip's baked AO).
		out_albedo = base * sun_lit * (1.0 - sun_ambient) * ao_factor;
		out_emission = base * (block_lit + sun_lit * sun_ambient * cloud_dim) * ao_factor;

		// Eye adaptation: lift in low local light when dark-adapted, push toward
		// blowout in bright. light_est = area illuminance here (sun + block,
		// pre-shadow, not albedo). Scales the lit composite only — self-illum /
		// silhouette below stay as authored. No-op when eye_adaptation == 0.
		float ea_gain = eye_adaptation_gain(dot(sun_lit + block_lit, vec3(0.299, 0.587, 0.114)));
		out_albedo *= ea_gain;
		out_emission *= ea_gain;
	}

	// Self-illumination (inert at default 0), modulated by albedo so the glow
	// carries the texture's detail instead of a flat wash. After the lighting
	// composite and before the silhouette blend, so a silhouetted mesh still
	// collapses to its flat tint rather than glowing through.
	out_emission += base * emission_tint * emission_strength;

	// Silhouette blend toward a flat tint, post-lighting (matches sprite_lit):
	// ALBEDO fades to zero (so light() stops adding sun) while EMISSION fades to
	// the tint, yielding `silhouette_tint` at silhouette_amount = 1 regardless of
	// the lighting state under the mesh.
	ALBEDO = mix(out_albedo, vec3(0.0), silhouette_amount);
	EMISSION = mix(out_emission, silhouette_tint, silhouette_amount);

#ifdef MODEL_ALPHA_CUTOUT
	// Alpha-cutout variant (model_lit_cutout.gdshader only — it defines this
	// macro before the include). The atlas carries an alpha channel that keys out
	// the facial-feature backgrounds: the red / white separator padding the Synty
	// atlas packs around each eye / brow / mouth sprite. Hard scissor at 0.5 so
	// only the feature itself draws over the skin, and it stays in the opaque
	// queue (no transparency sorting). Inert for every other material that pairs
	// this include (model_lit / model_lit_character don't define the macro, so
	// ALPHA keeps its default 1.0 and those meshes render fully opaque as before).
	ALPHA = tex.a;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
#endif
}

void light() {
	// ATTENUATION is Godot's directional shadow atlas (terrain + this mesh's
	// own cast shadow). Compose cloud shadow as min() so a cast shadow + cloud
	// shadow read as one shadow, not two stacked. Per-source energy gate
	// matches sprite_lit_common / voxel_clip: LIGHT_COLOR = color*energy*PI.
	const float INV_PI = 0.31830989;
	float light_energy = LIGHT_COLOR.r * INV_PI;
	float cloud = cloud_shadow_ground(world_pos, TIME);
	float cloud_dim = 1.0 - cloud * cloud_shadow_strength;
	DIFFUSE_LIGHT += ALBEDO * light_energy * min(ATTENUATION, cloud_dim);
}
