shader_type spatial;
// model_lit plus the large-surface detail set: a tangent-space normal map (with
// optional packed height for crevice darkening) and a low-frequency second
// albedo layer. Identical lighting to model_lit — see the MODEL_SURFACE_DETAIL
// block in the shared body for what each uniform does.
//
// roof_lit already pairs that block with the roof-specific column cutaway and
// the derelict-hole mask; this is the plain variant, for meshes that want the
// normal map and nothing architectural. The procedural tree trunks are the first
// consumer (TreeTrunk generates tangents for exactly this).
//
// A wrapper rather than a fork, for the same reason roof_lit is one: the
// lighting model is already duplicated across ~18 shaders here and forking it
// again would be another copy to keep in step.
render_mode ambient_light_disabled, depth_draw_opaque, cull_back;

#define MODEL_SURFACE_DETAIL
#include "res://shaders/model_lit_body.gdshaderinc"
