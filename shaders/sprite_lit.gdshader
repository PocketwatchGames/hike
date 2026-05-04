shader_type spatial;
// Opaque queue (no blend_mix): the alpha cut in fragment() (discard if
// tex.a < 0.5) gives alpha-cut silhouettes without putting the sprite into
// the transparent queue. Transparent queue requires per-frame back-to-front
// sorting which prevents Godot from batching draws across spatially-spread
// sprites; opaque queue lets sprites sharing the same material batch into
// one draw call. The dither-fade in fragment() also uses discard, not
// partial alpha, so visibility stippling still works in the opaque queue.
// All current art is alpha-cut compatible — if a future sprite genuinely
// needs soft-alpha edges it'll need to opt into a separate blend_mix variant.
//
// Body is shared with sprite_lit_character.gdshader via the include. The
// character variant adds `stencil_mode write, compare_always, 8` so its
// pixels are flagged as "character/interactive" in the stencil buffer; the
// X-ray pass reads that flag to skip sprite-on-sprite occlusion. This file
// (props) writes no stencil so X-ray still fires correctly when the player
// is behind a tree / barrel / etc.
render_mode depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform;

#include "res://shaders/sprite_lit_body.gdshaderinc"
