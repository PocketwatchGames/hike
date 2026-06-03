shader_type spatial;
// Character / discoverable variant of model_lit: identical body, but stamps
// stencil = 8 on every written pixel (same contract + value as
// sprite_lit_character). The X-ray next_pass — model_xray, wired on the
// material — declares `stencil_mode read, compare_not_equal, 8`, so it only
// draws at pixels NOT stamped by another character. That suppresses
// character-over-character X-ray false positives while still firing through
// voxel terrain, sprite_lit / model_lit props (which leave stencil 0), etc.
//
// Pair this shader with a model_xray next_pass on the material (see
// forest_bunny_lit.tres). Used by model mobs and mesh interactives that want
// to silhouette through cover.
render_mode ambient_light_disabled, depth_draw_opaque, cull_back;
stencil_mode write, compare_always, 8;

#include "res://shaders/model_lit_body.gdshaderinc"
