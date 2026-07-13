using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	// Gates aiming and look-driven rotation. Returns false during dash and
	// sprint so the player commits to facing movement direction during the
	// burst — both _aiming (which drives the aim reticle, ranged routing,
	// gamepad stick fallback) and the rotation block in _PhysicsProcess
	// consult this single function so they can't drift out of sync.
	private bool CanLook()
	{
		return _dashTimeRemaining <= 0f && !_sprinting;
	}

	// Recompute _sprinting each tick from current state. Sprint engages when
	// Dash is held past the initial dash burst with move input AND the
	// player has stamina to spend; once stamina hits zero, sprint drops
	// entirely (no speed boost, no anim, no continuing drain). After an
	// exhaustion drop the player must RELEASE Dash and press it again to
	// re-engage — holding the button through a stamina refill won't
	// re-enter sprint. The existing staminaRechargeDelay still gates when
	// the bar starts refilling after sprint ends. Disabled while airborne
	// on land (no "air sprint") — swimming keeps its own sprint variant
	// since it's a continuous surface contact, not an arc.
	private void UpdateSprintState()
	{
		bool oldSprinting = _sprinting;
		bool dashHeld = Input.IsActionPressed("Dash");
		if (!dashHeld)
		{
			_sprintLockout = false;
		}
		if (data == null)
		{
			_sprinting = false;
			return;
		}
		bool runnerBlocks = _runner != null
			&& _runner.IsBusy
			&& _runner.Current.profile != data.dashActionProfile;
		bool surfaceAllowsSprint = _grounded || _waterState == EWaterState.Swimming;
		bool wantsSprint = dashHeld
			&& _dashTimeRemaining <= 0f
			&& _inputMove.LengthSquared() > 0.0001f
			&& _curInteractive == null
			&& !runnerBlocks
			&& surfaceAllowsSprint
			&& _stamina > 0f
			&& !_sprintLockout;
		_sprinting = wantsSprint;
		// Latch the exhaustion lockout when sprint ends because stamina
		// hit zero while the button was still held. Won't fire on a
		// voluntary release (dashHeld is false there) or on a context
		// change like an attack (stamina would still be positive).
		if (oldSprinting && !_sprinting && _stamina <= 0f && dashHeld)
		{
			_sprintLockout = true;
		}
	}

	// Centralized cancel for dash-and-sprint. Called from attack handlers so
	// committing to a swing always wins over an in-flight movement state.
	// TryAbort on the runner only fires AbortActive when the active tier's
	// canAbort is true (set on the dash tier in dash_action.tres), so this
	// also explicitly zeroes the per-actor dash timers — AbortActive only
	// resets the runner's PlayerAction, not Player's physics state.
	private void CancelDashAndSprint()
	{
		if (_runner != null && _runner.IsBusy && _runner.Current.profile == data?.dashActionProfile)
		{
			_runner.TryAbort();
		}
		_dashTimeRemaining = 0f;
		_sprinting = false;
	}

	private void UpdateTerrainSpeed(float dt)
	{
		_terrainSpeed = 1f;
		float modifier = data != null ? data.foliageSpeedModifier : 1f;
		foreach (Foliage foliage in _foliageCollisions)
		{
			// Scale the foliage slow by this actor's susceptibility: 1 = full
			// slow, 0 = unaffected, intermediate = partial.
			float slowed = Mathf.Lerp(1f, foliage.speed, modifier);
			_terrainSpeed = Mathf.Min(_terrainSpeed, slowed);
		}

		// Fold in the ground block's own speed multiplier (mud slows, road
		// speeds up). Separate axis from the foliage floor above — multiplied,
		// not min'd — so a road through tall grass nets the two effects.
		BlockData ground = GroundTypeResolver.ResolveBlock(_world?.WorldState, GlobalPosition);
		if (ground != null)
		{
			_terrainSpeed *= ground.speedMultiplier;
		}

		_terrainSpeed *= SlopeSpeedFactor(dt);
	}

	// Speed scale for running up/down hills: >1 going downhill, <1 uphill, 1 on
	// the flat. The bonus/penalty are authored AT the steepest walkable slope
	// (FloorMaxAngle) and scale linearly with the grade. Directional, so it can't
	// be baked into the static pathfinding cost — recomputed per tick here and
	// folded into _terrainSpeed.
	//
	// We can't read the slope from GetFloorNormal(): at this point in the tick
	// IsOnFloor() is false (it reflects the prior MoveAndSlide), and voxel floors
	// are flat tops with vertical walls anyway, so the normal is ~straight up
	// even on a hill. Instead we measure the grade the body is actually walking —
	// vertical over horizontal displacement since last tick — smoothed, since the
	// player climbs stepped voxels in bursts (flat tread, then a riser).
	private Vector3 _slopePrevPos;
	private bool _slopePrevPosInit;
	private float _slopeGrade; // smoothed dY/dHoriz; + uphill, - downhill

	// Below this per-tick horizontal step (m), don't refresh the grade — standing
	// still or pure vertical motion carries no slope signal.
	private const float SlopeMinHorizStep = 0.005f;
	// Smoothing time constant (s) for the grade estimate. Long enough to ride out
	// the per-step Y bursts of stepped terrain, short enough to track a real
	// slope change within a stride.
	private const float SlopeGradeTau = 0.25f;

	private float SlopeSpeedFactor(float dt)
	{
		if (data == null)
		{
			return 1f;
		}
		Vector3 pos = GlobalPosition;
		if (!_slopePrevPosInit)
		{
			_slopePrevPos = pos;
			_slopePrevPosInit = true;
			return 1f;
		}
		Vector3 d = pos - _slopePrevPos;
		_slopePrevPos = pos;
		float horiz = Mathf.Sqrt(d.X * d.X + d.Z * d.Z);

		float k = dt > 0f ? 1f - Mathf.Exp(-dt / SlopeGradeTau) : 1f;
		if (_grounded && horiz > SlopeMinHorizStep)
		{
			_slopeGrade = Mathf.Lerp(_slopeGrade, d.Y / horiz, k);
		}
		else if (!_grounded)
		{
			// Airborne (jump / fall) isn't a slope — bleed the estimate to flat so
			// a leap doesn't leave a stale grade biasing the next grounded step.
			_slopeGrade = Mathf.Lerp(_slopeGrade, 0f, k);
		}

		if (!_grounded)
		{
			SlopeDebug($"airborne grade={_slopeGrade:F3}", 1f);
			return 1f;
		}
		float maxGrade = Mathf.Tan(FloorMaxAngle);
		if (maxGrade < 0.0001f)
		{
			return 1f;
		}
		// s: + uphill, - downhill, magnitude = fraction of the max walkable grade.
		float s = Mathf.Clamp(_slopeGrade / maxGrade, -1f, 1f);
		// Ease the magnitude so shallow grades already read strongly and the curve
		// flattens toward the max slope (exponent < 1). Sign preserved separately.
		float eased = Mathf.Pow(Mathf.Abs(s), data.slopeSpeedEaseExponent);
		float cap = s >= 0f ? data.uphillSpeedPenalty : data.downhillSpeedBonus;
		float factor = 1f - Mathf.Sign(s) * eased * cap;
		SlopeDebug($"grade={_slopeGrade:F3} s={s:F2} eased={eased:F2} ({(s >= 0f ? "uphill" : "downhill")}) factor={factor:F3} horiz={horiz:F3}", factor);
		return factor;
	}

	private ulong _slopeDebugLastMs;
	private void SlopeDebug(string msg, float factor)
	{
		if (!CVars.debugSlopes.Value)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		if (now - _slopeDebugLastMs < 200)
		{
			return;
		}
		_slopeDebugLastMs = now;
		GD.Print($"[slopeSpeed] {msg}");
	}

	// React to walls hit by an in-flight dash. Head-on contact (dash direction
	// within data.dashWallHeadOnAngle of the wall normal) short-circuits the
	// dash, dropping into the glide window. Glancing contact reprojects the
	// dash direction onto the wall plane so the next tick continues at full
	// dash speed along the tangent — MoveAndSlide has already removed the
	// into-wall component from Velocity, but without reprojecting _dashDir the
	// next tick would push back into the wall again. Skips floors and ceilings:
	// step-up / step-down handles ground transitions, and a head-bonk on a
	// ceiling shouldn't kill horizontal momentum.
	// Tear down dash physics at the end of the dash phase: zero the timer
	// and arm the glide window so velocity tapers instead of snapping.
	// Called from natural timeout and the head-on wall short-circuit. The
	// i-frame status effect is runner-managed (applied at t=0 via an
	// ApplyStatusEffect event, auto-expires on its own duration timer), so
	// a wall short-circuit at t<duration leaves a small invuln tail — fine.
	// Airborne wall-jump probe. Sweeps the player's collider
	// wallJumpCheckDistance forward in the movement/yaw direction; on a hit
	// whose normal is steeper than the walkable floor cutoff (cos FloorMaxAngle)
	// and not an overhang (n.Y >= 0), the player's velocity is replaced with
	// the wall-jump kick: vertical = wallJumpSpeedY, horizontal = (wall normal
	// × wallJumpSpeedXZ) + the tangent component of incoming velocity. The
	// normal-aligned kick gives a predictable peel-off independent of approach
	// angle; preserving the full tangent keeps along-wall momentum (Mirror's
	// Edge / Titanfall style) so wall-running into a wall jump reads as
	// continuous rather than rebounding. Gated on Velocity.Y >
	// -wallJumpMaxFallingSpeed so a long fall can't be saved by kicking off a
	// passing wall. Cancels any in-flight dash so the dash velocity override
	// doesn't clobber the kick.
	// Linear approach of horizontal velocity toward a target at a fixed rate
	// (m/s² × dt = max step per call). Used by the water / ground / air input
	// branches to ramp velocity instead of snapping. Caller passes the XZ
	// vectors with Y already zeroed; result still has Y=0 and is recomposed
	// with the existing Velocity.Y by the caller.
	private static Vector3 ApproachXZ(Vector3 currentXZ, Vector3 target, float step)
	{
		Vector3 toTarget = target - currentXZ;
		float toTargetLen = toTarget.Length();
		if (toTargetLen <= step)
		{
			return target;
		}
		return currentXZ + toTarget * (step / toTargetLen);
	}

	private bool TryWallJump()
	{
		if (data == null || !CanWallJump || _world == null || _waterState != EWaterState.None)
		{
			return false;
		}
		if (Velocity.Y <= -data.wallJumpMaxFallingSpeed)
		{
			return false;
		}
		if (_wallJumpAirControlTimer > 0f)
		{
			return false;
		}
		if (_stamina <= 0f)
		{
			return false;
		}

		Vector3 forward;
		if (_inputMove.LengthSquared() > 0.0001f)
		{
			forward = new Vector3(_inputMove.X, 0f, _inputMove.Z).Normalized();
		}
		else
		{
			float yaw = Rotation.Y;
			forward = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
		}

		using KinematicCollision3D hit = MoveAndCollide(forward * data.wallJumpCheckDistance, testOnly: true);
		if (hit == null)
		{
			return false;
		}

		Vector3 n = hit.GetNormal();
		float floorDotMin = Mathf.Cos(FloorMaxAngle);
		if (n.Y >= floorDotMin || n.Y < 0f)
		{
			return false;
		}

		// Decompose incoming horizontal velocity around the wall normal. The
		// into-wall component (Velocity · nHoriz, negative when moving into the
		// wall) is discarded; the tangent (along-wall) component is preserved
		// verbatim and added to a fixed normal-aligned kick. n.Y is in
		// [0, floorDotMin) by the gates above, so nHoriz is guaranteed non-zero.
		Vector3 nHoriz = new Vector3(n.X, 0f, n.Z).Normalized();
		Vector3 incomingXZ = new(Velocity.X, 0f, Velocity.Z);
		Vector3 tangent = incomingXZ - incomingXZ.Dot(nHoriz) * nHoriz;
		Vector3 horiz = nHoriz * data.wallJumpSpeedXZ + tangent;

		_dashTimeRemaining = 0f;
		_dashFreezeGravity = false;

		Velocity = new Vector3(horiz.X, data.wallJumpSpeedY, horiz.Z);
		_grounded = false;
		_coyoteTimeEndMs = 0;
		_jumpHeld = true;
		_wallJumpAirControlTimer = data.wallJumpAirControlTime;
		ConsumeStamina(data.wallJumpStaminaCost);

		PlayOneShot(EAnimation.Jump);
		SpawnWorldEffect(_wallJumpFootFx);
		SpawnWorldEffect(_wallJumpEffortFx);
		return true;
	}

	// Mid-air ("double") jump. Reached from the jump input only while falling —
	// no ground / coyote / swim jump was available and no wall jump landed. Spends
	// one of _airJumpsRemaining (refilled to AirJumpsMax on landing) and relaunches
	// at the standard jumpSpeed, preserving horizontal velocity. Returns false when
	// no charges remain, so the fall continues unbroken.
	private bool TryAirJump()
	{
		if (data == null || _airJumpsRemaining <= 0)
		{
			return false;
		}
		_airJumpsRemaining--;
		Velocity = new Vector3(Velocity.X, data.jumpSpeed, Velocity.Z);
		_grounded = false;
		_coyoteTimeEndMs = 0;
		_jumpHeld = true;
		PlayOneShot(EAnimation.Jump);
		SpawnWorldEffect(_airJumpFx);
		return true;
	}

	// Hand off from dash into normal motion. Velocity direction is preserved
	// — the magnitude is clamped horizontally to the ambient sprint speed
	// (sprintSpeed on land, swimSprintSpeed in water). Airborne dashes
	// return uncapped: air drag + gravity carry the player out of the dash
	// over the next several ticks without an explicit glide window.
	private void EndDash()
	{
		_dashTimeRemaining = 0f;
		if (data == null)
		{
			return;
		}
		float cap;
		if (_grounded)
		{
			cap = data.sprintSpeed;
		}
		else if (_waterState == EWaterState.Swimming)
		{
			cap = data.swimSprintSpeed;
		}
		else
		{
			return;
		}
		Vector3 horiz = new(Velocity.X, 0f, Velocity.Z);
		float horizSpeed = horiz.Length();
		if (horizSpeed > cap && horizSpeed > 0.001f)
		{
			Vector3 capped = horiz * (cap / horizSpeed);
			Velocity = new Vector3(capped.X, Velocity.Y, capped.Z);
		}
	}

	// Resolves the player's current slope contact in two bands:
	//   _sliding         — strict steep band [slideSurfaceMinNormalY, cos(FloorMaxAngle))
	//   _onSkateSurface  — extended skate band [slideSurfaceMinNormalY, skateContinueMaxNormalY)
	// _sliding drives the puff FX and skate initiation; _onSkateSurface is the
	// superset that keeps skate momentum alive through walkable ramp runouts.
	// _slideNormal tracks the most-upright surface in the extended band so
	// ApplySkatingMotion can project gravity onto it regardless of which
	// sub-band the player is currently riding.
	private void UpdateSlideState()
	{
		if (data == null || _waterState == EWaterState.Swimming)
		{
			_sliding = false;
			_onSkateSurface = false;
			return;
		}

		float floorDotMin = Mathf.Cos(FloorMaxAngle);
		float slideMin = data.slideSurfaceMinNormalY;
		float skateMax = data.skateContinueMaxNormalY;
		bool foundSlide = false;
		bool foundSkateSurf = false;
		Vector3 bestNormal = Vector3.Up;
		float bestY = -1f;

		int count = GetSlideCollisionCount();
		for (int i = 0; i < count; i++)
		{
			KinematicCollision3D col = GetSlideCollision(i);
			Vector3 n = col.GetNormal();
			if (n.Y < slideMin || n.Y >= skateMax)
			{
				continue;
			}
			if (n.Y > bestY)
			{
				bestY = n.Y;
				bestNormal = n;
			}
			foundSkateSurf = true;
			if (n.Y < floorDotMin)
			{
				foundSlide = true;
			}
		}

		// Airborne with no extended-band hit this tick — probe directly below
		// to catch surfaces we're about to land on (and to hold contact across
		// the brief gaps between voxel-face transitions on a discontinuous
		// slope). The probe accepts the full extended band; _sliding flips
		// only if the probe lands inside the strict steep band.
		if (!foundSkateSurf && !_grounded)
		{
			using KinematicCollision3D probe = MoveAndCollide(
				Vector3.Down * data.stepHeight, testOnly: true);
			if (probe != null)
			{
				Vector3 n = probe.GetNormal();
				if (n.Y >= slideMin && n.Y < skateMax)
				{
					bestNormal = n;
					foundSkateSurf = true;
					if (n.Y < floorDotMin)
					{
						foundSlide = true;
					}
				}
			}
		}

		_sliding = foundSlide;
		_onSkateSurface = foundSkateSurf;
		if (foundSkateSurf)
		{
			_slideNormal = bestNormal;
		}
	}

	// Skating state machine. Initiates skating when the player lands on a
	// slide surface aligned with the slope's downhill direction with enough
	// inbound momentum; exits when the slope flattens to walkable, contact
	// is lost beyond the grace window, or speed drops below the floor.
	// Jump-driven exit is handled inline in the Jump input handler.
	private void UpdateSkating(bool wasOnFloor, float inboundFallSpeed)
	{
		if (data == null || _world == null)
		{
			return;
		}

		bool wasSkating = _skating;
		UpdateSkatingInner(wasOnFloor, inboundFallSpeed);
		if (wasSkating != _skating && CVars.debugSlopes.Value)
		{
			string ts = System.DateTime.Now.ToString("HH:mm:ss.fff");
			float angle = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(_slideNormal.Y, -1f, 1f)));
			Vector3 horizVel = new(Velocity.X, 0f, Velocity.Z);
			GD.Print(_skating
				? $"[skate] ENTER {ts} slope={angle:F1}° speed={horizVel.Length():F1}m/s fall={inboundFallSpeed:F1}m/s"
				: $"[skate] EXIT  {ts} speed={horizVel.Length():F1}m/s");
		}
	}

	private void UpdateSkatingInner(bool wasOnFloor, float inboundFallSpeed)
	{
		ulong now = _world.GameTimeMs;
		if (!_skating)
		{
			// Initiation requires: just landed (not walked-into a slope),
			// current contact is in the extended skate band (anything
			// steeper than skateContinueMaxNormalY — includes walkable
			// ramps so a moderate slope can still launch a skate when
			// alignment is sharp), inbound horizontal velocity meets the
			// speed floor, the landing was an actual fall (not stepping
			// down a small ledge), and direction aligns with the slope's
			// projected-downhill direction.
			if (_onSkateSurface && !wasOnFloor && inboundFallSpeed >= data.skateInitiationMinFallSpeed)
			{
				Vector3 downhill = Vector3.Down.Slide(_slideNormal);
				Vector3 downhillHoriz = new(downhill.X, 0f, downhill.Z);
				Vector3 horizVel = new(Velocity.X, 0f, Velocity.Z);
				float dhLen = downhillHoriz.Length();
				float velLen = horizVel.Length();
				if (dhLen > 0.01f && velLen >= data.skateInitiationMinSpeed)
				{
					float align = horizVel.Dot(downhillHoriz) / (velLen * dhLen);
					if (align >= data.skateInitiationAlignDot)
					{
						_skating = true;
						_skateContactLostMs = 0;
					}
				}
			}
			return;
		}

		// Exit table.
		//  - Airborne with no slope below: run out the grace window so brief
		//    voxel-face-transition gaps don't drop the state.
		//  - On a steep (unwalkable) slope: never exit on speed — gravity
		//    will rebuild momentum even if the player has stalled or reversed
		//    direction up the slope.
		//  - On a walkable surface (skate-band ramp OR flat ground past the
		//    band): exit only when horizontal speed has decayed below moveSpeed.
		//    Lets the player carry skate momentum across ramp runouts onto
		//    flat ground until friction drains it back into normal-control
		//    territory.
		if (!_grounded && !_onSkateSurface)
		{
			if (_skateContactLostMs == 0)
			{
				_skateContactLostMs = now;
			}
			else if (now - _skateContactLostMs > SkateContactGraceMs)
			{
				_skating = false;
				_skateContactLostMs = 0;
			}
			return;
		}
		_skateContactLostMs = 0;
		if (_sliding)
		{
			return;
		}
		Vector3 horizCheck = new(Velocity.X, 0f, Velocity.Z);
		if (horizCheck.LengthSquared() < data.moveSpeed * data.moveSpeed)
		{
			_skating = false;
		}
	}

	// Skating velocity build. Steers the current XZ heading toward input
	// (yaw-rate limited), applies brake when input opposes heading,
	// integrates slope-tangent gravity into the horizontal component, drains
	// friction, and caps to skateMaxSpeed. Y is left alone here — the
	// airborne gravity branch below adds full gravity, and MoveAndSlide
	// projects out the into-slope component each tick.
	private void ApplySkatingMotion(float dt)
	{
		Vector3 horiz = new(Velocity.X, 0f, Velocity.Z);
		float horizSpeed = horiz.Length();
		Vector3 heading = horizSpeed > 0.001f
			? horiz / horizSpeed
			: new Vector3(Mathf.Sin(Rotation.Y), 0f, Mathf.Cos(Rotation.Y));

		Vector3 inputXZ = new(_inputMove.X, 0f, _inputMove.Z);
		float inputMag = inputXZ.Length();
		if (inputMag > 0.001f && horizSpeed > 0.01f)
		{
			Vector3 inputDir = inputXZ / inputMag;
			float currentYaw = Mathf.Atan2(heading.X, heading.Z);
			float targetYaw = Mathf.Atan2(inputDir.X, inputDir.Z);
			float yawDelta = Mathf.Wrap(targetYaw - currentYaw, -Mathf.Pi, Mathf.Pi);
			float maxStep = data.skateSteerYawRate * inputMag * dt;
			float yawStep = Mathf.Clamp(yawDelta, -maxStep, maxStep);
			float newYaw = currentYaw + yawStep;
			heading = new Vector3(Mathf.Sin(newYaw), 0f, Mathf.Cos(newYaw));

			// Brake when input meaningfully opposes the (newly-steered) heading.
			float align = inputDir.Dot(heading);
			if (align < -data.skateBrakeDotThreshold)
			{
				horizSpeed = Mathf.Max(0f, horizSpeed - data.skateBrakeDecel * -align * dt);
			}
		}

		// Slope-tangent gravity adds momentum along the slope's downhill
		// projection. Adds the XZ part to the heading-scaled velocity; the
		// perpendicular-to-heading component naturally curves the trajectory
		// toward downhill when input is held off-axis. When the player has
		// glided past the skate band onto effectively flat ground we use Up
		// as the normal — the projection collapses to zero tangent gravity,
		// so only friction drains the carried momentum.
		Vector3 surfaceNormal = _onSkateSurface ? _slideNormal : Vector3.Up;
		Vector3 gravityVec = Vector3.Down * _world.SimData.gravity;
		Vector3 gravityAlongSlope = gravityVec - gravityVec.Dot(surfaceNormal) * surfaceNormal;
		Vector3 newHoriz = heading * horizSpeed
			+ new Vector3(gravityAlongSlope.X, 0f, gravityAlongSlope.Z) * dt;

		// Friction is applied to the magnitude after gravity injection so a
		// shallow slope reaches a finite terminal speed.
		float newSpeed = newHoriz.Length();
		if (newSpeed > 0.001f)
		{
			float drop = Mathf.Min(newSpeed, data.skateFriction * dt);
			newSpeed -= drop;
			newHoriz = newHoriz.Normalized() * newSpeed;
		}

		if (newSpeed > data.skateMaxSpeed)
		{
			newHoriz = newHoriz * (data.skateMaxSpeed / newSpeed);
		}

		Velocity = new Vector3(newHoriz.X, Velocity.Y, newHoriz.Z);
	}

	// Publishes floor angle + unwalkable-wall hits to the static Debug* fields
	// so DiagnosticsOverlay can render them, and prints a per-hit log line
	// throttled to changes ≥2° or stale ≥500ms. "Unwalkable" here means an
	// upward-facing surface (n.Y > 0) whose normal is below cos(FloorMaxAngle)
	// — i.e. a slope the body classifies as wall, not floor. Vertical walls
	// (n.Y ≈ 0) and overhangs (n.Y < 0) are skipped: the question is "what
	// ramp face just stopped the climb", not "did we run into a cliff".
	private void UpdateSlopeDebug()
	{
		float floorDotMin = Mathf.Cos(FloorMaxAngle);

		if (IsOnFloor())
		{
			Vector3 fn = GetFloorNormal();
			DebugFloorAngleDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(fn.Y, -1f, 1f)));
		}
		else
		{
			DebugFloorAngleDeg = float.NaN;
		}

		bool moving = _inputMove.LengthSquared() > 0.0001f;
		int count = GetSlideCollisionCount();
		for (int i = 0; i < count; i++)
		{
			using KinematicCollision3D c = GetSlideCollision(i);
			Vector3 n = c.GetNormal();
			if (n.Y <= 0f || n.Y >= floorDotMin)
			{
				continue;
			}
			float angleDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(n.Y, -1f, 1f)));
			Vector3 pos = c.GetPosition();

			DebugLastWallAngleDeg = angleDeg;
			DebugLastWallNormal = n;
			DebugLastWallPosition = pos;
			DebugLastWallHitMs = _world?.GameTimeMs ?? 0;
			DebugHasWallHit = true;

			if (!moving)
			{
				continue;
			}
			bool angleChanged = float.IsNaN(_debugLastLoggedWallAngle)
				|| Mathf.Abs(angleDeg - _debugLastLoggedWallAngle) > 2f;
			ulong nowMs = _world?.GameTimeMs ?? 0;
			bool stale = nowMs == 0 || (nowMs - _debugLastWallLogMs) > 500;
			if (!angleChanged && !stale)
			{
				continue;
			}
			_debugLastLoggedWallAngle = angleDeg;
			_debugLastWallLogMs = nowMs;
			GD.Print($"[slope] wall hit angle={angleDeg:F1}° normal=({n.X:F2},{n.Y:F2},{n.Z:F2}) at ({pos.X:F2},{pos.Y:F2},{pos.Z:F2})");
		}
	}

	private void HandleDashWallCollisions()
	{
		float floorDotMin = Mathf.Cos(FloorMaxAngle);
		float headOnDot = Mathf.Cos(data.dashWallHeadOnAngle);
		int count = GetSlideCollisionCount();
		for (int i = 0; i < count; i++)
		{
			using KinematicCollision3D c = GetSlideCollision(i);
			Vector3 n = c.GetNormal();
			if (Mathf.Abs(n.Y) >= floorDotMin)
			{
				continue;
			}
			float hitDot = -_dashDir.Dot(n);
			if (hitDot >= headOnDot)
			{
				EndDash();
				return;
			}
			Vector3 tangent = _dashDir - _dashDir.Dot(n) * n;
			if (tangent.LengthSquared() > 1e-6f)
			{
				_dashDir = tangent.Normalized();
			}
		}
	}

	// Mobs the player is currently overlapping get nudged toward a target
	// horizontal velocity along the player's direction of travel. The push
	// is a velocity TARGET, not a per-frame impulse — running into a mob
	// for many ticks doesn't compound, so corpses / merchants no longer
	// fly across the map. Skips dead and Freeze-pinned mobs entirely so
	// settled corpses and idle-locked NPCs stay where they are. Player no
	// longer carries the Mob bit in its CollisionMask, so MoveAndSlide
	// can't surface mob contacts; the overlap query against MobSpatialHash
	// is now the only path that turns "player touches mob" into a reaction.
	private static readonly List<Mob> _pushTouchedScratch = [];
	private void PushTouchedMobs()
	{
		if (data == null || data.mobPushStrength <= 0f)
		{
			return;
		}
		MobSpatialHash hash = _world?.MobSpatialHash;
		if (hash == null)
		{
			return;
		}
		Vector3 vel = Velocity;
		vel.Y = 0f;
		float speed = vel.Length();
		if (speed < 0.01f)
		{
			return;
		}
		Vector3 dir = vel / speed;
		// Tangent is dir rotated 90° clockwise in XZ (right-hand). Used to
		// score how off-center the player hit the mob and to apply the
		// "slip" impulse that scoots the mob out of the player's path.
		Vector3 tangent = new Vector3(-dir.Z, 0f, dir.X);
		// Capping the mob's resulting horizontal speed (along the push
		// direction) at speed * mobPushStrength is the fix for the
		// "merchant flies off the map" bug — without it, every physics
		// tick of contact added another mass²-amplified impulse.
		float maxPushSpeed = speed * data.mobPushStrength;
		float maxSlipSpeed = speed * data.mobPushSlip;
		// 1m covers the widest player+mob capsule overlap (player 0.25 +
		// goblin/villager 0.35 = 0.6, padded so a single fast tick doesn't
		// step past the contact band before this runs).
		const float QueryRadius = 1f;
		const float ContactRadius = 0.6f;
		const float ContactRadiusSq = ContactRadius * ContactRadius;
		const float MaxVerticalSeparation = 1.5f;

		_pushTouchedScratch.Clear();
		hash.QueryRadius(GlobalPosition, QueryRadius, _pushTouchedScratch);

		Vector3 playerPos = GlobalPosition;
		for (int i = 0; i < _pushTouchedScratch.Count; i++)
		{
			Mob mob = _pushTouchedScratch[i];
			if (mob == null || !mob.alive || mob.Freeze)
			{
				continue;
			}
			Vector3 toMob = mob.GlobalPosition - playerPos;
			if (Mathf.Abs(toMob.Y) > MaxVerticalSeparation)
			{
				continue;
			}
			float distSq = toMob.X * toMob.X + toMob.Z * toMob.Z;
			if (distSq > ContactRadiusSq)
			{
				continue;
			}
			Vector3 mobVel = mob.LinearVelocity;

			// Forward push: top up the mob's velocity along player heading
			// to maxPushSpeed. No-op if the mob is already going faster.
			float currentAlong = mobVel.X * dir.X + mobVel.Z * dir.Z;
			float deltaAlong = currentAlong < maxPushSpeed ? maxPushSpeed - currentAlong : 0f;

			// Slip: nudge the mob sideways AWAY from the player's path,
			// proportional to how off-center the contact was. A dead-center
			// hit gives no slip; a graze on the edge gives full slip. Sign
			// of lateralOffset picks left vs. right; only push further away
			// (never pull the mob across the player's path).
			float lateralOffset = toMob.X * tangent.X + toMob.Z * tangent.Z;
			float slipScale = Mathf.Clamp(lateralOffset / ContactRadius, -1f, 1f);
			float currentLateral = mobVel.X * tangent.X + mobVel.Z * tangent.Z;
			float targetLateral = maxSlipSpeed * slipScale;
			float deltaLateral = 0f;
			if (slipScale > 0f && currentLateral < targetLateral)
			{
				deltaLateral = targetLateral - currentLateral;
			}
			else if (slipScale < 0f && currentLateral > targetLateral)
			{
				deltaLateral = targetLateral - currentLateral;
			}

			if (deltaAlong == 0f && deltaLateral == 0f)
			{
				continue;
			}
			// ApplyImpulse divides by mass internally, so multiply by mass
			// here to make the resulting velocity change exactly the
			// (deltaAlong, deltaLateral) pair regardless of mob mass.
			Vector3 impulse = (dir * deltaAlong + tangent * deltaLateral) * mob.Mass;
			mob.ApplyImpulse(new Vector3(impulse.X, 0f, impulse.Z));
		}
	}

	public void AddTerrainModifier(Foliage foliage)
	{
		_foliageCollisions.Add(foliage);
	}

	public void RemoveTerrainModifier(Foliage foliage)
	{
		_foliageCollisions.Remove(foliage);
	}

	public void WaterAreaEntered()
	{
		_waterOverlapCount++;
		if (_waterOverlapCount == 1)
		{
			// Pick plunge over splash when the player drops in fast. Velocity.Y
			// at this signal still reflects inbound fall speed — water is an
			// Area3D, not a colliding body, so MoveAndSlide hasn't zeroed Y.
			float fallSpeed = -Velocity.Y;
			PackedScene scene = (fallSpeed >= WaterPlungeSpeedThreshold && _waterPlungeFx != null)
				? _waterPlungeFx
				: _waterEnterSplashFx;
			SpawnWorldEffect(scene);
			OnWaterEnter?.Invoke(this);
		}
	}

	public void WaterAreaExited()
	{
		_waterOverlapCount--;
		if (_waterOverlapCount == 0)
		{
			OnWaterExit?.Invoke(this);
		}
	}

	private void UpdateWaterState()
	{
		EWaterState prev = _waterState;
		int fx = Mathf.FloorToInt(GlobalPosition.X);
		int fy = Mathf.FloorToInt(GlobalPosition.Y);
		int fz = Mathf.FloorToInt(GlobalPosition.Z);

		VoxelType voxelAtFeet = _world.WorldState.GetVoxelWorld(fx, fy, fz);
		if (voxelAtFeet != VoxelType.Water || data == null)
		{
			_waterState = EWaterState.None;
			return;
		}

		// Measure the contiguous water column at this XZ, scanning up and down
		// from the feet voxel so the swim/wade decision is independent of where
		// the player currently sits within the column (e.g. just splashed in and
		// not yet risen to the surface). Identical to Mob.UpdateWaterState and
		// the pathfinder's wade/swim split, so the player and mobs agree on what
		// counts as a swim cell at the same swimDepthThreshold.
		int topY = fy;
		while (_world.WorldState.GetVoxelWorld(fx, topY + 1, fz) == VoxelType.Water)
		{
			topY++;
		}
		int bottomY = fy;
		while (_world.WorldState.GetVoxelWorld(fx, bottomY - 1, fz) == VoxelType.Water)
		{
			bottomY--;
		}
		int columnDepth = topY - bottomY + 1;
		int thresholdVoxels = Mathf.Max(1, Mathf.FloorToInt(data.swimDepthThreshold));
		_waterState = columnDepth >= thresholdVoxels ? EWaterState.Swimming : EWaterState.Shallow;

		_waterSurfaceY = topY + 1;

		// Going over your head breaks sneak — splashing in is plainly audible.
		// Only the swim-edge counts; wading through shallows is fine.
		if (prev != EWaterState.Swimming && _waterState == EWaterState.Swimming)
		{
			_sneaking = false;
		}
	}

	private void ApplyWaterPhysics(float dt)
	{
		float targetY = _waterSurfaceY - data.waterSurfaceOffset;
		float depthBelowSurface = targetY - GlobalPosition.Y;

		if (depthBelowSurface > 0f)
		{
			Velocity += Vector3.Up * Mathf.Min(depthBelowSurface, 1f) * data.buoyancyAcceleration * dt;
		}
		else
		{
			Velocity += Vector3.Down * data.buoyancyAcceleration * 0.5f * dt;
		}

		// Drag to damp vertical oscillation and bleed off horizontal entry
		// momentum. Y uses waterDrag (sized to kill the buoyancy bounce); XZ
		// uses waterHorizontalDrag, applied to the velocity RELATIVE to the
		// local water current so a stationary swimmer in a current drifts
		// downstream rather than being dragged toward zero. The current ×
		// waterCurrentDrag drift target matches the one the swim approach
		// uses (see the EWaterState.Swimming branch in _PhysicsProcess), so
		// at steady-state with no input the player parks at exactly the
		// drift target.
		float horizDecay = 1f - data.waterHorizontalDrag * dt;
		if (horizDecay < 0f)
		{
			horizDecay = 0f;
		}
		Vector3 waterCurrent = _world.WorldState.SampleWaterCurrent(GlobalPosition);
		Vector3 driftTarget = new Vector3(waterCurrent.X, 0f, waterCurrent.Z) * data.waterCurrentDrag;
		float newX = driftTarget.X + (Velocity.X - driftTarget.X) * horizDecay;
		float newZ = driftTarget.Z + (Velocity.Z - driftTarget.Z) * horizDecay;
		Velocity = new Vector3(
			newX,
			Velocity.Y - Velocity.Y * data.waterDrag * dt,
			newZ);

		// Water current is folded into the swim-acceleration target above
		// (see the EWaterState.Swimming branch in _PhysicsProcess) rather
		// than re-added per tick, so input-driven inertia and current drift
		// can't compound across frames.

		// Clamp sinking speed
		if (Velocity.Y < -data.waterSinkSpeed)
		{
			Velocity = new Vector3(Velocity.X, -data.waterSinkSpeed, Velocity.Z);
		}
	}
}
