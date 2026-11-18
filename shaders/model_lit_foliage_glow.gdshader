shader_type spatial;
// model_lit for alpha-cut, two-sided plant meshes whose glow follows an
// emission MASK (the glowing flowers). Pair every material on this shader with
// a StationaryLight on the prop — emission alone casts no light.
render_mode ambient_light_disabled, depth_draw_opaque, cull_disabled;

#define MODEL_ALPHA_CUTOUT
#define MODEL_TWO_SIDED
#define MODEL_EMISSION_MAP
#define MODEL_FOLIAGE_SWAY
#include "res://shaders/model_lit_body.gdshaderinc"
