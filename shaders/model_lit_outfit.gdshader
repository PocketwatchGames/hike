shader_type spatial;
// model_lit_character for recolourable clothing: `albedo_tex` is the polysplit
// genericRGB region mask and the three per-instance outfit_* colours replace
// it (see MODEL_RGB_MASK in the body). Same stencil contract as
// model_lit_character so the model_xray next_pass treats it as a character.
render_mode ambient_light_disabled, depth_draw_opaque, cull_back;
stencil_mode write, compare_always, 8;

#define MODEL_RGB_MASK
#include "res://shaders/model_lit_body.gdshaderinc"
