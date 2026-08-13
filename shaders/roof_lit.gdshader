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
// Roofs are the largest single surface on screen, so they get the growth
// overlay (moss/snow) on top of the detail set.
#define MODEL_SURFACE_OVERLAY
// A roof IS architecture, unlike every other model_lit_body consumer.
// A roof is one object over one space, so it takes the column cutaway as a unit.
// Roof.UpdateClipParticipation picks the value from the footprint column nearest
// the player; a fragment can only see itself, and per-fragment participation
// leaves the roof standing in the shape of the walls.
#define CLIP_PARTICIPATION_INSTANCE
#include "res://shaders/model_lit_body.gdshaderinc"
