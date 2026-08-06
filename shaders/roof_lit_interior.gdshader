shader_type spatial;
// Inner shell of a broken roof: the same mesh, back faces only.
//
// A hole cut by `discard` has no thickness — the cut removes fragments from the
// top surface AND the soffit at the same world column, so the opening is a clean
// shaft through the slab and the roof reads as paper however thick it really is.
// The cut walls simply have no geometry.
//
// This pass supplies them. cull_front renders the solid's interior, and
// broken_bias (< 1 here) makes its holes smaller than the outer surface's, so a
// ring of interior survives around every opening. Everywhere the roof is solid
// the outer surface is nearer and depth-tests over this, so it costs nothing
// visible except inside a hole.
render_mode ambient_light_disabled, depth_draw_opaque, cull_front;

#define MODEL_SURFACE_DETAIL
// A roof IS architecture, unlike every other model_lit_body consumer.
// Same per-instance participation as the outer shell — the two must cut together
// or a hole's interior ring survives its own roof. See roof_lit.
#define CLIP_PARTICIPATION_INSTANCE
#include "res://shaders/model_lit_body.gdshaderinc"
