shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, ambient_light_disabled, specular_disabled;

global uniform float camera_clip;
global uniform vec3 debug_solid; // (0,0,0) = disabled, otherwise forces ALBEDO.
global uniform float light_falloff_exp; // power applied to lightmap value (>1 = darker mids).

// RGBA8 light map: R = sunlight visibility mask 0..1 (single source, max-fill,
// pow'd in this shader). GBA = per-color-channel block light, post-pow at
// deposit time so overlapping lights add their perceptual brightness directly.
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
// Day/night-driven scalars for the sun. sun_intensity is overall brightness
// (0..1+); sun_color is the RGB tint (warm dawn, cool noon, etc.). sun_ambient
// is the fraction of BFS sun mask treated as sky-bounce ambient that survives
// the directional shadow — see fragment() / light() and GetPerceivedLight in
// WorldState.cs (kept in sync so visual and gameplay agree).
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;

// Sun direction for the cloud-shadow include. Declared BEFORE the include
// because the include's function body references sun_world_dir, and GLSL
// requires identifiers to be declared before use. Other sun-direction uses
// below (reverse directional shading) pick up this same declaration.
global uniform vec3 sun_world_dir;

// Cloud occlusion parameters shared with the sky + fog + sprite + water
// shaders (registered by SkyController). cloud_shadow.gdshaderinc uses
// these to project cloud shadows along the sun direction.
global uniform vec2 cloud_scroll;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
// How much of the sun's total brightness a full cloud blocks on terrain.
// 1.0 = full cloud leaves block light only; 0.6 ≈ old ATTENUATION-only
// behaviour (direct sun killed, ambient sky bounce survives).
global uniform float cloud_shadow_strength;
#include "res://shaders/cloud_shadow.gdshaderinc"

uniform sampler2DArray tile_array : filter_nearest, repeat_enable;

// Tile layer indices — must match VoxelTypeInfo.TILE_* in C#.
// Values are the BASE layer of a tile's contiguous variant block. The real
// sampled layer is resolved per-fragment in `resolve_layer` from the base,
// the elevation band (world Y), and a position hash.
const float TILE_STONE = 0.0;
const float TILE_DIRT = 1.0;
// Grass top's base is 2; variants/bands occupy layers 2..17 — resolved in
// resolve_layer() from the tile_variants[] entry at index 2.
const float TILE_GRASS_TOP = 2.0;
// "Auto" sentinels — when a vertex carries one of these ids, the fragment
// picks the real tile from surface slope. Two variants:
//   TILE_AUTO (255):      flat / slope / wall driven by the per-vertex KitId.
//   TILE_AUTO_PATH (254): never flat-grass; dirt by default; stone if steep.
//                         Authored content wants a worn/earthen look
//                         regardless of slope. Will become a kit overlay.
// Must match VoxelTypeInfo.TILE_AUTO / TILE_AUTO_PATH.
const float TILE_AUTO_PATH_THRESHOLD = 253.5;
const float TILE_AUTO_THRESHOLD = 254.5;

// Per base-tile variant config: .x = num elevation bands, .y = variants per
// band. Indexed by base layer id; entries we never index may be stale. Keep
// TILE_VARIANT_TABLE_SIZE in sync with VoxelTypeInfo.TILE_VARIANT_TABLE_SIZE.
// A (1,1) entry means "no variation" — the effective layer equals the base.
const int TILE_VARIANT_TABLE_SIZE = 32;
uniform vec2 tile_variants[TILE_VARIANT_TABLE_SIZE];
uniform float band_origin_y = 0.0;
uniform float band_height = 32.0;

// Low-frequency grayscale overlay multiplied into albedo. Breaks up obvious
// tiling in large stretches of the same material without needing multi-tile
// variants. Sampled on world_vertex.xz so top-facing ground gets the most
// benefit; sides read the same pattern projected straight down, which is
// acceptable for an isometric view.
uniform sampler2D detail_noise : filter_linear, repeat_enable;
uniform float detail_noise_freq = 0.03125;  // ~1/32 world units per cycle.
uniform float detail_noise_strength = 0.2;

// Detail normal perturbation on the ground. Sampled as a tangent-space
// normal map in the XZ plane; its influence is scaled by how much the
// surface faces up, so cliffs stay clean and ground gets the break-up.
uniform sampler2D detail_normal : filter_linear, repeat_enable, hint_normal;
uniform float detail_normal_freq = 0.5;  // ~2 world units per cycle.
uniform float detail_normal_strength = 0.35;

// Path slope rule: dirt above PATH_STONE_HI, stone below PATH_STONE_LO. This
// AUTO_PATH branch stays kit-independent for now — it'll fold into a per-kit
// overlay system later.
const float SLOPE_PATH_STONE_LO = 0.35;
const float SLOPE_PATH_STONE_HI = 0.55;

// Per-kit palette uploaded by ChunkMesh.SetKits at world start.
//   kit_tiles[i] = (flat, wall, edge_overlay, _) tile_array layer indices.
//   kit_bands[i] = (wall_lo, wall_hi, _, _). One smoothstep: y<wall_lo = wall,
//                   y>wall_hi = flat. No intermediate slope tile — authored
//                   dirt/edge appearance comes from the overlay layer.
// Index = the per-vertex KitId picked via barycentric argmax (parallel to
// tile_ids picking). MAX_KITS sized for headroom; only the first
// WorldGenData.Kits.Length entries are meaningful.
const int MAX_KITS = 16;
uniform vec4 kit_tiles[MAX_KITS];
uniform vec4 kit_bands[MAX_KITS];

global uniform vec3 fill_world_dir;
global uniform vec3 sun_tint_color;
global uniform vec3 fill_tint_color;

const float MIN_AMBIENT = 0.0;

varying vec3 world_vertex;
varying vec3 world_normal;
// Tile ids for the 3 corners of this triangle (constant across the tri).
varying flat vec3 tile_ids;
// Per-vertex blend-noise amplitude, interpolated to fragments.
varying float blend_amp;
// Per-vertex sharpness in [0,1]. 0 = use interpolated smooth NORMAL,
// 1 = use dFdx/dFdy-derived face normal (hard flat-shaded crease). Authored
// per-cell by the DC mesher: 1 for authored-sharp materials (Stone/Wood),
// 0 for soft terrain. Interpolates across mixed quads so creases soften.
varying float sharpness;
// Fragment→light varying. 1 = unclouded, 0 = fully overcast.
// Written in fragment(), read in light() so cloud shadow composes with
// the directional shadow atlas as min() rather than multiply — see light().
varying float cloud_dim;
// Kit ids for the 3 corners of this triangle (constant across the tri).
// Barycentric argmax picks which corner's kit wins at each fragment (parallel
// to tile_ids), so kit boundaries blend with the same noise-jittered edge as
// tile boundaries.
varying flat vec3 kit_ids;
// Per-corner authored overlay ids. 0 = no overlay, paint the kit's flat/wall
// tile as-is; non-zero = sample the kit's edge-overlay tile and blend over.
// Picked by the same barycentric argmax as tile_ids and kit_ids so boundaries
// inherit the shared noise-jittered edge.
varying flat vec3 overlay_ids;

// Tunes the spatial frequency of the per-tile-edge noise. Higher = finer
// jitter along the boundary; too high looks like static, too low looks like
// big linear smears.
const float BLEND_NOISE_FREQ = 1.7;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = NORMAL;
	tile_ids = CUSTOM0.xyz;
	blend_amp = CUSTOM0.w;
	sharpness = CUSTOM1.x;
	kit_ids = CUSTOM1.yzw;
	overlay_ids = CUSTOM2.xyz;
}

// Hash-based 3D value noise. Good enough for breaking up tile-edge seams; we
// only need it to be locally varied and reproducible across chunk boundaries.
float hash13(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// Resolve the concrete tile_array layer from a tile's base layer plus
// elevation band (world Y) and position-hashed variant. Tiles configured
// with (1,1) collapse to base_layer — identical to the pre-variant code.
float resolve_layer(float base_layer, vec3 world_pos) {
	int id = clamp(int(base_layer), 0, TILE_VARIANT_TABLE_SIZE - 1);
	vec2 cfg = tile_variants[id];
	float bands = max(1.0, cfg.x);
	float per_band = max(1.0, cfg.y);
	float band = clamp(floor((world_pos.y - band_origin_y) / band_height), 0.0, bands - 1.0);
	// floor() makes the variant pick stable within each unit voxel so
	// adjacent fragments in the same voxel pick the same tile, but
	// neighbouring voxels vary.
	float h = hash13(floor(world_pos) + vec3(37.2, 11.7, 91.3));
	float variant = clamp(floor(h * per_band), 0.0, per_band - 1.0);
	return base_layer + band * per_band + variant;
}

// Triplanar sample of a tile with variant/band resolution. `blend` is the
// axis weight vector computed from the shading normal in fragment().
vec4 sample_tile(float base_layer, vec3 world_pos, vec3 blend) {
	float layer = resolve_layer(base_layer, world_pos);
	vec4 tex_xy = texture(tile_array, vec3(world_pos.x, -world_pos.y, layer));
	vec4 tex_xz = texture(tile_array, vec3(world_pos.x,  world_pos.z, layer));
	vec4 tex_yz = texture(tile_array, vec3(world_pos.z, -world_pos.y, layer));
	return tex_xz * blend.y + tex_xy * blend.z + tex_yz * blend.x;
}
float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash13(i + vec3(0.0, 0.0, 0.0));
	float n100 = hash13(i + vec3(1.0, 0.0, 0.0));
	float n010 = hash13(i + vec3(0.0, 1.0, 0.0));
	float n110 = hash13(i + vec3(1.0, 1.0, 0.0));
	float n001 = hash13(i + vec3(0.0, 0.0, 1.0));
	float n101 = hash13(i + vec3(1.0, 0.0, 1.0));
	float n011 = hash13(i + vec3(0.0, 1.0, 1.0));
	float n111 = hash13(i + vec3(1.0, 1.0, 1.0));
	float nx00 = mix(n000, n100, f.x);
	float nx10 = mix(n010, n110, f.x);
	float nx01 = mix(n001, n101, f.x);
	float nx11 = mix(n011, n111, f.x);
	float nxy0 = mix(nx00, nx10, f.y);
	float nxy1 = mix(nx01, nx11, f.y);
	return mix(nxy0, nxy1, f.z);
}

void fragment() {
	// Per-fragment shading normal: lerp between the interpolated smooth NORMAL
	// (sharpness=0) and a dFdx/dFdy-derived face normal (sharpness=1). The
	// derivative form gives the current triangle's plane — same look as a
	// SurfaceTool flat-shade group, but without needing one, so we can vary
	// smoothness smoothly per vertex. Flip the derivative normal into the same
	// hemisphere as the smooth normal to stay invariant to rasterizer winding.
	vec3 face_n_raw = cross(dFdx(world_vertex), dFdy(world_vertex));
	vec3 smooth_n = normalize(world_normal);
	vec3 face_n = normalize(face_n_raw * sign(dot(face_n_raw, smooth_n) + 1e-6));
	vec3 shaded_normal = normalize(mix(smooth_n, face_n, clamp(sharpness, 0.0, 1.0)));

	// Detail normal map perturbation on the ground plane. Sampled in XZ only;
	// the influence is scaled by how ground-facing the surface is so vertical
	// walls keep their clean silhouette. This is a cheap way to give glancing-
	// angle ground some texture at isometric viewing angles without painting
	// the perturbation into every tile.
	vec2 dn_uv = world_vertex.xz * detail_normal_freq;
	vec3 dn_sample = texture(detail_normal, dn_uv).xyz * 2.0 - 1.0;
	// Normal map tangent basis for the XZ plane: tangent-x → world_x,
	// tangent-y → world_z, tangent-z → world_y (up). Reconstruct a world-
	// space normal from the sample.
	vec3 dn_world = normalize(vec3(dn_sample.x, dn_sample.z, dn_sample.y));
	float ground_weight = clamp(shaded_normal.y, 0.0, 1.0);
	shaded_normal = normalize(mix(shaded_normal, dn_world, detail_normal_strength * ground_weight));

	// Pick which corner's tile this fragment shows. COLOR.rgb was set per
	// vertex to (1,0,0)/(0,1,0)/(0,0,1) so the rasterizer's linear interpolation
	// gives us the barycentric weights for free. Perturb each weight with a
	// 3D noise sample (different offset per corner) and take the argmax — this
	// is a hard pick (no smearing) but the boundary becomes irregular instead
	// of a straight triangle bisector. blend_amp = 0 (e.g. man-made walls)
	// collapses back to the un-perturbed bisector for a crisp seam.
	vec3 bary = COLOR.rgb / max(COLOR.r + COLOR.g + COLOR.b, 1e-5);
	vec3 np = world_vertex * BLEND_NOISE_FREQ;
	float n0 = vnoise(np);
	float n1 = vnoise(np + vec3(13.7, 0.0, 0.0));
	float n2 = vnoise(np + vec3(0.0, 17.3, 7.1));
	vec3 jitter = (vec3(n0, n1, n2) - 0.5) * blend_amp;
	vec3 weights = bary + jitter;
	float tile_index;
	float kit_index;
	float overlay_index;
	if (weights.x >= weights.y && weights.x >= weights.z) {
		tile_index = tile_ids.x;
		kit_index = kit_ids.x;
		overlay_index = overlay_ids.x;
	} else if (weights.y >= weights.z) {
		tile_index = tile_ids.y;
		kit_index = kit_ids.y;
		overlay_index = overlay_ids.y;
	} else {
		tile_index = tile_ids.z;
		kit_index = kit_ids.z;
		overlay_index = overlay_ids.z;
	}

	// Triplanar blend — DC vertices have arbitrary normals, so a dominant-axis
	// pick produces visible seams. Sample the tile on all three planes and
	// blend by |normal|^k.
	vec3 blend = pow(abs(shaded_normal), vec3(4.0));
	blend /= (blend.x + blend.y + blend.z + 1e-5);

	vec4 tex_color;
	if (tile_index >= TILE_AUTO_THRESHOLD) {
		// AUTO terrain: per-kit flat↔wall via a single smoothstep on normal.y.
		// No intermediate slope tile — dirt/edge appearance is painted by the
		// overlay layer only where worldgen authored OverlayId != 0. Keeps
		// cliff tops sharp (no auto dirt ring) and makes dirt an intentional
		// marker for walkable slopes and small features. Each concrete tile
		// runs through sample_tile so kit tiles that happen to be variant-
		// capable bases (e.g. grass) get per-fragment band/variant resolution.
		int kit_idx = clamp(int(kit_index), 0, MAX_KITS - 1);
		vec4 tiles = kit_tiles[kit_idx];
		vec4 bands = kit_bands[kit_idx];
		float s = clamp(shaded_normal.y, 0.0, 1.0);
		float w_flat = smoothstep(bands.x, bands.y, s);
		float w_wall = 1.0 - w_flat;
		vec4 t_flat = sample_tile(tiles.x, world_vertex, blend);
		vec4 t_wall = sample_tile(tiles.y, world_vertex, blend);
		tex_color = t_flat * w_flat + t_wall * w_wall;

		// Overlay pass: if this fragment's voxel has a non-zero OverlayId,
		// paint the kit's edge tile on top. tiles.z holds the edge overlay
		// tile index; only the flat surface gets it (a wall cliff face with
		// overlay authored would still read as the kit's wall tile).
		if (overlay_index > 0.5) {
			vec4 t_overlay = sample_tile(tiles.z, world_vertex, blend);
			// Blend based on how flat the surface is — overlay rides on top of
			// flat ground, fades out on walls (otherwise a cliff-face voxel
			// stamped edge would overwrite the wall tile).
			tex_color = mix(tex_color, t_overlay, w_flat);
		}
	} else if (tile_index >= TILE_AUTO_PATH_THRESHOLD) {
		// AUTO_PATH terrain: only dirt or stone, picked by slope. Kit-
		// independent for now — folds into a per-kit overlay system later.
		float s = clamp(shaded_normal.y, 0.0, 1.0);
		float w_dirt = smoothstep(SLOPE_PATH_STONE_LO, SLOPE_PATH_STONE_HI, s);
		float w_stone = 1.0 - w_dirt;
		vec4 t_dirt  = sample_tile(TILE_DIRT,  world_vertex, blend);
		vec4 t_stone = sample_tile(TILE_STONE, world_vertex, blend);
		tex_color = t_dirt * w_dirt + t_stone * w_stone;
	} else {
		tex_color = sample_tile(tile_index, world_vertex, blend);
	}

	// Macro detail overlay: a low-frequency grayscale noise multiplied into
	// albedo as a small brightness modulation. Breaks up same-tile repetition
	// over large areas without adding any texture-array variants.
	float macro = texture(detail_noise, world_vertex.xz * detail_noise_freq).r;
	float overlay = mix(1.0 - detail_noise_strength, 1.0 + detail_noise_strength, macro);
	tex_color.rgb *= overlay;

	// Sample lightmap per-pixel so it interpolates smoothly across the face.
	// Push 0.5 along the outward normal so the sample lands in the adjacent
	// air voxel — without this, DC vertices on cave walls would sample inside
	// the solid (or worse, near a sunlit-air neighbour) and caves would glow.
	vec3 light_sample_pos = world_vertex + shaded_normal * 0.5;
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = texture(light_map, uvw);
	float sun_mask = pow(lm.r, light_falloff_exp);
	// Cloud shadow: pass to light() via cloud_dim so it composes with
	// ATTENUATION as min() (darkest occluder wins) rather than multiplying
	// onto the direct-sun term. Ambient sky-bounce (EMISSION half) is still
	// cloud-dimmed below — clouds do occlude sky light.
	// cloud_shadow_ground is Y-independent to avoid faceted polygon-edge
	// artifacts on sloped triangles.
	float cloud = cloud_shadow_ground(world_vertex, TIME);
	cloud_dim = 1.0 - cloud * cloud_shadow_strength;
	vec3 sun_lit = sun_mask * sun_intensity * sun_color;
	vec3 block_lit = lm.gba;

	// "Reverse" directional shading: each light darkens faces by its tint
	// color in proportion to how much they face away from it. Faces facing
	// the light are unaffected (multiplied by 1).
	float sun_facing = max(0.0, dot(shaded_normal, -sun_world_dir));
	float fill_facing = max(0.0, dot(shaded_normal, -fill_world_dir));
	vec3 sun_tint = mix(sun_tint_color, vec3(1.0), sun_facing);
	vec3 fill_tint = mix(fill_tint_color, vec3(1.0), fill_facing);

	vec3 base = tex_color.rgb * sun_tint * fill_tint;

	// Split the lighting across ALBEDO and EMISSION so light() can shadow
	// just the direct portion of the sun via ATTENUATION. The BFS sun mask
	// is treated as part-direct, part-ambient-skylight (see sun_ambient):
	//   ALBEDO    = direct sun color  (scaled by ATTENUATION in light())
	//   EMISSION  = ambient sky bounce + block light (always-on)
	// With ambient_light_disabled, ALBEDO contributes only via DIFFUSE_LIGHT,
	// so the final pixel = ALBEDO * ATTENUATION + EMISSION.
	ALBEDO = base * sun_lit * (1.0 - sun_ambient);
	EMISSION = base * (block_lit + sun_lit * sun_ambient * cloud_dim);

	if (dot(debug_solid, vec3(1.0)) > 0.001) {
		ALBEDO = vec3(0.0);
		EMISSION = debug_solid;
	}
	if (world_vertex.y > camera_clip) {
		discard;
	}
}

void light() {
	// ATTENUATION is Godot's directional shadow atlas — terrain self-shadow
	// plus sprite shadow proxies, already composed correctly in the atlas.
	// No LIGHT_COLOR / NdotL: sun color/direction stylization is baked into
	// ALBEDO via sun_color and sun_tint in fragment().
	//
	// Cloud shadow (cloud_dim from fragment()) and the shadow atlas both
	// occlude direct sun. Compose as min(): whichever is darker wins,
	// instead of multiplying them — standing in a cast shadow on a cloudy
	// day should read as one shadow, not two stacked. Ambient sky-bounce
	// is still cloud-dimmed in EMISSION (see fragment()).
	DIFFUSE_LIGHT += ALBEDO * min(ATTENUATION, cloud_dim);
}
