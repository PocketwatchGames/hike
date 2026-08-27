using System.Collections.Generic;
using Godot;

// Input shim that maps weapon-attack input actions to ActionRunner calls.
// All timeline / phase / event-walking logic lives in ActionRunner. The
// generalization to consumable Use, channeled actions, and (phase 5)
// interactives all reuse the same runner, so adding a new input is just
// another entry that constructs a context and calls TryStart.
public partial class Player : CharacterBody3D, IActionActor, IAimTarget
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
				// Arced lobs author reach directly (projectileMaxRange, speed
				// ignored); flat flight derives it as speed × lifetime.
				float baseRange = ev.projectileArcing
					? ev.projectileMaxRange
					: ev.projectileSpeed * ev.projectileLifetimeSeconds;
				return baseRange * rangeScale;
			}
			if ((ev.type & EItemEventType.Melee) != 0)
			{
				return ev.range;
			}
		}
		return 0f;
	}

	// Dash press. Dash is a runner-driven action (not item-backed): the press
	// runs gates here, then TryStart fires the dashActionProfile so its
	// ApplyMotion / ApplyStatusEffect / PlayAnim / fx events drive the motion,
	// i-frames, animation, and AV cues. Cooldown lives on the player rather
	// than on an item because dash isn't an inventory entry. Weapon-Active
	// blocks dash (committed swing) — though a press near the swing's end
	// banks as the queued input instead; weapon-Charging and interactive-
	// Active are interrupted so the player can dash out of a draw or out of a
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
		// A traversal owns position for its span (both branches return early in
		// _PhysicsProcess), so a dash started here would spend stamina and cooldown
		// and move nobody. A fresh press can't reach this — TryTraversalPress claims
		// it first — but a dash banked before the wall and fired by _queuedDash can.
		if (Climbing || Mantling)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		// Committed weapon swing (Active phase with a weapon profile): dash can't
		// cut in, but a press inside the queue window of the swing (and the
		// dash's own cooldown) ending banks it as the queued input — fired by
		// ProcessInput the moment the runner frees, replacing any queued attack
		// tap (newest input wins). Earlier presses simply drop. Checked before
		// the cooldown / fall gates: readiness folds the dash cooldown in, and
		// the fall gate re-evaluates at fire time.
		if (_runner.IsBusy
			&& _runner.Phase == EActionPhase.Active
			&& _runner.Current.profile != null
			&& _runner.Current.interactiveAction == null)
		{
			ulong readyMs = _runner.Current.endMs > _dashCooldownEndMs ? _runner.Current.endMs : _dashCooldownEndMs;
			if (data != null && readyMs <= now + (ulong)(data.weaponQueueWindowSeconds * 1000f))
			{
				ClearQueuedInput();
				// Also drop any pending (still-held) attack press — it PREDATES this
				// dash press, and leaving it latched lets its upcoming release bank a
				// queued tap that out-races the dash at the fire blocks (tap fires
				// first and its clear eats the dash). Newest input wins means the
				// dash beats the older attack press, not just older banked inputs.
				_pendingWeaponPressSlot = null;
				_pendingWeaponPressActionName = null;
				_queuedDash = true;
			}
			return;
		}
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
		// Other busy runner states (Charging, interactive Active) abort cleanly
		// to make room for the dash.
		if (_runner.IsBusy)
		{
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
		// Dash is an overt action — like the swing/use cluster in
		// ProcessInput's sneak-break list. Cleared here as well so the intent
		// stays local to dash if that list is ever refactored.
		_sneaking = false;
	}

	// Resolve the weapon a press on `slot` should drive. Normally the equipped
	// weapon, but an empty melee slot (WeaponLeft) falls back to the player's
	// unarmed weapon so a bare-handed player still punches. The unarmed
	// WeaponState is built once and cached — it carries its own combo / level
	// state like any inventory weapon. Returns null only when the slot is empty
	// and there's no unarmed fallback (or it's the ranged slot).
	WeaponState GetMeleeWeaponOrUnarmed(EInventorySlot slot)
	{
		WeaponState weapon = _inventory?.GetWeapon(slot);
		if (weapon != null)
		{
			return weapon;
		}
		if (slot == EInventorySlot.WeaponMelee && data?.unarmedWeapon != null)
		{
			_unarmedWeapon ??= new WeaponState(data.unarmedWeapon);
			return _unarmedWeapon;
		}
		return null;
	}

	// Drop the banked queued input — attack tap and dash alike (see
	// _queuedWeaponTapSlot / _queuedDash). Called whenever something supersedes
	// it: a fresh attack press (any slot), another overt button, getting hit,
	// or death.
	void ClearQueuedInput()
	{
		_queuedWeaponTapSlot = null;
		_queuedWeaponTapActionName = null;
		_queuedDash = false;
	}

	// Earliest game-time at which a press on `weapon` could actually start: the
	// later of its own cooldown ending and the runner's current action finishing.
	// Unknowable while the runner is Charging (the player decides when to
	// release), so that returns MaxValue — a tap banked then can't promise a
	// bounded wait and fails the queue-window check.
	ulong WeaponReadyTimeMs(WeaponState weapon)
	{
		ulong ready = weapon?.cooldownExpireMs ?? 0;
		if (_runner != null && _runner.IsBusy)
		{
			if (_runner.Phase == EActionPhase.Charging)
			{
				return ulong.MaxValue;
			}
			if (_runner.Current.endMs > ready)
			{
				ready = _runner.Current.endMs;
			}
		}
		return ready;
	}

	void TryStartWeaponAction(EInventorySlot slot, string actionName)
	{
		// A fresh press supersedes any banked queued tap — the newest input wins,
		// whether it's the same weapon (this press re-banks on release if it also
		// lands mid-cooldown) or a different one.
		ClearQueuedInput();
		// Committing to an attack always wins over an in-flight movement burst —
		// cancel any active dash and end sprint before the gate. After the
		// cancel the runner is free (dash tier has canAbort=true), so IsBusy
		// below is true only when ANOTHER action is in flight.
		CancelDash();
		if (_runner == null)
		{
			return;
		}
		WeaponState weapon = GetMeleeWeaponOrUnarmed(slot);
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

		// Can't-start-yet press: the runner is busy (an attack's Active phase, a
		// consumable, an interactive) or this weapon is still cooling — latch as
		// a pending press instead of dropping it. ProcessInput's polling either
		// converts it to a real start when the runner frees and the cooldown ends
		// (button still held), or banks it as a queued tap (released inside the
		// queue window of the weapon becoming ready — see WeaponReadyTimeMs).
		// Charging only begins at conversion time, not at the original press —
		// keeping the chargeT timeline anchored to ready-time (not press-time)
		// is what the runner expects, and lets the player hold through the tail
		// of the previous cycle without burning charge time on the inactive
		// window. Banking while busy is what makes fast mashing land on light
		// attacks whose whole cycle is Active (cooldown 0), and lets a press on
		// the OTHER weapon queue a swap that fires the moment this one finishes.
		ulong now = _world?.GameTimeMs ?? 0;
		if (_runner.IsBusy || weapon.cooldownExpireMs > now)
		{
			_pendingWeaponPressSlot = slot;
			_pendingWeaponPressActionName = actionName;
			return;
		}
		_pendingWeaponPressSlot = null;
		_pendingWeaponPressActionName = null;

		// Point the weapon at the player's own status effects so its weapon-mod reads
		// fold in any active forge upgrade for this slot (a Flaming edge on the melee
		// weapon, Seeking on the bow). The upgrade lives on the player, never composed
		// onto the weapon item — see StatusEffectController.SetWielderUpgradeSource.
		EUpgradeSlot upgradeSlot = slot switch
		{
			EInventorySlot.WeaponMelee => EUpgradeSlot.Melee,
			EInventorySlot.WeaponRanged => EUpgradeSlot.Ranged,
			_ => EUpgradeSlot.None,
		};
		weapon.statusEffects?.SetWielderUpgradeSource(_statusEffects, upgradeSlot);

		var context = new ActionContext
		{
			primaryItem = weapon,
			sourceSlot = slot,
		};
		if (_runner.TryStart(weapon.data.actionProfile, context))
		{
			// Swinging near a triggered hostile commits the player to the fight,
			// releasing a guard companion to attack even before any hit lands.
			TryEngageCombatFromWeaponUse();
			// The wielded weapon pops into the hand and persists there; using
			// the other weapon next swaps it. No-op when it's already the held
			// model (re-swinging the same weapon). Tracked for the anim system so
			// this weapon's WeaponAnimSet drives the stance / charge / attack poses.
			_heldVisual?.SetWeapon(weapon.data.heldModel, weapon.data.wieldHand);
			// Push any mod-authored idle fx (a Flaming sword's flame) onto the
			// in-hand model. Recomputed per draw since the wielded weapon changed.
			_heldVisual?.SetWeaponIdleFx(weapon.statusEffects?.WeaponModIdleFx());
			_wieldedWeapon = weapon;
			ValidateAnimSet(weapon.data.animSet, weapon.data.displayName);
		}
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
		CancelDash();
		if (_runner == null || _runner.IsBusy)
		{
			return;
		}
		ItemState item = _inventory?.GetActiveConsumable();
		if (item == null || item.data is not SpellData spell)
		{
			return;
		}
		ItemActionProfile profile = spell.actionProfile;
		if (profile == null)
		{
			return;
		}
		// Recipe-backed alchemy spell: refuse the cast when the party reagent pool
		// can't afford it (the analog of a weapon's ammo gate). Only gate spells that
		// actually cost reagents — a reagentless spell casts freely.
		SpellData attuned = _inventory.AttunedSpell;
		if (attuned != null && attuned.reagents.Count > 0 && GetSpellAmmo() <= 0)
		{
			return;
		}

		var context = new ActionContext
		{
			verb = EActionVerb.Use,
			primaryItem = item,
			sourceSlot = EInventorySlot.Equipment,
		};
		_runner.TryStart(profile, context);
	}

	void ReleaseUseConsumable()
	{
		if (_runner == null || !_runner.IsBusy)
		{
			return;
		}
		if (_runner.Current.context.sourceSlot != EInventorySlot.Equipment)
		{
			return;
		}
		_runner.OnInputReleased();
	}

	// Drive the lantern's action from its dedicated slot — the Lantern input's
	// counterpart to TryUseActiveConsumable. Runs the lantern's charge profile
	// through the runner: a quick tap fires the low tier (toggle the light,
	// refused by the same HasFuel gate the manual douse uses), while a full hold
	// reaches the fuel-costed heal tier that auto-casts when charged.
	void TryUseLantern()
	{
		CancelDash();
		if (_runner == null || _runner.IsBusy)
		{
			return;
		}
		ItemState item = _inventory?.GetEquipped(EInventorySlot.Lantern);
		if (item?.data is not LanternData lanternData || lanternData.actionProfile == null)
		{
			return;
		}
		var context = new ActionContext
		{
			verb = EActionVerb.Use,
			primaryItem = item,
			sourceSlot = EInventorySlot.Lantern,
		};
		_runner.TryStart(lanternData.actionProfile, context);
	}

	void ReleaseUseLantern()
	{
		if (_runner == null || !_runner.IsBusy)
		{
			return;
		}
		if (_runner.Current.context.sourceSlot != EInventorySlot.Lantern)
		{
			return;
		}
		_runner.OnInputReleased();
	}

	// Drives the crescendo cues while the lantern's held heal spell charges: the
	// player-carried charge glow grows with the hold and the screen shake builds
	// toward the auto-cast, both keyed off the heal tier's charge fraction. The
	// heal tier is identified generically as a Lantern-slot Charging tier with a
	// fuelCost (the toggle tier has none), so a plain light toggle produces no
	// glow or shake. Called each physics tick right after the runner ticks;
	// resolves to zero (glow off, no shake) whenever the heal isn't charging.
	void UpdateLanternHealCharge()
	{
		float t = 0f;
		if (_runner != null
			&& _runner.Phase == EActionPhase.Charging
			&& _runner.Current.context.sourceSlot == EInventorySlot.Lantern
			&& (_runner.Current.selectedTier?.fuelCost ?? 0f) > 0f)
		{
			t = _runner.CurrentChargeT;
		}
		if (_lanternHealLight != null)
		{
			_lanternHealLight.LightEnergy = t * _lanternHealLightPeakEnergy;
			_lanternHealLight.Visible = t > 0f;
		}
		// Build the shake with t² so it stays calm early and rushes in at the
		// end. Fed as a short per-frame impulse (range 0 = full strength) rather
		// than a persistent source, so it needs no scene node and self-clears the
		// instant charging stops. GameCamera.Current is re-checked each call, so
		// this is robust to camera spawn order.
		if (t > 0f && _lanternHealShakePeakMagnitude > 0f)
		{
			float magnitude = t * t * _lanternHealShakePeakMagnitude;
			GameCamera.Current?.Shake?.AddImpulse(magnitude, LanternHealShakeImpulseSeconds, GlobalPosition, 0f, GlobalPosition);
		}
	}

	// Lifetime of each per-frame charge-shake impulse. A hair over two physics
	// frames so successive impulses overlap into a continuous shake, but the tail
	// decays out within ~2 frames once charging ends.
	const float LanternHealShakeImpulseSeconds = 0.05f;

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
	// player. Position / Forward use the player's body transform; Forward is the
	// aim direction (basis Z axis) composed with the live aim pitch so ranged
	// hitscan / projectile fire along the auto-aimed elevation. The player body
	// itself only rotates around Y — pitch lives purely in this composition so
	// the model, capsule, and basis stay flat.
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

	// IAimTarget — world-space body center ranged attackers aim at. Mirrors
	// Mob.AimCenter: the hurtbox collision shape sits chest-height in player.tscn,
	// so its global position is a true center, well above the feet. Cached in
	// _Ready; falls back to the body origin until then.
	public Vector3 AimCenter => _hurtBoxShape != null ? _hurtBoxShape.GlobalPosition : GlobalPosition;
	CollisionShape3D _hurtBoxShape;
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
		WeaponData weaponData = _inventory?.GetWeapon(EInventorySlot.WeaponRanged)?.data;
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
		float range = GetWeaponRange(EInventorySlot.WeaponRanged);
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
			if (mob == null || !mob.CanTarget(weaponData))
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
			// Range gate = true 3D distance, matching the shot's reach exactly. The
			// shot flies a straight line of length `range`, so the hittable set is a
			// sphere of radius `range`; gating the assist on that same sphere means
			// it only ever pitches toward a target the shot can actually reach — no
			// "aim pulls up at it but the arrow falls short" zone. (A per-axis XZ/Y
			// gate would be a taller cylinder whose far-high rim sits out at
			// range*sqrt(2), outside the sphere.) distSq is reused below as the
			// closest-candidate tiebreak.
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
			// a mob behind a wall.
			// we already have the exact target point; if the env ray hits
			// before reaching AimCenter, the mob is occluded.
			using var envQuery = PhysicsRayQueryParameters3D.Create(origin, center, (uint)ECollisionLayer.Solid);
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
		// Pitch has NO manual counterpart — the stick only drives yaw — so the
		// assist must commit fully to the target's elevation, otherwise a steeply
		// elevated target (a perched bird overhead) is unhittable: scaling pitch
		// by the yaw-nudge strength would leave the aim line short of it with no
		// way to finish the aim. Fade it in with cone proximity (t01) only, not
		// `strength`, so it eases in as the player pans onto the target and
		// reaches the true target pitch at full lock.
		_aimPitchRadians = t01 * bestPitch;
	}
	// Device-tagged aim input for the reticle's positional cursor, snapshotted
	// once per reticle frame. The active device travels WITH the value so
	// AimingReticle interprets it correctly without re-querying
	// InputDevice.Current at each read site (the value's MEANING is
	// device-dependent — gamepad is a rate basis, mouse is consumed motion —
	// which is exactly the context a bare Vector2 loses). Camera-yaw-rotated at
	// the input boundary. Directional aim doesn't read this — its heading flows
	// through the player's body facing (_inputLook) — so this serves only the
	// positional/arced path. "Consume" because the mouse value is a per-frame
	// motion delta that this read clears; the gamepad value is an idempotent
	// re-sample of the stick.
	public AimInput ConsumeAimInput()
	{
		if (InputDevice.Current == InputDevice.EDevice.Gamepad)
		{
			// Stick deflection (rate basis); re-sampled each frame, nothing to clear.
			return new AimInput(InputDevice.EDevice.Gamepad, new Vector2(_inputLook.X, _inputLook.Z));
		}
		// Mouse: hand off the accumulated motion delta and reset, so the next frame
		// starts fresh (a frame with no motion contributes zero, not a stale repeat).
		Vector2 delta = new(_mouseAimWorldDelta.X, _mouseAimWorldDelta.Z);
		_mouseAimWorldDelta = Vector3.Zero;
		return new AimInput(InputDevice.EDevice.KeyboardMouse, delta);
	}

	// Snap the player's body yaw to face `worldPos`. Used by the aiming
	// reticle when a charge tier transitions from Positional to Directional
	// so the next directional raycast fires through the previous cursor
	// instead of the previously-held facing. No-op if the target sits on
	// top of the player (sub-millimeter offset → no defined direction).
	public void SnapAimYawToward(Vector3 worldPos)
	{
		Vector3 delta = worldPos - GlobalPosition;
		if (delta.X * delta.X + delta.Z * delta.Z < 1e-6f)
		{
			return;
		}
		Rotation = new Vector3(0f, Mathf.Atan2(delta.X, delta.Z), 0f);
	}

	public ulong GameTimeMs => _world?.GameTimeMs ?? 0;
	public uint AttackHurtboxMask => (uint)ECollisionLayer.HurtBox;
	public Rid? SelfHurtBoxRid => _hurtBox?.GetRid();
	public Node3D AttackerNode => this;
	public float OutgoingDamageMultiplier => _statusEffects?.FoldStat(EStat.OutgoingDamage, 1f) ?? 1f;
	// IActionActor — melee-only damage scale from the hosted member's strength.
	public float MeleeDamageMultiplier => Member?.strength ?? 1f;

	// IActionActor — per-level offense scale from the forge upgrade occupying the
	// firing weapon's slot (Melee/Ranged); other slots (consumables, unarmed) carry
	// no upgrade and resolve to a neutral 1. Scales outgoing damage + delivered
	// buildups via the shared SimData curve. See StatusEffectController.ActiveUpgradeLevel.
	public float OutgoingLevelScale(EInventorySlot slot)
	{
		EUpgradeSlot upgradeSlot = slot switch
		{
			EInventorySlot.WeaponMelee => EUpgradeSlot.Melee,
			EInventorySlot.WeaponRanged => EUpgradeSlot.Ranged,
			_ => EUpgradeSlot.None,
		};
		if (upgradeSlot == EUpgradeSlot.None)
		{
			return 1f;
		}
		int level = _statusEffects?.ActiveUpgradeLevel(upgradeSlot) ?? 0;
		return _world?.SimData?.LevelOutgoingScale(level) ?? 1f;
	}

	// Receiver-side per-level resistance (<=1) from the forge upgrade on the Armor
	// slot, applied to incoming damage (Player.ApplyResistance) and combat buildup
	// (StatusEffectController via the incomingLevelResist callback). Neutral 1 when
	// no Armor upgrade is slotted.
	public float IncomingLevelResist => _world?.SimData?.LevelIncomingResist(_statusEffects?.ActiveUpgradeLevel(EUpgradeSlot.Armor) ?? 0) ?? 1f;
	public ETeam ActorTeam => ETeam.Player;
	// IActionActor — fire any active status effect's on-attack-impact burst at
	// the swing/ray impact point. Shares the controller path with Mob so an
	// enchant authored as a StatusEffectData works identically on the player.
	public void TriggerAttackImpact(Vector3 position) => _statusEffects?.TriggerAttackImpact(this, position);
	// IActionActor — body-carried on-attack projectile mods (a Fairy boon's
	// homing missiles), fired by the Melee / Hitscan handlers regardless of the
	// wielded weapon. Shares the controller path with Mob.
	public Godot.Collections.Array<WeaponModData> BodyOnAttackMods(EWeaponModAttackTrigger trigger, EInventorySlot slot) => _statusEffects?.BodyOnAttackMods(trigger, slot);
	// Positional-aim handlers (DoSpawnAreaEffect) read the live aim cursor
	// off the reticle. Nullable in case Initialize hasn't run; callers check
	// HasAimWorldPosition before reading AimWorldPosition.
	public AimingReticle AimingReticle => _aimingReticle;
	public void PlayAnim(EAnimation anim)
	{
		PlayOneShot(anim);
	}

	// Seeded from an ApplyMotion event. Direction resolves per the event's
	// EMotionDirection: Movement prefers active move input (lets a dash go
	// sideways / backward independent of facing) and falls back to facing
	// when there's no input; Facing always commits to body yaw (a weapon
	// lunge strikes where the player aims, not where the stick is pushed).
	// A negative forwardSpeed (a hop-back / recoil event) is folded into
	// _dashDir so the body travels the reverse axis while _dashSpeed stays a
	// positive magnitude — this keeps the wall-slide reprojection, head-on
	// test, and speed-line emitter (all keyed off _dashDir as the true travel
	// heading) correct. The dash state machine in _PhysicsProcess consumes
	// these fields.
	// `freezeGravity` is ignored: the player cannot leave the ground under their
	// own power, so there is no dash hang to suppress gravity for. Mobs (fliers
	// in particular) still honour it.
	public void ApplyMotion(float forwardSpeed, float duration, bool freezeGravity, EMotionDirection direction)
	{
		Vector3 facing = new Vector3(Mathf.Sin(Rotation.Y), 0f, Mathf.Cos(Rotation.Y));
		Vector3 dir;
		if (direction == EMotionDirection.Movement && _inputMove.LengthSquared() > 0f)
		{
			dir = _inputMove.Normalized();
		}
		else
		{
			dir = facing;
		}
		if (forwardSpeed < 0f)
		{
			dir = -dir;
			forwardSpeed = -forwardSpeed;
		}
		_dashDir = dir;
		_dashSpeed = forwardSpeed;
		_dashTimeRemaining = duration;
		// A dash has just begun — let any held status effect (e.g. the fairy-
		// corpse buff) fire its on-dash burst: a radial knockback + Dizzy
		// shockwave around the player. No-op unless an active effect authors one.
		_statusEffects?.TriggerDashBurst(this, GlobalPosition);
	}
}

// Device-tagged aim input consumed by AimingReticle's positional cursor. The
// device is carried alongside the value because the positional cursor advances
// the two devices by DIFFERENT accumulation laws — and only the device tells
// the reticle which to apply:
//   • Gamepad: Value is the right-stick deflection (camera-yaw-rotated, 0..1).
//     A RATE basis — the cursor integrates it as velocity (× range × speed × dt),
//     so it keeps moving while the stick is held. Range-coupled by design.
//   • Mouse: Value is the cursor's world-XZ delta accumulated since the last
//     reticle frame (camera-yaw-rotated, meters). An absolute DELTA the cursor
//     adds directly — it moves only when the mouse moves, and the gain is
//     range-independent (range only clamps the result).
public readonly record struct AimInput(InputDevice.EDevice Device, Vector2 Value);
