shader_type spatial;
// Cap-mask material for props that clip as a UNIT (roofs), as opposed to
// cap_mask_terrain, which clips per fragment because terrain SHOULD be sliced
// at the plane.
//
// A prop has to resolve the cutaway exactly the way its visible material does
// or the mask and the geometry disagree: model_lit tests the flat mesh origin,
// so testing per fragment here would leave the mask white ("cap here") over the
// upper half of a roof that is still fully drawn, and the cap plane would paint
// black across it.
//
// BLACK = "visible geometry covers this pixel, do not cap." Discarding (the
// prop is clipped away) leaves the mask viewport's white clear, which is what
// turns a cut-away roof into a proper black cap instead of a hole onto the sky.
// Without this pass a roof is invisible anywhere it has no terrain behind it —
// the clear stays white and the cap plane draws straight over it.
render_mode unshaded, depth_draw_opaque, cull_back, shadows_disabled;

global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
global uniform vec3 camera_clip_growth_center;
global uniform float camera_clip_growth_max_radius;
global uniform float camera_clip_growth_edge_softness;
// Architecture: participates in the visibility-fan cut. See clip_dither.
#include "res://shaders/clip_dither.gdshaderinc"

// Column-cutaway participation for the whole instance, set by Roof alongside the
// visible material's. Only roofs use this material, and Roof always sets it — the
// mask and the geometry have to resolve the cutaway identically or the cap plane
// paints black over a roof that is still drawn.

varying flat vec3 model_origin;
// Per-fragment world position, needed because the iris disk resolves per
// fragment even for props that clip as a unit — see model_lit_body, which must
// make the identical call or mask and geometry disagree across the fan edge.
varying vec3 world_vertex;

void vertex() {
	model_origin = MODEL_MATRIX[3].xyz;
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (clip_dither_should_discard_anchored(model_origin, world_vertex, FRAGCOORD.xy)) {
		discard;
	}
	ALBEDO = vec3(0.0);
}
