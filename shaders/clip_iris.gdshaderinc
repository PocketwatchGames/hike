// The iris — the cutaway's SECOND, lower cut plane, opened around the player so a
// space reads before they are standing in it.
//
// Its height is `clip_iris_target`, a plateau boundary just above the player's
// eyes, and that is the same everywhere: the iris never varies the height it cuts
// at, only WHERE it cuts. Everything in this file answers the "where".
//
// The shape is a circle measured in the CAMERA PLANE, not in world XZ, so it is a
// true circle on screen; a world-space circle would be squashed into an ellipse by
// the camera's pitch and stop reading as an iris. Projecting onto the camera's
// right/up vectors rather than reading SCREEN_UV is what lets the off-screen passes
// (cap mask, reflections) resolve the identical disk the main pass does, since each
// renders through its own viewport.
//
// Included by clip_dither.gdshaderinc (the discard paths) and clip_cap.gdshader
// (the black interior fill), which must resolve the same shape or the cap draws
// over the region the cut just opened.
global uniform bool clip_iris_enabled;
global uniform vec3 clip_iris_center;
global uniform vec3 clip_iris_right;
global uniform vec3 clip_iris_up;
global uniform float clip_iris_radius;
// Metres of DITHERED ramp inside the boundary, matching the stipple the height
// transition uses so the iris's edge belongs to the same effect.
global uniform float clip_iris_edge;
// The plane revealed inside the iris. Always below camera_clip while active, and
// the SAME height everywhere: participation varies, elevation does not. That is
// what keeps every exposed face a horizontal section at one height, so the black
// interior of solid still reads as poche and a building stays legible in iso.
global uniform float clip_iris_target;
// The MAIN camera, so every pass — the cap mask, the reflections, the fog march —
// casts the same rays however it renders. See clip_iris_ray_anchor.
global uniform vec3 clip_iris_eye;
global uniform bool clip_iris_perspective;

// Borrowed from the FOLIAGE cutaway so the two read as one effect. Shared rather
// than copied: they are the same shape by intent, and two numbers would drift.
// .x scales the horizontal component, .y the vertical; (1.6, 1.2) is roughly 16:9,
// slightly wider than tall. See tree_cards_lit.gdshader for the authored meaning.
global uniform vec2 foliage_player_fade_aspect;

// Is the iris cutting at all? Named rather than inlined because four consumers ask
// it and they must not drift apart.
bool clip_iris_active()
{
	return clip_iris_enabled && clip_iris_radius > 0.0;
}

// Where the main camera's view ray through this point crosses the iris plane. Every
// point on one ray maps to the same anchor. At the shipping -40° ortho pitch the two
// cut planes are ~1.2 m apart along a ray, which moved the noisy boundary by more
// than its whole dithered edge.
vec3 clip_iris_ray_anchor(vec3 world_pos)
{
	vec3 dir = clip_iris_perspective ? world_pos - clip_iris_eye : cross(clip_iris_right, clip_iris_up);
	if (abs(dir.y) < 1e-4)
	{
		return world_pos;
	}
	return world_pos + dir * ((clip_iris_target - world_pos.y) / dir.y);
}

// Distance from the iris centre, compared against clip_iris_radius — the SHAPE,
// shared by everything that asks where the boundary is.
//
// The foliage cutaway's shape, arrived at the same way: an anisotropic ellipse whose
// edge is wobbled by world-space noise, so the canopy fade and the ceiling reveal
// look like one system rather than two effects that happen to open around the player.
// Aspect and noise are both used exactly as tree_cards_lit does, so the two cannot
// drift into different shapes.
//
// The noise is anchored to the WORLD, so a notch sits over the same patch of ground
// frame to frame and the boundary sweeps through it as the player walks, rather than
// crawling with the camera. Being the same field means both effects wobble in step
// where they meet.
//
// Evaluated at the RAY ANCHOR, never at the point itself. The boundary has to be one
// answer per PIXEL: a wall fragment, the mask behind it and the two cap planes all
// sit at different depths along the same view ray, and each deciding the boundary at
// its own depth left pixels at the edge where the wall was stippled away but neither
// cap claimed the hole — the ground beyond showed through.
float clip_iris_shape_distance(vec3 world_pos)
{
	vec3 anchor = clip_iris_ray_anchor(world_pos);
	vec3 delta = anchor - clip_iris_center;
	vec2 screen = vec2(dot(delta, clip_iris_right), dot(delta, clip_iris_up));
	float dist = length(screen / max(foliage_player_fade_aspect, vec2(1e-3)));
	float n = sin(anchor.x * 1.3 + anchor.z * 0.7) * 0.3
	        + sin(anchor.x * 0.5 - anchor.z * 1.7) * 0.2
	        + sin(anchor.x * 2.1 + anchor.y * 1.1 + anchor.z * 0.4) * 0.15;
	return dist + n;
}

// 1 deep inside the iris, 0 outside it, ramped across clip_iris_edge.
float clip_iris_coverage(vec3 world_pos)
{
	if (!clip_iris_active())
	{
		return 0.0;
	}
	float dist = clip_iris_shape_distance(world_pos);
	return clamp((clip_iris_radius - dist) / max(clip_iris_edge, 1e-3), 0.0, 1.0);
}

// Hard in/out, for consumers that fill a REGION rather than stipple a boundary —
// the cap planes, which are flat quads and can only be at one height each.
bool clip_iris_inside(vec3 world_pos)
{
	if (!clip_iris_active())
	{
		return false;
	}
	return clip_iris_shape_distance(world_pos) <= clip_iris_radius;
}

// Smooth [0,1] "is the iris removing this point", for the volumetric consumers,
// which have no fragment to dither and need a continuous value.
float clip_iris_visibility(vec3 world_pos)
{
	if (!clip_iris_enabled || world_pos.y <= clip_iris_target)
	{
		return 1.0;
	}
	return 1.0 - clip_iris_coverage(world_pos);
}
