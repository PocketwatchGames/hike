shader_type spatial;
// Alpha-BLEND variant of model_lit: identical lighting / AO / discovery body
// (the shared include), but defines MODEL_ALPHA_BLEND so the include feathers
// the mesh against the albedo alpha instead of scissoring it. For translucent
// overlay geometry worn by a character — the webbing skirt a Webbed actor
// drags around — where a cutout would turn soft strands into hard blobs.
//
// depth_draw_never + cull_disabled because that is what an overlay wants: it
// must not write depth over the body it hangs on, and both faces of the open
// cone should draw so the far side reads through the near one. Blend is
// Godot's default blend_mix.
//
// Same lack of stencil as the BASE model_lit — an overlay is not a
// discovery-silhouette target of its own.
render_mode ambient_light_disabled, depth_draw_never, cull_disabled;

#define MODEL_ALPHA_BLEND
#include "res://shaders/model_lit_body.gdshaderinc"
