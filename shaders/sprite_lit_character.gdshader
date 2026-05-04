shader_type spatial;
// Sprite_lit variant for player + mobs + interactives. Identical body to
// sprite_lit.gdshader (shared via include); the only difference is the
// `stencil_mode write` line below.
//
// Stencil contract: every pixel this base pass writes (i.e. every visible
// pixel of a character or interactive sprite, after alpha-cut + depth test)
// stamps the stencil buffer with value 8. The next_pass X-ray shader
// declares `stencil_mode read, compare_not_equal, 8` so it only runs at
// pixels NOT stamped by another character/interactive. That's how we
// suppress player-vs-campfire sprite-on-sprite false positives without
// changing any depth comparison logic — voxel terrain and sprite_lit
// props leave stencil at 0, so X-ray still fires correctly behind them.
//
// Stencil value 8 was picked because 2 (water backface) and 4 (water
// surface) are already in use by voxel_water* / *_reflection. Treat all
// three as raw values, not bit flags — the existing readers do exact
// equality compares, and overwriting stencil=4 with 8 at a character
// pixel that happens to sit over water is fine because the reflection /
// water-clip-cap passes only care about the water surface itself.
render_mode depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform;
stencil_mode write, compare_always, 8;

#include "res://shaders/sprite_lit_body.gdshaderinc"
