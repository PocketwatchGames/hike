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

global uniform float cloud_shadow_scale;
global uniform float cloud_speed;
global uniform vec2 cloud_direction;
global uniform float cloud_cutoff;
global uniform float cloud_power;

uniform sampler2DArray tile_array : filter_nearest, repeat_enable;

// Tile layer indices — must match VoxelTypeInfo.TILE_* in C#.
const float TILE_STONE = 0.0;
const float TILE_DIRT = 1.0;
const float TILE_GRASS_TOP = 2.0;
// "Auto" sentinels — when a vertex carries one of these ids, the fragment
// picks the real tile from surface slope. Two variants:
//   TILE_AUTO (255):      grass / dirt / stone by slope (default natural land).
//   TILE_AUTO_PATH (254): never grass; dirt by default; stone if steep. Used
//                         where authored content wants a worn/earthen look
//                         regardless of slope.
// Must match VoxelTypeInfo.TILE_AUTO / TILE_AUTO_PATH.
const float TILE_AUTO_PATH_THRESHOLD = 253.5;
const float TILE_AUTO_THRESHOLD = 254.5;

// Slope bands for AUTO terrain. world_normal.y = 1 = flat-up, 0 = vertical.
const float SLOPE_GRASS_LO = 0.85;
const float SLOPE_GRASS_HI = 0.95;
const float SLOPE_DIRT_LO = 0.40;
const float SLOPE_DIRT_HI = 0.60;
// Path slope rule: dirt above PATH_STONE_HI, stone below PATH_STONE_LO.
const float SLOPE_PATH_STONE_LO = 0.35;
const float SLOPE_PATH_STONE_HI = 0.55;

global uniform vec3 sun_world_dir;
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
}

// Hash-based 3D value noise. Good enough for breaking up tile-edge seams; we
// only need it to be locally varied and reproducible across chunk boundaries.
float hash13(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
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
	if (weights.x >= weights.y && weights.x >= weights.z) {
		tile_index = tile_ids.x;
	} else if (weights.y >= weights.z) {
		tile_index = tile_ids.y;
	} else {
		tile_index = tile_ids.z;
	}

	// Triplanar blend — DC vertices have arbitrary normals, so a dominant-axis
	// pick produces visible seams. Sample the tile on all three planes and
	// blend by |normal|^k.
	vec3 blend = pow(abs(shaded_normal), vec3(4.0));
	blend /= (blend.x + blend.y + blend.z + 1e-5);

	vec4 tex_color;
	if (tile_index >= TILE_AUTO_THRESHOLD) {
		// AUTO terrain: pick grass/dirt/stone from surface slope and blend
		// across the slope thresholds so the transition isn't a hard band.
		float s = clamp(shaded_normal.y, 0.0, 1.0);
		float w_grass = smoothstep(SLOPE_GRASS_LO, SLOPE_GRASS_HI, s);
		float w_not_stone = smoothstep(SLOPE_DIRT_LO, SLOPE_DIRT_HI, s);
		float w_stone = 1.0 - w_not_stone;
		float w_dirt = max(0.0, w_not_stone - w_grass);
		vec4 t_grass = texture(tile_array, vec3(world_vertex.x, -world_vertex.y, TILE_GRASS_TOP)) * blend.z
			+ texture(tile_array, vec3(world_vertex.x,  world_vertex.z, TILE_GRASS_TOP)) * blend.y
			+ texture(tile_array, vec3(world_vertex.z, -world_vertex.y, TILE_GRASS_TOP)) * blend.x;
		vec4 t_dirt = texture(tile_array, vec3(world_vertex.x, -world_vertex.y, TILE_DIRT)) * blend.z
			+ texture(tile_array, vec3(world_vertex.x,  world_vertex.z, TILE_DIRT)) * blend.y
			+ texture(tile_array, vec3(world_vertex.z, -world_vertex.y, TILE_DIRT)) * blend.x;
		vec4 t_stone = texture(tile_array, vec3(world_vertex.x, -world_vertex.y, TILE_STONE)) * blend.z
			+ texture(tile_array, vec3(world_vertex.x,  world_vertex.z, TILE_STONE)) * blend.y
			+ texture(tile_array, vec3(world_vertex.z, -world_vertex.y, TILE_STONE)) * blend.x;
		tex_color = t_grass * w_grass + t_dirt * w_dirt + t_stone * w_stone;
	} else if (tile_index >= TILE_AUTO_PATH_THRESHOLD) {
		// AUTO_PATH terrain: only dirt or stone, picked by slope.
		float s = clamp(shaded_normal.y, 0.0, 1.0);
		float w_dirt = smoothstep(SLOPE_PATH_STONE_LO, SLOPE_PATH_STONE_HI, s);
		float w_stone = 1.0 - w_dirt;
		vec4 t_dirt = texture(tile_array, vec3(world_vertex.x, -world_vertex.y, TILE_DIRT)) * blend.z
			+ texture(tile_array, vec3(world_vertex.x,  world_vertex.z, TILE_DIRT)) * blend.y
			+ texture(tile_array, vec3(world_vertex.z, -world_vertex.y, TILE_DIRT)) * blend.x;
		vec4 t_stone = texture(tile_array, vec3(world_vertex.x, -world_vertex.y, TILE_STONE)) * blend.z
			+ texture(tile_array, vec3(world_vertex.x,  world_vertex.z, TILE_STONE)) * blend.y
			+ texture(tile_array, vec3(world_vertex.z, -world_vertex.y, TILE_STONE)) * blend.x;
		tex_color = t_dirt * w_dirt + t_stone * w_stone;
	} else {
		vec4 tex_xy = texture(tile_array, vec3(world_vertex.x, -world_vertex.y, tile_index));
		vec4 tex_xz = texture(tile_array, vec3(world_vertex.x,  world_vertex.z, tile_index));
		vec4 tex_yz = texture(tile_array, vec3(world_vertex.z, -world_vertex.y, tile_index));
		tex_color = tex_xz * blend.y + tex_xy * blend.z + tex_yz * blend.x;
	}

	// Sample lightmap per-pixel so it interpolates smoothly across the face.
	// Push 0.5 along the outward normal so the sample lands in the adjacent
	// air voxel — without this, DC vertices on cave walls would sample inside
	// the solid (or worse, near a sunlit-air neighbour) and caves would glow.
	vec3 light_sample_pos = world_vertex + shaded_normal * 0.5;
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = texture(light_map, uvw);
	// Cloud shadow: noise-driven soft occlusion on the sun channel, tinting
	// toward shadow_color. Only affects sun-lit areas; block light (torches)
	// is unaffected. Cloud pattern moves over time for a living sky feel.
	float sun_mask = pow(lm.r, light_falloff_exp);
	// Cloud shadow infrastructure is in place (uniforms, noise function) but
	// disabled until terrain self-shadowing is added. Without real sun
	// occlusion the cloud effect creates artifacts at indoor/outdoor
	// transitions. Once the shadow map includes terrain, fold cloud noise
	// into the shadow term instead of the sun channel.
	// float cloud_noise = vnoise(vec3(world_vertex.xz * cloud_shadow_scale + TIME * cloud_speed * cloud_direction, 0.0));
	// float cloud = pow(clamp((cloud_noise - cloud_cutoff) / (1.0 - cloud_cutoff), 0.0, 1.0), cloud_power);
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
	EMISSION = base * (block_lit + sun_lit * sun_ambient);

	if (dot(debug_solid, vec3(1.0)) > 0.001) {
		ALBEDO = vec3(0.0);
		EMISSION = debug_solid;
	}
	if (world_vertex.y > camera_clip) {
		discard;
	}
}

void light() {
	// ATTENUATION on a DirectionalLight3D is the shadow attenuation only
	// (no lambert pre-mul), so we use it as a pure shadow mask. We deliberately
	// don't include LIGHT_COLOR or NdotL here — sun color/direction stylization
	// is already baked into ALBEDO via sun_color and sun_tint in fragment().
	DIFFUSE_LIGHT += ALBEDO * ATTENUATION;
}
