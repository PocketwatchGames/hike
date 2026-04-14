shader_type canvas_item;

// Vignette-only overlay. DOF is handled by Godot's built-in
// CameraAttributesPractical.dof_blur_far_* (true post-process slot, blurs
// transparents like water correctly). This shader just darkens the edges.

uniform float vignette_radius : hint_range(0.0, 1.0) = 0.55;
uniform float vignette_softness : hint_range(0.0, 1.0) = 0.45;
uniform float vignette_strength : hint_range(0.0, 1.0) = 0.5;

void fragment()
{
	vec2 centered = SCREEN_UV - 0.5;
	float dist = length(centered) * 1.4142136;
	float v = smoothstep(vignette_radius, vignette_radius + vignette_softness, dist);
	COLOR = vec4(0.0, 0.0, 0.0, v * vignette_strength);
}
