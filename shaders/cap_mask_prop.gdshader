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
#include "res://shaders/clip_dither.gdshaderinc"

varying flat vec3 model_origin;

void vertex() {
	model_origin = MODEL_MATRIX[3].xyz;
}

void fragment() {
	if (clip_dither_should_discard(model_origin, FRAGCOORD.xy)) {
		discard;
	}
	ALBEDO = vec3(0.0);
}
