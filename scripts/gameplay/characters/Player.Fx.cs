using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	// Resolve the base-model package scene for a gender. Falls back to the
	// Female entry when the gender has no authored package, so the player always
	// has a body. Returns null only if the map is empty.
	private PackedScene ResolveGenderPackage(EGender gender)
	{
		if (_modelPackages == null)
		{
			return null;
		}
		if (_modelPackages.TryGetValue((int)gender, out PackedScene scene) && scene != null)
		{
			return scene;
		}
		_modelPackages.TryGetValue((int)EGender.Female, out PackedScene fallback);
		return fallback;
	}

	// Resolve the voice-over bank for a gender. Falls back to the Female entry
	// when the gender has no authored bank, so the player always has a voice (or
	// null only when the map is empty — every vocalization then no-ops).
	private VoiceData ResolveGenderVoice(EGender gender)
	{
		if (_voices == null)
		{
			return null;
		}
		if (_voices.TryGetValue((int)gender, out VoiceData voice) && voice != null)
		{
			return voice;
		}
		_voices.TryGetValue((int)EGender.Female, out VoiceData fallback);
		return fallback;
	}

	// Spawn a voice clip from the resolved bank, applying its pitch shift. World
	// variant leaves the clip behind in world space (hurt / death); Self variant
	// parents it to the player so it tracks the body (out-of-breath). Both no-op
	// on a null scene or before _voice resolves.
	private void SpawnVoice(PackedScene scene)
	{
		if (scene == null || _world == null)
		{
			return;
		}
		Fx.Create(scene, _world, GlobalPosition, _voice?.pitchShift ?? 1f);
	}

	private void SpawnVoiceSelf(PackedScene scene)
	{
		if (scene == null)
		{
			return;
		}
		Fx.Create(scene, this, Vector3.Zero, _voice?.pitchShift ?? 1f);
	}

	// Instance the spawned gender's model package as a child of the player and
	// bind its drivers (animator + held-item socket). Only one rig is ever built.
	private void SpawnModelPackage(EGender gender)
	{
		PackedScene packageScene = ResolveGenderPackage(gender);
		if (packageScene == null)
		{
			return;
		}
		_modelPackageInstance = packageScene.Instantiate<PlayerModelPackage>();
		AddChild(_modelPackageInstance);
		_animator = _modelPackageInstance.animator;
		_heldVisual = _modelPackageInstance.heldVisual;
	}

	// Show the instanced model and wire its drivers. Deferred to Initialize (not
	// _Ready) because the gender that selects the base model only arrives with
	// the PlayerState member. GameClient calls Initialize synchronously right after
	// instantiating the scene, before any frame is processed, so there's no
	// window where the player renders unselected.
	private void ActivateVisual()
	{
		if (_animator == null)
		{
			return;
		}
		_animator.SetActive(true);
		_activeVisual = _animator.visual;
		// Footfalls fire from a Call Method Track authored on the model's
		// movement clips (OnFootstep) at the exact foot-contact frame.
		_animator.OnFootstep += EmitFootstep;
		// Dirt puffs synced to the shovel's scoop frames on the dig clip.
		_animator.OnDigDirt += EmitDigDirt;
		// Validate the base set's clip strings against the live library now
		// that the animator (and its library) exist. Weapon sets validate
		// lazily the first time they're wielded.
		ValidateAnimSet(data?.baseAnims, "base/unarmed");
	}

	// Spawn one footstep + footprint at the current foot position, fired from
	// the model's foot-contact method track. State gates the spawn: skip while
	// ungrounded, swimming, or interacting. Shallow water (wading) plays the
	// water splash; a rain puddle on terrain plays the squelchy puddle FX (the
	// CPU puddle mirror agrees with the puddle the shader draws here).
	private void EmitFootstep()
	{
		if (_world == null)
		{
			return;
		}
		if (!_grounded || _waterState == EWaterState.Swimming || _curInteractive != null
			|| (_runner != null && _runner.LocksMovement) || _birdsEye)
		{
			return;
		}
		Vector3 pos = GlobalPosition;
		EGroundType ground = GroundTypeResolver.Resolve(_world.WorldState, pos);
		if (_waterState == EWaterState.Shallow)
		{
			FootstepEmitter.Emit(_world, pos, _shallowWaterFootstepFx);
		}
		else if (TerrainWetness.IsPuddleStep(_world.WorldState, pos, SlopeNormalY()))
		{
			FootstepEmitter.Emit(_world, pos, _puddleFootstepFx);
			// Ring out a ripple on the puddle surface (voxel_clip puddle pass).
			SkyController.Current?.EmitWaterRipple(new Vector2(pos.X, pos.Z), _puddleFootstepRippleStrength);
		}
		else
		{
			FootstepEmitter.Emit(_world, pos, ground, _footstepEffects);
			float fpAlphaMul = _statusEffects?.FoldStat(EStat.FootprintAlpha, 1f) ?? 1f;
			float fpDurMul = _statusEffects?.FoldStat(EStat.FootprintDuration, 1f) ?? 1f;
			FootprintEmitter.Emit(_world, pos, GlobalRotation.Y, ground, _footprintTexture, _footprintSize, fpAlphaMul, fpDurMul, gated: false);
		}
	}

	// Upward component of the terrain normal the player is walking on, derived
	// from the measured movement grade (_slopeGrade) rather than GetFloorNormal:
	// voxel floors are flat tops with vertical walls, so the collision normal
	// reads ~straight up even on a hill (see SlopeSpeedFactor). The DC terrain
	// MESH the puddle shader samples is genuinely sloped, so this measured grade
	// is what matches its flatness gate. grade = tan(theta) -> normalY = cos(theta).
	// (Cross-slope traversal carries little vertical motion, so a sidehill step
	// can still read flat — acceptable for a footstep cue.)
	private float SlopeNormalY()
	{
		return 1f / Mathf.Sqrt(1f + _slopeGrade * _slopeGrade);
	}

	// Spawn one dirt puff at the player's feet, fired from the dig clip's scoop
	// method track so the burst lands on each shovel stroke. Parented to the
	// world so the puff stays put rather than tracking the player.
	private void EmitDigDirt()
	{
		if (_world == null || _digDirtFx == null)
		{
			return;
		}
		FootstepEmitter.Emit(_world, GlobalPosition, _digDirtFx);
	}

	// One-shot effect parented to World so it stays put as the player
	// continues to move (matching the footstep / ripple convention). Silently
	// no-ops when scene is unset or before Initialize has wired _world.
	private void SpawnWorldEffect(PackedScene scene)
	{
		if (scene == null || _world == null)
		{
			return;
		}
		Fx.Create(scene, _world, GlobalPosition);
	}

	// One-shot effect parented to the player (local origin) so its audio +
	// particles track the body as it keeps moving — used for self-anchored
	// cues like the out-of-breath pant, as opposed to SpawnWorldEffect which
	// leaves the effect behind in world space.
	private void SpawnSelfEffect(PackedScene scene)
	{
		if (scene == null)
		{
			return;
		}
		Fx.Create(scene, this, Vector3.Zero);
	}

	// Drives a loop's lifetime from a "should be active" flag. When `active`
	// flips true and we don't already own an instance, instantiate parented
	// to the player so the loop tracks the body. When it flips false, Stop()
	// the existing instance — it cleans itself up after the trailing audio +
	// particles wind down — and drop our reference so the next activation
	// gets a fresh node.
	private void UpdateLoopEffect(ref Fx instance, PackedScene scene, bool active)
	{
		if (active)
		{
			if (instance == null && scene != null)
			{
				instance = Fx.Create(scene, this, Vector3.Zero);
			}
		}
		else if (instance != null)
		{
			instance.Stop();
			instance = null;
		}
	}

	// Slide-loop driver with per-ground-type scene selection. Resolves the
	// current EGroundType each tick and swaps the active Fx wholesale when
	// the surface type changes mid-slide (e.g. skating from grass onto
	// stone). Missing dictionary entries silently emit nothing for that
	// ground type so a partially-authored player.tscn still works on the
	// surfaces it covers.
	private void UpdateSlideLoop(bool active)
	{
		PackedScene target = null;
		if (active && _world != null && _slideLoopFx != null)
		{
			EGroundType ground = GroundTypeResolver.Resolve(_world.WorldState, GlobalPosition);
			_slideLoopFx.TryGetValue(ground, out target);
		}
		if (target == _slideLoopScene)
		{
			return;
		}
		if (_slideLoop != null)
		{
			_slideLoop.Stop();
			_slideLoop = null;
		}
		if (target != null)
		{
			_slideLoop = Fx.Create(target, this, Vector3.Zero);
		}
		_slideLoopScene = target;
	}
}
