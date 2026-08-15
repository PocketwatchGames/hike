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
// There are TWO cut planes. `camera_clip` is the base, over the whole world.
// Inside the iris — a player-centred region, see clip_iris.gdshaderinc — a lower
// plane applies instead, revealing a space the player is walking toward before
// they reach it.
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
// The Bayer table and the hash that breaks up its grid live in dither.gdshaderinc:
// the poche fill uses the same two, and it abuts this stipple on screen.
#include "res://shaders/dither.gdshaderinc"

// The iris — the second, lower cut plane, applied inside a player-centred region.
// It opens as the player approaches a low space or somewhere the camera cannot see
// into, so the space reads before they are standing in it; once they actually are
// hidden, the BASE plane latches down instead and the iris closes itself. Its
// shape, and everything that resolves it, lives in the include; this file only
// asks whether a fragment is covered.
#include "res://shaders/clip_iris.gdshaderinc"

// Lowest plane anywhere in play. The discard paths early-out below this without
// resolving anything, and the iris is what makes camera_clip alone the wrong
// answer — a fragment under the base can still be above the iris's lower plane.
float clip_lowest_plane()
{
	float lowest = min(camera_clip, camera_clip_prev);
	if (clip_iris_active())
	{
		lowest = min(lowest, clip_iris_target);
	}
	return lowest;
}

// The cut HEIGHT in force at a point: the base plane everywhere, dropped to the
// iris's plane wherever the iris covers it. The shader twin of
// ClipIris.ClipHeightAt, and what any consumer wanting a height rather than a
// keep/cut decision must ask — `camera_clip` alone is only the whole answer while
// the iris is closed, and reading it as one is how a volumetric effect keeps
// treating an interior the iris has already opened as sealed.
float clip_height_at(vec3 world_pos)
{
	if (!clip_iris_active() || clip_iris_target >= camera_clip)
	{
		return camera_clip;
	}
	// A THRESHOLD rather than a blend across the edge ramp: the two planes sit far
	// apart (camera_clip parks at 1e20 outdoors), so any height between them is one
	// neither cut is at. Half coverage is where the geometry's stipple is half on,
	// so this boundary sits inside that one.
	return clip_iris_coverage(world_pos) < 0.5 ? camera_clip : clip_iris_target;
}

// Smooth [0,1] "how much is this point part of the visible interior
// (below the animating cutaway clip)". 1 = fully drawn / below-clip,
// 0 = fully cut-away / above-clip; the transition follows the same
// player-centered growth iris as the dither discard. Volumetric consumers
// (fog/shafts) use this directly instead of the per-fragment dither so the
// cutaway dissolves into them smoothly rather than snapping at camera_clip.
float clip_visibility_smooth(vec3 world_pos)
{
	// Below the lowest plane in play on both endpoints, nothing can be cutting
	// here — so this returns immediately, which is what keeps the fog raymarch
	// (48-256 calls per pixel) doing no work everywhere under the clip.
	if (world_pos.y <= clip_lowest_plane())
	{
		return 1.0;
	}
	bool above_prev = world_pos.y > camera_clip_prev;
	bool above_new = world_pos.y > camera_clip;

	float height_visibility = above_prev && above_new ? 0.0 : 1.0;
	if (above_prev != above_new)
	{
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
		height_visibility = above_new ? (1.0 - local_blend) : local_blend;
	}
	// The two planes are INDEPENDENT cuts, composed rather than nested — see the
	// note on clip_dither_should_discard_anchored.
	return min(height_visibility, clip_iris_visibility(world_pos));
}

// Bayer + hash threshold compare, shared by both discard paths below.
// `clip_visibility`: 1 = keep the fragment, 0 = cut it away.
bool clip_dither_test(float clip_visibility, vec2 frag_xy)
{
	ivec2 ip = ivec2(frag_xy);
	int bx = ip.x & 3;
	int by = ip.y & 3;
	// Add a hash-noise offset of half a level to the threshold, to break the
	// residual ordered-pattern feel of pure Bayer. Cheap (one int hash) and
	// stable per-pixel so the noise isn't temporally noisy. Scaled to the
	// table's own step, so it stays half a level whatever the tile size.
	float threshold = DITHER_BAYER_4[by * 4 + bx];
	threshold += (dither_hash(ip) - 0.5) * (1.0 / 16.0);
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

// Does the iris cut this fragment, where the base plane did not?
//
// Reached only for fragments already BELOW the base clip — so this is purely the
// second, lower plane, and everything outside the iris falls straight back out.
// The boundary stipples through the same Bayer test the height transition uses,
// so the iris's edge and the cutaway's edge read as one effect rather than two.
//
// BOTH conditions, and neither is negotiable: the shape decides whether a fragment
// participates, the plateau decides the height it is cut at. Geometry below
// clip_iris_target is never touched however deep inside the shape it sits, and
// nothing above it is ever cut at any other elevation.
//
// Height comes from the anchor (a mesh that must cut as a unit is never sliced
// horizontally); the shape comes from the fragment, because its boundary is
// VERTICAL and slicing across a vertical boundary is exactly what it is for.
bool clip_iris_discard(vec3 height_anchor, vec3 fragment_pos, vec2 frag_xy)
{
	if (!clip_iris_active() || height_anchor.y <= clip_iris_target)
	{
		return false;
	}
	float coverage = clip_iris_coverage(fragment_pos);
	if (coverage <= 0.0)
	{
		return false;
	}
	return clip_dither_test(1.0 - coverage, frag_xy);
}

// The BASE plane alone, its animated transition included.
bool clip_base_discard(vec3 height_anchor, vec2 frag_xy)
{
	bool above_prev = height_anchor.y > camera_clip_prev;
	bool above_new = height_anchor.y > camera_clip;
	// Above the clip on both endpoints: gone, no transition to resolve.
	if (above_prev && above_new)
	{
		return true;
	}
	// Below both: the base is not cutting here. The disk still might, but that is
	// the caller's question, not this one's.
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
	vec2 to_center = height_anchor.xz - camera_clip_growth_center.xz;
	float dist = length(to_center);
	float phase = dist / max(camera_clip_growth_max_radius, 1e-3);
	float n = sin(height_anchor.x * 1.3 + height_anchor.z * 0.7) * 0.06
	        + sin(height_anchor.x * 0.5 - height_anchor.z * 1.7) * 0.04
	        + sin(height_anchor.x * 2.1 + height_anchor.z * 0.4) * 0.03;
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
	// Detected from the pair itself so a mid-fade reversal (GameCamera.
	// RequestClip swaps prev/new + flips blend) gets the new direction for
	// free, with no visual pop at the swap instant.
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

// Two positions, because the axes of the test want different granularity, and
// the split is by the SHAPE OF EACH BOUNDARY:
//
//   height_anchor — resolves the HEIGHT clip, whose boundary is horizontal.
//                   Meshes that must cut as a unit (roofs, props, sprites) pass
//                   a flat per-instance origin so they are never sliced through
//                   the middle at that plane.
//   fragment_pos  — resolves the disk, whose boundary is VERTICAL. Always the
//                   per-fragment world position, even for those meshes, because
//                   slicing across a vertical boundary is exactly what it is for.
//
// The two planes are INDEPENDENT cuts, composed with OR — the disk is NOT a
// branch of the base test. Nesting it under "below both base planes" excluded it
// from the entire transition band, and the band spans everything the base is
// currently changing: the disk went dark for the whole fade and snapped back the
// instant camera_clip_prev caught up. Walking out of a cave that read as the disk
// popping in at the end of the animation; walking in, as it popping out at the
// start. Composed, both directions hand over continuously.
bool clip_dither_should_discard_anchored(vec3 height_anchor, vec3 fragment_pos, vec2 frag_xy)
{
	return clip_base_discard(height_anchor, frag_xy)
		|| clip_iris_discard(height_anchor, fragment_pos, frag_xy);
}

// One position for both axes — terrain and anything else already testing its
// own per-fragment world position.
bool clip_dither_should_discard(vec3 world_pos, vec2 frag_xy)
{
	return clip_dither_should_discard_anchored(world_pos, world_pos, frag_xy);
}
