shader_type spatial;
// model_lit for meshes with single-sided sheets that must read from both sides
// (a ship's sail, loose canvas). Opaque, no cutout — the two-sided half of
// model_lit_foliage_glow without the plant-specific defines.
render_mode ambient_light_disabled, depth_draw_opaque, cull_disabled;

#define MODEL_TWO_SIDED
#include "res://shaders/model_lit_body.gdshaderinc"
