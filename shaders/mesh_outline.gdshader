shader_type spatial;
// Selection outline for SOLID 3D interactives — the mesh analog of the sprite
// targeting outline (highlight_outline.gdshader). Wired as the next_pass on a
// 3D interactive's material (statue / sign / chest / ladder rungs). Inverted-
// hull outline: render BACK faces (cull_front) pushed outward along the normal,
// flooded with `outline_color`. The expanded shell is hidden behind the mesh's
// own front faces except at the silhouette rim, where it reads as the outline.
//
// SCREEN-CONSTANT WIDTH: the normal push is scaled by the vertex's view-space
// distance, so the rim is a roughly constant PIXEL thickness whether it wraps a
// 0.07m ladder rung or a 3m statue. A fixed world-space width was invisible
// (sub-pixel) on the small rungs while fine on the statue — distance scaling
// fixes both at once. `outline_width` is therefore an angular value (≈ screen
// fraction), not meters; tune it for pixels, not centimeters.
//
// `selected` is the per-instance gate driven by GameClient when this interactive
// is the player's current highlight target (mirrors how the sprite outline only
// shows on the selected interactive). At 0 the hull collapses AND the fragment
// discards, so non-selected interactives pay nothing.
//
// depth_draw_never: the shell still depth-TESTs so its interior hides behind the
// model's front faces and only the silhouette rim shows — but it doesn't write
// depth (it's a UI affordance, nothing should occlude against it).
render_mode unshaded, cull_front, ambient_light_disabled, depth_draw_never;

uniform vec4 outline_color : source_color = vec4(1.0, 1.0, 0.8, 1.0);
// Screen-constant outline thickness. World push at a vertex = outline_width *
// view_distance, so apparent PIXEL width stays fixed regardless of how far the
// camera is or how small the mesh is — a 0.07m ladder rung and a 3m statue get
// the same rim. This is the fix for the "outline is sub-pixel on the rungs" bug:
// a fixed world-space width (the old 0.03m) was ~half a pixel at this game's far,
// near-orthographic camera (~80m, 14° FOV). `outline_width` is an angular value
// (≈ screen fraction × 4), NOT meters — tune for pixels. ~0.0025 ≈ 2 chunky px.
uniform float outline_width : hint_range(0.0, 0.02) = 0.0025;
// Per-instance selection gate. 0 = no outline (collapsed hull + discard);
// GameClient sets 1 on the active highlight target.
instance uniform float selected : hint_range(0.0, 1.0) = 0.0;

void vertex() {
	// View-space distance of this vertex from the camera (positive = in front).
	// Scaling the push by it keeps the rim a constant pixel width under the
	// perspective camera. Divide by model scale because VERTEX is model-space, so
	// a 0.5-scaled prop pushes the same screen amount as an unscaled one.
	float view_dist = -(VIEW_MATRIX * MODEL_MATRIX * vec4(VERTEX, 1.0)).z;
	float model_scale = length(MODEL_MATRIX[0].xyz);
	float push = outline_width * max(view_dist, 0.0) / max(model_scale, 1e-4);
	VERTEX += normalize(NORMAL) * push * selected;
}

void fragment() {
	if (selected <= 0.001) {
		discard;
	}
	// Flat bright outline color via ALBEDO (matches the sprite outline; EMISSION
	// did not render in this unshaded path and came out black).
	ALBEDO = outline_color.rgb;
	EMISSION = vec3(0.0);
}
