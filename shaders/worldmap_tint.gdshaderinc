// World-map authoring overlay: tints terrain by per-chunk region / zone colour.
//
// Driven by the `worldmap_tint` CVar (0 off / 1 region / 2 zone). The CPU side
// (WorldMapTintOverlay) builds a per-chunk-column colour LUT from the live world
// and pushes the globals below. Lit terrain shaders sample the LUT by the
// fragment's world XZ -> chunk cell and blend it into BASE before the
// ALBEDO/EMISSION split, so the tint reads in every lighting condition.
//
// When `worldmap_tint_strength <= 0` (the default, and whenever the CVar is off)
// this is a strict no-op on a uniform branch — the GPU hoists it out of
// per-fragment work, so the shipped game is byte-identical to pre-feature and
// pays nothing. The LUT is only built/uploaded while the overlay is on.
//
// Caller declares these globals before including (other shaders may also own
// them, so we don't redeclare here):
//
//   global uniform sampler2D worldmap_tint_tex;   // per-chunk colour LUT
//   global uniform vec2 worldmap_tint_origin;     // world XZ at LUT texel (0,0)
//   global uniform vec2 worldmap_tint_size;       // LUT size in chunks (x, z)
//   global uniform float worldmap_tint_strength;  // 0 = off, else blend weight

vec3 apply_worldmap_tint(vec3 base, vec3 world_pos)
{
	if (worldmap_tint_strength <= 0.0) {
		return base;
	}
	// CHUNK_SIZE is 16 voxels (ChunkState.SIZE). Map world XZ -> chunk cell ->
	// LUT texel centre.
	vec2 chunkf = (world_pos.xz - worldmap_tint_origin) / 16.0;
	vec2 uv = (floor(chunkf) + 0.5) / worldmap_tint_size;
	if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
		return base;
	}
	vec3 tint = texture(worldmap_tint_tex, uv).rgb;
	return mix(base, tint, worldmap_tint_strength);
}
