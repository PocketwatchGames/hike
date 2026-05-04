shader_type spatial;
// Flat-on-ground variant of sprite_lit. Same opaque queue + cull rationale
// as sprite_lit.gdshader; the body include differs (flat vertex stage
// laying the quad in the world XZ plane, camera-yawed). Fragment + light
// math is identical via sprite_lit_common.gdshaderinc.
//
// No stencil mode here — that's a sprite_lit_character concern (player /
// mobs / interactives that want X-ray when occluded). If a flat sprite
// later needs X-ray a sibling sprite_lit_flat_character.gdshader can be
// authored alongside it.
render_mode depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform;

#include "res://shaders/sprite_lit_flat_body.gdshaderinc"
