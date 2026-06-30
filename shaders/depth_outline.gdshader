shader_type spatial;
render_mode unshaded, depth_test_disabled, depth_draw_never, cull_disabled;
// Outline only un-stamped terrain/detail. Characters/mobs stamp stencil 8 and
// trees stamp 16, so this skips them and the line never lands on a non-terrain
// subject. Only the frontmost surface's stencil counts, so a subject hidden
// behind terrain still lets that terrain edge draw.
stencil_mode read, compare_equal, 0;

// Screen-space depth-discontinuity outline. A fullscreen quad parented to the
// scene MainCamera (like FogQuad), reconstructing view-space linear depth from
// DEPTH_TEXTURE at each pixel + its 4 cardinal neighbors and drawing a line
// wherever the depth JUMPS — a silhouette / cliff edge that flat orthographic
// lighting can't separate from the surface behind it. Continuous slopes ramp
// smoothly and stay under the threshold, so only true discontinuities (cliff
// lips, terraces, stacked ledges) get a line.
//
// Two-sided: the dark line lands on the INTERIOR (nearer) side of a jump — the
// surface that owns the edge — reading as an inked lip; an optional light rim
// marks the EXTERIOR (farther) side. Edges against the sky are suppressed (a sky
// neighbor is clamped to the center depth) so the effect stays on real
// geometry-vs-geometry discontinuities, not the world silhouette.

// DEPTH_TEXTURE is a uniform hint in Godot 4.x. Nearest so neighbor samples
// read true per-pixel depth instead of a blurred ramp across the edge.
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

uniform vec4 dark_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform float dark_strength : hint_range(0.0, 1.0) = 0.6;
uniform vec4 light_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float light_strength : hint_range(0.0, 1.0) = 0.0;

// Depth gap (world meters) over which an edge fades in. Below min: no line, so
// gentle slopes and ground-hugging props stay unoutlined.
uniform float edge_min_depth : hint_range(0.0, 16.0, 0.01) = 3.0;
uniform float edge_max_depth : hint_range(0.0, 16.0, 0.01) = 4.0;

// Neighbor sample offset in texels. 1 = thinnest (one pixel of the low-res
// SceneViewport, which upscales to a chunky pixel-art line).
uniform float thickness : hint_range(1.0, 4.0, 1.0) = 1.0;

void vertex()
{
	// QuadMesh(2,2): VERTEX.xy in [-1,1] emitted as clip space covers the whole
	// screen regardless of the mesh's world position. w=1 skips perspective divide.
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

// View-space distance (positive, meters) reconstructed from the depth buffer.
// INV_PROJECTION_MATRIX must be passed in — custom functions don't inherit the
// fragment-stage built-ins. Sky reads as reverse-Z 0.0; the caller flags it.
float view_dist(vec2 uv, mat4 inv_proj, out bool is_sky)
{
	float dz = texture(depth_tex, uv).x;
	is_sky = dz <= 0.0;
	vec2 ndc = uv * 2.0 - 1.0;
	vec4 v = inv_proj * vec4(ndc, dz, 1.0);
	return -v.z / v.w;
}

void fragment()
{
	vec2 texel = thickness / vec2(textureSize(depth_tex, 0));

	bool c_sky;
	float dc = view_dist(SCREEN_UV, INV_PROJECTION_MATRIX, c_sky);
	if (c_sky)
	{
		ALBEDO = vec3(0.0);
		ALPHA = 0.0;
	}
	else
	{
		// max_nearer: a neighbor is closer than center -> center is on the
		//   exterior (background) side. max_farther: a neighbor is farther ->
		//   center is on the interior (foreground/lip) side. Sky neighbors clamp
		//   to dc so they form no edge.
		float max_nearer = 0.0;
		float max_farther = 0.0;
		bool n_sky;
		float dn;

		dn = view_dist(SCREEN_UV + vec2(texel.x, 0.0), INV_PROJECTION_MATRIX, n_sky);
		if (n_sky) { dn = dc; }
		max_nearer = max(max_nearer, dc - dn);
		max_farther = max(max_farther, dn - dc);

		dn = view_dist(SCREEN_UV + vec2(-texel.x, 0.0), INV_PROJECTION_MATRIX, n_sky);
		if (n_sky) { dn = dc; }
		max_nearer = max(max_nearer, dc - dn);
		max_farther = max(max_farther, dn - dc);

		dn = view_dist(SCREEN_UV + vec2(0.0, texel.y), INV_PROJECTION_MATRIX, n_sky);
		if (n_sky) { dn = dc; }
		max_nearer = max(max_nearer, dc - dn);
		max_farther = max(max_farther, dn - dc);

		dn = view_dist(SCREEN_UV + vec2(0.0, -texel.y), INV_PROJECTION_MATRIX, n_sky);
		if (n_sky) { dn = dc; }
		max_nearer = max(max_nearer, dc - dn);
		max_farther = max(max_farther, dn - dc);

		// Dark line on the interior (nearer) side of the edge, light rim on the
		// exterior (farther) side. Interior keeps the line on the surface that owns
		// the edge, so water sitting behind an above-water edge can't cover it.
		float dark_e = smoothstep(edge_min_depth, edge_max_depth, max_farther) * dark_strength;
		float light_e = smoothstep(edge_min_depth, edge_max_depth, max_nearer) * light_strength;

		// One pixel is foreground OR background of a given edge, not both —
		// pick the stronger term and blend toward its color via blend_mix.
		if (dark_e >= light_e)
		{
			ALBEDO = dark_color.rgb;
			ALPHA = dark_e * dark_color.a;
		}
		else
		{
			ALBEDO = light_color.rgb;
			ALPHA = light_e * light_color.a;
		}
	}
}
