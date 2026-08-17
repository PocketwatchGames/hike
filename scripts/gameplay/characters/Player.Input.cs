using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	// Mouse FACING input (Directional aim). `deflection01` is the virtual aim
	// cursor's offset from center divided by the disk radius; only its angle
	// drives the body yaw, so the radius (hence magnitude) is just a feel knob.
	public void ProcessMouseLook(Vector2 deflection01, float cameraYaw)
	{
		_inputLook = new Vector3(deflection01.X, 0, deflection01.Y).Rotated(Vector3.Up, cameraYaw);
	}

	// Mouse POSITIONAL input (Positional/Arced aim). `deltaScreen` is a world-unit
	// cursor displacement in screen axes (meters); rotated into world XZ and
	// accumulated until the reticle consumes it (ConsumeAimInput). Range-independent
	// by construction — the reticle adds it straight to the cursor, range only
	// clamps. Separate from _inputLook because facing and the ground cursor are
	// genuinely different quantities (a heading vs a point), gated differently.
	public void AddMouseAimDelta(Vector2 deltaScreen, float cameraYaw)
	{
		_mouseAimWorldDelta += new Vector3(deltaScreen.X, 0, deltaScreen.Y).Rotated(Vector3.Up, cameraYaw);
	}

	// Squared move-input magnitude above which a cancelOnMove ritual bails. Small
	// enough that any deliberate step cancels, above resting stick drift.
	const float MoveCancelThresholdSq = 0.04f;

	void HandleInteractInput()
	{
		if (InteractMenuOpen)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		if (Input.IsActionJustPressed("Interact"))
		{
			if (_curInteractive != null)
			{
				CancelInteract();
				return;
			}
			if (_highlightInteractive != null && _highlightInteractive.CanActorInteract(this))
			{
				// Count the merged menu (world actions + always-available self-actions):
				// a tap runs the world DEFAULT, a hold opens the menu where the self-
				// actions live. With self-actions present the menu is always multi-entry,
				// so a highlighted interactive always offers the hold-to-menu path.
				int menuCount = _highlightInteractive.GetActions(this)?.Count ?? 0;
				menuCount += _selfActions?.Count ?? 0;
				if (menuCount > 1)
				{
					_interactPressActive = true;
					_interactHoldStartMs = now;
					InteractHoldProgress = 0f;
					return;
				}
				if (menuCount == 1)
				{
					if (TryStartInteractiveAction(_highlightInteractive))
					{
						_highlightInteractive = null;
						onHighlightChanged?.Invoke(null);
					}
				}
				return;
			}
			// Nothing highlighted: pressing interact opens the self-action menu (Pray,
			// ...). Never auto-runs — the menu always comes up for a non-default action.
			RequestSelfMenu();
		}
		if (_interactPressActive)
		{
			ulong elapsed = now > _interactHoldStartMs ? now - _interactHoldStartMs : 0;
			InteractHoldProgress = Mathf.Clamp(elapsed / ContextButtonHoldMs, 0f, 1f);
			bool stillHeld = Input.IsActionPressed("Interact");
			if (!stillHeld)
			{
				_interactPressActive = false;
				InteractHoldProgress = 0f;
				// Tap (released before threshold): start the default action.
				if (_highlightInteractive != null && _highlightInteractive.CanActorInteract(this))
				{
					if (TryStartInteractiveAction(_highlightInteractive))
					{
						_highlightInteractive = null;
						onHighlightChanged?.Invoke(null);
					}
				}
			}
			else if (elapsed >= ContextButtonHoldMs)
			{
				_interactPressActive = false;
				InteractMenuOpen = true;
				onInteractMenuOpenRequested?.Invoke();
			}
		}
	}

	void CancelInteract()
	{
		// If the runner is mid-interactive, abort it so completionEvents
		// don't fire. Weapon actions are gated by their own canAbort flag
		// inside TryAbort, which interactive actions skip — they always
		// abort cleanly.
		if (_runner != null && _runner.IsBusy && _runner.Current.interactiveAction != null)
		{
			_runner.TryAbort();
		}
		SetCurInteractive(null);
		_highlightInteractive = null;
		onHighlightChanged?.Invoke(null);
	}

	static readonly Dictionary<EInventorySlot, string> _weaponActions = new()
	{
		{ EInventorySlot.WeaponMelee, "AttackMelee" },
		{ EInventorySlot.WeaponRanged, "AttackRanged" }
	};
	// Zero the cached input vectors so _PhysicsProcess stops applying the
	// last-known stick deflection while gameplay input is suppressed (e.g.
	// inventory open). Without this, opening a modal mid-movement leaves the
	// player coasting in the held direction since ProcessInput is the only
	// thing that refreshes these.
	public void ClearInput()
	{
		_inputMove = Vector3.Zero;
		_inputLook = Vector3.Zero;
		_mouseAimWorldDelta = Vector3.Zero;
	}


	public void ProcessInput(float cameraYaw)
	{
		Vector2 move = Vector2.Zero;
		move.X -= Input.GetActionStrength("MoveLeft");
		move.X += Input.GetActionStrength("MoveRight");
		move.Y -= Input.GetActionStrength("MoveUp");
		move.Y += Input.GetActionStrength("MoveDown");
		move = move.LengthSquared() > 1 ? move.Normalized() : move;
		_inputMove = new Vector3(move.X, 0, move.Y).Rotated(Vector3.Up, cameraYaw);

		// Look input. Gamepad: every frame from the right-stick axes (stick
		// centered → _inputLook = Zero, so the rotation block falls back to
		// move direction). KBM: ProcessMouseLook only writes _inputLook
		// while Aim is held (GameClient gates the motion event), but a stale
		// _inputLook can survive an Aim release until the next mouse event,
		// so explicitly zero it on KBM frames without Aim to guarantee the
		// rotation block sees a clean state.
		if (InputDevice.Current == InputDevice.EDevice.Gamepad)
		{
			Vector2 look = Vector2.Zero;
			look.X -= Input.GetActionStrength("LookLeft");
			look.X += Input.GetActionStrength("LookRight");
			look.Y -= Input.GetActionStrength("LookUp");
			look.Y += Input.GetActionStrength("LookDown");
			look = look.LengthSquared() > 1 ? look.Normalized() : look;
			_inputLook = new Vector3(look.X, 0, look.Y).Rotated(Vector3.Up, cameraYaw);
		}
		else if (!Input.IsActionPressed("Aim"))
		{
			_inputLook = Vector3.Zero;
		}

		// Riding a vehicle: keep the steering vectors computed above (the boat
		// reads MountMoveInput) but drop every other action press — the only
		// control while mounted is Interact to dismount. Dismount reparents the
		// rider out of the vehicle; safe here because ProcessInput runs from
		// _Process, not the physics flush.
		if (_mount != null)
		{
			if (Input.IsActionJustPressed("Interact"))
			{
				Dismount();
			}
			return;
		}

		// Bird's-eye lock drops every action press for the duration of the
		// overview shot. Movement velocity is already gated by the _birdsEye
		// speed=0 check farther down, but we still need to drop jump / dash /
		// weapon presses so a held button while the camera is up can't punch
		// through the lock. ui_cancel is handled by GameClient
		// since it shares ESC with TogglePause and needs to consume the input
		// before TogglePause sees it.
		if (_birdsEye)
		{
			_inputMove = Vector3.Zero;
			_inputLook = Vector3.Zero;
			return;
		}

		// Hitstun rejects every action press for the duration of the flinch.
		// Movement / look input has already been latched above so the body
		// keeps coasting in the held direction (subject to knockback velocity);
		// what we drop is interact, jump, dash, weapon attacks, consumables,
		// and the sneak toggle. The runner is still allowed to tick down its
		// in-flight action on its own so wind-downs complete naturally.
		if (_hitstunTime > 0f)
		{
			return;
		}

		// InteractCancel shares its gamepad binding with Interact, so only
		// consume the frame when there is actually something for it to abort:
		// a runner-driven interactive, or a weapon mid-charge. Otherwise fall
		// through and let Interact (and the other action presses) fire on the
		// same input event.
		if (Input.IsActionJustPressed("InteractCancel") && _runner != null && _runner.IsBusy)
		{
			if (_runner.Current.interactiveAction != null)
			{
				CancelInteract();
				return;
			}
			// Charging always aborts via TryAbort — bail out of a charged
			// weapon without releasing it into a swing/shot.
			if (_runner.Phase == EActionPhase.Charging)
			{
				_runner.TryAbort();
				return;
			}
		}

		// Handle interact input. Multi-action interactives split tap vs hold:
		// a tap (release before ContextButtonHoldMs) runs the default
		// action; a hold past the threshold raises the options modal via
		// onInteractMenuOpenRequested. Single-action interactives still run
		// on JustPressed so the snappy feel is preserved.
		HandleInteractInput();

		// Voluntary bail from a cancelOnMove ritual (Pray): the moment the player
		// feeds movement input, abort it — the fade unwinds and no completion effect
		// fires. Distinct from locksMovement (Pray doesn't lock) and interruptOnDamage.
		if (_inputMove.LengthSquared() > MoveCancelThresholdSq
			&& _runner != null && _runner.IsBusy
			&& (_runner.Current.interactiveAction?.cancelOnMove ?? false))
		{
			CancelInteract();
		}

		if (Input.IsActionJustPressed("Jump") || Input.IsActionJustPressed("UseItem") || Input.IsActionJustPressed("AttackMelee") || Input.IsActionJustPressed("AttackContextSensitive") || Input.IsActionJustPressed("Dash"))
		{
			CancelInteract();
		}

		// Sneak is broken by overt actions: jumping, swinging, firing, using
		// a consumable. Gated on input intent rather than action success so a
		// pressed-but-blocked attack (no ammo, runner busy) still ends sneak —
		// the player is plainly not trying to stay quiet.
		if (Input.IsActionJustPressed("Jump")
			|| Input.IsActionJustPressed("AttackMelee")
			|| Input.IsActionJustPressed("AttackRanged")
			|| Input.IsActionJustPressed("AttackContextSensitive")
			|| Input.IsActionJustPressed("UseItem")
			|| Input.IsActionJustPressed("Dash"))
		{
			_sneaking = false;
		}

		// Any non-attack overt button cancels a banked queued tap — the player
		// has moved on to something else. Attack presses cancel it inside
		// TryStartWeaponAction instead (the fresh press supersedes and may
		// re-bank on its own release).
		if (Input.IsActionJustPressed("Jump")
			|| Input.IsActionJustPressed("UseItem")
			|| Input.IsActionJustPressed("Dash")
			|| Input.IsActionJustPressed("Sneak")
			|| Input.IsActionJustPressed("Interact"))
		{
			ClearQueuedInput();
		}

		// Sneak input model is player-selectable (CVars.sneakHold). Hold-to-sneak
		// mirrors the button: while held the player sneaks whenever they can, and
		// stands the instant it's released — the overt-action breaks above still
		// drop sneak momentarily, but it re-latches next frame if the button is
		// still down. Toggle mode flips on each press; a press there also doubles
		// as the player-initiated abort key while a runner action is in flight
		// (charging always cancels; Active cancels only if the selected tier opts
		// in via canAbort). A successful abort consumes the press — the player
		// wanted to bail out of the attack, not also flip into sneak.
		if (CVars.sneakHold.Value)
		{
			_sneaking = Input.IsActionPressed("Sneak");
		}
		else if (Input.IsActionJustPressed("Sneak"))
		{
			_sneaking = !_sneaking;
		}

		// Beginning a sneak-block opens the parry window. Rising edge only, so an
		// overt action that momentarily drops sneak and re-latches the same frame
		// (swing-while-held) doesn't restart the window — only a fresh crouch does.
		if (_sneaking && !_wasSneaking)
		{
			BeginSneakBlock();
		}
		// Stopping the block arms a short re-engage cooldown so the guard can't
		// instantly block/parry again (no flicker-blocking). Falling edge only —
		// in hold mode a hit that drops sneak re-latches next frame and never
		// reaches here, so holding through a hit keeps the guard up.
		else if (!_sneaking && _wasSneaking && data != null)
		{
			_blockCooldownEndMs = (_world?.GameTimeMs ?? 0) + (ulong)(data.blockReengageCooldown * 1000f);
		}
		_wasSneaking = _sneaking;

		if (Input.IsActionJustPressed("UseItem"))
		{
			TryUseActiveConsumable();
		}
		if (Input.IsActionJustReleased("UseItem"))
		{
			ReleaseUseConsumable();
		}

		if (Input.IsActionJustPressed("Lantern"))
		{
			TryUseLantern();
		}
		if (Input.IsActionJustReleased("Lantern"))
		{
			ReleaseUseLantern();
		}

		// Jumping belongs to the legacy movement model only; the climb model
		// replaces it with interact-to-climb. Gated here as well as unbound in
		// InputBindings so a stray ActionPress (the headless bot pulses "Jump")
		// cannot launch the player in a model that has no jump.
		if (!CVars.climbMovement.Value && Input.IsActionJustPressed("Jump"))
		{
			bool swimSurfaceJump = _waterState == EWaterState.Swimming && GlobalPosition.Y >= _waterSurfaceY - data.waterJumpOffset;
			// Skating routes to the ground-jump branch (preserves XZ momentum)
			// and exits skate mode. The intent is a "skate jump" that lets the
			// player chain ramps or launch off the bottom of a slope with their
			// accumulated speed intact — not a wall-jump kick away from the
			// slope normal.
			if (_grounded || _world.GameTimeMs < _coyoteTimeEndMs || swimSurfaceJump || _skating)
			{
				float jumpSpeed = swimSurfaceJump ? data.swimJumpSpeed : data.jumpSpeed;
				Velocity = new Vector3(Velocity.X, jumpSpeed, Velocity.Z);
				_grounded = false;
				_coyoteTimeEndMs = 0;
				_jumpHeld = true;
				if (_skating && CVars.debugSlopes.Value)
				{
					string ts = System.DateTime.Now.ToString("HH:mm:ss.fff");
					Vector3 horizVel = new(Velocity.X, 0f, Velocity.Z);
					GD.Print($"[skate] EXIT  {ts} speed={horizVel.Length():F1}m/s (jump)");
				}
				_skating = false;
				_skateContactLostMs = 0;
				PlayOneShot(EAnimation.Jump);
				SpawnWorldEffect(_jumpFx);
			}
			else if (_waterState == EWaterState.Swimming)
			{
				Velocity = new Vector3(Velocity.X, data.swimVerticalSpeed, Velocity.Z);
			}
			else
			{
				// Falling past coyote time: a wall jump wins if there's a wall to
				// kick off; otherwise spend a mid-air jump if any remain.
				if (!TryWallJump())
				{
					TryAirJump();
				}
			}
		}
		else if (!Input.IsActionPressed("Jump"))
		{
			_jumpHeld = false;
		}

		if (Input.IsActionJustPressed("Dash"))
		{
			TryStartDash();
		}

		// Convert a pending pre-cooldown press if the player is still holding
		// the button and the cooldown has now elapsed. Runs before this
		// frame's JustPressed handling so a press that lands on the exact
		// frame the cooldown expires still goes through TryStartWeaponAction
		// normally — the pending field is only set when cooldown is in flight.
		if (_pendingWeaponPressSlot is EInventorySlot pendingSlot)
		{
			if (!Input.IsActionPressed(_pendingWeaponPressActionName))
			{
				// Released before the weapon could fire — a completed tap the
				// runner never saw. If the release lands inside the player's
				// queue window of the weapon becoming READY (current action done
				// AND this weapon's cooldown complete — see WeaponReadyTimeMs),
				// bank it as a queued tap fired by the block below; an earlier
				// release is simply dropped. The window is player-wide input
				// feel (PlayerData), not per-weapon data.
				WeaponState pendingWeapon = GetMeleeWeaponOrUnarmed(pendingSlot);
				ulong nowMs = _world?.GameTimeMs ?? 0;
				if (pendingWeapon != null && data != null
					&& WeaponReadyTimeMs(pendingWeapon) <= nowMs + (ulong)(data.weaponQueueWindowSeconds * 1000f))
				{
					_queuedWeaponTapSlot = pendingSlot;
					_queuedWeaponTapActionName = _pendingWeaponPressActionName;
				}
				_pendingWeaponPressSlot = null;
				_pendingWeaponPressActionName = null;
			}
			else if (_runner != null && !_runner.IsBusy)
			{
				WeaponState pendingWeapon = GetMeleeWeaponOrUnarmed(pendingSlot);
				ulong nowMs = _world?.GameTimeMs ?? 0;
				if (pendingWeapon != null && pendingWeapon.cooldownExpireMs <= nowMs)
				{
					TryStartWeaponAction(pendingSlot, _pendingWeaponPressActionName);
				}
			}
		}

		// Fire a queued tap the moment its weapon is ready: runner free AND the
		// weapon's cooldown complete. A busy runner is the NORMAL waiting state
		// here (the tap was banked mid-cycle, possibly for the other weapon), so
		// it waits rather than cancels — abandonment is handled by the explicit
		// cancels (other overt buttons, a fresh attack press, getting hit,
		// death). The synthesized press+release pair activates tier 0 at zero
		// charge, exactly like the fast tap the player actually performed.
		if (_queuedWeaponTapSlot is EInventorySlot tapSlot)
		{
			WeaponState tapWeapon = GetMeleeWeaponOrUnarmed(tapSlot);
			ulong tapNowMs = _world?.GameTimeMs ?? 0;
			if (tapWeapon == null)
			{
				ClearQueuedInput();
			}
			else if (_runner != null && !_runner.IsBusy && tapWeapon.cooldownExpireMs <= tapNowMs)
			{
				string tapAction = _queuedWeaponTapActionName;
				ClearQueuedInput();
				TryStartWeaponAction(tapSlot, tapAction);
				ReleaseWeaponAction(tapSlot);
			}
		}

		// Fire a queued dash once the runner frees and the dash cooldown clears.
		// Single attempt: TryStartDash re-checks its remaining gates (stamina,
		// fall speed) and a refusal drops the dash rather than retrying forever.
		if (_queuedDash)
		{
			ulong dashNowMs = _world?.GameTimeMs ?? 0;
			if (_runner != null && !_runner.IsBusy && dashNowMs >= _dashCooldownEndMs)
			{
				_queuedDash = false;
				TryStartDash();
			}
		}

		foreach (var (slot, actionName) in _weaponActions)
		{
			if (Input.IsActionJustPressed(actionName))
			{
				TryStartWeaponAction(slot, actionName);
			}
			if (Input.IsActionJustReleased(actionName))
			{
				ReleaseWeaponAction(slot);
			}
		}

		// AttackContextSensitive routes to ranged when Aim is held at press
		// time, melee otherwise. Slot is latched until release so a mid-press
		// Aim toggle doesn't switch which weapon's release fires.
		if (Input.IsActionJustPressed("AttackContextSensitive"))
		{
			EInventorySlot slot = Input.IsActionPressed("Aim")
				? EInventorySlot.WeaponRanged
				: EInventorySlot.WeaponMelee;
			_contextSensitiveAttackSlot = slot;
			TryStartWeaponAction(slot, "AttackContextSensitive");
		}
		if (Input.IsActionJustReleased("AttackContextSensitive") && _contextSensitiveAttackSlot is EInventorySlot latchedSlot)
		{
			ReleaseWeaponAction(latchedSlot);
			_contextSensitiveAttackSlot = null;
		}
	}
}
