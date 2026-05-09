shader_type spatial;
render_mode unshaded, blend_mix, depth_test_disabled, depth_draw_never, cull_disabled, fog_disabled;

// Fullscreen pass that warps SCREEN_TEXTURE UVs by a per-fragment heat
// value sampled from a player-centric 2D field. The field is populated
// in HeatField.cs from ambient air temperature (uniform baseline) and
// active WarmthZones (additive disks). This pass renders inside
// SceneViewport so it can use INV_PROJECTION_MATRIX / INV_VIEW_MATRIX
// for depth-based world-XZ reconstruction.
//
// Render order: this material has a low render_priority so it draws
// FIRST in the transparent pass. Godot 4 captures SCREEN_TEXTURE once
// at the start of the transparent pass, so anything drawn later (water,
// fog, sprites) won't appear in our SCREEN_TEXTURE sample — but those
// draws also haven't deposited their pixels yet, so they composite
// normally on top of the warped output. Trade-off: water and fog
// themselves don't get warped, but the opaque world (terrain, sprites,
// shadows) does.
//
// Sky pixels (depth = 1.0) and zero-heat pixels are discarded — both
// are identity passthroughs, and discarding leaves the rendered scene
// completely untouched there (no overwrite of pre-shimmer transparents).

global uniform sampler2D heat_field : repeat_disable, filter_linear;
global uniform vec2 heat_field_origin_xz;
global uniform float heat_field_size;

uniform sampler2D screen_tex : hint_screen_texture, repeat_disable, filter_linear;
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_nearest;

uniform float shimmer_amplitude : hint_range(0.0, 0.02) = 0.003;
uniform float shimmer_speed : hint_range(0.0, 20.0) = 4.0;
uniform float shimmer_noise_scale_x : hint_range(10.0, 200.0) = 80.0;
uniform float shimmer_noise_scale_y : hint_range(10.0, 200.0) = 60.0;

void vertex()
{
	// Pin the quad to the far plane in NDC regardless of mesh transform —
	// the standard fullscreen-pass trick. depth_test_disabled keeps it
	// drawing over the rest of the SubViewport's transparent pass.
	POSITION = vec4(VERTEX.xy * 2.0, 1.0, 1.0);
}

void fragment()
{
	vec2 uv = SCREEN_UV;
	float depth = texture(depth_tex, uv).r;

	// Sky pixels: identity passthrough. Discard rather than re-write the
	// sampled color so transparents drawn after this pass (water/fog/etc.)
	// composite onto the rendered scene unchanged.
	if (depth >= 0.9999)
	{
		discard;
	}

	// Reconstruct world position from non-linear depth.
	vec3 ndc = vec3(uv * 2.0 - 1.0, depth);
	vec4 view = INV_PROJECTION_MATRIX * vec4(ndc, 1.0);
	view.xyz /= max(view.w, 1e-6);
	vec3 world_pos = (INV_VIEW_MATRIX * vec4(view.xyz, 1.0)).xyz;

	// Sample heat at the reconstructed world XZ. Out-of-bounds clamps to
	// the field edge — the edge holds the ambient baseline so behavior is
	// continuous past the field's coverage radius.
	vec2 field_uv = clamp(
		(world_pos.xz - heat_field_origin_xz) / heat_field_size,
		vec2(0.0), vec2(1.0));
	float heat = texture(heat_field, field_uv).r;

	// Zero-heat pixels: identity passthrough, discard. This is the
	// common case (most of the screen, most of the time) and the
	// discard keeps the unwarped transparent pass intact across nearly
	// the whole frame.
	if (heat <= 0.0)
	{
		discard;
	}

	// Two-octave time-scrolling sin/cos noise. Vertical bias on the warp
	// so distortion reads as rising air rather than horizontal wobble.
	float t = TIME * shimmer_speed;
	vec2 nuv = vec2(uv.x * shimmer_noise_scale_x, uv.y * shimmer_noise_scale_y);
	float n1 = sin(nuv.x + t) * cos(nuv.y * 0.7 + t * 1.3);
	float n2 = sin(nuv.x * 1.7 - t * 0.5) * cos(nuv.y * 1.3 - t * 0.9);
	vec2 warp = vec2(n1 + n2 * 0.4, (n1 + n2) * 0.7) * shimmer_amplitude * heat;

	ALBEDO = textureLod(screen_tex, uv + warp, 0.0).rgb;
}
