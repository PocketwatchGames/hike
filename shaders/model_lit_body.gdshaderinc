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
#ifdef MODEL_SURFACE_DETAIL
#include "res://shaders/roof_broken.gdshaderinc"
#endif

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

// Pushes the light-map sample OUT ALONG THE SURFACE NORMAL, on top of the fixed
// half-voxel lift below. For a mesh that casts its own occlusion into the light
// map, the half-voxel lift alone is not enough: a roof stamps its sheet in the
// very voxel row its eave fascia stands in, so the fascia samples the shadow it
// is itself casting and goes dark from the base upward. Offsetting along the
// normal walks the sample out of that column into open air, the same normal-bias
// trick shadow maps use for acne. 0 = exact previous behaviour, so every
// material that does not opt in is untouched.
uniform float light_normal_bias : hint_range(0.0, 2.0) = 0.0;

// Per-material multiplicative tint applied to the sampled albedo BEFORE the
// lighting math. Default (1,1,1) leaves the texture untouched (back-compatible
// for character materials). Used by rock_lit.tres to brighten/warm the Synty
// rock palette (which UV-samples a flat mid-grey region) into a stone tone
// that reads at desert biome ambient without changing the source atlas.
uniform vec3 color_tint : source_color = vec3(1.0, 1.0, 1.0);

#ifdef MODEL_ALPHA_BLEND
// Alpha-blend variant (model_lit_blend.gdshader only). Whole-mesh opacity on top
// of the atlas alpha, so an owner can fade the piece in/out without duplicating
// the material. Gated by the #define — the ~18 materials sharing this body must
// not gain a uniform (or an ALPHA write) they never asked for.
uniform float alpha : hint_range(0.0, 1.0) = 1.0;
// Albedo tiling across the overlay's own UVs. A swept primitive (the skirt's
// cone) maps one U all the way around, so a single-motif texture would show as
// one patch on the front with a transparent seam at the back; 3 across turns
// the same motif into clumps that read from every angle. The mesh cannot say
// this — PrimitiveMesh has no UV scale — so the material does.
uniform vec2 uv_scale = vec2(1.0);
#endif

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

// Optional specular glint from the real sun DirectionalLight, for wet/glossy
// surfaces (slimes). Default strength 0 is inert, so every other material that
// shares this body renders exactly as before. Blinn-Phong, evaluated in
// light(): specular_color tints the highlight, specular_shininess tightens it
// (higher = smaller, glossier hotspot).
uniform float specular_strength : hint_range(0.0, 8.0) = 0.0;
uniform vec3 specular_color : source_color = vec3(1.0);
uniform float specular_shininess : hint_range(1.0, 128.0) = 32.0;

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

// --- Ceiling-clip anchor (instance uniform) ---------------------------------
// Metres added to the mesh origin's Y before the cutaway height test below.
// A visual that FLOATS above the thing it belongs to (the forge's hovering
// upgrade preview) otherwise clips on its own, metres before the prop under it
// does — the peek iris eats the floating model while the pedestal stays. A
// negative offset drops its anchor back onto the host so the two cut as one
// piece. Driven from ClipAnchor.cs; 0 = clip at the mesh's own origin, which is
// every other mesh. Declared after the shared names and before the xray-only
// ones so the instance-uniform indices stay aligned with model_xray.gdshader.
instance uniform float clip_anchor_offset = 0.0;

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

#ifdef MODEL_SURFACE_DETAIL
// --- Large-surface detail (roof_lit) ---------------------------------------
// A big tiled surface reads flat and repetitive with one albedo alone. Gated by
// a #define rather than default-off uniforms so the ~18 materials sharing this
// body compile byte-identically to before — they don't even pay for the
// samplers. Mirrors how MODEL_ALPHA_CUTOUT is switched on below.

// Tangent-space normal map, perturbing the wrap-lambert form term below. Godot's
// NORMAL_MAP is no use here: this shader lights from its own `world_normal`
// varying, which NORMAL_MAP never reaches. Needs mesh tangents.
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;
uniform float normal_strength : hint_range(0.0, 2.0) = 1.0;
// Crevice darkening from the normal map's ALPHA channel, matching the voxel
// atlas's RGB=normal / A=height packing. 0 when the map carries no height.
uniform float height_shade : hint_range(0.0, 1.0) = 0.0;

// Second albedo layer at a DIFFERENT frequency. detail_scale below 1 makes it a
// macro variation spanning many tiles of the base, which is what stops a large
// roof reading as one texture stamped over and over. Centred on 1, so a
// mid-grey map is a no-op and only the light/dark drift shows.
uniform sampler2D detail_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform float detail_scale = 0.11;
uniform float detail_strength : hint_range(0.0, 1.0) = 0.0;
#endif

#ifdef MODEL_SURFACE_OVERLAY
// --- Growth overlay: moss, snow, soot ---------------------------------------
// A REPLACEMENT layer, unlike detail_tex above. detail_tex multiplies, so it can
// only modulate the base's own value — it can never put a light material on a
// dark one. This mixes toward its own albedo instead, which is what lets moss
// sit on dark wood and still read as moss.
//
// Its own #define, nested under MODEL_SURFACE_DETAIL's samplers rather than
// added to them, so shaders that only want the normal map (model_lit_detail)
// don't pay for two more.
uniform sampler2D overlay_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform vec3 overlay_tint : source_color = vec3(1.0, 1.0, 1.0);
uniform float overlay_scale = 1.0;
uniform float overlay_strength : hint_range(0.0, 1.0) = 0.0;

// WHERE it grows. PROCEDURAL, not a mask texture: a tiling mask repeats its own
// silhouette across a big roof and the eye locks onto the shape long before it
// notices the albedo repeating. Sampled on world XZ (like roof_broken) rather
// than UV, so patches also stay put when a roof is resized and never line up
// with the texture's own tiling.
//
// Patches per meter. Low = a few broad colonies, high = speckle.
uniform float overlay_noise_scale = 0.09;
// Slides the threshold: 0 = bare, 1 = fully overgrown. One slider ages a whole
// village.
uniform float overlay_coverage : hint_range(0.0, 1.0) = 0.35;
// Width of the transition band. Wide reads as moss thinning out at the fringe
// rather than being cut off at an edge.
uniform float overlay_edge_softness : hint_range(0.005, 0.6) = 0.3;

// Moss takes the LOW GROUND first — crevices hold water and trap debris, so it
// creeps along the grout and plank gaps before it ever covers a proud face.
// Driven by the height packed in the normal map's alpha (1 = proud, 0 = deep).
// Inert on a normal map with no height, where alpha samples as 1.
uniform float overlay_crevice_bias : hint_range(0.0, 1.0) = 0.45;

// Moss collects on surfaces that hold water and debris — it does not cling to a
// vertical fascia or the underside of a soffit. Weighting by upward-facing-ness
// is what keeps it off those without needing a second mask.
uniform float overlay_up_bias : hint_range(0.0, 1.0) = 0.8;

// Moss is soft: it should bury the base's crevices, not inherit them. Flattens
// the tangent normal and cancels the height crevice darkening where it grows.
uniform float overlay_flatten : hint_range(0.0, 1.0) = 0.85;

// Favour the side that is shaded on average: it stays damp longest, which is
// why moss picks a side of a roof at all. Keyed to the NOON sun's bearing, so
// the "mossy side" is whichever way faces away from it — correct for either
// hemisphere without hardcoding one. 0 = no directional preference.
global uniform vec3 sun_noon_horizontal;
uniform float overlay_shade_bias : hint_range(0.0, 1.0) = 0.0;

// Three octaves of the same integer-hashed value noise the derelict-hole mask
// uses (roof_broken.gdshaderinc, already included above) — no second hash to
// keep in step. The octave steps are deliberately NON-INTEGER: at 2.0 and 4.0
// the lattices share grid lines and the sum grows a faint square grid of its
// own, which is the very artefact this replaces.
float overlay_growth_field(vec2 p, float scale)
{
	float f = roof_broken_noise(p * scale);
	f += roof_broken_noise(p * scale * 2.03) * 0.5;
	f += roof_broken_noise(p * scale * 4.11) * 0.25;
	return f * (1.0 / 1.75);
}

// TRIPLANAR, because a single XZ projection collapses on anything vertical: an
// eave fascia has no XZ variation down its height, so the field goes constant
// and the growth smears into vertical bands. Projecting per axis also stops a
// steep pitch stretching the patches by 1/cos(pitch).
//
// The near-zero-weight planes are branched out rather than summed. A roof is
// large and coherent, so whole slopes take the same branch and the divergence
// cost is nil — a flat slope pays for one projection, exactly as before.
float overlay_growth_triplanar(vec3 world_pos, vec3 n, float scale)
{
	vec3 w = abs(n);
	w /= max(w.x + w.y + w.z, 1e-4);
	float f = 0.0;
	if (w.y > 0.001) { f += overlay_growth_field(world_pos.xz, scale) * w.y; }
	if (w.x > 0.001) { f += overlay_growth_field(world_pos.zy, scale) * w.x; }
	if (w.z > 0.001) { f += overlay_growth_field(world_pos.xy, scale) * w.z; }
	return f;
}
#endif

#ifdef MODEL_SURFACE_DETAIL
// How far gone a derelict roof is: 0 = intact, 1 = nothing left. Procedural
// rather than a mask texture, specifically so the CPU can evaluate the same
// function — that is what lets the sun-occlusion pass leave the matching voxel
// columns open and put a real god ray under each hole. See roof_broken.
//
// An INSTANCE uniform so one shared material serves every roof and the visible
// mesh and its shadow proxy can be handed the same value without duplicating
// materials.
instance uniform float broken : hint_range(0.0, 1.0) = 0.0;
instance uniform float broken_scale = 0.35;
instance uniform float broken_edge_scale = 1.6;
instance uniform float broken_edge_jagged = 0.35;
// Scales the hole threshold for THIS pass. The inner-shell pass (roof_lit_
// interior) sets it below 1 so its holes are smaller than the outer surface's,
// leaving a ring of slab interior visible through every opening — which is the
// only thing that gives a discard-cut hole any apparent thickness. Cutting both
// surfaces at the same threshold punches a clean shaft through the slab and the
// roof reads as paper.
instance uniform float broken_bias = 1.0;
// Contact darkening just OUTSIDE a hole, faking the occlusion of a real cut
// edge. Cheap, and it does most of the work of selling depth at a distance.
instance uniform float broken_rim_darken : hint_range(0.0, 1.0) = 0.55;
instance uniform float broken_rim_width : hint_range(0.001, 0.5) = 0.09;
#endif

varying vec3 world_pos;
varying vec3 world_normal;
// Object-space Y forwarded for the contact-AO height ramp in fragment().
varying float object_y;
// Baked per-vertex occlusion brightness (COLOR.r): 1 = open, <1 = occluded.
varying float vert_brightness;
// Where the ceiling clip resolves this mesh: its world origin, shifted by
// clip_anchor_offset. Flat, so the test resolves once per instance instead of
// per fragment — that's what keeps a mesh from being sliced at the clip plane;
// the whole model dithers/culls as a unit, matching sprites (which clip against
// their flat per-sprite anchor). See fragment().
varying flat vec3 clip_anchor;

#ifdef MODEL_SURFACE_DETAIL
// World-space tangent frame for the normal map. Same uniform-scale assumption
// as world_normal below.
varying vec3 world_tangent;
varying vec3 world_binormal;
#endif

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	clip_anchor = MODEL_MATRIX[3].xyz + vec3(0.0, clip_anchor_offset, 0.0);
	object_y = VERTEX.y;
	vert_brightness = COLOR.r;
	// Uniform-scale rig, so transforming the normal by MODEL_MATRIX (without
	// the inverse-transpose) is correct.
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
#ifdef MODEL_SURFACE_DETAIL
	world_tangent = normalize((MODEL_MATRIX * vec4(TANGENT, 0.0)).xyz);
	world_binormal = normalize((MODEL_MATRIX * vec4(BINORMAL, 0.0)).xyz);
#endif
}

void fragment() {
	// Ceiling cutaway: test the whole mesh against the clip by its anchor (a
	// flat varying), NOT the per-fragment world_pos — otherwise a mesh
	// straddling the clip gets sliced at the plane. A prop should either fully
	// dither out or stay, as a unit, like sprites do (which clip against their
	// flat anchor). Runs before any other work.
	// Height clips by origin (never slice a prop at the plane); the visibility
	// fan clips per fragment, since a roof spans metres horizontally and only
	// the part over the player's sightline should come off.
	//
	// CLIP_PARTICIPATION_INSTANCE: the mesh is too wide for any one fragment to
	// decide whether it takes part in the column cutaway, so the CPU decides for
	// the whole instance and hands it down. Roofs only — everything else resolves
	// per fragment from the mask.
#ifdef CLIP_PARTICIPATION_INSTANCE
	if (clip_dither_should_discard_anchored(clip_anchor, world_pos, FRAGCOORD.xy)) {
		discard;
	}
#else
	if (clip_dither_should_discard_anchored(clip_anchor, world_pos, FRAGCOORD.xy)) {
		discard;
	}
#endif

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

#ifdef MODEL_SURFACE_DETAIL
	// Holes first — the cheapest possible reject, before anything samples the
	// surface. The shadow proxy runs the SAME test (roof_shadow_caster), so a
	// hole lets light through instead of casting a solid shadow.
	float broken_field = roof_broken_field(world_pos.xz, broken_scale, broken_edge_scale, broken_edge_jagged);
	if (broken > 0.0 && broken_field < broken * broken_bias) {
		discard;
	}
#endif

#ifdef MODEL_ALPHA_BLEND
	vec4 tex = texture(albedo_tex, UV * uv_scale);
#else
	vec4 tex = texture(albedo_tex, UV);
#endif
	vec3 base = tex.rgb * color_tint;
	// Palette recolor: flat-replace the sampled hue with the chosen tone before
	// lighting (inert at recolor_amount 0, the default for every non-skin/hair
	// mesh). See the instance-uniform comment above.
	base = mix(base, recolor, recolor_amount);

	// The normal the form term shades against. Plain geometry unless a detail
	// map perturbs it below.
	vec3 shading_normal = normalize(world_normal);
#ifdef MODEL_SURFACE_DETAIL
	vec3 detail = texture(detail_tex, UV * detail_scale).rgb;
	base *= mix(vec3(1.0), detail * 2.0, detail_strength);

	// Rim: darken the band just outside the hole boundary. Uses the field
	// already computed for the discard, so it costs a smoothstep.
	if (broken > 0.0 && broken_rim_darken > 0.0)
	{
		float edge = smoothstep(broken * broken_bias, broken * broken_bias + broken_rim_width, broken_field);
		base *= mix(1.0 - broken_rim_darken, 1.0, edge);
	}

	vec4 nh = texture(normal_tex, UV);

	// Resolved after the normal SAMPLE (it reads the packed height) but before
	// the normal is USED, because a growth layer has to flatten what it fills.
	float overlay_amount = 0.0;
#ifdef MODEL_SURFACE_OVERLAY
	if (overlay_strength > 0.0)
	{
		// Geometry normal, not the mapped one: whether moss can settle here is a
		// property of the surface, not of the bump detail sitting on it.
		vec3 geo_n = normalize(world_normal);
		float patch = overlay_growth_triplanar(world_pos, geo_n, overlay_noise_scale);
		// Let the crevices pull the field over the threshold, so growth reaches
		// into the grout well before it covers the faces either side of it.
		patch += (1.0 - nh.a) * overlay_crevice_bias;
		// +1 facing fully away from the noon sun, -1 facing into it, and ~0 on a
		// flat roof or one whose slopes run along the sun's bearing — which is
		// the right answer for all three without a special case.
		float shade_facing = -dot(geo_n, normalize(sun_noon_horizontal));
		patch += shade_facing * overlay_shade_bias;
		// coverage drives the threshold DOWN, so 1 floods and 0 leaves bare.
		float threshold = 1.0 - overlay_coverage;
		overlay_amount = smoothstep(threshold - overlay_edge_softness,
			threshold + overlay_edge_softness, patch);
		// SIGNED remap, not clamp(n.y): clamping sends a vertical face and a
		// soffit to the same 0, so there is no bias value that gives a wall moss
		// while keeping it off the underside. Mapped -1..1 into 0..1 they
		// separate — soffit 0, vertical 0.5, upward 1 — and one knob orders all
		// three the way they actually weather.
		float upness = geo_n.y * 0.5 + 0.5;
		overlay_amount *= mix(1.0, upness, overlay_up_bias);
		overlay_amount *= overlay_strength;
	}
#endif

	// Z is RECONSTRUCTED, never read. A normal map imported as "Normal Map"
	// compresses to RGTC/BC5, which stores only RG and samples blue as 0 — read
	// directly that would give z = -1 and turn the surface inside out. Rebuilding
	// it from xy is exact for any unit normal, so both BC5 and full-RGB maps work.
	vec2 tangent_xy = (nh.xy * 2.0 - 1.0) * normal_strength;
	float base_crevice = 1.0 - (1.0 - nh.a) * height_shade;
#ifdef MODEL_SURFACE_OVERLAY
	{
		float soften = overlay_amount * overlay_flatten;
		tangent_xy = mix(tangent_xy, vec2(0.0), soften);
		base_crevice = mix(base_crevice, 1.0, soften);
	}
#endif
	vec3 tangent_normal = vec3(tangent_xy,
		sqrt(max(1.0 - dot(tangent_xy, tangent_xy), 0.0)));
	shading_normal = normalize(world_tangent * tangent_normal.x
		+ world_binormal * tangent_normal.y
		+ shading_normal * tangent_normal.z);
	base *= base_crevice;
#ifdef MODEL_SURFACE_OVERLAY
	// Mix, not multiply — this is the whole point of a second real layer.
	base = mix(base, texture(overlay_tex, UV * overlay_scale).rgb * overlay_tint,
		overlay_amount);
#endif
#endif

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
		// Geometry normal, not the mapped one — this is about which voxel column
		// to read, and a bump map must not move the sample.
		vec3 light_sample_pos = world_pos + vec3(0.0, 0.5, 0.0)
			+ normalize(world_normal) * light_normal_bias;
		vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
		vec4 lm = textureLod(light_map, uvw, 0.0);
		float sun_mask = pow(lm.r, light_falloff_exp);

		// Wrap-lambert form term from the real surface normal. sun_world_dir
		// points FROM the sun, so the surface-to-sun vector is -sun_world_dir.
		// The wrap (ndl*0.5+0.5) keeps back faces softly lit rather than
		// crushed to black, since the lightmap already carries world occlusion.
		vec3 to_sun = -normalize(sun_world_dir);
		float ndl = dot(shading_normal, to_sun);
		float wrap = ndl * 0.5 + 0.5;
		float form = mix(1.0, wrap, form_strength);

		float cloud = cloud_shadow_ground_gated_up(world_pos, TIME);
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

#ifdef MODEL_ALPHA_BLEND
	// Alpha-BLEND variant (model_lit_blend.gdshader only). The mesh is a
	// translucent overlay laid over a character (the web skirt), so the atlas
	// alpha has to feather rather than scissor — a hard key turns soft webbing
	// into crunchy blobs. The wrapper pairs this with depth_draw_never, so the
	// piece sorts as ordinary transparency and never writes depth over the body
	// it hangs on.
	ALPHA = tex.a * alpha;
#endif
}

void light() {
	// ATTENUATION is Godot's directional shadow atlas (terrain + this mesh's
	// own cast shadow). Compose cloud shadow as min() so a cast shadow + cloud
	// shadow read as one shadow, not two stacked. Per-source energy gate
	// matches sprite_lit_common / voxel_clip: LIGHT_COLOR = color*energy*PI.
	const float INV_PI = 0.31830989;
	float light_energy = LIGHT_COLOR.r * INV_PI;
	float cloud = cloud_shadow_ground_gated_up(world_pos, TIME);
	float cloud_dim = 1.0 - cloud * cloud_shadow_strength;
	DIFFUSE_LIGHT += ALBEDO * light_energy * min(ATTENUATION, cloud_dim);

	// Opt-in Blinn-Phong specular (inert at specular_strength 0). Sun-driven, so
	// it shares the same shadow + cloud gate as the diffuse term.
	if (specular_strength > 0.0) {
		vec3 half_vec = normalize(VIEW + LIGHT);
		float ndh = max(dot(normalize(NORMAL), half_vec), 0.0);
		float spec = pow(ndh, specular_shininess);
		SPECULAR_LIGHT += spec * specular_strength * specular_color * light_energy * min(ATTENUATION, cloud_dim);
	}
}
