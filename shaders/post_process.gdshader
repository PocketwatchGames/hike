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

// Night vision — screenspace "see in the dark" sell. Player.cs pushes this
// global in 0..1 (the NightVision-stat degree) every frame via
// RenderingServer.GlobalShaderParameterSet; it's declared in project.godot's
// [shader_globals] and seeded to 0 in ChunkManager. At 0 the effect is an
// exact no-op. This is PURELY visual — the gameplay (the player actually
// perceiving mobs/loot in the dark) lives in PlayerPerception, independent
// of this. Doing it here (once, on the final image) instead of in every lit
// material keeps the lighting shaders clean.
global uniform float night_vision;

// Directional motion blur. Reusable channel — any system that needs to
// smear the framebuffer in a screen-space direction can drive these. Today
// it's the camera rotation (set in GameCamera.RotateLeft/Right and decayed
// in UpdateCamera); a planned "fly up to overview" effect will share it.
// When `motion_blur_strength` is 0, the loop is skipped and the cost is
// a single sample.
uniform float motion_blur_strength : hint_range(0.0, 1.0) = 0.0;
uniform vec2 motion_blur_dir = vec2(1.0, 0.0);

// Radial zoom blur, driven 0..1 by SlowMotionController during the death-cam
// punch-in. Samples along the line from the screen centre toward each pixel
// (scaling UVs inward) so the smear reads as a fast zoom rather than a
// directional pan — the right shape for a camera lunging forward. When 0 the
// loop is skipped. Takes precedence over the directional blur above; the two
// don't co-occur in practice.
uniform float radial_blur_strength : hint_range(0.0, 1.0) = 0.0;

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

// Screenspace flash. A brief full-screen wash toward `screen_flash_color`,
// driven 0..1 by ScreenEffectsController.Flash and decayed there. A reusable
// channel any effect can punch (lightning, a fairy winking out, an explosion):
// the trigger picks the color + peak intensity, the controller fades it. Sits
// above the vignette so it reads as a true full-screen flash, edges included.
uniform vec4 screen_flash_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float screen_flash : hint_range(0.0, 1.0) = 0.0;

// Both pushed pre-scaled from GameClient — the C# side maps the player's
// health fraction through the threshold ramp + per-component max, so the
// shader just applies whatever 0..1 it's handed.
uniform float low_health_desaturation : hint_range(0.0, 1.0) = 0.0;
uniform float low_health_dim : hint_range(0.0, 1.0) = 0.0;

// Heartbeat thump tint. GameClient drives `low_health_pulse` 0..1 on a
// lub-dub cadence (faster the closer to death; decelerating to a stop on
// death), already pre-scaled by its max. Each thump washes the
// (desaturated) scene toward `low_health_pulse_color` — a faint blood-red
// surge synced with the heartbeat SFX. The desaturation itself is also
// breathed by C# (color bleeds back a touch on each thump) so the pulse
// reads even on a fully grey screen.
uniform float low_health_pulse : hint_range(0.0, 1.0) = 0.0;
uniform vec3 low_health_pulse_color : source_color = vec3(0.5, 0.0, 0.0);

void fragment()
{
	vec3 scene;
	if (radial_blur_strength > 0.0)
	{
		// Radial zoom blur centred on screen: sample the framebuffer along the
		// line through the centre, scaling UVs symmetrically inward AND outward
		// around the current pixel. Symmetric so one path reads correctly for
		// both a zoom-in (slow-mo death cam) and a zoom-out (bird's-eye fly-up).
		// MAX_SPREAD is the total radial scale span at full strength (~10%).
		const int RSAMPLES = 8;
		const float MAX_SPREAD = 0.1;
		float spread = MAX_SPREAD * radial_blur_strength;
		vec2 toCenter = SCREEN_UV - 0.5;
		vec3 accum = vec3(0.0);
		for (int i = 0; i < RSAMPLES; i++)
		{
			// t in [-0.5, 0.5] → s in [1 - spread/2, 1 + spread/2].
			float t = (float(i) / float(RSAMPLES - 1)) - 0.5;
			float s = 1.0 + spread * t;
			accum += texture(screen_tex, 0.5 + toCenter * s).rgb;
		}
		scene = accum / float(RSAMPLES);
	}
	else if (motion_blur_strength > 0.0)
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

	// Night vision: lift the dark end of the image toward a dim mid and wash
	// the lifted pixels toward grey, leaving bright pixels and true black
	// alone. Applied to the scene first so the low-health dim / vignette
	// below still layer on top of the night-vision'd image.
	if (night_vision > 0.0)
	{
		// Brightness = max channel (HSV "value").
		float v = max(scene.r, max(scene.g, scene.b));
		if (v > 0.0)
		{
			const float MID = 0.5;
			// Below this brightness the lift tapers to nothing, so pitch
			// black and the deepest shadow stay dark (you can't see in true
			// darkness). Small because this acts on the final tonemapped
			// image, where even dim ambient already sits above near-zero.
			const float FLOOR = 0.04;
			// Caps the lift at full degree (1.0). Quadratic in the degree so
			// weak night vision stays subtle and strong reserves the big lift.
			const float MAX_STRENGTH = 0.6;
			float response = night_vision * night_vision * MAX_STRENGTH;
			// lifted = sqrt(MID * v): raises v toward MID for v < MID; for
			// v >= MID it's <= v so max() clamps back to identity (no seam,
			// bright pixels untouched).
			float lifted = max(v, sqrt(MID * v));
			float gate = smoothstep(0.0, FLOOR, v);
			float amount = response * gate;
			float target = mix(v, lifted, amount);
			vec3 boosted = scene * (target / v);
			// Scotopic desaturation, scaled by how hard this pixel was lifted
			// (lift_frac = 1 - v/target, 0 when unlifted). Bright pixels keep
			// their colour; the dim stuff we crank up reads near-grey.
			const float DESAT_STRENGTH = 0.6;
			float lift_frac = 1.0 - v / target;
			float luma = dot(boosted, vec3(0.299, 0.587, 0.114));
			scene = mix(boosted, vec3(luma), lift_frac * DESAT_STRENGTH);
		}
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
	if (low_health_pulse > 0.0)
	{
		scene = mix(scene, low_health_pulse_color, low_health_pulse);
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

	if (screen_flash > 0.0)
	{
		scene = mix(scene, screen_flash_color.rgb, screen_flash * screen_flash_color.a);
	}

	COLOR = vec4(scene, 1.0);
}
