shader_type spatial;
// Same shading model as voxel_water, because a cascade is the same substance as
// the pool it pours out of: no diffuse albedo, a volumetric composite, and the
// sheet drawn double-sided so it is visible from behind too.
//
// It BLENDS, where voxel_water composites by hand against the screen texture,
// and that difference is a correctness fix rather than a style choice. Godot
// captures the screen copy ONCE, before the transparent pass — a transparent
// object never sees another transparent object in it, whatever their render
// priorities say. The pool is transparent, so a sheet sampling `screen_tex` got
// back the opaque scene BEHIND the pool, which is bare rock: a fall hanging in
// front of bright water drew the grey cliff over it, and no ordering could fix
// it because the copy predates both of them. Real blending reads the actual
// framebuffer.
//
// PREMULTIPLIED, so the water's own light is emitted at full value and ALPHA
// carries only how much of the background it hides — which is exactly the split
// water_compose already returns. What it gives up is screen-space refraction,
// and there is no way to have both: refraction needs the copy this cannot trust.
//
// It WRITES DEPTH, and it draws BEFORE both water passes (render_priority -6;
// the ordering table lives in ChunkMesh.cs where the water materials are built).
// Both waters used to be depth_draw_never, so nothing but the fixed priority
// band decided the order, and the sheet painted over every pool at every
// distance — wrong for a pool between the camera and a fall pouring away from
// it, for the near lip of a plunge basin, for one fall crossing another, and for
// the sheet's own back face under cull_disabled. Going first WITH depth hands
// all four to the depth buffer: the pool depth-tests against the sheet, so a
// pool nearer than it blends over it and a pool behind it is culled. That a
// transparent material can occlude another this way is measured, not assumed —
// `depth_sort_check` (windowed) is the one-bit test.
//
// The whole sheet, shoulder included. Exempting the shoulder was tried and is
// wrong: the sweep starts on the face BETWEEN the last pool voxel and the air
// voxel and only ever moves further along the pour, so it never sits above the
// pool's footprint and has no pool underneath it to protect. It shares
// water_shore_foam with the pool so the surf band does not stop dead at the lip,
// which is a matter of matching what the pool PAINTS — not of covering it.
// Left out of the depth write it stayed drawn over every pool, which is the
// whole artifact this exists to fix and the most visible part of it.
//
// What going first costs is the sheet's own residual transparency — 6..27% down
// the curtain, more at the brink — which now shows the opaque scene rather than
// the pool directly behind it. There is no order that gets both; this is a
// choice of which error to take, and the sheet is shaded as the same water
// anyway, so what it hides it also largely reproduces.
render_mode blend_premul_alpha, depth_draw_always, cull_disabled, unshaded;

#include "res://shaders/waterfall_body.gdshaderinc"
