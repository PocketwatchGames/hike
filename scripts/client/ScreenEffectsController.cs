using Godot;

// Owns the full-screen post-process pass: vignette, motion-blur compositing,
// the red damage flash, and the low-health overlay (desaturate + dim +
// heartbeat thump with synced SFX). GameClient ticks this once per visual
// frame, feeding the current transition blur (bird's-eye fly-up / camera
// rotation); damage and death arrive via FlashDamage / NotifyPlayerDied.
// The live player and camera are read through GameClient.Current.
[GlobalClass]
public partial class ScreenEffectsController : Node
{
	[Export] public ShaderMaterial postProcessMaterial;

	// Global access for any effect that wants to punch a screen flash —
	// ItemEvent.ScreenFlash, the ScreenFlashEmitter node dropped into an Fx
	// scene, weather lightning, etc. Mirrors GameCamera.Current.
	public static ScreenEffectsController Current { get; private set; }

	[ExportGroup("Screen Flash")]
	// Default fade time (peak → 0) for a triggered flash when the caller doesn't
	// pass its own. The channel is colorless here — callers pick the color, so
	// one flash serves lightning, pickups, the fairy's death, and so on.
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float screenFlashFadeSeconds = 0.3f;

	[ExportGroup("Damage Feedback")]
	// Red-flash intensity = (damage / maxHealth) * scale, clamped to 1.
	// A scale of 2 means a 50% chunk drives the flash to its max; tune up
	// to make smaller chips more visible.
	[Export(PropertyHint.Range, "0.1,8,0.1")] public float damageFlashScale = 2f;
	// Seconds for the flash to decay from 1 → 0. Decay is linear; tune by
	// feel against the typical hit cadence.
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float damageFlashFadeSeconds = 0.4f;
	// Optional vignette mask. When null the shader's hint_default_white
	// drives a uniform red overlay — assign a soft-edged radial PNG to
	// make damage "bleed in from the screen edges".
	[Export] public Texture2D damageFlashTexture;
	[Export] public Color damageFlashColor = new Color(1f, 0.05f, 0.05f, 1f);
	// Health fraction below which the low-health overlay starts to ramp.
	// 0.333 = enters at 1/3 health; at 0 health the overlay is full
	// strength against the per-component max below.
	[Export(PropertyHint.Range, "0,1,0.01")] public float lowHealthThreshold = 1f / 3f;
	// Maximum desaturation and dim at 0 health. The ramp from
	// lowHealthThreshold → 0 health interpolates these toward 0 → max.
	[Export(PropertyHint.Range, "0,1,0.01")] public float lowHealthMaxDesaturation = 0.85f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float lowHealthMaxDim = 0.35f;
	// The whole low-health overlay (desat + dim + heartbeat) only lingers for
	// a window after the last hit, then fades out — so a player who survived a
	// scare isn't stuck staring at a grey screen. Taking damage refills the
	// timer to full, snapping the effect back to its nearness-to-death
	// intensity. This is the fade duration / window length in seconds.
	[Export(PropertyHint.Range, "1,30,0.5")] public float lowHealthEffectSeconds = 10f;

	// Heartbeat thump on the low-health overlay. Once the ramp is active the
	// screen pulses on a lub-dub cadence — the desaturation breathes (color
	// bleeds back a touch per thump) and a faint red tint surges — at a rate
	// that climbs from `Slow` at the threshold to `Fast` at 0 health. The
	// heartbeat SFX retriggers on each cycle.
	[Export(PropertyHint.Range, "20,200,1")] public float lowHealthHeartbeatSlowBpm = 55f;
	[Export(PropertyHint.Range, "20,260,1")] public float lowHealthHeartbeatFastBpm = 150f;
	// Fraction of the current desaturation the world's color regains at the
	// peak of each thump — the visible "breath" of the pulse.
	[Export(PropertyHint.Range, "0,1,0.01")] public float lowHealthHeartbeatDesaturationPulse = 0.35f;
	// Peak red tint mixed in at the crest of each thump.
	[Export(PropertyHint.Range, "0,1,0.01")] public float lowHealthHeartbeatRedTint = 0.22f;
	[Export] public Color lowHealthHeartbeatColor = new Color(0.5f, 0f, 0f);
	// On death the heartbeat decelerates from its live rate to a full stop —
	// and the thump tint fades out — over this window. Sourced from the
	// DeathScreen's fade-out time when one is wired so the heart and the
	// screen wind down together; this is the fallback when none is.
	[Export(PropertyHint.Range, "0.5,8,0.1")] public float lowHealthDeathSlowdownSeconds = 3f;
	// Heartbeat SFX, retriggered once per lub-dub cycle. Non-spatial (the
	// player's own heart) — wired to an AudioStreamPlayer on the Master bus
	// so the DeathScreen's World3D fade doesn't silence it mid-wind-down.
	[Export] public AudioStreamPlayer heartbeatAudio;
	[Export(PropertyHint.Range, "-40,6,0.5")] public float lowHealthHeartbeatVolumeDb = -4f;
	// Pitch climbs toward this at 0 health (adrenaline), then drifts down as
	// the heartbeat slows to a stop on death.
	[Export(PropertyHint.Range, "1,2,0.01")] public float lowHealthHeartbeatMaxPitch = 1.2f;

	[ExportGroup("Heartbeat Shape")]
	// Lub-dub envelope shape, in cycle-phase units. The lub sits at phase 0
	// (cycle boundary, where the SFX fires); the quieter dub follows shortly
	// after. Each is a smooth cosine bump of the given half-width.
	[Export(PropertyHint.Range, "0.01,0.4,0.005")] public float heartbeatLubWidth = 0.07f;
	[Export(PropertyHint.Range, "0.05,0.5,0.005")] public float heartbeatDubOffset = 0.2f;
	[Export(PropertyHint.Range, "0.01,0.4,0.005")] public float heartbeatDubWidth = 0.06f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float heartbeatDubStrength = 0.65f;
	// Pitch floor the slowing heartbeat sags toward as it dies out.
	[Export(PropertyHint.Range, "0.2,1,0.01")] public float heartbeatDeathPitch = 0.7f;
	// Bus-relative volume floor the dying heartbeat fades toward.
	[Export(PropertyHint.Range, "-60,0,1")] public float heartbeatDeathVolumeDb = -30f;

	// Red damage-flash intensity in [0, 1]. Bumped by FlashDamage on every
	// player hit (direct + DOT rollup), decayed linearly each frame so the
	// flash fades over damageFlashFadeSeconds.
	float _damageFlash;

	// Generic screen-flash state. Intensity ramps to a peak on Flash() and
	// decays linearly each frame over _screenFlashFadeActive seconds toward 0.
	float _screenFlash;
	Color _screenFlashColor = Colors.White;
	float _screenFlashFadeActive = 0.3f;

	// Heartbeat pulse state. `_heartbeatPhase` is the position in the current
	// lub-dub cycle in [0, 1); a cycle boundary retriggers the SFX. While the
	// player is alive the rate tracks the low-health ramp; on death we latch
	// the live rate and ease it (and the pulse amplitude) to zero over the
	// death-slowdown window, so the thump-thump audibly winds down.
	float _heartbeatPhase;
	bool _heartbeatActive;
	float _heartbeatLiveRate;
	bool _heartbeatDying;
	float _heartbeatDeathElapsed;
	float _heartbeatDeathStartRate;
	float _heartbeatDeathSlowdown;

	// Counts down from lowHealthEffectSeconds; refilled on every hit. The
	// normalized value (eased) is the master multiplier on the whole
	// low-health overlay, so it fades out a few seconds after the last hit.
	float _lowHealthEffectTimer;

	public override void _EnterTree()
	{
		Current = this;
	}

	public override void _ExitTree()
	{
		if (Current == this)
		{
			Current = null;
		}
	}

	// Trigger a one-shot full-screen flash toward `color`. `intensity` (0..1) is
	// the peak; `fadeSeconds` <= 0 falls back to screenFlashFadeSeconds. Stacks
	// max-of with any in-progress flash so a follow-up doesn't dim an active one.
	public void Flash(Color color, float intensity = 1f, float fadeSeconds = -1f)
	{
		intensity = Mathf.Clamp(intensity, 0f, 1f);
		if (intensity <= 0f) { return; }
		_screenFlashColor = color;
		_screenFlashFadeActive = fadeSeconds > 0f ? fadeSeconds : screenFlashFadeSeconds;
		if (intensity > _screenFlash)
		{
			_screenFlash = intensity;
		}
	}

	// Bumps the damage flash by the hit fraction of max health, scaled by
	// damageFlashScale and capped at 1. Stacks with whatever is already in
	// the buffer (max-of) so a follow-up hit during a fade doesn't shrink
	// the flash. Called from Player.OnHurtBoxHit (direct) and from
	// _PhysicsProcess after each DOT HUD flush, via GameClient.FlashDamage.
	public void FlashDamage(float amount)
	{
		Player player = GameClient.Current?.Player;
		if (amount <= 0f || player == null) { return; }
		float maxHealth = player.MaxHealth;
		if (maxHealth <= 0f) { return; }
		// Any hit refills the low-health overlay window — the effect snaps back
		// to full and resumes its nearness-to-death intensity (the ramp is
		// recomputed live from current health).
		_lowHealthEffectTimer = lowHealthEffectSeconds;
		float intensity = Mathf.Clamp(amount / maxHealth * damageFlashScale, 0f, 1f);
		if (intensity > _damageFlash)
		{
			_damageFlash = intensity;
		}
	}

	// Hand the heartbeat over to its death wind-down: latch the live rate
	// (fall back to the fast-BPM rate if the player died before the overlay
	// was ramping, e.g. a one-shot kill) and let UpdateHeartbeat decelerate it
	// to a stop. `deathScreenFadeOutSeconds` syncs the slowdown to the
	// DeathScreen fade; pass <= 0 to use lowHealthDeathSlowdownSeconds.
	public void NotifyPlayerDied(float deathScreenFadeOutSeconds)
	{
		_heartbeatDying = true;
		_heartbeatDeathElapsed = 0f;
		// Refill the window so the death wind-down is always at full strength,
		// even if the killing blow landed after the overlay had faded out.
		_lowHealthEffectTimer = lowHealthEffectSeconds;
		_heartbeatDeathStartRate = _heartbeatLiveRate > 0f ? _heartbeatLiveRate : lowHealthHeartbeatFastBpm / 60f;
		_heartbeatDeathSlowdown = deathScreenFadeOutSeconds > 0f
			? deathScreenFadeOutSeconds
			: lowHealthDeathSlowdownSeconds;
	}

	// Clear the death wind-down so the heartbeat goes fully idle (health is
	// restored, so the overlay ramp is 0); a fresh low-health episode will
	// re-engage it from scratch.
	public void ResetOnRespawn()
	{
		_heartbeatDying = false;
		_heartbeatActive = false;
		_heartbeatDeathElapsed = 0f;
		_lowHealthEffectTimer = 0f;
	}

	// Per-frame post-process update. `transitionBlur` / `transitionBlurDir`
	// carry the bird's-eye fly-up smear (0 when not transitioning); the camera
	// rotation blur is composited in via max-of.
	public void Tick(double deltaTime, float transitionBlur, Vector2 transitionBlurDir)
	{
		if (postProcessMaterial == null) { return; }
		GameClient client = GameClient.Current;
		GameCamera camera = client?.camera;
		Player player = client?.Player;

		postProcessMaterial.SetShaderParameter("vignette_radius", CVars.vignetteRadius.Value);
		postProcessMaterial.SetShaderParameter("vignette_softness", CVars.vignetteSoftness.Value);
		postProcessMaterial.SetShaderParameter("vignette_strength", CVars.vignetteStrength.Value);

		// Motion blur — combined max-of between the camera's rotation blur
		// (decays over rotationBlurDuration after a Q/E press) and the supplied
		// transition blur. The CVar gates only the rotation source so the
		// bird's-eye effect runs even when rotation blur is disabled. When
		// `motion_blur_strength` is 0 the shader skips the blur loop, so idle
		// frames pay nothing.
		float rotBlur = (CVars.rotationBlur.Value && camera != null) ? camera.RotationBlurStrength : 0f;
		float blurStrength = Mathf.Max(rotBlur, transitionBlur);
		Vector2 blurDir = transitionBlur > rotBlur || camera == null ? transitionBlurDir : camera.RotationBlurDir;
		postProcessMaterial.SetShaderParameter("motion_blur_strength", blurStrength);
		postProcessMaterial.SetShaderParameter("motion_blur_dir", blurDir);

		float dt = (float)deltaTime;
		if (_damageFlash > 0f && damageFlashFadeSeconds > 0f)
		{
			_damageFlash = Mathf.Max(0f, _damageFlash - dt / damageFlashFadeSeconds);
		}
		postProcessMaterial.SetShaderParameter("damage_flash", _damageFlash);
		postProcessMaterial.SetShaderParameter("damage_flash_color", damageFlashColor);
		// SetShaderParameter accepts a null Texture2D — the shader's
		// hint_default_white kicks in and the flash paints uniformly red.
		postProcessMaterial.SetShaderParameter("damage_flash_tex", damageFlashTexture);

		// Generic screen flash — decay toward 0 and push every frame (including
		// 0) so a finished flash clears.
		if (_screenFlash > 0f && _screenFlashFadeActive > 0f)
		{
			_screenFlash = Mathf.Max(0f, _screenFlash - dt / _screenFlashFadeActive);
		}
		postProcessMaterial.SetShaderParameter("screen_flash", _screenFlash);
		postProcessMaterial.SetShaderParameter("screen_flash_color", _screenFlashColor);

		// Low-health overlay. Ramp = (threshold - healthFrac) / threshold
		// so at threshold the ramp is 0, at 0 health the ramp is 1. Each
		// component (desat, dim) is sent pre-scaled by its max so the
		// shader just applies a 0..1.
		float ramp = 0f;
		if (player != null && lowHealthThreshold > 0f)
		{
			float maxHealth = player.MaxHealth;
			if (maxHealth > 0f)
			{
				float frac = Mathf.Clamp(player.Health / maxHealth, 0f, 1f);
				ramp = Mathf.Clamp((lowHealthThreshold - frac) / lowHealthThreshold, 0f, 1f);
			}
		}
		// Damage-gated fade. The overlay only lingers for a window after the
		// last hit (refilled in FlashDamage); past that it eases out. Eased
		// with smoothstep so it holds near full for most of the window and
		// drops off toward the end rather than dimming the whole time.
		_lowHealthEffectTimer = Mathf.Max(0f, _lowHealthEffectTimer - dt);
		float fade = lowHealthEffectSeconds > 0f
			? Mathf.SmoothStep(0f, 1f, _lowHealthEffectTimer / lowHealthEffectSeconds)
			: 0f;

		// Heartbeat thump. Active whenever the overlay is showing or the death
		// wind-down is still running. `pulse` is the lub-dub envelope scaled by
		// the death amplitude AND the damage-gated fade; it breathes the
		// desaturation (color bleeds back) and feeds the shader's red-tint
		// surge. The cadence still tracks nearness to death — only the
		// amplitude/volume fades with the window.
		float pulse = UpdateHeartbeat(dt, ramp, fade);
		float baseDesat = ramp * lowHealthMaxDesaturation * fade;
		float desat = baseDesat * (1f - pulse * lowHealthHeartbeatDesaturationPulse);
		postProcessMaterial.SetShaderParameter("low_health_desaturation", desat);
		postProcessMaterial.SetShaderParameter("low_health_dim", ramp * lowHealthMaxDim * fade);
		postProcessMaterial.SetShaderParameter("low_health_pulse", pulse * lowHealthHeartbeatRedTint);
		postProcessMaterial.SetShaderParameter("low_health_pulse_color",
			new Vector3(lowHealthHeartbeatColor.R, lowHealthHeartbeatColor.G, lowHealthHeartbeatColor.B));
	}

	// Advances the heartbeat phase and returns the current lub-dub envelope
	// value in [0, 1] (already scaled by the death-wind-down amplitude and the
	// damage-gated `fade`). The heartbeat is live while `ramp` > 0 and the fade
	// window is open; on death it ignores both and decelerates the latched rate
	// to a stop. Retriggers the SFX on each cycle boundary. Returns 0 when idle.
	float UpdateHeartbeat(float dt, float ramp, float fade)
	{
		bool active = (ramp > 0f && fade > 0f) || _heartbeatDying;
		if (!active)
		{
			_heartbeatActive = false;
			return 0f;
		}

		// Per-frame rate (cycles/sec) and the amplitude/pitch envelope.
		float rate;
		float amplitude;
		float pitch;
		if (_heartbeatDying)
		{
			_heartbeatDeathElapsed += dt;
			float t = _heartbeatDeathSlowdown > 0f
				? Mathf.Clamp(_heartbeatDeathElapsed / _heartbeatDeathSlowdown, 0f, 1f)
				: 1f;
			// Ease-out so the deceleration is steep at first then crawls to a
			// halt — reads as a heart giving out rather than a linear ramp.
			float ease = 1f - (t * t);
			rate = _heartbeatDeathStartRate * ease;
			amplitude = ease;
			pitch = Mathf.Lerp(heartbeatDeathPitch, 1f, ease);
		}
		else
		{
			float bpm = Mathf.Lerp(lowHealthHeartbeatSlowBpm, lowHealthHeartbeatFastBpm, ramp);
			rate = bpm / 60f;
			_heartbeatLiveRate = rate;
			amplitude = 1f;
			pitch = Mathf.Lerp(1f, lowHealthHeartbeatMaxPitch, ramp);
		}
		// The damage-gated window fades the heartbeat's loudness/strength
		// without touching its cadence. Death refills the window, so the
		// wind-down always plays at full.
		amplitude *= fade;

		// Fire the first beat the instant the overlay engages, then on every
		// cycle wrap. New beats stop once the dying rate has crawled to zero.
		bool beat = false;
		if (!_heartbeatActive)
		{
			_heartbeatActive = true;
			_heartbeatPhase = 0f;
			beat = true;
		}
		else
		{
			_heartbeatPhase += rate * dt;
			if (_heartbeatPhase >= 1f)
			{
				_heartbeatPhase -= Mathf.Floor(_heartbeatPhase);
				beat = true;
			}
		}

		if (beat && heartbeatAudio != null && amplitude > 0.02f)
		{
			heartbeatAudio.PitchScale = pitch;
			heartbeatAudio.VolumeDb = Mathf.Lerp(heartbeatDeathVolumeDb, lowHealthHeartbeatVolumeDb, amplitude);
			heartbeatAudio.Play();
		}

		return HeartbeatEnvelope(_heartbeatPhase) * amplitude;
	}

	// Two smooth cosine bumps per cycle — the loud lub at the boundary and a
	// softer dub just after — forming the thump-thump shape.
	float HeartbeatEnvelope(float phase)
	{
		float lub = HeartbeatBump(phase, 0f, heartbeatLubWidth);
		float dub = HeartbeatBump(phase, heartbeatDubOffset, heartbeatDubWidth) * heartbeatDubStrength;
		return Mathf.Max(lub, dub);
	}

	// Cosine bump centered at `center` (cycle-wrapped) with the given
	// half-width: 1 at the center, smoothly to 0 at ±width, 0 beyond.
	static float HeartbeatBump(float phase, float center, float width)
	{
		float d = Mathf.Abs(phase - center);
		d = Mathf.Min(d, 1f - d);
		if (d >= width)
		{
			return 0f;
		}
		return 0.5f * (1f + Mathf.Cos(d / width * Mathf.Pi));
	}
}
