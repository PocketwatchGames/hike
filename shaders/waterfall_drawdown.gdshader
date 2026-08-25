shader_type spatial;
// The DRAWDOWN: the flat run carried upstream of the lip, lying over the pool
// the fall is leaving. Identical code to the sheet (waterfall_body.gdshaderinc);
// only how it sorts differs, and that has to be declared here.
//
// It does NOT write depth, and it draws AFTER both water passes
// (render_priority -1). That is the opposite of the sheet, and for the opposite
// reason: the sheet hangs in air beyond the lip and has to depth-sort against
// water it can be in front of or behind, while this lies directly ON the pool's
// own surface and has to COMPOSITE with it.
//
// Sharing the sheet's depth write was tried and is exactly wrong. Writing depth
// first culled the pool underneath, and with no screen_tex to fall back on, the
// run's own ~38% transparency then showed raw opaque bed — so the streaks
// composited over stone and the whole drawdown read as a patch of wet rock laid
// across the water. Nothing about the streak tuning could fix that; the water it
// is supposed to be thickening had been removed from behind it.
//
// It also needs no depth write of its own. It sits above the pool surface and
// beyond nothing, so there is no case where it should occlude rather than blend.
render_mode blend_premul_alpha, depth_draw_never, cull_disabled, unshaded;

#include "res://shaders/waterfall_body.gdshaderinc"
