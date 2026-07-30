shader_type spatial;
// Roof variant of model_lit. Same lighting, plus the large-surface detail set:
// a tangent-space normal map (+ optional packed height), a low-frequency second
// albedo layer to break up tiling, and a hole mask for derelict roofs. See the
// MODEL_SURFACE_DETAIL block in the shared body for what each uniform does.
//
// A wrapper rather than a fork: the lighting model is duplicated across ~18
// shaders in this project already, and forking it again would be a nineteenth
// copy to keep in step.
render_mode ambient_light_disabled, depth_draw_opaque, cull_back;

#define MODEL_SURFACE_DETAIL
#include "res://shaders/model_lit_body.gdshaderinc"
