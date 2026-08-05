shader_type spatial;
// Alpha-cutout variant of model_lit: identical lighting / AO / discovery body
// (the shared include), but defines MODEL_ALPHA_CUTOUT so the include scissors
// the mesh against the atlas alpha channel. Used for the player's modular face
// features (eyes / brows / mouth), whose Synty atlas cells pack the sprite on a
// red + white separator background that must be keyed out so only the feature
// draws over the skin. Pair with a material whose atlas has alpha authored in
// the facial-feature band (see model_lit_face_cutout.tres / the keyed
// medievalTexture_bodyColor1 atlas).
//
// Same render_mode as the BASE model_lit (no stencil): these small features are
// ordinary occluders, not discovery-silhouette targets. Alpha scissor keeps the
// material in the opaque queue, so depth_draw_opaque is still correct.
render_mode ambient_light_disabled, depth_draw_opaque, cull_back;

#define MODEL_ALPHA_CUTOUT
// Props take part, gated on the mesh origin sitting above the cut plane —
// ground-standing props are exempt, elevated ones are not. Characters
// (model_lit_character, model_xray) deliberately do NOT define this.
#define CLIP_FAN_PARTICIPATES
#include "res://shaders/model_lit_body.gdshaderinc"
