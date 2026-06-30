using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	public void BeginBirdsEye()
	{
		if (_birdsEye)
		{
			return;
		}
		_birdsEye = true;
		onBirdsEye?.Invoke(true);
	}

	// Asks GameClient to begin the fly-down. The movement lock is held until
	// OnBirdsEyeReturnComplete fires from the camera driver.
	public void RequestEndBirdsEye()
	{
		if (!_birdsEye)
		{
			return;
		}
		onBirdsEye?.Invoke(false);
	}

	public void OnBirdsEyeReturnComplete()
	{
		_birdsEye = false;
		// A tree climb rides the bird's-eye lifecycle: the camera landing back
		// on the player is also when the player drops out of the canopy, so
		// restore the model and clear concealment here. Exit can be triggered
		// by ESC or by taking damage (see OnHurtBoxHit) — both route through
		// the fly-down, so this single restore covers every path.
		if (_hidden)
		{
			_hidden = false;
			SetModelVisible(true);
		}
	}

	// Bird's-eye lifts the audio listener off the player's head toward the
	// overview camera so ground-positional (World3D) audio recedes as the view
	// climbs. The listener is a child of the player; TopLevel detaches it from
	// the player transform so a world-space position sticks (the player is
	// movement-locked during the overlook, but knockback can still nudge it).
	// Pass null to restore the authored head-local rest pose on the way down.
	public void SetAudioListenerWorldOverride(Vector3? worldPos)
	{
		if (_audioListener == null)
		{
			return;
		}
		if (worldPos.HasValue)
		{
			_audioListener.TopLevel = true;
			_audioListener.GlobalPosition = worldPos.Value;
		}
		else if (_audioListener.TopLevel)
		{
			_audioListener.TopLevel = false;
			_audioListener.Position = AUDIO_LISTENER_REST_POS;
		}
	}

	// Entered from ClimbableTree.Complete. Conceals the player (hidden from
	// mobs + model hidden) and lifts into the bird's-eye overlook. The matching
	// restore lives in OnBirdsEyeReturnComplete, driven by the bird's-eye
	// fly-down — there is no explicit "descend" call, the player leaves the tree
	// by ending bird's-eye (ESC) or by taking damage.
	public void EnterClimbableTree()
	{
		if (_hidden || _birdsEye)
		{
			return;
		}
		_hidden = true;
		SetModelVisible(false);
		BeginBirdsEye();
	}

	// Entered from Forge.Complete (EActionVerb.Camp) when the camp screen opens
	// at a lit campfire. Conceals the player from mobs (the camp action is gated
	// by NoDangerRequirement, but time can pass / mobs can wander while the modal
	// is up) and switches the loop pose to SitIdle. The model stays visible.
	// ExitCamp, called when the camp screen closes, restores both.
	public void EnterCamp()
	{
		if (_camping)
		{
			return;
		}
		_camping = true;
		_hidden = true;
	}

	public void ExitCamp()
	{
		if (!_camping)
		{
			return;
		}
		_camping = false;
		_hidden = false;
	}

	// Toggles the player's model subtree visibility (hide / birds-eye).
	void SetModelVisible(bool visible)
	{
		if (_activeVisual != null)
		{
			_activeVisual.Visible = visible;
		}
	}

	// Shader global driving the screenspace night-vision effect (lifts darks +
	// desaturates the final image) while a night-vision effect is active. Read
	// by shaders/post_process.gdshader. Purely visual; the gameplay relief
	// lives in PlayerPerception, keyed off the same NightVision stat.
	private static readonly StringName NightVisionGlobal = "night_vision";

	// Push the player's night-vision *degree* (0..1) to the shader global each
	// frame. Mirrors PlayerPerception's relief term: NightVision is a
	// multiplicative stat where value-1 is the fraction of darkness relieved,
	// so no effect (stat == 1) yields 0 and a 1.85 stat yields 0.85. At 0 the
	// shader path is an exact identity, so this is free when nothing grants it.
	private void UpdateNightVisionShaderGlobal()
	{
		float nightVision = Mathf.Clamp(ComposeStat(EStat.NightVision) - 1f, 0f, 1f);
		RenderingServer.GlobalShaderParameterSet(NightVisionGlobal, nightVision);
	}

	private void UpdateVisibility()
	{
		float targetLightMax = _world.SimData?.TargetLightMax ?? 0.75f;
		float lightFactor = targetLightMax > 0f ? Mathf.Clamp(_world.GetPerceivedLight(GlobalPosition) / targetLightMax, 0, 1) : 0f;

		float speedFactor = data.moveSpeed > 0f ? Mathf.Clamp(Mathf.Pow(Velocity.Length() / data.moveSpeed, data.visibilityMovementPower), data.visibilityMovementMin, 1f) : 1f;

		float camouflage = 0f;
		foreach (Foliage foliage in _foliageCollisions)
		{
			camouflage = Mathf.Max(camouflage, foliage.camouflage);
		}

		visibility = Mathf.Clamp(lightFactor * speedFactor * (1.0f - camouflage), 0f, 1f);
		visibilityLight = lightFactor;
		visibilitySpeed = speedFactor;
		visibilityCamouflage = Mathf.Max(0f, 1f - camouflage);

		Vector3 horizVel = Velocity;
		horizVel.Y = 0f;
		CurrentDecibels = PlayerPerception.ComputeMovementDecibels(horizVel.Length(), data.sneakSpeed, data.moveSpeed, data.sneakDecibels, data.runDecibels);
	}

	// Smooth the dark-adaptation state toward "how dark is it where I stand".
	// Reuses visibilityLight (set by UpdateVisibility immediately above): the
	// player's own perceived light, 0 (pitch dark) .. 1 (>= perception-saturation).
	// Asymmetric like a real pupil — dilate slowly toward darkness, constrict fast
	// toward light. Drives the eye_adaptation render global (via GameClient) and
	// the perception darkness relief (via PlayerPerception).
	private void UpdateEyeDilation(float dt)
	{
		if (data == null)
		{
			return;
		}
		float target = Mathf.Clamp(1f - visibilityLight, 0f, 1f);
		float tau = target > EyeDilation ? data.eyeDilationDilateSeconds : data.eyeDilationConstrictSeconds;
		float k = 1f - Mathf.Exp(-dt / Mathf.Max(tau, 0.001f));
		EyeDilation = Mathf.Lerp(EyeDilation, target, k);
	}
}
