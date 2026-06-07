// Ground-stain projector sampling.
//
// GroundStainProjector renders flat ground marks (scorch, footprints, blood,
// worn paths) from straight above into a SubViewport, exposing the result as
// `ground_stain_tex`. Lit ground shaders sample it at each fragment's world
// position and composite it into the surface BASE color BEFORE the
// ALBEDO/EMISSION lighting split — so a stain darkens/tints the surface in
// every lighting condition (open sun, shade, swamp, right next to a fire),
// unlike a Godot Decal, which only modifies ALBEDO (the direct-sun fraction)
// and washes out wherever the always-on EMISSION term dominates.
//
// World -> UV uses the projector camera's basis directly (same scheme as
// block_light_shadow.gdshaderinc): project the world-relative offset onto the
// camera's right/up axes and divide by the orthographic full extent. Avoids a
// mat4 global, which Godot 4.6 declines to register from project.godot's
// [shader_globals] section.
//
// Caller is responsible for declaring these globals before including this file
// (other shaders may also own them, so we don't redeclare to avoid multi-decl
// errors):
//
//   global uniform sampler2D ground_stain_tex;   // GroundStainProjector RT
//   global uniform vec3 ground_stain_origin;     // projector camera position
//   global uniform vec3 ground_stain_right;      // projector camera +X (world)
//   global uniform vec3 ground_stain_up;         // projector camera +Y (world, V-flipped)
//   global uniform float ground_stain_size;      // orthographic full extent (world units)
//   global uniform float ground_stain_strength;  // 0..1 master composite weight
//   global uniform bool ground_stain_enabled;    // CVar ground_stain

// Composites the stain over `base` and returns the stained color. `world_pos`
// is the fragment world position. The projector RT is a transparent SubViewport,
// which stores PREMULTIPLIED color (rgb = stain_color * coverage) over a
// (0,0,0,0) clear, with coverage in alpha. So the correct "over" composite,
// scaled by the master strength, is:
//   base * (1 - coverage*strength) + premultiplied_rgb * strength
// (A plain mix(base, rgb, a) would darken feathered edges, since rgb is already
// premultiplied by a.) Overlapping stains blend correctly in the RT first.
//
// When the CVar is off the projector stops rendering and the global is false,
// so this returns `base` unchanged — byte-identical to pre-feature. The branch
// is on a uniform bool so the GPU can hoist it out of per-fragment work.
// Out-of-frustum fragments (the projector covers a finite radius around the
// player) have no stain data and are likewise returned unchanged.
vec3 apply_ground_stain(vec3 base, vec3 world_pos)
{
	if (!ground_stain_enabled) {
		return base;
	}
	vec3 offset = world_pos - ground_stain_origin;
	float inv_size = 1.0 / max(ground_stain_size, 1e-4);
	vec2 uv = vec2(
		dot(offset, ground_stain_right) * inv_size + 0.5,
		dot(offset, ground_stain_up) * inv_size + 0.5
	);
	if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
		return base;
	}
	vec4 stain = texture(ground_stain_tex, uv);
	return base * (1.0 - stain.a * ground_stain_strength) + stain.rgb * ground_stain_strength;
}
