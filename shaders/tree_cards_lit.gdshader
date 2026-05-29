shader_type spatial;
// Card-based canopy shader for hand-authored trees (see FoliageMultiMesh.cs /
// FoliageCluster.cs / tree_cards_lit.tres). Mirrors model_lit's ALBEDO +
// EMISSION split, so it composes with Godot's directional shadow atlas and
// the world lightmap exactly like every other lit shader in the project.
//
// Differences from model_lit:
//   1. Alpha cutout (discard < 0.5) + cull_disabled — leaf-cluster cards are
//      double-sided alpha-tested geometry. Each card already carries the
//      correct normal and silhouette by construction, so the legacy
//      tree_lit.gdshader's sphere-normal pass, silhouette-breakup pass, and
//      shadow-in-middle fallback are gone.
//   2. Two-sided lighting — flips the card mesh normal for back faces so
//      the wrap-lambert form term reads correctly from either side of the
//      card (only relevant when sphere_normal_strength < 1).
//   3. Tint pipeline:
//        - leaf_tint_a / leaf_tint_b are SHADER uniforms (set per-material
//          from variant.LeafTintA / LeafTintB).
//        - INSTANCE_COLOR.r carries the per-BLOB mix in [0, 1] — every card
//          in one blob shares the value, different blobs in one tree get
//          different values. So one tree reads as N tonal clusters rather
//          than one flat color.
//        - per_tree_tint_variation hashes the tree's WORLD position for a
//          small signed shift on top of the per-blob mix, so a forest of
//          identical-variant trees still reads as varied. Without this,
//          every tree built from the same variant had the same tintMix
//          and the canopy looked monochrome at forest scale.
//   4. INSTANCE_CUSTOM packs four per-card values:
//        .x   = wind phase seed — VERTEX.x sways by
//               sin(TIME * sway_frequency + phase * TAU), tip_mask-modulated
//               so the card flaps around its base.
//        .yzw = blob-to-card offset, pre-rotated by the card's inverse basis
//               in C# so a single MODEL_MATRIX rotation in the shader
//               recovers the WORLD-space direction from blob center to card.
//               That direction IS the sphere normal used for the wrap-lambert
//               form term — it lets every card in a blob shade as if it sat
//               on a smooth sphere, regardless of its individual mesh normal.
//               sphere_normal_strength blends it with the card mesh normal
//               (1 = pure sphere shading, 0 = pure card-normal shading).
render_mode ambient_light_disabled, depth_draw_opaque, cull_disabled;

// filter_linear on the leaves: smooths color sampling between texels so
// the texture doesn't read as chunky pixel grids on each card. Alpha is
// still hard-cut at alpha_cutoff in fragment() so the silhouette stays
// crisp — linear filtering just lets the silhouette boundary cross any
// sub-texel position, which reads as anti-aliased edges during wind sway
// instead of texel-popping flicker.
uniform sampler2D albedo_tex : source_color, filter_linear;
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;

// Wrap-lambert blend amount. Higher = more darkening on the shadow side of
// a blob (more value contrast). Default 0.75 is stronger than model_lit's
// 0.55 because pixel-art foliage reads better with crisp top-vs-underside
// shading; drop back toward 0.5 for softer.
uniform float form_strength : hint_range(0.0, 1.0) = 0.75;

// Blend between the card's geometric normal (0) and the blob-derived sphere
// normal (1) for the wrap-lambert form term. Default 1.0 — each blob reads
// as a soft sphere lit from the sun direction, which gives the "stacked
// cluster balls" depth the pixel-art reference shows. Drop toward 0 to A/B
// against the original random-card-normal shading.
uniform float sphere_normal_strength : hint_range(0.0, 1.0) = 1.0;

// Per-card dome bump. Treats each card's UV (centered at 0.5) as a position
// on a hemisphere: the normal at the UV center points along the card's
// outward direction; the normal at the edges tilts radially outward in the
// card's TBN frame. At 1.0 each card reads as a small ball of leaves; at 0
// each card is flat (current behavior). Independent of sphere_normal_strength
// so the per-card detail and the per-blob cluster shape can be A/B'd
// separately — but note they blend together: card_dome perturbs card_normal
// BEFORE the sphere_normal blend, so a high sphere_normal_strength still
// dominates the result and per-card variation is suppressed. Drop
// sphere_normal_strength below ~0.7 to see the dome contribution clearly.
uniform float card_dome_strength : hint_range(0.0, 1.0) = 0.5;

// Tint endpoints — multiplied by the leaf texture's RGB at the end. Three
// independent knobs decide how far each card's mix value drifts from 0.5:
//
//   per_tree_tint_variation : signed shift hashed from the tree's world
//                             position. Makes a forest of one variant
//                             read as varied. 0 = all trees identical,
//                             1 = trees span tint_a → tint_b fully.
//
//   per_cluster_tint_variation : strength on the per-cluster mix value
//                             that FoliageMultiMesh writes into COLOR.r.
//                             0 = all clusters in a tree share the
//                             midpoint; 1 = clusters span tint_a → tint_b
//                             fully.
//
//   per_card_tint_variation : signed shift hashed from the per-card wind
//                             phase (INSTANCE_CUSTOM.x). 0 = all cards in
//                             one cluster share the cluster's tint;
//                             1 = cards span tint_a → tint_b fully within
//                             a cluster.
//
// All three combine additively around 0.5, clamped into [0, 1].
uniform vec3 leaf_tint_a : source_color = vec3(0.78, 1.0, 0.62);
uniform vec3 leaf_tint_b : source_color = vec3(1.05, 1.0, 0.45);
uniform float per_tree_tint_variation : hint_range(0.0, 1.0) = 0.4;
uniform float per_cluster_tint_variation : hint_range(0.0, 1.0) = 1.0;
uniform float per_card_tint_variation : hint_range(0.0, 1.0) = 0.2;

// Per-tree world position, set per-material by FoliageMultiMesh when the
// tree is added to the scene. Drives the per-tree tint hash. We CAN'T use
// MODEL_MATRIX[3] for this because in MultiMesh rendering MODEL_MATRIX[3]
// is the per-CARD world position — hashing it would give per-card variation
// instead of per-tree, defeating the point of the per_tree axis. Defaults
// to (0,0,0) so unconfigured trees just share the same tint slot.
uniform vec3 tree_origin = vec3(0.0);

// Wind sway parameters. Amplitude is in METERS of horizontal displacement at
// the card's tip (UV.y = 0). The rustle's TEMPORAL frequency now comes from
// the shared `wind_phase` global (declared below) — SkyController integrates
// weather palette frequency + a gust-speed term, so all foliage shares one
// rustle clock that speeds up during stormy weather. Per-card phase OFFSET
// still comes from INSTANCE_CUSTOM.x so cards don't sway in lock-step.
uniform float sway_amplitude = 0.12;

// Directional wind bend — pushes card TIPS downstream along wind_dir, scaled by
// the global wind_amplitude (which is GustedWindSpeed * windToSwayMeters from
// SkyController, so it tracks the live gusted weather). Higher leaves bend more
// than lower leaves via a quadratic height term (taller branches catch more
// wind in the real world; the quadratic exaggerates the read so the top of the
// canopy noticeably leans while trunk-adjacent clusters stay nearly still).
// canopy_height is the world-space height from tree_origin to the top of the
// canopy, computed at rebuild from FoliageCluster positions + radii.
uniform float wind_bend_strength = 1.0;
uniform float canopy_height = 4.0;

// -- Global uniforms shared with model_lit / sprite_lit / voxel_clip -------

global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_offset;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"

// Off-axis directional fills shared with voxel_clip. Each darkens surfaces
// by its color in proportion to how much the shading normal faces AWAY
// from the fill — together they sculpt slope direction independently of
// the sun. Without these, tree clusters miss the directional read terrain
// has, so a sun-facing slope and a side-facing slope shade nearly the
// same. Set by SkyController.
global uniform vec3 fill_a_world_dir;
global uniform vec3 fill_b_world_dir;
global uniform vec3 fill_a_color;
global uniform vec3 fill_b_color;

// Wind globals shared with detail_sprite / voxel_water. wind_dir is normalized
// horizontal (.y unused); wind_amplitude is GustedWindSpeed × windToSwayMeters
// in meters, set per-frame by SkyController. wind_phase is the pre-integrated
// rustle phase (palette frequency + gust-speed term), shared with grass so all
// foliage shares one rustle clock that speeds up in storms.
global uniform vec3 wind_dir;
global uniform float wind_amplitude;
global uniform float wind_phase;

// Block-light shadow projector — see shaders/block_light_shadow.gdshaderinc.
// Multiplies block_lit so a sprite/tree silhouette above this fragment
// dims the torch/lamp contribution here. Sampling at world_pos (per
// fragment) gives the canopy interior the same per-pixel dim that terrain
// uses, so a torch lighting the ground under a canopy doesn't artificially
// punch through the leaves overhead.
global uniform sampler2D block_light_shadow_tex : filter_linear;
global uniform vec3 block_light_shadow_origin;
global uniform vec3 block_light_shadow_right;
global uniform vec3 block_light_shadow_up;
global uniform float block_light_shadow_size;
global uniform float block_light_shadow_strength;
global uniform float block_light_shadow_blur_uv;
global uniform bool block_light_shadow_enabled;
#include "res://shaders/block_light_shadow.gdshaderinc"

varying vec3 world_pos;
varying vec3 world_normal;
// World-space TANGENT and BINORMAL of the card mesh, used in fragment() to
// rotate the UV-derived dome normal into world space. Same transform as
// world_normal (MODEL_MATRIX rotation); the card's uniform scale doesn't
// matter after normalize.
varying vec3 world_tangent;
varying vec3 world_binormal;
// World-space outward direction from the card's blob center, recovered from
// the INSTANCE_CUSTOM.yzw offset (pre-rotated by the card's inverse basis
// in C# — see FoliageMultiMesh.ComputeCardTransform). Normalized in vertex(),
// renormalized in fragment() because interpolation across a card-sized
// quad can stretch it slightly.
varying vec3 sphere_normal_world;
// Per-card final tint (per-tree hash + per-blob INSTANCE_COLOR.r mixed
// between leaf_tint_a/b). Computed once in vertex() since it's uniform
// across the card.
varying vec3 v_card_tint;

// iq's hash13 — 3D position → [0, 1). Sufficient quality for per-tree mix
// variation in a forest. Same construction as legacy tree_lit.gdshader.
float hash13(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}

void vertex() {
	// Wind sway: per-card sin wave displaces VERTEX in object space, with the
	// tip moving most and the pivot edge (bottom-center, UV.y = 1 in Godot's
	// QuadMesh convention) holding still. Displacing in VERTEX.x means each
	// card sways along its own local right axis, so cards facing different
	// world directions wave through different world axes — adds visual
	// variation without per-card authoring.
	float phase = INSTANCE_CUSTOM.x;
	float tip_mask = 1.0 - UV.y;
	VERTEX.x += sin(wind_phase + phase * TAU) * sway_amplitude * tip_mask;

	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;

	// Directional wind bend in world space: push the card's tip downstream
	// along wind_dir, with magnitude tracking the live gusted wind (via the
	// wind_amplitude global). The bend is concentrated at card tips
	// (tip_mask) so each card flaps around its base, AND at higher canopy
	// positions (height01²) so the top of the tree leans noticeably while
	// trunk-adjacent clusters stay nearly still — matches how real branches
	// flex (longer lever arm at the top). canopy_height is set per-tree by
	// FoliageMultiMesh from the cluster bounds.
	float height01 = clamp((world_pos.y - tree_origin.y) / max(canopy_height, 1e-3), 0.0, 1.0);
	float bend_weight = tip_mask * height01 * height01;
	world_pos += wind_dir * (wind_amplitude * wind_bend_strength * bend_weight);

	// World_pos is no longer just MODEL_MATRIX * VERTEX, so set POSITION
	// explicitly to keep the bent vertex in sync with what fragment() reads.
	// Same pattern detail_sprite uses for its world-space bend.
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_pos, 1.0);
	// Uniform per-card scale + rigid rotation, so MODEL_MATRIX without
	// inverse-transpose is correct for the normal and TBN axes.
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	world_tangent = normalize((MODEL_MATRIX * vec4(TANGENT, 0.0)).xyz);
	world_binormal = normalize((MODEL_MATRIX * vec4(BINORMAL, 0.0)).xyz);

	// Sphere normal: blob-to-card offset packed in card-local frame, rotated
	// to world via MODEL_MATRIX (uniform per-card scale cancels in normalize).
	// Guard against a degenerate "card sits exactly at blob center" case
	// (radius < 1e-3 → length squared < 1e-6) by falling back to world up.
	vec3 offset_card_local = INSTANCE_CUSTOM.yzw;
	if (dot(offset_card_local, offset_card_local) < 1e-6) {
		sphere_normal_world = vec3(0.0, 1.0, 0.0);
	} else {
		sphere_normal_world = normalize((MODEL_MATRIX * vec4(offset_card_local, 0.0)).xyz);
	}

	// Three additive offsets around the 0.5 neutral, each gated by its
	// own strength uniform so they can be A/B'd independently.
	//   - per-tree: hash13 of the tree's WORLD position (the `tree_origin`
	//               uniform set per-material by FoliageMultiMesh). All
	//               cards in one tree share the same hash so this axis
	//               shifts the whole tree together — never produces
	//               per-card scatter. MODEL_MATRIX[3] would NOT work for
	//               this in MultiMesh rendering since it's per-card.
	//   - per-cluster: hash13 of (COLOR.r-as-cluster-id mixed with tree_origin).
	//               Each cluster in a tree gets its own offset, AND the
	//               cluster-to-offset mapping varies tree to tree — so two
	//               trees don't share the cluster-0-is-dark / cluster-1-
	//               is-yellow pattern.
	//   - per-card: hash13 of the per-card wind phase + tree_origin.
	//               INSTANCE_CUSTOM.x is already a deterministic per-card
	//               random; mixing in tree_origin keeps it varying per
	//               tree as well.
	// Per-instance color comes through COLOR, not INSTANCE_COLOR — there
	// is no distinct INSTANCE_COLOR identifier in Godot shaders.
	float tree_hash = hash13(tree_origin * 0.7);
	float cluster_hash = hash13(vec3(COLOR.r * 13.7, tree_origin.x * 0.31, tree_origin.z * 0.31));
	float card_hash = hash13(vec3(INSTANCE_CUSTOM.x * 17.3, tree_origin.x * 0.29, tree_origin.z * 0.29));
	float tree_offset = (tree_hash - 0.5) * per_tree_tint_variation;
	float cluster_offset = (cluster_hash - 0.5) * per_cluster_tint_variation;
	float card_offset = (card_hash - 0.5) * per_card_tint_variation;
	float final_mix = clamp(0.5 + tree_offset + cluster_offset + card_offset, 0.0, 1.0);
	v_card_tint = mix(leaf_tint_a, leaf_tint_b, final_mix);
}

void fragment() {
	vec4 tex = texture(albedo_tex, UV);
	if (tex.a < alpha_cutoff) {
		discard;
	}
	// Per-card tint (per-tree hash + per-blob mix between leaf_tint_a/b).
	// Computed in vertex(); see v_card_tint declaration.
	vec3 base = tex.rgb * v_card_tint;

	// Card mesh normal flipped for back faces — only matters when
	// sphere_normal_strength < 1 (the sphere normal already encodes which
	// side of the blob each card sits on, independent of camera view).
	vec3 card_normal = world_normal;
	if (!FRONT_FACING) {
		card_normal = -card_normal;
	}
	// Per-card dome bump: treat the card's UV as a position on a unit disk
	// centered at (0.5, 0.5). The dome normal in tangent space is
	// (uv_centered.x, uv_centered.y, sqrt(1 - r²)) — radially-outward at
	// the edges, straight-out at the center. Godot's UV.y is flipped
	// relative to BINORMAL, so we negate the y component to recover
	// standard tangent-space convention. Rotated to world via world_tangent
	// / world_binormal / card_normal. Outside the inscribed circle (r > 1
	// at the quad corners) the dome is undefined — clamped to a small
	// height so the normal stays valid even there (textures usually have
	// alpha-cut to 0 in those corners anyway).
	vec2 uv_centered = vec2(UV.x - 0.5, 0.5 - UV.y) * 2.0;
	float r_sq = dot(uv_centered, uv_centered);
	float dome_h = sqrt(max(0.001, 1.0 - r_sq));
	vec3 dome_world = normalize(
		uv_centered.x * world_tangent +
		uv_centered.y * world_binormal +
		dome_h * card_normal);
	vec3 card_normal_domed = normalize(mix(card_normal, dome_world, card_dome_strength));
	// Blend toward the sphere normal — at strength=1, every card on one
	// blob shades by its position on the blob, so the cluster reads as a
	// soft sphere rather than a swarm of randomly-oriented quads. The
	// per-card dome detail is suppressed at strength=1; lower it to see
	// dome shading take over.
	vec3 shade_normal = normalize(mix(card_normal_domed, normalize(sphere_normal_world), sphere_normal_strength));

	// Editor-preview fallback — light globals aren't registered until the
	// game starts, so a standalone .tres preview gets vec3(0) for
	// light_map_inv_size. Fall back to "full bright" so authoring sees the
	// recolored base without a cave-dimmed read. Mirrors model_lit.
	bool globals_ready = length(light_map_inv_size) > 1e-6;
	if (!globals_ready) {
		ALBEDO = base;
		EMISSION = vec3(0.0);
	} else {
		// Push sample point up half a voxel into air so we read the adjacent
		// air voxel's light instead of zero inside a solid (same trick the
		// sprite / particle / model shaders use).
		vec3 light_sample_pos = world_pos + vec3(0.0, 0.5, 0.0);
		vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
		vec4 lm = textureLod(light_map, uvw, 0.0);
		float sun_mask = pow(lm.r, light_falloff_exp);

		vec3 to_sun = -normalize(sun_world_dir);
		float ndl = dot(shade_normal, to_sun);
		float wrap = ndl * 0.5 + 0.5;
		// Lerp form to 0 during sunset / moonrise. Drives off sun_intensity
		// directly (the same scalar SkyController fades through the
		// day/night cycle), so form follows whatever cadence the sky is
		// already using. At sun_intensity >= 1 form is at full authored
		// strength; as the sun dims toward 0 the directional shading fades
		// linearly to nothing, so ndl flipping sign at the horizon can't
		// produce a hard pop. Block lights through EMISSION handle
		// nighttime canopy lighting.
		float sun_active = clamp(sun_intensity, 0.0, 1.0);
		float effective_form_strength = form_strength * sun_active;
		float form = mix(1.0, wrap, effective_form_strength);

		float cloud = cloud_shadow_ground(world_pos, TIME);
		float cloud_dim = 1.0 - cloud * cloud_shadow_strength;

		vec3 sun_lit = sun_mask * sun_intensity * sun_color * form;
		// Per-fragment block_lit dimmed by the sprite/tree silhouette
		// projector — matches voxel_clip's receive path. Trees don't
		// currently CAST into the projector themselves; this only RECEIVES.
		vec3 block_lit = lm.gba * block_light_shadow_factor(world_pos);

		// Off-axis fill tints — same composition voxel_clip uses. Reads
		// shade_normal so the fills follow the cluster sphere shading,
		// giving each blob a sun-independent directional read. Multiplied
		// into `base` so they affect BOTH the direct sun and ambient halves
		// of the split, matching terrain.
		float fill_a_facing = max(0.0, dot(shade_normal, -fill_a_world_dir));
		float fill_b_facing = max(0.0, dot(shade_normal, -fill_b_world_dir));
		vec3 fill_a_tint = mix(fill_a_color, vec3(1.0), fill_a_facing);
		vec3 fill_b_tint = mix(fill_b_color, vec3(1.0), fill_b_facing);
		vec3 base_filled = base * fill_a_tint * fill_b_tint;

		ALBEDO = base_filled * sun_lit * (1.0 - sun_ambient);
		EMISSION = base_filled * (block_lit + sun_lit * sun_ambient * cloud_dim);
	}
}

void light() {
	// Same composition as model_lit: ATTENUATION carries Godot's directional
	// shadow atlas, cloud_dim is min()'d in so a cast shadow + cloud shadow
	// read as one.
	const float INV_PI = 0.31830989;
	float light_energy = LIGHT_COLOR.r * INV_PI;
	float cloud = cloud_shadow_ground(world_pos, TIME);
	float cloud_dim = 1.0 - cloud * cloud_shadow_strength;
	DIFFUSE_LIGHT += ALBEDO * light_energy * min(ATTENUATION, cloud_dim);
}
