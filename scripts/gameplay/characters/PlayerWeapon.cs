using System.Collections.Generic;
using Godot;

// Input shim that maps weapon-attack input actions to ActionRunner calls.
// All timeline / phase / event-walking logic lives in ActionRunner. The
// generalization to consumable Use, channeled actions, and (phase 5)
// interactives all reuse the same runner, so adding a new input is just
// another entry that constructs a context and calls TryStart.
public partial class Player : CharacterBody3D, IActionActor
{

	// Effective reach of the weapon equipped in `slot`, accounting for live
	// charge state. While the runner is Charging that slot, samples the
	// currently-selected tier and live charge fraction; otherwise samples the
	// snap tier (tier 0) at chargeT=0 — what an immediate fire would produce.
	// Hitscan and projectile range both scale with the tier's `rangeScale01`
	// (projectile scales lifetime, matching DoProjectile); melee range is
	// the authored value (DoMelee ignores the scalar).
	public float GetWeaponRange(EInventorySlot slot)
	{
		WeaponState weapon = _inventory?.GetWeapon(slot);
		ItemActionProfile profile = weapon?.data?.actionProfile;
		if (profile?.chargedActions == null || profile.chargedActions.Count == 0)
		{
			return 0f;
		}

		ItemAction tier;
		float chargeT;
		if (_runner != null
			&& _runner.Phase == EActionPhase.Charging
			&& _runner.Current.context.sourceSlot == slot)
		{
			tier = _runner.Current.selectedTier ?? profile.chargedActions[0];
			chargeT = _runner.CurrentChargeT;
		}
		else
		{
			tier = profile.chargedActions[0];
			chargeT = 0f;
		}

		if (tier?.events == null)
		{
			return 0f;
		}

		for (int i = 0; i < tier.events.Count; i++)
		{
			ItemEvent ev = tier.events[i];
			if (ev == null)
			{
				continue;
			}
			float rangeScale = ItemAction.SampleRangeScale(tier, chargeT);
			if ((ev.type & EItemEventType.Hitscan) != 0)
			{
				return ev.hitScanRange * rangeScale;
			}
			if ((ev.type & EItemEventType.Projectile) != 0 && ev.projectileScene != null)
			{
				return ev.projectileSpeed * ev.projectileLifetimeSeconds * rangeScale;
			}
			if ((ev.type & EItemEventType.Melee) != 0)
			{
				return ev.meleeRange;
			}
		}
		return 0f;
	}

	// Dash press. Dash is a runner-driven action (not item-backed): the press
	// runs gates here, then TryStart fires the dashActionProfile so its
	// ApplyMotion / ApplyStatusEffect / PlayAnim / fx events drive the motion,
	// i-frames, animation, and AV cues. Cooldown lives on the player rather
	// than on an item because dash isn't an inventory entry. Weapon-Active
	// blocks dash (committed swing); weapon-Charging and interactive-Active
	// are interrupted so the player can dash out of a draw or out of a
	// chest-open prompt.
	void TryStartDash()
	{
		if (data?.dashActionProfile == null || _runner == null)
		{
			return;
		}
		if (_stamina <= 0f)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		if (now < _dashCooldownEndMs)
		{
			return;
		}
		// Prevent dash from cancelling a long fall. Velocity.Y is signed —
		// negative is downward — so this rejects fast descents while allowing
		// upward arcs and gentle falls.
		if (Velocity.Y < -data.dashMaxFallSpeed)
		{
			return;
		}
		// Block dash during a committed weapon swing (Active phase with a
		// weapon profile). Other runner states (Charging, interactive Active)
		// abort cleanly to make room for the dash.
		if (_runner.IsBusy)
		{
			bool weaponActive = _runner.Phase == EActionPhase.Active
				&& _runner.Current.profile != null
				&& _runner.Current.interactiveAction == null;
			if (weaponActive)
			{
				return;
			}
			_runner.TryAbort();
		}
		var context = new ActionContext();
		if (!_runner.TryStart(data.dashActionProfile, context))
		{
			return;
		}
		_dashCooldownEndMs = now + (ulong)(data.dashCooldown * 1000f);
		// Spend stamina unconditionally — stamina is allowed to go negative,
		// and the recharge delay re-arms either way.
		_stamina -= data.dashStaminaCost;
		_staminaRechargeStartMs = now + (ulong)(data.staminaRechargeDelay * 1000f);
		// Dash is an overt action — like the swing/jump/use cluster in
		// ProcessInput's sneak-break list. Cleared here as well so the intent
		// stays local to dash if that list is ever refactored.
		_sneaking = false;
	}

	void TryStartWeaponAction(EInventorySlot slot, string actionName)
	{
		// Committing to an attack always wins over an in-flight movement burst —
		// cancel any active dash and end sprint before the gate. After the
		// cancel the runner is free (dash tier has canAbort=true), so the
		// IsBusy check below only rejects when ANOTHER action is in flight.
		CancelDashAndSprint();
		if (_runner == null || _runner.IsBusy)
		{
			return;
		}
		WeaponState weapon = _inventory?.GetWeapon(slot);
		if (weapon?.data?.actionProfile == null)
		{
			return;
		}
		// Per-weapon ammo gate. maxAmmo > 0 marks an ammo-bearing weapon; an
		// empty one (ammo <= 0) blocks the press even if individual tiers on
		// the profile don't all flag useAmmo — the bow shouldn't dry-fire
		// just because you walked over to its melee-bash tier.
		if (weapon.data.maxAmmo > 0 && weapon.ammo <= 0)
		{
			return;
		}

		// Pre-cooldown press: latch and let ProcessInput's polling fire it when
		// cooldown ends, provided the player is still holding the button.
		// Charging only begins at conversion time, not at the original press —
		// keeping the chargeT timeline anchored to cooldown-end (not press-time)
		// is what the runner expects, and lets the player hold through the tail
		// of the cooldown without burning charge time on the inactive window.
		ulong now = _world?.GameTimeMs ?? 0;
		if (weapon.cooldownExpireMs > now)
		{
			_pendingWeaponPressSlot = slot;
			_pendingWeaponPressActionName = actionName;
			return;
		}
		_pendingWeaponPressSlot = null;
		_pendingWeaponPressActionName = null;

		var context = new ActionContext
		{
			primaryItem = weapon,
			sourceSlot = slot,
		};
		_runner.TryStart(weapon.data.actionProfile, context);
	}

	void ReleaseWeaponAction(EInventorySlot slot)
	{
		if (_runner == null || !_runner.IsBusy)
		{
			return;
		}
		// Only the input that started the in-flight action commits its release.
		if (_runner.Current.context.sourceSlot != slot)
		{
			return;
		}
		_runner.OnInputReleased();
	}

	void TryUseActiveConsumable()
	{
		// Same as TryStartWeaponAction — consumable use is an overt action
		// that ends the movement burst.
		CancelDashAndSprint();
		if (_runner == null || _runner.IsBusy)
		{
			return;
		}
		ItemState item = _inventory?.GetActiveConsumable();
		if (item == null || item.data is not ConsumableData consumableData)
		{
			return;
		}
		ItemActionProfile profile = consumableData.actionProfile;
		if (profile == null)
		{
			return;
		}

		var context = new ActionContext
		{
			verb = EActionVerb.Use,
			primaryItem = item,
			sourceSlot = EInventorySlot.Consumable,
		};
		_runner.TryStart(profile, context);
	}

	void ReleaseUseConsumable()
	{
		if (_runner == null || !_runner.IsBusy)
		{
			return;
		}
		if (_runner.Current.context.sourceSlot != EInventorySlot.Consumable)
		{
			return;
		}
		_runner.OnInputReleased();
	}

	// Starts the interactive's action at `actionIndex` through the runner and
	// stashes (interactive, actionIndex) on the player so the movement-lock
	// and Interacting-anim checks elsewhere can key off _curInteractive.
	// Returns true on a successful start. _PhysicsProcess clears
	// _curInteractive when the runner finishes the action.
	public bool TryStartInteractiveAction(IInteractive interactive, int actionIndex = 0)
	{
		if (_runner == null || _runner.IsBusy || interactive == null)
		{
			return false;
		}
		var actions = interactive.GetActions(this);
		if (actions == null || actionIndex < 0 || actionIndex >= actions.Count)
		{
			return false;
		}
		InteractiveAction action = actions[actionIndex];
		if (action == null)
		{
			return false;
		}
		var context = new ActionContext
		{
			verb = action.verb,
			primaryInteractive = interactive,
			interactiveActionIndex = actionIndex,
			supportingItems = GatherSupportingItems(action.requirements),
		};
		if (!_runner.TryStart(action, context))
		{
			return false;
		}
		SetCurInteractive(interactive, actionIndex);
		return true;
	}

	// Walk a requirements list and gather any supporting items the action
	// needs from the inventory (e.g. lockpicks). Currently only resolves
	// HasReagentRequirement; future requirement types may add more. Returns
	// null when nothing matches — saves a list allocation in the common case.
	System.Collections.Generic.List<ItemState> GatherSupportingItems(Godot.Collections.Array<ActionRequirement> requirements)
	{
		if (requirements == null || _inventory == null)
		{
			return null;
		}
		System.Collections.Generic.List<ItemState> result = null;
		for (int r = 0; r < requirements.Count; r++)
		{
			if (requirements[r] is not HasReagentRequirement reagentReq) { continue; }
			if (reagentReq.reagent == null) { continue; }
			foreach (ItemState item in _inventory.EnumerateAll())
			{
				if (item != null && item.data == reagentReq.reagent)
				{
					result ??= new System.Collections.Generic.List<ItemState>();
					if (!result.Contains(item))
					{
						result.Add(item);
					}
				}
			}
		}
		return result;
	}

	// IActionActor — what ActionRunner and ItemEventHandlers read from the
	// player. Position / Forward use the player's body transform; Forward
	// matches the existing aim direction (basis Z axis, same as
	// PlayerWeapon's old Melee/Hitscan code), composed with the live aim
	// pitch so ranged hitscan / projectile fire along the auto-aimed
	// elevation. The player body itself only rotates around Y — pitch lives
	// purely in this composition so the sprite, capsule, and basis stay flat.
	public Vector3 ActorWorldPosition => GlobalPosition;
	public Vector3 ActorForward
	{
		get
		{
			Vector3 horizontal = GlobalTransform.Basis.Z;
			if (_aimPitchRadians == 0f)
			{
				return horizontal;
			}
			// basis.Z is always horizontal (player rotates around Y only), so
			// composing with world Up by sin/cos of pitch produces a unit
			// vector tilted in the vertical plane along the facing.
			return horizontal * Mathf.Cos(_aimPitchRadians) + Vector3.Up * Mathf.Sin(_aimPitchRadians);
		}
	}
	public float AimPitchRadians => _aimPitchRadians;
	// Updated each physics tick by UpdateAimAssist. Zero when not aiming with
	// a ranged weapon that authored a pitch range; otherwise the smoothed
	// elevation angle (radians, positive = up) toward the assist target.
	float _aimPitchRadians;

	// Vertical chest-pivot offset above feet. Must match the +Up shift the
	// hitscan and projectile handlers apply to ActorWorldPosition so the
	// assist origin and the actual shot share an origin.
	const float AimOriginHeight = 1f;

	// Reused across ticks to avoid allocating the candidate list every frame.
	List<Mob> _aimAssistScratch;

	// Per-physics-tick aim assist for ranged weapons. Stateless: every tick
	// reads the stick-driven yaw, picks the best mob inside the weapon's
	// yaw + pitch cones (LOS-checked), and applies a static bias to yaw +
	// pitch. Nothing accumulates across ticks — the effect is a pure
	// spatial function of (stickYaw, target).
	//
	// Curve: bias = strength × smoothstep-falloff(yawDistanceOutsideSilhouette).
	// Inside the target's silhouette (yaw delta ≤ halfWidth) the bias is at
	// max strength; at the cone edge it's zero. The same scalar drives both
	// yaw and pitch so they fade in/out together as the player pans across
	// the target.
	//
	// Target selection scores by max(0, sqrt(Δyaw² + Δpitch²) − halfWidth);
	// targets the aim already covers tie at 0 and the closer one wins
	// (the "aiming at the target ⇒ closer wins" rule). Distance is the
	// tiebreak.
	//
	// Must run AFTER the stick-driven rotation block in _PhysicsProcess so
	// the cone is evaluated against the just-applied yaw and the bias lands
	// before _runner.Tick reads ActorForward.
	void UpdateAimAssist()
	{
		if (!_aiming)
		{
			_aimPitchRadians = 0f;
			return;
		}
		WeaponData weaponData = _inventory?.GetWeapon(EInventorySlot.WeaponRight)?.data;
		if (weaponData == null)
		{
			_aimPitchRadians = 0f;
			return;
		}
		float pitchRangeRad = Mathf.DegToRad(weaponData.pitchRangeDegrees);
		float yawAssistRad = Mathf.DegToRad(weaponData.yawAssistDegrees);
		float strength = Mathf.Clamp(weaponData.aimAssistStrength, 0f, 1f);
		if (yawAssistRad <= 0f || strength <= 0f)
		{
			// Without a yaw cone or any strength there's no assist to apply.
			_aimPitchRadians = 0f;
			return;
		}
		float range = GetWeaponRange(EInventorySlot.WeaponRight);
		World3D world3D = GetWorld3D();
		if (range <= 0f || world3D == null || _world?.MobSpatialHash == null)
		{
			_aimPitchRadians = 0f;
			return;
		}

		Vector3 origin = GlobalPosition + Vector3.Up * AimOriginHeight;
		float stickYaw = Rotation.Y;
		var spaceState = world3D.DirectSpaceState;
		var selfBodyExclude = new Godot.Collections.Array<Rid> { GetRid() };

		_aimAssistScratch ??= new List<Mob>(16);
		_aimAssistScratch.Clear();
		_world.MobSpatialHash.QueryRadius(origin, range, _aimAssistScratch);

		Mob bestMob = null;
		float bestCost = float.PositiveInfinity;
		float bestDistSq = float.PositiveInfinity;
		float bestPitch = 0f;
		float bestYaw = 0f;
		float bestHalfWidth = 0f;

		for (int i = 0; i < _aimAssistScratch.Count; i++)
		{
			Mob mob = _aimAssistScratch[i];
			if (mob == null || !mob.alive || mob.burrowed)
			{
				continue;
			}
			// Assist gates on discovery — undiscovered mobs aren't yet "real"
			// to the player's awareness, so the bow shouldn't auto-correct
			// toward them. Hidden/Detected mobs still take direct hits, the
			// player just doesn't get assist help to land them.
			if (mob.playerPerceptionState != EPlayerPerceptionState.Discovered)
			{
				continue;
			}
			Vector3 center = mob.AimCenter;
			Vector3 delta = center - origin;
			float horizontalDistSq = delta.X * delta.X + delta.Z * delta.Z;
			if (horizontalDistSq < 1e-6f)
			{
				continue;
			}
			float horizontalDist = Mathf.Sqrt(horizontalDistSq);
			float distSq = horizontalDistSq + delta.Y * delta.Y;
			if (distSq > range * range)
			{
				continue;
			}
			float targetPitch = Mathf.Atan2(delta.Y, horizontalDist);
			if (pitchRangeRad > 0f && Mathf.Abs(targetPitch) > pitchRangeRad)
			{
				continue;
			}
			float targetYaw = Mathf.Atan2(delta.X, delta.Z);
			float deltaYaw = Mathf.AngleDifference(stickYaw, targetYaw);
			if (Mathf.Abs(deltaYaw) > yawAssistRad)
			{
				continue;
			}
			// LOS clip against environment — keeps the assist from acquiring
			// a mob behind a wall. Cheaper than the old two-pass clip because
			// we already have the exact target point; if the env ray hits
			// before reaching AimCenter, the mob is occluded.
			using var envQuery = PhysicsRayQueryParameters3D.Create(origin, center, (uint)ECollisionLayer.Environment);
			envQuery.CollideWithBodies = true;
			envQuery.CollideWithAreas = false;
			envQuery.Exclude = selfBodyExclude;
			var envResult = spaceState.IntersectRay(envQuery);
			if (envResult.Count > 0)
			{
				continue;
			}
			// Width-aware angular cost: mobs the aim already covers score 0
			// and tie-break on distance. clearanceRadius is the authored
			// mob half-width used for path clearance — same physical width
			// as the silhouette we want to "stick to".
			float radius = mob.mobData != null ? mob.mobData.clearanceRadius : 0.4f;
			float halfWidth = Mathf.Atan2(radius, horizontalDist);
			float angular = Mathf.Sqrt(deltaYaw * deltaYaw + targetPitch * targetPitch);
			float cost = Mathf.Max(0f, angular - halfWidth);
			if (cost < bestCost || (cost == bestCost && distSq < bestDistSq))
			{
				bestCost = cost;
				bestDistSq = distSq;
				bestMob = mob;
				bestPitch = targetPitch;
				bestYaw = targetYaw;
				bestHalfWidth = halfWidth;
			}
		}

		if (bestMob == null)
		{
			// No candidate in the cone → no bias at all. The next tick will
			// re-evaluate; this is a pure spatial function of stickYaw and
			// the world, no time-domain memory.
			_aimPitchRadians = 0f;
			return;
		}

		// Curve input: how far the stick yaw is OUTSIDE the target's
		// silhouette. Inside the silhouette → 0 (max assist); at the cone
		// edge → coneExcess (zero assist). Same scalar drives both yaw and
		// pitch bias so they fade in/out together as the player pans across
		// the target.
		float stickDeltaYaw = Mathf.Abs(Mathf.AngleDifference(stickYaw, bestYaw));
		float excessYaw = Mathf.Max(0f, stickDeltaYaw - bestHalfWidth);
		float coneExcess = yawAssistRad - bestHalfWidth;
		float t01 = coneExcess <= 0f ? 1f : (1f - Mathf.SmoothStep(0f, coneExcess, excessYaw));
		float bias = strength * t01;

		if (bias > 0f)
		{
			Rotation = new Vector3(0f, Mathf.LerpAngle(stickYaw, bestYaw, bias), 0f);
		}
		_aimPitchRadians = bias * bestPitch;
	}
	public ulong GameTimeMs => _world?.GameTimeMs ?? 0;
	public uint AttackHurtboxMask => (uint)ECollisionLayer.HurtBox;
	public Rid? SelfHurtBoxRid => _hurtBox?.GetRid();
	public Node3D AttackerNode => this;
	public void PlayAnim(EAnimation anim)
	{
		PlayOneShot(anim);
	}

	// Seeded from an ApplyMotion event in the dash action profile. Direction
	// preference: active move input first (lets the player dash sideways or
	// backward independent of facing); fall back to facing rotation so a
	// stationary dash still goes somewhere. The dash state machine in
	// _PhysicsProcess consumes these fields.
	public void ApplyMotion(float speed, float duration, bool freezeGravity)
	{
		Vector3 dir;
		if (_inputMove.LengthSquared() > 0f)
		{
			dir = _inputMove.Normalized();
		}
		else
		{
			dir = new Vector3(Mathf.Sin(Rotation.Y), 0f, Mathf.Cos(Rotation.Y));
		}
		_dashDir = dir;
		_dashSpeed = speed;
		_dashTimeRemaining = duration;
		_dashFreezeGravity = freezeGravity;
	}
}
