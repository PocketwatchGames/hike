shader_type canvas_item;

// Fullscreen post-process. Composites in this order over the rendered
// scene sampled from the framebuffer:
//   1. low-health desaturation + dim (drive both pre-scaled from C#)
//   2. red damage-flash tint shaped by an optional vignette texture
//   3. authored edge vignette (the original responsibility of this pass)
//
// The PostProcess CanvasLayer sits at layer 0, below the HUD's default
// layer 1, so SCREEN_TEXTURE only carries the 3D world (BloomQuad
// upscaled output) — the HUD never gets desaturated or red-tinted.

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;

uniform float vignette_radius : hint_range(0.0, 1.0) = 0.55;
uniform float vignette_softness : hint_range(0.0, 1.0) = 0.45;
uniform float vignette_strength : hint_range(0.0, 1.0) = 0.5;

// Damage flash. `damage_flash` ramps 0..1 and decays in GameClient; the
// texture's alpha shapes WHERE the red lands (edge-heavy vignette PNG
// makes the screen "bleed in from the edges"). Default-white means an
// unconfigured shader still flashes a uniform red, so authoring the
// texture later is a pure improvement.
uniform sampler2D damage_flash_tex : hint_default_white, filter_linear;
uniform vec4 damage_flash_color : source_color = vec4(1.0, 0.05, 0.05, 1.0);
uniform float damage_flash : hint_range(0.0, 1.0) = 0.0;

// Both pushed pre-scaled from GameClient — the C# side maps the player's
// health fraction through the threshold ramp + per-component max, so the
// shader just applies whatever 0..1 it's handed.
uniform float low_health_desaturation : hint_range(0.0, 1.0) = 0.0;
uniform float low_health_dim : hint_range(0.0, 1.0) = 0.0;

void fragment()
{
	vec3 scene = texture(screen_tex, SCREEN_UV).rgb;

	if (low_health_desaturation > 0.0)
	{
		float luma = dot(scene, vec3(0.299, 0.587, 0.114));
		scene = mix(scene, vec3(luma), low_health_desaturation);
	}
	if (low_health_dim > 0.0)
	{
		scene *= 1.0 - low_health_dim;
	}

	if (damage_flash > 0.0)
	{
		vec4 mask = texture(damage_flash_tex, SCREEN_UV);
		float a = mask.a * damage_flash_color.a * damage_flash;
		scene = mix(scene, damage_flash_color.rgb * mask.rgb, a);
	}

	vec2 centered = SCREEN_UV - 0.5;
	float dist = length(centered) * 1.4142136;
	float v = smoothstep(vignette_radius, vignette_radius + vignette_softness, dist);
	scene = mix(scene, vec3(0.0), v * vignette_strength);

	COLOR = vec4(scene, 1.0);
}
