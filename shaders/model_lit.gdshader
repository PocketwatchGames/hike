shader_type spatial;
// Lit shader for 3D skinned-character / prop meshes. Thin wrapper — all logic
// (lighting, AO, discovery dither/silhouette) lives in model_lit_body.gdshaderinc.
//
// This BASE variant writes no stencil, so the player's / mobs' X-ray passes
// treat meshes drawn with it (trees, statues, signs, chests-as-cover) as
// ordinary occluders to silhouette through — the same role sprite_lit plays
// (vs sprite_lit_character) for billboards. Mobs / discoverable meshes that
// want their OWN through-cover silhouette use model_lit_character instead.
render_mode ambient_light_disabled, depth_draw_opaque, cull_back;

// Props take part, gated on the mesh origin sitting above the cut plane —
// ground-standing props are exempt, elevated ones are not. Characters
// (model_lit_character, model_xray) deliberately do NOT define this.
#define CLIP_FAN_PARTICIPATES
#include "res://shaders/model_lit_body.gdshaderinc"
