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

// Directional motion blur. Reusable channel — any system that needs to
// smear the framebuffer in a screen-space direction can drive these. Today
// it's the camera rotation (set in GameCamera.RotateLeft/Right and decayed
// in UpdateCamera); a planned "fly up to overview" effect will share it.
// When `motion_blur_strength` is 0, the loop is skipped and the cost is
// a single sample.
uniform float motion_blur_strength : hint_range(0.0, 1.0) = 0.0;
uniform vec2 motion_blur_dir = vec2(1.0, 0.0);

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
	vec3 scene;
	if (motion_blur_strength > 0.0)
	{
		// Symmetric N-tap line blur along motion_blur_dir. Symmetric (not
		// trailing) so the smear reads as "the world is moving" without
		// committing to a leading edge — feels balanced against the eye's
		// expectation during a quick camera spin.
		const int SAMPLES = 6;
		// Max UV offset at full strength. 0.06 ≈ 6% of screen width, heavy
		// enough to fully obscure sprite-snap artifacts during rotation.
		const float MAX_SPREAD = 0.03;
		float spread = MAX_SPREAD * motion_blur_strength;
		vec3 accum = vec3(0.0);
		for (int i = 0; i < SAMPLES; i++)
		{
			float t = (float(i) / float(SAMPLES - 1)) - 0.5;
			vec2 offset = motion_blur_dir * t * spread;
			accum += texture(screen_tex, SCREEN_UV + offset).rgb;
		}
		scene = accum / float(SAMPLES);
	}
	else
	{
		scene = texture(screen_tex, SCREEN_UV).rgb;
	}

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
