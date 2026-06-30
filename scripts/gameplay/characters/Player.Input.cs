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
				Godot.Collections.Array<InteractiveAction> actions = _highlightInteractive.GetActions(this);
				if (actions != null && actions.Count > 1)
				{
					_interactPressActive = true;
					_interactHoldStartMs = now;
					InteractHoldProgress = 0f;
					return;
				}
				if (actions != null && actions.Count == 1)
				{
					if (TryStartInteractiveAction(_highlightInteractive))
					{
						_highlightInteractive = null;
						onHighlightChanged?.Invoke(null);
					}
				}
			}
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
		{ EInventorySlot.WeaponLeft, "AttackMelee" },
		{ EInventorySlot.WeaponRight, "AttackRanged" }
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

	// Consumable quick-select wheel input. A tap of ConsumableCycleRight cycles
	// to the next consumable (on release); holding past ConsumableWheelHoldMs
	// opens the HUD item wheel instead, where the right stick highlights a belt
	// slot and release selects it. Runs after the right-stick look block in
	// ProcessInput so it can claim the stick (zeroing _inputLook) for wheel
	// navigation while the wheel is open.
	void HandleConsumableWheelInput()
	{
		Hud hud = Hud.Current;
		ulong now = _world?.GameTimeMs ?? 0;

		if (Input.IsActionJustPressed("ConsumableCycleRight"))
		{
			_consumableWheelPressActive = true;
			_consumableWheelPressStartMs = now;
		}

		if (_consumableWheelPressActive && Input.IsActionPressed("ConsumableCycleRight"))
		{
			if (!_consumableWheelOpen && now - _consumableWheelPressStartMs >= ContextButtonHoldMs)
			{
				_consumableWheelOpen = true;
				hud?.ShowItemWheel();
			}
			if (_consumableWheelOpen && hud != null)
			{
				Vector2 stick = new(
					Input.GetActionStrength("LookRight") - Input.GetActionStrength("LookLeft"),
					Input.GetActionStrength("LookDown") - Input.GetActionStrength("LookUp"));
				hud.UpdateItemWheelHighlight(stick);
				// The right stick drives the wheel, not character facing, while
				// the wheel is open.
				_inputLook = Vector3.Zero;
			}
		}

		if (_consumableWheelPressActive && Input.IsActionJustReleased("ConsumableCycleRight"))
		{
			_consumableWheelPressActive = false;
			if (_consumableWheelOpen)
			{
				int index = hud?.CloseItemWheelAndGetSelection() ?? -1;
				if (index >= 0)
				{
					_inventory?.SelectConsumable(index);
				}
				_consumableWheelOpen = false;
			}
			else
			{
				_inventory?.CycleConsumable(+1);
			}
		}
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

		if (Input.IsActionJustPressed("ConsumableCycleLeft"))
		{
			_inventory?.CycleConsumable(-1);
		}
		HandleConsumableWheelInput();
		if (Input.IsActionJustPressed("ConsumableSelect1"))
		{
			_inventory?.SelectConsumable(0);
		}
		if (Input.IsActionJustPressed("ConsumableSelect2"))
		{
			_inventory?.SelectConsumable(1);
		}
		if (Input.IsActionJustPressed("ConsumableSelect3"))
		{
			_inventory?.SelectConsumable(2);
		}

		// Sneak is a toggle. Pressing also doubles as the player-initiated
		// abort key while a runner action is in flight (charging always
		// cancels; Active cancels only if the selected tier opts in via
		// canAbort). A successful abort consumes the press — the player
		// wanted to bail out of the attack, not also flip into sneak.
		if (Input.IsActionJustPressed("Sneak"))
		{
			_sneaking = !_sneaking;
		}

		// Toggle the active companion between following and staying put.
		if (Input.IsActionJustPressed("CompanionToggleStay"))
		{
			_world?.Companion?.ToggleStayCommand();
		}

		if (Input.IsActionJustPressed("UseItem"))
		{
			TryUseActiveConsumable();
		}
		if (Input.IsActionJustReleased("UseItem"))
		{
			ReleaseUseConsumable();
		}

		if (Input.IsActionJustPressed("Jump"))
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
				TryWallJump();
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
				? EInventorySlot.WeaponRight
				: EInventorySlot.WeaponLeft;
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
