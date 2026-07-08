using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	// Bounds on move-cycle playback when terrain retimes the stride (foliage drag
	// + the ground block's speed multiplier). The floor keeps a heavily slowed
	// step animating — and firing footsteps — fast enough to read as a slow
	// trudge rather than a freeze; the ceiling stops a hastened surface from
	// spinning the legs into a silly blur.
	private const float MinMoveAnimSpeed = 0.5f;
	private const float MaxMoveAnimSpeed = 1.5f;

	// One-shots (attack, die, jump) latch the resolved clip and let the animator
	// drive itself to completion — Finished flips because these anims are authored
	// with loop=false. While a one-shot is latched, UpdateAnimation defers; once
	// Finished (or the animator gets reassigned by something else) we clear the
	// latch and resume the state-driven loop pick.
	// `overridesCharge` keeps the one-shot playing over a held charge pose (the
	// block reaction passes true); everything else yields to a charge. Resolves
	// through AnimName, so a wielded weapon's override for the slot wins and an
	// unmapped / missing clip falls back silently.
	public void PlayOneShot(EAnimation anim, bool overridesCharge = false)
	{
		if (_animator == null || data == null)
		{
			return;
		}
		StringName name = AnimName(anim);
		if (name == default || !_animator.HasAnimation(name))
		{
			return;
		}
		_oneShotClip = name;
		_oneShotIsHitstun = anim == EAnimation.Hitstun;
		_oneShotOverridesCharge = overridesCharge;
		// restart: a re-fired one-shot (mashing an attack that maps to the same
		// clip) must replay from the start rather than no-op on the in-flight clip.
		_animator.Play(name, restart: true);
	}

	// Resolve an EAnimation slot to a clip name, preferring the wielded weapon's
	// override and falling back to the unarmed clip. The single chokepoint that
	// makes the whole standard anim set per-weapon overridable with an automatic
	// unarmed fallback — a weapon set leaves a slot blank (or names a clip the
	// active animator lacks) and the unarmed clip is used.
	private StringName AnimName(EAnimation anim)
	{
		WeaponAnimSet set = _wieldedWeapon?.data?.animSet;
		if (set != null)
		{
			StringName ov = set.GetOverride(anim);
			if (ov != default && _animator != null && _animator.HasAnimation(ov))
			{
				return ov;
			}
		}
		return data != null ? data.GetAnimationName(anim) : default;
	}

	// Load-time validation: each WeaponAnimSet's clip strings must exist in the
	// live animation library. Deduped so a set is checked once (the first time
	// it's used / wielded). Missing clips are logged, not fatal.
	private readonly System.Collections.Generic.HashSet<WeaponAnimSet> _validatedAnimSets = new();
	private void ValidateAnimSet(WeaponAnimSet set, string label)
	{
		if (set == null || _animator == null || !_validatedAnimSets.Add(set))
		{
			return;
		}
		set.Validate(_animator.HasAnimation, label);
	}

	// EAnimation charge slot for the in-flight weapon charge, or null when the
	// player isn't charging a weapon. Tier (selectedTierIndex, clamped at 1)
	// picks Charge1 vs Charge2; locomotion picks Idle / Walk / Run on the
	// 75%-of-run-speed split (standing → Idle, moving below → Walk, at/above →
	// Run, so a slowing charge stays in Walk). The slot resolves to a clip
	// through AnimName like every other state — one path.
	const float ChargeRunSpeedFraction = 0.75f;
	private EAnimation? WeaponChargeSlot(float speedSq, bool intentMoving)
	{
		if (_runner == null || !_runner.IsBusy
			|| _runner.Phase != EActionPhase.Charging
			|| _runner.Current.context.primaryItem is not WeaponState)
		{
			return null;
		}
		bool heavy = _runner.Current.selectedTierIndex >= 1;
		bool moving = intentMoving || speedSq > MoveLoopEnterSpeedSq;
		if (!moving)
		{
			return heavy ? EAnimation.Charge2Idle : EAnimation.Charge1Idle;
		}
		float runThreshold = (data?.moveSpeed ?? 0f) * ChargeRunSpeedFraction;
		if (speedSq >= runThreshold * runThreshold)
		{
			return heavy ? EAnimation.Charge2Run : EAnimation.Charge1Run;
		}
		return heavy ? EAnimation.Charge2Walk : EAnimation.Charge1Walk;
	}

	private void UpdateAnimation()
	{
		if (_animator == null || data == null)
		{
			return;
		}
		// Default the animator back to authored speed every tick — the
		// movement-loop branch below re-enables status retiming when (and only
		// when) it picks a speed-scaled loop. One-shots (attack, hitstun, jump,
		// die) take the early return below, so this default sticks for them.
		_animator.effectSpeedMultiplier = 1f;

		// Track airborne dwell time. Cleared the instant we hit ground so the
		// next lift-off starts a fresh grace window. Running up a slope tends
		// to lose floor contact for a frame or two between step-up cycles, and
		// without this the player flickers to "fall" each time.
		if (_grounded)
		{
			_airborneStartMs = 0;
		}
		else if (_airborneStartMs == 0 && _world != null)
		{
			_airborneStartMs = _world.GameTimeMs;
		}

		// Any held charge (consumable or weapon) clears a stale, non-priority
		// one-shot so the charge pose shows immediately; a block reaction
		// (overridesCharge) survives to play over the charge.
		bool chargingNow = _runner != null && _runner.IsBusy
			&& _runner.Phase == EActionPhase.Charging;
		if (chargingNow && !_oneShotOverridesCharge)
		{
			_oneShotClip = default;
		}

		// Movement-locked consumable / scroll held pose (drink / eat / read).
		// chargeEvents PlayAnim fires PlayOneShot on press; a looping clip never
		// reports Finished, so this override clears the stale latch and lets the
		// loop pick swap back to Idle/Run the instant Charging ends. Weapon
		// charges take the Charge* slot branch in the loop pick below instead;
		// weapon ATTACKS fire through their tier's PlayAnim event (animName =
		// Attack / Attack2) like any other timeline one-shot.
		EAnimation? chargeAnimOverride = null;
		if (chargingNow && _runner.LocksMovement
			&& _runner.Current.context.primaryItem is not WeaponState)
		{
			chargeAnimOverride = ResolveChargeAnim(_runner.Current.profile);
		}

		if (_oneShotClip != default)
		{
			// Hitstun is gated solely by _hitstunTime — when the timer hits zero
			// the latch releases regardless of the clip's loop flag or Finished
			// state, so a looping hitstun clip doesn't trap the player past the
			// flinch window. Other one-shots hold while the animator says the
			// clip is still playing.
			if (_oneShotIsHitstun)
			{
				if (_hitstunTime > 0f)
				{
					return;
				}
				_oneShotClip = default;
			}
			else
			{
				if (_animator.CurrentAnimation == _oneShotClip && !_animator.Finished)
				{
					return;
				}
				_oneShotClip = default;
			}
		}

		EAnimation loopAnim;
		// Horizontal speed only — vertical motion belongs to fall/jump/grav,
		// not to the run-vs-idle decision. While stepping up a slope the body
		// briefly leaves the floor and Velocity.Y from gravity dominates the
		// 3D length, which used to flip the pick to "run" for a frame and
		// then back to "idle" once we re-grounded.
		Vector3 horizVel = new(Velocity.X, 0f, Velocity.Z);
		float speedSq = horizVel.LengthSquared();
		// "Wants to move" includes input even when blocked by a wall —
		// otherwise pushing into geometry zeroes Velocity and snaps us back to
		// idle while the player is visibly trying to run.
		bool intentMoving = _inputMove.LengthSquared() > 0.0001f;
		bool fallReady = !_grounded
			&& _airborneStartMs != 0
			&& _world != null
			&& _world.GameTimeMs - _airborneStartMs >= FallGraceMs;
		if (_health <= 0f)
		{
			loopAnim = EAnimation.Dead;
		}
		else if (_camping || !IsActive)
		{
			// Sit by the fire: the controlled player while the camp screen is open
			// (_camping), and every inactive party member gathered around the
			// campfire (!IsActive). An inactive member only stands once selected —
			// becoming the controlled member clears this and resumes normal
			// locomotion. Movement is gated either way, so the body is stationary.
			loopAnim = EAnimation.SitIdle;
		}
		else if (_mount != null)
		{
			// Seated on a vehicle: paddle-rest vs paddle-stroke per the mount's
			// propulsion state. The vehicle owns the body transform, so locomotion
			// speed / ground state below are irrelevant here.
			loopAnim = _mount.IsPropelling ? _mount.MoveAnim : _mount.IdleAnim;
		}
		else if (chargeAnimOverride.HasValue)
		{
			loopAnim = chargeAnimOverride.Value;
		}
		else if (WeaponChargeSlot(speedSq, intentMoving) is EAnimation chargeSlot)
		{
			// Holding a weapon charge: the Charge* slot (tier x locomotion) takes
			// priority over normal locomotion and resolves to the weapon's clip
			// through AnimName, same as every other slot.
			loopAnim = chargeSlot;
		}
		else if (_curInteractive != null)
		{
			// Interaction holds the player still (movement speed is forced to
			// 0 above) — show the interaction loop regardless of water/ground
			// state until the action completes or is cancelled.
			loopAnim = EAnimation.Interacting;
		}
		else if (_waterState == EWaterState.Swimming)
		{
			// Sprint underwater swaps the moving variant only — the idle pose
			// is the same whether or not Dash is held. (Holding Dash while
			// idle in water is still "sprint intent" per UpdateSprintState,
			// but visually there's nothing to differentiate from a normal
			// tread until the player starts moving.)
			EAnimation swimMove = _sprinting ? EAnimation.SwimSprint : EAnimation.Swim;
			loopAnim = PickMoveLoop(speedSq, intentMoving, swimMove, EAnimation.SwimIdle);
		}
		else if (_dashTimeRemaining > 0f)
		{
			// Still dashing after the Dash one-shot anim has finished. The dash
			// velocity sits far from the input-target velocity, which would put
			// the skid/skate branch below in charge and show Skating -- but a
			// dash should read as a hard run, so fall back to Sprint until the
			// dash ends and normal loop selection resumes. (Water dashes are
			// already handled by the swimming branch above.)
			loopAnim = EAnimation.Sprint;
		}
		else if (_skating || _skidding)
		{
			// Skate anim wins over fall — on a steep slope _grounded is false
			// and the airborne grace would otherwise flip the model to the
			// fall pose every tick the skate ticks past FallGraceMs. Also
			// fires for grounded skids (sharp direction changes), so the
			// player visibly slides their feet during sharp turns at speed.
			loopAnim = EAnimation.Skating;
		}
		else if (fallReady)
		{
			loopAnim = EAnimation.Fall;
		}
		else if (_sneaking)
		{
			loopAnim = PickMoveLoop(speedSq, intentMoving, EAnimation.Sneak, EAnimation.SneakIdle);
		}
		else if (_sprinting)
		{
			// Sprint replaces run as the moving variant; idle stays the same
			// (sprint intent without movement is a transient state that
			// resolves to one or the other within a frame).
			loopAnim = PickMoveLoop(speedSq, intentMoving, EAnimation.Sprint, EAnimation.Idle);
		}
		else
		{
			loopAnim = PickMoveLoop(speedSq, intentMoving, EAnimation.Run, EAnimation.Idle);
		}
		// One path: every slot — locomotion, charge pose, anything — resolves
		// through AnimName, preferring the wielded weapon's override and falling
		// back to the unarmed clip.
		StringName loopName = AnimName(loopAnim);
		if (loopName != default)
		{
			_animator.Play(loopName);
		}

		// Status retiming (Cold etc.) is gated per-anim by AnimationData —
		// only loops authored with affectedBySpeedMultiplier track statusAnimMul
		// (movement anims whose underlying action is also slowed by statusMoveMul).
		// One-shots already returned above, so this branch only runs for loops.
		// Charge slots aren't mapped with that flag, so they're naturally excluded.
		if (data.IsAnimationSpeedAffected(loopAnim))
		{
			// Retime the move cycle to the terrain speed scalar (foliage + ground
			// block) so a mud-slowed / road-hastened stride doesn't foot-slide and
			// its footstep events slow down or speed up with it. Floored so a very
			// slow surface still reads as walking, not a freeze.
			float moveAnimSpeed = Mathf.Clamp(_terrainSpeed, MinMoveAnimSpeed, MaxMoveAnimSpeed);
			_animator.effectSpeedMultiplier = (_statusEffects?.FoldStat(EStat.AnimSpeed, 1f) ?? 1f) * moveAnimSpeed;
		}

		// Drive the anim-audio loop off the same loopAnim. Only idle / run /
		// swim_idle have audio; everything else (fall, dead, interacting,
		// active swim, weapon charge) is silent for the anim-loop layer.
		PackedScene animLoopTarget = null;
		if (_health > 0f)
		{
			if (loopAnim == EAnimation.Idle) animLoopTarget = _idleLoopFx;
			else if (loopAnim == EAnimation.Run) animLoopTarget = _runLoopFx;
			else if (loopAnim == EAnimation.SwimIdle) animLoopTarget = _swimIdleLoopFx;
		}
		UpdateAnimLoop(animLoopTarget);
	}

	// Pulls the held-pose anim out of an ItemActionProfile's chargeEvents.
	// Used by UpdateAnimation to drive movement-locked actions (consumables,
	// scrolls) as a sustained loop. Returns the first PlayAnim event's
	// animName; null when the profile has no charge anim authored.
	private static EAnimation? ResolveChargeAnim(ItemActionProfile profile)
	{
		if (profile?.chargeEvents == null)
		{
			return null;
		}
		for (int i = 0; i < profile.chargeEvents.Count; i++)
		{
			ItemEvent ev = profile.chargeEvents[i];
			if (ev != null && (ev.type & EItemEventType.PlayAnim) != 0)
			{
				return ev.animName;
			}
		}
		return null;
	}

	// Swap the active anim-loop wholesale on state change. No-op when target
	// matches the currently-playing scene, so this is safe to call every frame.
	private void UpdateAnimLoop(PackedScene scene)
	{
		if (scene == _animLoopScene)
		{
			return;
		}
		if (_animLoopFx != null)
		{
			_animLoopFx.Stop();
			_animLoopFx = null;
		}
		if (scene != null)
		{
			_animLoopFx = Fx.Create(scene, this, Vector3.Zero);
		}
		_animLoopScene = scene;
	}

	private EAnimation PickMoveLoop(float speedSq, bool intentMoving, EAnimation moveAnim, EAnimation idleAnim)
	{
		// Input intent forces "moving" — keeps the run anim playing while
		// pinned against geometry, where Velocity would otherwise be ~0.
		if (intentMoving || speedSq > MoveLoopEnterSpeedSq)
		{
			return moveAnim;
		}
		if (speedSq < MoveLoopExitSpeedSq)
		{
			return idleAnim;
		}
		// Hold-current band — compare the animator's currently-playing clip
		// against each candidate's authored name to decide which side of the
		// band to stick to. Both lookups are dictionary reads, so this is
		// cheap to run every tick.
		StringName current = _animator.CurrentAnimation;
		if (current == AnimName(moveAnim))
		{
			return moveAnim;
		}
		if (current == AnimName(idleAnim))
		{
			return idleAnim;
		}
		return idleAnim;
	}

	// Drives the in-hand model each tick off authoritative runner / animator
	// state. The weapon channel is set event-side (TryStartWeaponAction) and
	// only needs conceal toggling here; the consumable channel mirrors the live
	// Use action. A consumable in hand conceals the weapon (the potion replaces
	// the sword); so does any clip authored with AnimationData.hidesHeldItem.
	private void UpdateHeldItemVisual()
	{
		if (_heldVisual == null)
		{
			return;
		}

		PackedScene itemModel = null;
		if (_runner != null && _runner.IsBusy
			&& _runner.Current.context.sourceSlot == EInventorySlot.Equipment)
		{
			itemModel = _runner.Current.context.primaryItem?.data?.heldModel;
		}
		_heldVisual.SetActiveItem(itemModel);

		// While aiming, draw the equipped ranged weapon so the bow is in hand
		// for the full aim/draw — not just once an attack fires (the event-side
		// SetWeapon in TryStartWeaponAction). The weapon channel is persistent,
		// so the bow stays in hand after aim ends; a later melee swing swaps it
		// back. Only ranged weapons are forced here — aiming with nothing but a
		// melee weapon equipped leaves the existing held model untouched.
		if (_aiming)
		{
			WeaponState ranged = _inventory?.GetWeapon(EInventorySlot.WeaponRanged);
			PackedScene rangedModel = ranged?.data?.heldModel;
			if (rangedModel != null)
			{
				_heldVisual.SetWeapon(rangedModel, ranged.data.wieldHand);
				// Mod-authored idle fx (a Flaming bow's flame) rides the drawn bow
				// for the whole aim, not just once a shot fires.
				_heldVisual.SetWeaponIdleFx(ranged.statusEffects?.WeaponModIdleFx());
				// The drawn bow becomes the wielded weapon, so its anim set drives
				// the stance / charge poses while aiming and after aim ends.
				_wieldedWeapon = ranged;
				ValidateAnimSet(ranged.data.animSet, ranged.data.displayName);
			}
		}

		bool animHides = data != null && _animator != null
			&& data.AnimationHidesHeldItem(_animator.CurrentAnimation);
		_heldVisual.SetWeaponConcealed(itemModel != null || animHides);
	}
}
