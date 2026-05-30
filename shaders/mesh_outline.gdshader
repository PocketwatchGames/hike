shader_type spatial;
// Screen-space selection outline for SOLID 3D interactives (statue, sign, chest,
// ladder). The mesh analog of the sprite targeting outline — and the correct
// technique for concave models, where an inverted-hull outline fails (it traces
// every interior crease, not just the silhouette).
//
// How it works: GameCamera renders the currently-selected interactive's meshes
// into an off-screen "outline mask" SubViewport (camera synced to the main
// camera, culls to OutlineLayer only). The mask is alpha-coverage — geometry
// writes alpha 1, empty stays alpha 0 — so it's independent of the model's
// albedo (a black-textured fold doesn't punch a hole). This fullscreen quad
// (authored under MainCamera like FogQuad) then samples that mask: a pixel that
// is OUTSIDE the silhouette but within `outline_width` texels of an INSIDE
// pixel becomes the outline color. That paints a constant-width ring hugging
// exactly the model's screen silhouette, matching the sprite outline.
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, skip_vertex_transform, fog_disabled;

// Outline mask: alpha = silhouette coverage of the selected meshes. Set per
// frame from GameCamera (the mask SubViewport's texture). filter_nearest +
// inner-resolution mask keeps the ring chunky, matching the pixel-art look.
uniform sampler2D outline_mask_tex : filter_nearest;
uniform vec4 outline_color : source_color = vec4(1.0, 1.0, 0.8, 1.0);
// Ring thickness in mask texels (mask is at inner viewport resolution, so this
// is chunky pixels — 1 matches the sprite outline's 1-px ring).
uniform int outline_width = 1;

varying vec2 screen_uv;

void vertex() {
	// Fullscreen NDC quad (same technique as fog_volumetric's FogQuad): a
	// QuadMesh(2,2) has VERTEX.xy in [-1,1]; emit straight to clip space at the
	// far plane (z=1, w=1, no perspective divide) so it covers the whole screen.
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
	screen_uv = VERTEX.xy * 0.5 + 0.5;
}

void fragment() {
	vec2 texel = 1.0 / vec2(textureSize(outline_mask_tex, 0));
	// No V flip: the composite quad and the mask are both rendered by 3D cameras
	// of identical orientation into SubViewports, so screen_uv maps straight onto
	// the mask. (The upscale shader's flip is for inner→outer-camera sampling, a
	// different context — don't copy it here.)
	vec2 uv = screen_uv;

	float center = texture(outline_mask_tex, uv).a;
	// Inside the silhouette: let the model show through (no ring over the body).
	if (center >= 0.5) {
		discard;
	}

	// Outside: ring iff any neighbor within outline_width texels is inside.
	bool near_inside = false;
	int r = outline_width;
	for (int y = -r; y <= r; y++) {
		for (int x = -r; x <= r; x++) {
			if (x == 0 && y == 0) { continue; }
			vec2 o = vec2(float(x), float(y)) * texel;
			if (texture(outline_mask_tex, uv + o).a >= 0.5) {
				near_inside = true;
			}
		}
	}
	if (!near_inside) {
		discard;
	}

	ALBEDO = outline_color.rgb;
	ALPHA = outline_color.a;
}
