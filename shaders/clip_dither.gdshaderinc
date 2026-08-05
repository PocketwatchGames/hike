// Dithered ceiling-clip transition with player-centered growth.
//
// GameCamera animates `camera_clip_blend` from 0 to 1 over a short fade
// each time the cutaway elevation changes (player walks under a new
// ceiling, or out from under one). During the fade, fragments that sit
// in the band between the prev and new clip Y participate; within the
// band, the transition isn't uniform-in-time — it spreads outward from
// the player. A pixel close to the player crosses its threshold early
// in the blend; a pixel far from the player crosses late. So instead of
// the whole band fading at once, a noise-edged disk opens at the player
// and grows to subsume the band by blend=1. Matches the foliage cutaway
// aesthetic so the two effects feel like one system.
//
// Caller must declare these globals before #include:
//
//   global uniform float camera_clip;
//   global uniform float camera_clip_prev;
//   global uniform float camera_clip_blend;
//   global uniform vec3  camera_clip_growth_center;
//   global uniform float camera_clip_growth_max_radius;
//   global uniform float camera_clip_growth_edge_softness;
//
// 8x8 Bayer over a 4x4 — wider repeat tile reads as noise rather than a
// visible grid at the brief fade durations the cutaway uses, while still
// being a single index → one mul-add lookup per fragment.
const float CLIP_DITHER_BAYER_8[64] = float[](
	 0.0/64.0, 32.0/64.0,  8.0/64.0, 40.0/64.0,  2.0/64.0, 34.0/64.0, 10.0/64.0, 42.0/64.0,
	48.0/64.0, 16.0/64.0, 56.0/64.0, 24.0/64.0, 50.0/64.0, 18.0/64.0, 58.0/64.0, 26.0/64.0,
	12.0/64.0, 44.0/64.0,  4.0/64.0, 36.0/64.0, 14.0/64.0, 46.0/64.0,  6.0/64.0, 38.0/64.0,
	60.0/64.0, 28.0/64.0, 52.0/64.0, 20.0/64.0, 62.0/64.0, 30.0/64.0, 54.0/64.0, 22.0/64.0,
	 3.0/64.0, 35.0/64.0, 11.0/64.0, 43.0/64.0,  1.0/64.0, 33.0/64.0,  9.0/64.0, 41.0/64.0,
	51.0/64.0, 19.0/64.0, 59.0/64.0, 27.0/64.0, 49.0/64.0, 17.0/64.0, 57.0/64.0, 25.0/64.0,
	15.0/64.0, 47.0/64.0,  7.0/64.0, 39.0/64.0, 13.0/64.0, 45.0/64.0,  5.0/64.0, 37.0/64.0,
	63.0/64.0, 31.0/64.0, 55.0/64.0, 23.0/64.0, 61.0/64.0, 29.0/64.0, 53.0/64.0, 21.0/64.0
);

// Hash a screen-space pixel coord to a small per-frag rotation/offset so
// the 8x8 tile doesn't lock to screen alignment — breaks up the residual
// grid feel without changing the average coverage at any threshold.
float clip_dither_hash(ivec2 p)
{
	int n = p.x * 374761393 + p.y * 668265263;
	n = (n ^ (n >> 13)) * 1274126177;
	return float((n ^ (n >> 16)) & 0xFFFF) / 65535.0;
}

// --- Player visibility fan -------------------------------------------
// Removes exactly the geometry standing between the camera and ground the
// player can see, so an outdoor cutaway is targeted instead of taking every
// roof in frame off at once.
//
// VisibilityFan bakes what the player can see at eye level — a star-shaped
// polygon about the player — into a BIN_COUNT x 1 R32F strip of radii
// indexed by bearing. The test is NOT "is this fragment over the polygon".
// It is: continue the fragment's view ray onward to the sight plane, and
// ask whether it LANDS in the polygon. If it does, this fragment sits
// between the camera and ground the player can see, and it is what needs to
// go.
//
// That distinction is the whole design. Testing the fragment's own position
// removes what is ABOVE the visible ground, which both misses a building
// standing in front of the player and over-removes an entire column above a
// window. Testing the landing point removes an occluder through its full
// thickness, and nothing else: geometry that occludes nothing projects past
// the polygon and survives, so a distant ridgeline or a wall on the far side
// of the player is never touched. The effect needs no near-field term and no
// activation gate — it is inert until something is genuinely in the way.
//
// visibility_fan_enabled is false whenever the clip comes from a real
// ceiling, so indoor cutaways are untouched: the cut helper returns 1.0 and
// every consumer collapses to its previous behavior exactly.
global uniform bool visibility_fan_enabled;
// World-anchored, NOT player-polar. The temporal smoothing lives in this
// texture, and a radial r(theta) strip would anchor it to the player: a
// lagging radius gets reinterpreted from a new origin every frame, so the
// stale shape is a different region rather than "where I could see a moment
// ago", and it wobbles as the player walks. Per world cell, a lagging value
// means one thing no matter where the player went.
global uniform sampler2D visibility_fan_tex : filter_linear, repeat_disable;
// World XZ of the grid's (0,0) corner, and the window's total world size.
global uniform vec2 visibility_fan_origin_xz;
global uniform float visibility_fan_extent;
// World Y of the surface view rays are projected onto (the player's eye
// level). Fragments at or below it can never be in front of it.
global uniform float visibility_fan_plane_y;
// Height the fan cuts from, INDEPENDENT of camera_clip. The two compose: the
// ceiling clip removes everything above it at full width, the fan removes only
// occluders above this lower plane. Folding them into one value made them
// mutually exclusive, so a ceiling overhead switched the fan off and windows
// under it stopped opening.
global uniform float visibility_fan_clip_y;
// Main-camera pose, pushed explicitly rather than read from INV_VIEW_MATRIX
// so the off-screen mask passes resolve the identical volume the main pass
// does. dir is the constant forward for the orthographic presets; the
// perspective presets need a per-fragment direction from the position.
global uniform vec3 visibility_fan_camera_pos;
global uniform vec3 visibility_fan_view_dir;
global uniform bool visibility_fan_perspective;

// 1 where the player can see this patch of ground, 0 where they cannot,
// fractional across the boundary ramp (written CPU-side while rasterizing).
// The explicit bounds test is what makes outside-the-window read as unseen —
// repeat_disable would otherwise smear the edge texel outward forever.
float visibility_fan_contains(vec2 world_xz)
{
	vec2 uv = (world_xz - visibility_fan_origin_xz) / visibility_fan_extent;
	if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
	{
		return 0.0;
	}
	return texture(visibility_fan_tex, uv).r;
}

// 1 = this fragment stands between the camera and visible ground (cut it),
// 0 = it occludes nothing (keep it), fractional across the boundary ramp.
// The caller folds it into its own visibility so the dither does the stipple.
float visibility_fan_cut(vec3 world_pos)
{
	if (!visibility_fan_enabled)
	{
		return 1.0;
	}
	vec3 view_dir = visibility_fan_perspective
		? normalize(world_pos - visibility_fan_camera_pos)
		: visibility_fan_view_dir;
	// A ray that isn't descending never reaches the sight plane. Guards the
	// divide as well as the level-camera degenerate case.
	if (view_dir.y > -1e-4)
	{
		return 0.0;
	}
	float travel = (visibility_fan_plane_y - world_pos.y) / view_dir.y;
	// Negative means the plane is BEHIND this fragment along the ray — the
	// fragment is at or under eye level, so it is not in front of anything.
	if (travel <= 0.0)
	{
		return 0.0;
	}
	return visibility_fan_contains(world_pos.xz + view_dir.xz * travel);
}

// Smooth [0,1] "how much is this point part of the visible interior
// (below the animating cutaway clip)". 1 = fully drawn / below-clip,
// 0 = fully cut-away / above-clip; the transition follows the same
// player-centered growth iris as the dither discard. Volumetric consumers
// (fog/shafts) use this directly instead of the per-fragment dither so the
// cutaway dissolves into them smoothly rather than snapping at camera_clip.
float clip_visibility_smooth(vec3 world_pos)
{
	// Deliberately ignores the visibility fan: camera_clip is a real ceiling
	// again, so the "above the clip is removed rock" reasoning volumetrics rely
	// on holds unchanged, and the fan's ray projection stays out of the fog
	// raymarch (which calls this once per step, 48-256 per pixel).
	bool above_prev = world_pos.y > camera_clip_prev;
	bool above_new = world_pos.y > camera_clip;
	if (above_prev && above_new)
	{
		return 0.0;
	}
	if (!above_prev && !above_new)
	{
		return 1.0;
	}
	// In the transition band — compute a player-centered phase that
	// normalizes the horizontal distance from the growth center (player
	// position) by max_radius, with world-stable sin-noise on the edge.
	vec2 to_center = world_pos.xz - camera_clip_growth_center.xz;
	float dist = length(to_center);
	float phase = dist / max(camera_clip_growth_max_radius, 1e-3);
	float n = sin(world_pos.x * 1.3 + world_pos.z * 0.7) * 0.06
	        + sin(world_pos.x * 0.5 - world_pos.z * 1.7) * 0.04
	        + sin(world_pos.x * 2.1 + world_pos.z * 0.4) * 0.03;
	phase = clamp(phase + n, 0.0, 1.0);
	if (camera_clip > camera_clip_prev) {
		phase = 1.0 - phase;
	}
	float softness = max(camera_clip_growth_edge_softness, 1e-3);
	float leading_phase = -softness + camera_clip_blend * (1.0 + 2.0 * softness);
	float local_blend = clamp(0.5 - (phase - leading_phase) / (2.0 * softness), 0.0, 1.0);
	return above_new ? (1.0 - local_blend) : local_blend;
}

// Bayer + hash threshold compare, shared by both discard paths below.
// `clip_visibility`: 1 = keep the fragment, 0 = cut it away.
bool clip_dither_test(float clip_visibility, vec2 frag_xy)
{
	ivec2 ip = ivec2(frag_xy);
	int bx = ip.x & 7;
	int by = ip.y & 7;
	// Add a small hash-noise offset (±1/128) to the threshold to break
	// the residual ordered-pattern feel of pure Bayer. Cheap (one int
	// hash) and stable per-pixel so the noise isn't temporally noisy.
	float threshold = CLIP_DITHER_BAYER_8[by * 8 + bx];
	threshold += (clip_dither_hash(ip) - 0.5) * (1.0 / 64.0);
	// Floor at 0 — the Bayer table's lowest cell is exactly 0, and hash
	// subtraction would otherwise drop the threshold negative there. At
	// visibility = 0 (end of a fade-out) the test `0 <= negative` is
	// FALSE, so those pixels skip the discard for the whole band-logic
	// phase and only disappear when the post-fade `above_prev &&
	// above_new` early-out takes over — producing the sparse stuck
	// pixels that pop off at transition completion.
	threshold = max(threshold, 0.0);
	return clip_visibility <= threshold;
}

// Two positions, because the two axes of the test want different granularity:
//
//   height_anchor — resolves the HEIGHT clip. Meshes that must cut as a unit
//                   (roofs, props, sprites) pass a flat per-instance origin so
//                   they are never sliced at the horizontal plane.
//   fan_pos       — resolves the FAN. Always the per-fragment world position,
//                   even for those meshes: the fan's boundary is vertical, and
//                   slicing across it is exactly what it is for. A roof spans
//                   many metres horizontally, so anchoring the fan at its origin
//                   would take the whole roof off (or none of it) rather than
//                   just the part over the player's sightline.
//
// Callers where one position serves both use clip_dither_should_discard below.
bool clip_dither_should_discard_anchored(vec3 height_anchor, vec3 fan_pos, vec2 frag_xy)
{
	vec3 world_pos = height_anchor;
	bool above_prev = world_pos.y > camera_clip_prev;
	bool above_new = world_pos.y > camera_clip;
	// Above the ceiling clip on both endpoints: gone, full width, as always.
	if (above_prev && above_new)
	{
		return true;
	}

	// The fan is a SEPARATE cut at its own, lower plane, so it still applies
	// under an active ceiling clip — that is what lets a first-storey window
	// open while the player stands under the eaves. Folding the two into one
	// value made them mutually exclusive, which is the same fact that made
	// second-storey frames reappear only when a ceiling took over.
	//
	// Note the asymmetry: participation is decided by the ANCHOR (a prop's flat
	// origin), while the cut itself resolves per fragment. So a prop only takes
	// part when the whole thing is placed above the cut plane — it is elevated
	// architecture, like a second-storey window frame — and once it qualifies,
	// only the part actually occluding comes off. A well or a player standing on
	// the ground is anchored below the plane and is exempt outright, however
	// tall it is, which is what stopped well.tscn's roof dissolving and the
	// player's own head being sliced.
	//
	// That only holds if the cut plane stays at or above the sight plane; see
	// the floor on _visibilityCutY in GameCamera, without which the plane can
	// sink below the player's feet and every ground-standing prop qualifies.
	//
	// OPT-IN on top of that: only shaders that #define CLIP_FAN_PARTICIPATES
	// take part at all. Characters are excluded outright rather than by anchor,
	// since a flying mob is legitimately anchored high and must never dissolve.
#ifdef CLIP_FAN_PARTICIPATES
	if (visibility_fan_enabled && world_pos.y > visibility_fan_clip_y)
	{
		float fan = visibility_fan_cut(fan_pos);
		if (fan > 0.0 && clip_dither_test(1.0 - fan, frag_xy))
		{
			return true;
		}
	}
#endif

	if (!above_prev && !above_new)
	{
		return false;
	}
	// In the transition band — compute a player-centered phase that
	// normalizes the horizontal distance from the growth center (player
	// position) by max_radius. World-stable sin-noise wobbles the phase
	// before the ring test, giving the iris an irregular advancing edge
	// in the same signature the foliage cutaway uses, so the two effects
	// read as one system. The noise is parked in world space — notches
	// stay over the same patches of geometry frame to frame; the ring
	// sweeps through them as blend rises.
	vec2 to_center = world_pos.xz - camera_clip_growth_center.xz;
	float dist = length(to_center);
	float phase = dist / max(camera_clip_growth_max_radius, 1e-3);
	float n = sin(world_pos.x * 1.3 + world_pos.z * 0.7) * 0.06
	        + sin(world_pos.x * 0.5 - world_pos.z * 1.7) * 0.04
	        + sin(world_pos.x * 2.1 + world_pos.z * 0.4) * 0.03;
	// Clamp BEFORE the direction flip so the safety applies in both
	// directions. Pixels beyond max_radius (phase > 1) are forced to
	// phase = 1 — they sweep with the iris ring's boundary instead of
	// being "orphaned" outside it. Without this, leading_phase only
	// reaches 1 + softness at blend = 1, so a pixel at phase 1.1 lands
	// at local_blend ≈ 0.75 — still 25% visible — and then snaps to
	// discard via the early-out the instant _clipPrev catches up to
	// _clip. With the clamp those distant pixels reach local_blend = 1
	// (fully off) at the same blend as a pixel right at the max_radius
	// boundary. The trade-off: a small ring of distant pixels transitions
	// together rather than as a smooth gradient — invisible at typical
	// max_radius values since they're past the visible screen anyway.
	phase = clamp(phase + n, 0.0, 1.0);

	// Direction-flip when the cutaway is SHRINKING (clip rising = player
	// walking out of cover). Cutting case (clip dropping): close-to-
	// player pixels transition first so the cutaway hole OPENS from the
	// center outward. Revealing case (clip rising): reverse phase so
	// far-from-player pixels transition first and the hole CLOSES toward
	// the center — the player-centered patch is the last to reappear.
	// Detected purely from camera_clip vs camera_clip_prev so a mid-fade
	// reversal (GameCamera.RequestClip swaps prev/new + flips blend) gets
	// the new direction for free, with no visual pop at the swap instant.
	if (camera_clip > camera_clip_prev) {
		phase = 1.0 - phase;
	}

	// Iris ring: a THIN expanding band at the leading edge instead of a
	// gradient across the whole disk. `leading_phase` rises with blend
	// from just-before-0 to just-past-1 (offset by softness so the ring
	// fully covers phase=0 at blend=0 and phase=1 at blend=1). Pixels
	// well inside the leading edge are fully transitioned (local_blend=
	// 1, the dither floor is fully on), pixels well outside are fully
	// untouched (local_blend=0), and only pixels within ±softness of the
	// edge dither. Result: a clear ring sweeps outward, leaving solidly-
	// transitioned area behind and solidly-untouched area ahead.
	float softness = max(camera_clip_growth_edge_softness, 1e-3);
	float leading_phase = -softness + camera_clip_blend * (1.0 + 2.0 * softness);
	float local_blend = clamp(0.5 - (phase - leading_phase) / (2.0 * softness), 0.0, 1.0);

	// In the transition band: visibility goes from 1 -> 0 (fading out)
	// when the fragment is above the new clip but was below the prev,
	// and from 0 -> 1 (fading in) when above the prev but below the new.
	float clip_visibility = above_new ? (1.0 - local_blend) : local_blend;
	return clip_dither_test(clip_visibility, frag_xy);
}

// One position for both axes — terrain and anything else already testing its
// own per-fragment world position.
bool clip_dither_should_discard(vec3 world_pos, vec2 frag_xy)
{
	return clip_dither_should_discard_anchored(world_pos, world_pos, frag_xy);
}
