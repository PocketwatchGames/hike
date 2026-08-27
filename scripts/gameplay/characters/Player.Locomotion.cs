using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	// Gates aiming and look-driven rotation. Returns false during a dash so
	// the player commits to facing movement direction for the burst — both
	// _aiming (which drives the aim reticle, ranged routing, gamepad stick
	// fallback) and the rotation block in _PhysicsProcess consult this single
	// function so they can't drift out of sync.
	private bool CanLook()
	{
		return _dashTimeRemaining <= 0f;
	}

	// Centralized dash cancel. Called from attack handlers so committing to a
	// swing always wins over an in-flight dash.
	// TryAbort on the runner only fires AbortActive when the active tier's
	// canAbort is true (set on the dash tier in dash_action.tres), so this
	// also explicitly zeroes the per-actor dash timers — AbortActive only
	// resets the runner's PlayerAction, not Player's physics state.
	private void CancelDash()
	{
		if (_runner != null && _runner.IsBusy && _runner.Current.profile == data?.dashActionProfile)
		{
			_runner.TryAbort();
		}
		_dashTimeRemaining = 0f;
	}

	// Active mantle. While _mantleEndMs is non-zero the player is being carried
	// over a short ledge and normal locomotion is suspended — the same shape as
	// the _mount gate, for the same reason: something other than input owns
	// position this tick.
	//
	// Deadlines are on the SIM clock, not accumulated wall-clock delta: a mantle
	// is a gameplay event, so it has to slow with slow-mo and survive save/load
	// like every other timed world action.
	private ulong _mantleStartMs;
	private ulong _mantleEndMs;
	private Vector3 _mantleFrom;
	private Vector3 _mantleTo;
	// Whether the in-flight mantle ends on a water surface.
	private bool _mantleOntoWater;
	// Throttle for the per-tick `mantle_debug` trace.
	private ulong _mantleLogLastMs;

	// The ledge in front, surfaced through the ordinary interact path so it gets
	// tap-vs-hold, the prompt, and an authored icon for free.
	private MantleInteractive _mantleInteract;
	public MantleInteractive MantleInteract => _mantleInteract ??= new MantleInteractive(this);

	// Set when the interact action's completion event fires. The mantle starts
	// on the NEXT tick rather than inside Complete(): the runner is still
	// tearing the action down at that point, so TryFindMantle would see
	// _runner.IsBusy and refuse.
	private bool _mantlePending;

	public void OnMantleInteractComplete()
	{
		_mantlePending = true;
	}

	// Consume a pending mantle once the runner has finished. Re-queries rather
	// than trusting the candidate captured at press time, so a player who moved
	// during the action gets the ledge in front of them now, or none.
	private void TickPendingMantle()
	{
		if (!_mantlePending)
		{
			return;
		}
		if (_runner != null && _runner.IsBusy)
		{
			return;
		}
		_mantlePending = false;
		// A ledge wins over a wall face where both are offered: the short hop is
		// almost always what was meant, and a climb from the same spot stays
		// available once the player is standing on top of it.
		if (!TryStartMantle())
		{
			TryStartClimb();
		}
	}

	public bool Mantling => _mantleEndMs != 0;

	// True when an interact press here would mantle rather than do nothing —
	// the prompt layer reads this to show a climb hint.
	public bool CanMantle()
	{
		return TryFindMantle(out MantleProbe.Candidate _);
	}

	private bool TryFindMantle(out MantleProbe.Candidate candidate)
	{
		candidate = default;
		if (data == null || _world == null || Mantling)
		{
			return false;
		}
		// Swimming is a legitimate start state: hauling out of water onto a bank
		// is the same traversal, and _grounded is false out there by definition.
		bool swimming = _waterState == EWaterState.Swimming;
		if ((!_grounded && !swimming) || _mount != null)
		{
			return false;
		}
		if (_runner != null && _runner.IsBusy)
		{
			return false;
		}

		// BODY facing, not movement input. The gate at the bottom measures against
		// body yaw, so searching along anything else lets the two disagree — aim
		// at a wall while drifting sideways and the probe looks where you are
		// going while the gate judges where you are looking, so the ledge you are
		// staring at is never offered. Where the player is not aiming the body
		// already turns to follow movement, so this is the same direction anyway.
		Vector3 facing = BodyForward();

		TraversalProfile profile = TraversalProfileForQuery();
		_walkField.Refresh(_world.WorldState, _world, profile, GlobalPosition);

		// Anchor to the field's own surface under the player — the same anchor
		// the ledge guard measures drops from, so the two agree on where "here"
		// is. In water that layer IS the water surface (water columns are
		// standable for the player profile), so climbing out needs no separate
		// height convention.
		if (!_walkField.TryGetSurface(GlobalPosition, out float refY, out bool refIsWater, out bool _))
		{
			return false;
		}
		// ...but that convention only holds for a SWIMMER, who is at the surface.
		// A wading player stands on the BED, often a metre or two below it, and
		// anchoring their bands to the surface shifts every rise by the water's
		// depth — which reads the dry bank beside a river as a descend target and
		// carries the body down into it.
		if (refIsWater && !swimming)
		{
			refY = GlobalPosition.Y;
		}

		// Which way the player is FACING decides up or down where a column offers
		// both: meeting rock at wall height means they are at the foot of
		// something and want to go up, meeting open air means they are at an edge
		// and want to go down. Horizontal facing cannot say "up" or "down" on its
		// own — what it can say is what is in front of it, and that is the same
		// answer.
		Vector3 ahead = GlobalPosition + facing * data.mantleReach;
		bool rockAhead = Blocks.IsSolid(_world.WorldState.GetBlockWorld(
			Mathf.FloorToInt(ahead.X),
			Mathf.FloorToInt(refY + data.mantleMinRise),
			Mathf.FloorToInt(ahead.Z)));

		// In water there is no walking alternative, so any reachable bank counts
		// — no minimum rise. And there is nothing to climb DOWN to from a swim.
		MantleProbe.Settings settings = new(
			data.mantleReach,
			swimming ? 0f : data.mantleMinRise,
			data.mantleMaxRise,
			allowDescend: !swimming,
			preferDescend: !rockAhead);
		if (!MantleProbe.TryFind(_walkField, GlobalPosition, facing, refY, settings, out candidate)
			&& !TryFindWaterEntry(facing, refY, swimming, out candidate))
		{
			return false;
		}

		// Facing gate. The search above can find a ledge the player is merely
		// moving past; this requires they are actually looking at it. Measured
		// against BODY yaw, not the search direction, so strafing past a wall
		// while facing along it offers nothing.
		Vector3 toLedge = candidate.landing - GlobalPosition;
		toLedge.Y = 0f;
		if (toLedge.LengthSquared() < 1e-6f)
		{
			// Degenerate (the ledge is directly overhead or underfoot) — nothing
			// to face, so let it through.
			return true;
		}
		return BodyForward().Dot(toLedge.Normalized()) >= Mathf.Cos(data.mantleFacingAngle);
	}

	// Stepping across into water too deep to wade. This is NOT a ledge, which is
	// why MantleProbe cannot find it: the walk field reports a water column's
	// SURFACE as its standable height, so both sides of a drop-off are the same
	// height and the only thing that changed is the bed under them. A probe that
	// searches by height is blind to it by construction. The ledge barriers stop
	// the player walking in, so without this the wade/swim line has no crossing
	// at all — which is what standing in the shallows pressing dash felt like.
	private bool TryFindWaterEntry(Vector3 facing, float refY, bool swimming,
		out MantleProbe.Candidate candidate)
	{
		candidate = default;
		// Already in it; getting OUT is the ordinary mantle.
		if (swimming)
		{
			return false;
		}
		Vector3 ahead = GlobalPosition + facing * data.mantleReach;
		int wx = Mathf.FloorToInt(ahead.X);
		int wz = Mathf.FloorToInt(ahead.Z);
		if (!_walkField.TryGetSurface(wx, wz, refY, out float y, out bool isWater, out bool isSwim))
		{
			return false;
		}
		if (!isWater || !isSwim)
		{
			return false;
		}
		// Water further off in height than a mantle reaches is a fall or a
		// climb, and those affordances own it.
		if (Mathf.Abs(y - refY) > data.mantleMaxRise)
		{
			return false;
		}
		candidate = new MantleProbe.Candidate(new Vector3(wx + 0.5f, y, wz + 0.5f), y - refY, true);
		return true;
	}

	// Horizontal facing from body yaw. Continuous as the player turns, which is
	// why the climb prompt is placed along it rather than along the direction to
	// the ledge — the ledge is a voxel CENTRE, so that direction steps.
	private Vector3 BodyForward()
	{
		float yaw = Rotation.Y;
		return new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
	}

	// BodyForward for the climb_mark console command, which has to aim at the
	// same wall the climb probe would.
	public Vector3 BodyForwardForDebug() => BodyForward();

	// Yaw the body to look along a horizontal direction, so a traversal doesn't
	// read as a sideways slide.
	private void FaceAlong(Vector3 dir)
	{
		Vector3 flat = new Vector3(dir.X, 0f, dir.Z);
		if (flat.LengthSquared() > 1e-6f)
		{
			Rotation = new Vector3(Rotation.X, Mathf.Atan2(flat.X, flat.Z), Rotation.Z);
		}
	}

	// The carry every timed traversal shares — a mantle, and a climb's attach
	// and release. One axis leads and the other trails: climbing UP leads with
	// the rise so the body clears the lip before moving over it; going DOWN
	// leads with the forward carry so the player steps off the edge and then
	// lowers, rather than sinking through the ledge they are standing on.
	private static Vector3 TraversalCarry(Vector3 from, Vector3 to, float t)
	{
		const float LeadFraction = 0.6f;
		const float TrailStart = 0.25f;
		float lead = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(t / LeadFraction, 0f, 1f));
		float trail = Mathf.SmoothStep(0f, 1f, Mathf.Clamp((t - TrailStart) / (1f - TrailStart), 0f, 1f));

		bool descending = to.Y < from.Y;
		float vertT = descending ? trail : lead;
		float fwdT = descending ? lead : trail;

		return new Vector3(
			Mathf.Lerp(from.X, to.X, fwdT),
			Mathf.Lerp(from.Y, to.Y, vertT),
			Mathf.Lerp(from.Z, to.Z, fwdT));
	}

	// --- Fall-through tripwire (`fall_trace`) -------------------------------
	// Records what owned the player's position each tick and dumps the last
	// couple of seconds the moment the body ends up somewhere with no world
	// under it at all. Exists because falling through the map is only ever seen
	// AFTER the fact — by then the state that caused it is several ticks gone.
	private struct FallTraceEntry
	{
		public ulong ms;
		public Vector3 pos;
		public Vector3 vel;
		public bool grounded;
		public EWaterState water;
		public bool dash;
		public string owner;
	}

	private const int FallTraceLength = 150;
	private readonly FallTraceEntry[] _fallTrace = new FallTraceEntry[FallTraceLength];
	private int _fallTraceNext;
	private int _fallTraceCount;
	private bool _fallTraceFired;
	private int _fallTraceProbeCountdown;
	// Set by whichever branch placed the body this tick; cleared as it is read.
	private string _fallTraceOwner;

	// Tag the current tick with the branch that owns the body's position.
	// Cheap enough to leave in unconditionally: a literal assignment.
	private void FallTraceMark(string owner)
	{
		_fallTraceOwner = owner;
	}

	private void FallTraceTick()
	{
		string owner = _fallTraceOwner ?? (_grounded ? "grounded" : "air");
		_fallTraceOwner = null;
		if (!CVars.fallTrace.Value || _fallTraceFired)
		{
			return;
		}

		_fallTrace[_fallTraceNext] = new FallTraceEntry
		{
			ms = _world?.GameTimeMs ?? 0,
			pos = GlobalPosition,
			vel = Velocity,
			grounded = _grounded,
			water = _waterState,
			dash = _dashTimeRemaining > 0f,
			owner = owner,
		};
		_fallTraceNext = (_fallTraceNext + 1) % FallTraceLength;
		_fallTraceCount = Mathf.Min(_fallTraceCount + 1, FallTraceLength);

		// Only a body already falling hard can be outside the world, and the
		// probe is a long raycast — so gate it on that and rate-limit it.
		if (_grounded || Velocity.Y > -4f)
		{
			return;
		}
		if (--_fallTraceProbeCountdown > 0)
		{
			return;
		}
		_fallTraceProbeCountdown = 10;
		Vector3 from = GlobalPosition;
		using var query = PhysicsRayQueryParameters3D.Create(
			from, from + Vector3.Down * FallTraceProbeDepth, (uint)ECollisionLayer.Solid);
		if (GetWorld3D().DirectSpaceState.IntersectRay(query).Count != 0)
		{
			return;
		}

		_fallTraceFired = true;
		GD.Print($"[fall_trace] NO WORLD BELOW at ({from.X:F2},{from.Y:F2},{from.Z:F2})"
			+ $" — last {_fallTraceCount} ticks, oldest first:");
		int start = (_fallTraceNext - _fallTraceCount + FallTraceLength) % FallTraceLength;
		for (int k = 0; k < _fallTraceCount; k++)
		{
			FallTraceEntry e = _fallTrace[(start + k) % FallTraceLength];
			GD.Print($"  {e.ms} {e.owner,-16} pos=({e.pos.X:F2},{e.pos.Y:F2},{e.pos.Z:F2})"
				+ $" vel=({e.vel.X:F1},{e.vel.Y:F1},{e.vel.Z:F1})"
				+ $" grounded={(e.grounded ? 1 : 0)} water={e.water} dash={(e.dash ? 1 : 0)}");
		}
	}

	// How far down the tripwire looks for any world at all.
	private const float FallTraceProbeDepth = 400f;

	// How far a landing may be lifted to clear geometry before it is refused,
	// and the step the search takes. A dual-contoured surface sits within half a
	// voxel of the grid height the probe reported, so the budget only has to
	// cover that plus the capsule's own margin.
	private const float LandingClearMaxLift = 0.6f;
	private const float LandingClearStep = 0.1f;

	// Whether the movement capsule fits at a candidate landing, nudging it up in
	// small steps if it is shallowly buried. Uses the body's own shape against
	// the real collision world, which is the only thing that knows where the
	// meshed surface actually is.
	private bool TryClearLanding(ref MantleProbe.Candidate candidate)
	{
		Vector3 landing = candidate.landing;
		// "Does the body fit here" is a question ABOUT the world, and a ledge
		// barrier is not part of the world — it is felt and never seen (see
		// ECollisionLayer.LedgeBarrier). Left masked in, the very barrier
		// standing at the lip refuses the traversal meant to get past it.
		uint saved = CollisionMask;
		CollisionMask = saved & ~(uint)ECollisionLayer.LedgeBarrier;
		try
		{
			for (float lift = 0f; lift <= LandingClearMaxLift; lift += LandingClearStep)
			{
				Transform3D at = GlobalTransform;
				at.Origin = new Vector3(landing.X, landing.Y + lift, landing.Z);
				if (!TestMove(at, Vector3.Zero, null, 0.001f, recoveryAsCollision: true))
				{
					candidate = new MantleProbe.Candidate(
						new Vector3(landing.X, landing.Y + lift, landing.Z),
						candidate.rise + lift,
						candidate.ontoWater);
					return true;
				}
			}
		}
		finally
		{
			CollisionMask = saved;
		}
		return false;
	}

	// Begin a mantle if there's a ledge in front. Returns false when there isn't,
	// so the interact handler can fall through to its next meaning.
	private bool TryStartMantle()
	{
		if (!TryFindMantle(out MantleProbe.Candidate candidate))
		{
			return false;
		}

		// A traversal writes GlobalPosition directly for its whole duration, so a
		// landing the capsule does not fit in is a teleport into rock — and the
		// probe reads the nav grid's INTEGER column heights, which the dual
		// contoured mesh only approximates. Verify against the real collision
		// shape and lift the landing clear if it is shallowly buried; refuse
		// outright if it cannot be cleared, so the press falls through to a
		// plain dash instead of burying the player.
		if (!TryClearLanding(ref candidate))
		{
			if (CVars.mantleDebug.Value)
			{
				GD.Print($"[mantle] refused — landing ({candidate.landing.X:F2},"
					+ $"{candidate.landing.Y:F2},{candidate.landing.Z:F2}) is not clear");
			}
			return false;
		}

		_mantleFrom = GlobalPosition;
		_mantleTo = candidate.landing;
		_mantleOntoWater = candidate.ontoWater;
		_mantleStartMs = _world.GameTimeMs;
		_mantleEndMs = _mantleStartMs + (ulong)(data.mantleDuration * 1000f);
		Velocity = Vector3.Zero;
		CancelDash();

		// Face the ledge for the duration so the traversal doesn't read as a
		// sideways slide.
		FaceAlong(_mantleTo - _mantleFrom);

		// Jump is the nearest existing traversal clip; there is no authored
		// mantle animation yet, so this stands in until one exists.
		PlayOneShot(EAnimation.Jump);
		SpawnWorldEffect(_mantleFx);

		if (CVars.mantleDebug.Value)
		{
			GD.Print($"[mantle] start t0={_mantleStartMs} t1={_mantleEndMs} "
				+ $"from=({_mantleFrom.X:F2},{_mantleFrom.Y:F2},{_mantleFrom.Z:F2}) "
				+ $"to=({_mantleTo.X:F2},{_mantleTo.Y:F2},{_mantleTo.Z:F2}) rise={candidate.rise:F2}");
		}
		return true;
	}

	// Carry the player through an in-flight mantle. Rise leads and the forward
	// translation trails it, so the body clears the lip instead of cutting
	// through the corner of it.
	//
	// Mirrors TickMounted: while something other than input owns position, the
	// upkeep that must not stall (status effects, night vision, animation) still
	// ticks. Skipping it would pause DoT and buff timers for the traversal.
	private void TickMantle(float dt)
	{
		TickMantleMotion();
		FallTraceTick();
		_statusEffects?.Tick(dt);
		UpdateNightVisionShaderGlobal();
		UpdateAnimation();
	}

	private void TickMantleMotion()
	{
		ulong now = _world.GameTimeMs;
		ulong span = _mantleEndMs - _mantleStartMs;
		float t = span == 0 ? 1f : Mathf.Clamp((now - _mantleStartMs) / (float)span, 0f, 1f);

		GlobalPosition = TraversalCarry(_mantleFrom, _mantleTo, t);
		Velocity = Vector3.Zero;
		FallTraceMark("mantle");

		if (CVars.mantleDebug.Value && now - _mantleLogLastMs >= 100)
		{
			_mantleLogLastMs = now;
			GD.Print($"[mantle] tick now={now} t={t:F3} pos=({GlobalPosition.X:F2},{GlobalPosition.Y:F2},{GlobalPosition.Z:F2})");
		}

		if (t >= 1f)
		{
			_mantleStartMs = 0;
			_mantleEndMs = 0;
			// A drop into water ends in a swim, not standing on a ledge. Finishing
			// it grounded costs a tick of standing on the surface — long enough to
			// fire a landing sound — before UpdateWaterState takes it back.
			_grounded = !_mantleOntoWater;
			if (CVars.mantleDebug.Value)
			{
				GD.Print($"[mantle] complete at ({GlobalPosition.X:F2},{GlobalPosition.Y:F2},{GlobalPosition.Z:F2})");
			}
		}
	}

	// --- Surface climbing ---------------------------------------------------
	// A climb is a mantle's sustained sibling. The same timed carry pulls the
	// player onto the wall and pushes them back off it; what a mantle has no
	// equivalent of is the input-driven middle, and that is the whole reason
	// this needs a phase where a mantle needs only a pair of deadlines.
	private enum EClimbPhase
	{
		None,
		Entering,
		Attached,
		Exiting,
	}

	private EClimbPhase _climbPhase;
	private Vector3 _climbNormal;
	private Vector3 _climbFrom;
	private Vector3 _climbTo;
	private ulong _climbStartMs;
	private ulong _climbEndMs;
	// Which way the climb cycle is running: +1 up, -1 down, 0 held on a frame.
	// Drives the clip pick (ClimbAnim) and nothing else.
	private int _climbAnimSign;

	// Below this much lean away from the wall the move reads as sideways rather
	// than a descent, so the cycle keeps playing forward.
	private const float ClimbDescendAnimThreshold = 0.1f;

	public bool Climbing => _climbPhase != EClimbPhase.None;

	private ClimbProbe.Settings ClimbSettings()
	{
		return new ClimbProbe.Settings(data.climbReach, data.climbGripHeight);
	}

	// True when an interact press here would attach to a wall — the prompt layer
	// reads this the same way it reads CanMantle.
	public bool CanClimb()
	{
		return TryFindClimb(out ClimbProbe.Attachment _);
	}

	private bool TryFindClimb(out ClimbProbe.Attachment attachment)
	{
		attachment = default;
		if (!ClimbGatesOpen(needsFooting: false))
		{
			return false;
		}

		if (!ClimbProbe.TryFind(_world.WorldState, GlobalPosition, BodyForward(), ClimbSettings(), out attachment))
		{
			return false;
		}

		// Same facing gate a mantle applies, sign-flipped: the wall's outward
		// normal points back at the player, so looking AT it is a negative dot.
		return BodyForward().Dot(attachment.normal) <= -Mathf.Cos(data.climbFacingAngle);
	}

	private bool TryStartClimb()
	{
		if (!TryFindClimb(out ClimbProbe.Attachment attachment))
		{
			return false;
		}
		BeginClimbEntry(attachment, WallAnchor(attachment.voxel, attachment.normal, GlobalPosition), "wall");
		return true;
	}

	// Stepping backwards off the top of a climbable wall onto its face. The
	// inverse approach to TryStartClimb — there the wall is ahead and the player
	// walks into it, here the wall is underfoot and the drop is ahead.
	//
	// Runs LAST in TryTraversalPress, which is what keeps it from stealing short
	// drops: a ledge inside the mantle band is a hop down and reads better as
	// one, so mantle claims it first and only a wall too tall to hop reaches here.
	private bool TryStartClimbDescent()
	{
		if (!TryFindClimbDescent(out ClimbProbe.Attachment attachment, out float feetY))
		{
			return false;
		}

		Vector3 landing = WallAnchor(attachment.voxel, attachment.normal, GlobalPosition);
		landing.Y = feetY;
		BeginClimbEntry(attachment, landing, "lip");
		return true;
	}

	// The query half of TryStartClimbDescent, split out so the prompt can ask
	// the same question without backing over the edge to find the answer.
	// `feetY` is where the body would end up hanging under the lip.
	private bool TryFindClimbDescent(out ClimbProbe.Attachment attachment, out float feetY)
	{
		attachment = default;
		feetY = 0f;
		if (!ClimbGatesOpen(needsFooting: true))
		{
			return false;
		}
		if (!ClimbProbe.TryFindDescent(_world.WorldState, GlobalPosition, BodyForward(),
			ClimbSettings(), out attachment, out feetY))
		{
			return false;
		}

		// Facing gate, sign-flipped from TryFindClimb: the face here points the
		// way the player is GOING (out over the edge), not back at them, so
		// agreement is a positive dot.
		return BodyForward().Dot(attachment.normal) >= Mathf.Cos(data.climbFacingAngle);
	}

	// The gates both climb entries share. Kept apart from the probes so the two
	// cannot drift into disagreeing about when a climb is legal at all.
	// `needsFooting` is for entries that have to be STANDING on something — the
	// lip descent, which backs off ground the player is on. Attaching to a face
	// in front does not: swimming up to a cliff and climbing out of the water is
	// the same move as walking up to one, and the mantle already treats water as
	// a legitimate start state for exactly that reason.
	// Which gate refuses a climb right now, or "open". Names the specific test
	// rather than reporting a bare false — a refusal is never "cannot climb", it
	// is one condition, and swimming in particular fails a different one than
	// standing does.
	public string DescribeClimbGates(bool needsFooting)
	{
		if (data == null) { return "no PlayerData"; }
		if (_world == null) { return "no world"; }
		if (Climbing) { return "already climbing"; }
		if (Mantling) { return "mantling"; }
		if (_mount != null) { return "mounted"; }
		bool swimming = _waterState == EWaterState.Swimming;
		if (needsFooting ? !_grounded : (!_grounded && !swimming))
		{
			return $"no footing (grounded={_grounded} water={_waterState}"
				+ (needsFooting ? ", this entry REQUIRES footing)" : ")");
		}
		if (_runner != null && _runner.IsBusy) { return $"runner busy ({_runner.Phase})"; }
		return "open";
	}

	// Water state, for the climb diagnostic — a swim attach fails differently
	// from a standing one and the console has no other way to see it.
	public string WaterStateForDebug() => _waterState.ToString();

	private bool ClimbGatesOpen(bool needsFooting)
	{
		if (data == null || _world == null || Climbing || Mantling)
		{
			return false;
		}
		if (_mount != null)
		{
			return false;
		}
		bool swimming = _waterState == EWaterState.Swimming;
		if (needsFooting ? !_grounded : (!_grounded && !swimming))
		{
			return false;
		}
		return _runner == null || !_runner.IsBusy;
	}

	private void BeginClimbEntry(in ClimbProbe.Attachment attachment, Vector3 landing, string how)
	{
		_climbNormal = attachment.normal;
		_climbFrom = GlobalPosition;
		_climbTo = landing;
		_climbStartMs = _world.GameTimeMs;
		_climbEndMs = _climbStartMs + (ulong)(data.climbEnterDuration * 1000f);
		_climbPhase = EClimbPhase.Entering;
		Velocity = Vector3.Zero;
		_grounded = false;
		CancelDash();
		// Drop any world interactive that was highlighted. UpdateHighlightInteractive
		// is gated out for the whole climb (the Climbing branch returns before it),
		// so a prompt left standing here hangs on screen until the climb ends —
		// and Dash no longer means "interact", so it reads as a button that does
		// nothing. Mount() clears it for the same reason.
		ClearInteractive();
		// Face the wall, which is opposite the outward normal in both entries —
		// walking into a face and backing over a lip end up in the same pose.
		FaceAlong(-attachment.normal);

		// The carry onto the wall animates in whichever direction it travels:
		// backing over a lip lowers the body, walking onto a face holds a grip.
		_climbAnimSign = ClimbCarrySign(_climbFrom.Y, landing.Y);

		if (CVars.climbDebug.Value)
		{
			GD.Print($"[climb] attach ({how}) voxel=({attachment.voxel.X},{attachment.voxel.Y},{attachment.voxel.Z}) "
				+ $"face={attachment.face} to=({landing.X:F2},{landing.Y:F2},{landing.Z:F2})");
		}
	}

	// Where the body sits while gripping a face. Only the axis THROUGH the wall
	// is anchored; the tangent axis keeps the player's own coordinate, which is
	// what makes climbing sideways glide instead of snapping between the voxel
	// centres the channel is stored on.
	private Vector3 WallAnchor(Vector3I voxel, Vector3 normal, Vector3 current)
	{
		float offset = 0.5f + data.climbWallOffset;
		return new Vector3(
			normal.X != 0f ? voxel.X + 0.5f + normal.X * offset : current.X,
			current.Y,
			normal.Z != 0f ? voxel.Z + 0.5f + normal.Z * offset : current.Z);
	}

	// Sign of a carry's vertical travel — also the direction the climb cycle
	// plays while that carry runs.
	private static int ClimbCarrySign(float fromY, float toY)
	{
		if (toY > fromY)
		{
			return 1;
		}
		if (toY < fromY)
		{
			return -1;
		}
		return 0;
	}

	// The climb slot to show: one authored cycle played forward, reversed, or
	// held (the three clips are baked from the same climb.fbx).
	private EAnimation ClimbAnim()
	{
		if (_climbAnimSign > 0)
		{
			return EAnimation.Climb;
		}
		if (_climbAnimSign < 0)
		{
			return EAnimation.ClimbDown;
		}
		return EAnimation.ClimbIdle;
	}

	// Mirrors TickMantle: while something other than input owns position, the
	// upkeep that must not stall still ticks.
	private void TickClimb(float dt)
	{
		FallTraceMark("climb");
		FallTraceTick();
		if (_climbPhase == EClimbPhase.Attached)
		{
			TickClimbAttached(dt);
		}
		else
		{
			TickClimbCarry();
		}
		// Ledge barriers are invisible walls standing at the top of every drop,
		// and the climb sweep is a real capsule cast, so leaving them masked in
		// would catch the body on nothing the player can see — exactly the class
		// of bug the voxel-grid version could not have had. Recomputed here (the
		// climb keeps _grounded false, so this clears the bit) because the normal
		// tick that maintains it is skipped for the whole climb.
		UpdateLedgeBarrierMask();
		// Locomotion's continuous loops are driven by a method the climb branch
		// returns before, so whatever was running at the moment of attaching
		// would play for the whole climb — the swim loop being the audible one,
		// since attaching out of water is a normal way in.
		UpdateLoopEffect(ref _waterMovementLoop, _waterMovementLoopFx, false);
		UpdateLoopEffect(ref _foliageMovementLoop, _foliageMovementLoopFx, false);
		_statusEffects?.Tick(dt);
		UpdateNightVisionShaderGlobal();
		UpdateAnimation();
	}

	private void TickClimbCarry()
	{
		ulong now = _world.GameTimeMs;
		ulong span = _climbEndMs - _climbStartMs;
		float t = span == 0 ? 1f : Mathf.Clamp((now - _climbStartMs) / (float)span, 0f, 1f);

		GlobalPosition = TraversalCarry(_climbFrom, _climbTo, t);
		Velocity = Vector3.Zero;

		if (t < 1f)
		{
			return;
		}
		if (_climbPhase == EClimbPhase.Entering)
		{
			_climbPhase = EClimbPhase.Attached;
			return;
		}
		EndClimb();
	}

	private void EndClimb()
	{
		_climbPhase = EClimbPhase.None;
		_climbStartMs = 0;
		_climbEndMs = 0;
		Velocity = Vector3.Zero;
		_grounded = true;
	}

	// Climb motion, driven by the COLLIDER rather than by the voxel grid.
	//
	// The grid version quantized every hold to one of four axis faces, which is
	// why uneven rock misbehaved: the body followed a stepped approximation of a
	// surface the mesher had already smoothed, so it faced the wrong way, caught
	// on geometry the collider did not actually have, and snapped whenever the
	// quantized face flipped. Sweeping the real capsule against the real trimesh
	// gets the true surface normal for free.
	//
	// Per tick: build the motion in the wall's own basis, sweep the capsule along
	// it (sliding along whatever it hits and spending the remainder), then confirm
	// from the eye that there is still climbable rock to hold, and adopt its
	// normal. Failing that last step the whole move is rolled back — better to
	// stick for a tick than to slide off into the air.
	// Throttle for the per-tick `climb_debug` trace. 60 Hz of multi-line output
	// buries the one tick that matters.
	private ulong _climbTraceLastMs;

	private bool ClimbTracing()
	{
		if (!CVars.climbDebug.Value || _world == null)
		{
			return false;
		}
		ulong now = _world.GameTimeMs;
		if (now - _climbTraceLastMs < 100)
		{
			return false;
		}
		_climbTraceLastMs = now;
		return true;
	}

	private void TickClimbAttached(float dt)
	{
		Velocity = Vector3.Zero;

		Vector3 right = WallRight(_climbNormal);
		Vector3 wallUp = _climbNormal.Cross(right).Normalized();

		// Pressing INTO the wall climbs, pressing away from it descends. There is
		// no separate vertical axis to read on a dual-stick pad, and it continues
		// the lean the player already used to attach.
		float into = -_inputMove.Dot(_climbNormal);
		float lateral = _inputMove.Dot(right);
		Vector3 motion = (wallUp * into + right * lateral) * data.climbSpeed * dt;

		// Climbing stops AT the waterline. A swimmer can hold the face where it
		// meets the surface — that is how they attach — but hauling yourself down
		// a submerged cliff is not a thing, and the descent would otherwise run
		// as far as the rock stays dressed, which it now does one voxel under.
		if (motion.Y < 0f && _world?.WorldState != null)
		{
			int fy = Mathf.FloorToInt(GlobalPosition.Y + motion.Y);
			if (Blocks.IsWater(_world.WorldState.GetBlockWorld(
				Mathf.FloorToInt(GlobalPosition.X), fy, Mathf.FloorToInt(GlobalPosition.Z))))
			{
				motion.Y = Mathf.Max(motion.Y, fy + 1f - GlobalPosition.Y);
			}
		}

		bool moving = motion.LengthSquared() > 1e-10f;
		// Pressing into the wall runs the cycle forward, pressing away runs it
		// reversed, and a still stick holds one frame of it.
		if (!moving)
		{
			_climbAnimSign = 0;
		}
		else
		{
			_climbAnimSign = into < -ClimbDescendAnimThreshold ? -1 : 1;
		}
		bool trace = moving && ClimbTracing();
		if (trace)
		{
			GD.Print($"[climb] pos=({GlobalPosition.X:F2},{GlobalPosition.Y:F2},{GlobalPosition.Z:F2}) "
				+ $"n=({_climbNormal.X:F2},{_climbNormal.Y:F2},{_climbNormal.Z:F2}) "
				+ $"right=({right.X:F2},{right.Y:F2},{right.Z:F2}) into={into:F2} lat={lateral:F2} "
				+ $"mot=({motion.X:F3},{motion.Y:F3},{motion.Z:F3})");
		}

		// The whole state, not just position. Restoring position while keeping a
		// normal adopted from a glancing ray is what made a failed move
		// IRREVERSIBLE: the body came back but the search cone stayed rotated, so
		// it drifted further every tick until nothing was in front of it.
		Vector3 goodPosition = GlobalPosition;
		Vector3 goodNormal = _climbNormal;

		bool hitWall = false;
		Vector3 sweptNormal = Vector3.Zero;
		if (moving)
		{
			SweepClimbMotion(motion, out sweptNormal, out hitWall, trace);
		}

		// Aim the eye fan down whatever the capsule just hit, if it hit a wall.
		// That is what carries a hold around a concave corner; with nothing hit,
		// the fan searches about the hold already had.
		Vector3 searchAbout = hitWall ? sweptNormal : _climbNormal;
		if (trace)
		{
			GD.Print($"  moved to ({GlobalPosition.X:F2},{GlobalPosition.Y:F2},{GlobalPosition.Z:F2}) "
				+ $"searchAbout=({searchAbout.X:F2},{searchAbout.Y:F2},{searchAbout.Z:F2}) "
				+ $"(from {(hitWall ? "SWEEP" : "current normal")})");
		}

		// The fan DISCOVERS a candidate normal; SettleAgainstWall then has to
		// confirm the rock is straight in front along it. Both must pass.
		bool held = TryFindClimbContact(searchAbout, out Vector3 contactNormal, out Vector3 _, trace)
			&& SettleAgainstWall(contactNormal, dt, trace);
		if (held)
		{
			_climbNormal = contactNormal;
			if (trace)
			{
				GD.Print($"  RESULT adopt n=({contactNormal.X:F2},{contactNormal.Y:F2},{contactNormal.Z:F2})");
			}
		}
		else if (moving)
		{
			GlobalPosition = goodPosition;
			_climbNormal = goodNormal;
			if (trace)
			{
				GD.Print("  RESULT REVERTED to last held state (position and normal)");
			}
		}

		AlignToWall(dt);
	}

	// Settle the body a fixed distance off the rock DIRECTLY along `normal`, and
	// confirm that rock is climbable.
	//
	// This is the post-condition of a successful tick, and it is what makes a
	// climb impossible to strand: after every accepted tick the straight-in ray
	// hits dressed rock, so next tick's ray[0] cannot miss. Without it an
	// off-axis fan ray could re-aim the normal and HoldOffWall would then correct
	// the body ALONG that rotated normal — pushing it sideways instead of toward
	// the wall, a little further every tick, until the climber sat in a nook with
	// the wall 35 degrees off and only undressed rock in reach.
	private bool SettleAgainstWall(Vector3 normal, float dt, bool trace)
	{
		Vector3 eye = GlobalPosition + Vector3.Up * data.climbGripHeight;
		float reach = data.climbWallOffset + data.climbContactReach;

		PhysicsDirectSpaceState3D space = GetWorld3D().DirectSpaceState;
		using var query = PhysicsRayQueryParameters3D.Create(
			eye, eye - normal * reach, (uint)ECollisionLayer.Solid);
		query.CollideWithBodies = true;
		query.CollideWithAreas = false;
		query.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
		Godot.Collections.Dictionary result = space.IntersectRay(query);
		if (result.Count == 0)
		{
			if (trace)
			{
				GD.Print($"  settle: perpendicular ray along ({-normal.X:F2},{-normal.Y:F2},{-normal.Z:F2}) MISS "
					+ "— candidate normal does not face the rock");
			}
			return false;
		}

		var point = (Vector3)result["position"];
		var hitNormal = (Vector3)result["normal"];
		if (!IsClimbableContact(point, hitNormal))
		{
			if (trace)
			{
				GD.Print("  settle: rock straight ahead is not climbable");
			}
			return false;
		}

		HoldOffWall(point, normal, dt);
		return true;
	}

	// Sweep the capsule along `motion`, sliding along each surface met and
	// spending what is left, so a climber crossing a corner keeps the part of
	// their movement that the new face can still take.
	//
	// Reports the last WALL-like surface it ran into. That hit is the whole
	// answer at a concave corner: the eye fan only spans a few tens of degrees
	// about the normal it already has, so a face at right angles is invisible to
	// it — but the capsule runs straight into that face, and its normal is
	// exactly the hold the climber should transfer to. Floors and ceilings met in
	// passing are filtered out by slope, or climbing down onto ground would
	// re-aim the search at the dirt underfoot.
	private void SweepClimbMotion(Vector3 motion, out Vector3 wallNormal, out bool hitWall, bool trace)
	{
		const int MaxSlides = 4;
		wallNormal = Vector3.Zero;
		hitWall = false;

		for (int i = 0; i < MaxSlides; i++)
		{
			KinematicCollision3D hit = MoveAndCollide(motion);
			if (hit == null)
			{
				if (trace)
				{
					GD.Print($"  sweep[{i}] clear, travelled all of ({motion.X:F3},{motion.Y:F3},{motion.Z:F3})");
				}
				return;
			}
			Vector3 normal = hit.GetNormal();
			bool wallLike = Mathf.Abs(normal.Y) <= data.climbWallNormalMaxY;
			if (trace)
			{
				GD.Print($"  sweep[{i}] HIT n=({normal.X:F2},{normal.Y:F2},{normal.Z:F2}) "
					+ $"|y|={Mathf.Abs(normal.Y):F2} wallLike={(wallLike ? "yes" : "no (slope)")} "
					+ $"collider={hit.GetCollider()?.GetType().Name ?? "null"}");
			}
			if (wallLike)
			{
				wallNormal = normal;
				hitWall = true;
			}
			motion = hit.GetRemainder().Slide(normal);
			if (motion.LengthSquared() < 1e-10f)
			{
				return;
			}
		}
	}

	// Fan of rays out of the climber's eye looking for rock to hold. Rays rather
	// than a single probe along the normal because an uneven face falls away from
	// wherever the last normal pointed; spreading the fan across the wall plane
	// finds the surface again instead of declaring the hold lost.
	//
	// Only surfaces whose voxel is marked climbable count, so bare rock beside an
	// ivy patch reads as the edge of the patch — which is what stops a climb
	// wandering off onto undressed cliff.
	private bool TryFindClimbContact(Vector3 aboutNormal, out Vector3 contactNormal, out Vector3 contactPoint, bool trace = false)
	{
		contactNormal = aboutNormal;
		contactPoint = GlobalPosition;
		if (_world?.WorldState == null)
		{
			return false;
		}

		Vector3 right = WallRight(aboutNormal);
		Vector3 wallUp = aboutNormal.Cross(right).Normalized();
		Vector3 eye = GlobalPosition + Vector3.Up * data.climbGripHeight;
		if (trace)
		{
			GD.Print($"  fan from eye=({eye.X:F2},{eye.Y:F2},{eye.Z:F2}) about "
				+ $"({aboutNormal.X:F2},{aboutNormal.Y:F2},{aboutNormal.Z:F2}) spread={data.climbContactFanAngle:F0}deg");
		}
		float spread = Mathf.DegToRad(data.climbContactFanAngle);
		float reach = data.climbWallOffset + data.climbContactReach;

		// Straight in first: on flat wall it answers immediately, and it is the
		// direction whose normal best matches the hold the player already has.
		Span<Vector3> dirs = stackalloc Vector3[5];
		dirs[0] = -aboutNormal;
		dirs[1] = (-aboutNormal).Rotated(wallUp, spread).Normalized();
		dirs[2] = (-aboutNormal).Rotated(wallUp, -spread).Normalized();
		dirs[3] = (-aboutNormal).Rotated(right, spread).Normalized();
		dirs[4] = (-aboutNormal).Rotated(right, -spread).Normalized();

		PhysicsDirectSpaceState3D space = GetWorld3D().DirectSpaceState;
		var exclude = new Godot.Collections.Array<Rid> { GetRid() };
		for (int i = 0; i < dirs.Length; i++)
		{
			using var query = PhysicsRayQueryParameters3D.Create(
				eye, eye + dirs[i] * reach, (uint)ECollisionLayer.Solid);
			query.CollideWithBodies = true;
			query.CollideWithAreas = false;
			query.Exclude = exclude;
			Godot.Collections.Dictionary result = space.IntersectRay(query);
			if (result.Count == 0)
			{
				if (trace)
				{
					GD.Print($"    ray[{i}] dir=({dirs[i].X:F2},{dirs[i].Y:F2},{dirs[i].Z:F2}) MISS (reach {reach:F2})");
				}
				continue;
			}

			var normal = (Vector3)result["normal"];
			var point = (Vector3)result["position"];
			if (trace)
			{
				GD.Print($"    ray[{i}] dir=({dirs[i].X:F2},{dirs[i].Y:F2},{dirs[i].Z:F2}) "
					+ $"HIT p=({point.X:F2},{point.Y:F2},{point.Z:F2}) d={(point - eye).Length():F2} "
					+ $"n=({normal.X:F2},{normal.Y:F2},{normal.Z:F2})");
				if (TryResolveContactVoxel(point, normal, out Vector3I traced))
				{
					GD.Print($"           march->({traced.X},{traced.Y},{traced.Z}) "
						+ ClimbProbe.Describe(_world.WorldState, traced.X, traced.Y, traced.Z,
							ClimbProbe.FromNormal(normal)));
				}
				else
				{
					GD.Print($"           march found no solid within {data.climbContactDepthSearch:F2}m");
				}
			}
			if (!IsClimbableContact(point, normal))
			{
				continue;
			}
			contactNormal = normal;
			contactPoint = point;
			return true;
		}
		return false;
	}

	// Keep the body a fixed distance off whatever it is actually touching.
	//
	// The attach anchor comes from the voxel grid, but the DC hull bulges off the
	// voxel face plane, so a body placed by that arithmetic can start out already
	// inside the mesh — and on uneven rock it drifts nearer and further as it
	// moves along. Correcting only along the contact normal leaves the climber's
	// own movement across the wall untouched, and the per-tick clamp is what
	// keeps a sudden change of surface from reading as a teleport.
	private void HoldOffWall(Vector3 contactPoint, Vector3 normal, float dt)
	{
		Vector3 eye = GlobalPosition + Vector3.Up * data.climbGripHeight;
		float distance = (eye - contactPoint).Dot(normal);
		float correction = data.climbWallOffset - distance;
		float maxStep = data.climbSpeed * dt;
		GlobalPosition += normal * Mathf.Clamp(correction, -maxStep, maxStep);
	}

	// Is the rock under a contact point dressed for climbing? Steps a little way
	// INTO the surface before flooring, because the hit sits exactly on the
	// boundary and floors to whichever voxel the float lands in.
	// Spacing of the inward march below. Fine enough not to skip a voxel.
	private const float ContactMarchStep = 0.15f;

	// Is the rock behind a contact point dressed for climbing?
	private bool IsClimbableContact(Vector3 point, Vector3 normal)
	{
		return TryResolveContactVoxel(point, normal, out Vector3I voxel)
			&& ClimbProbe.IsClimbableFace(_world.WorldState, voxel.X, voxel.Y, voxel.Z,
				ClimbProbe.FromNormal(normal));
	}

	// The SOLID voxel that produced a contact point, found by marching inward
	// along the surface normal.
	//
	// A fixed step does not work, and this cost a debugging round. Dual
	// contouring puts ONE vertex per cell at the density minimizer, so the drawn
	// surface can sit as much as a whole voxel off the air/solid boundary — and
	// it leans furthest at a CONCAVE corner, where the vertex is pulled into the
	// crease. A fixed 0.2 m step therefore floors back into the air voxel and
	// every concave corner reads as NOT-SOLID, while flat and convex rock (whose
	// vertices sit near the boundary) works fine. Marching until rock is actually
	// found is independent of that displacement.
	private bool TryResolveContactVoxel(Vector3 point, Vector3 normal, out Vector3I voxel)
	{
		voxel = default;
		if (_world?.WorldState == null)
		{
			return false;
		}
		int steps = Mathf.Max(1, Mathf.CeilToInt(data.climbContactDepthSearch / ContactMarchStep));
		for (int i = 1; i <= steps; i++)
		{
			Vector3 inside = point - normal * (ContactMarchStep * i);
			var v = new Vector3I(
				Mathf.FloorToInt(inside.X), Mathf.FloorToInt(inside.Y), Mathf.FloorToInt(inside.Z));
			if (Blocks.IsSolid(_world.WorldState.GetBlockWorld(v.X, v.Y, v.Z)))
			{
				voxel = v;
				return true;
			}
		}
		return false;
	}

	// Lateral axis of the wall plane. A normal that has gone near-vertical (an
	// overhang rolling into a ceiling) leaves world-up parallel to it and the
	// cross product degenerate, so fall back to the body's own facing.
	private Vector3 WallRight(Vector3 normal)
	{
		Vector3 right = Vector3.Up.Cross(normal);
		if (right.LengthSquared() < 1e-6f)
		{
			right = BodyForward().Cross(normal);
		}
		return right.LengthSquared() < 1e-6f ? Vector3.Right : right.Normalized();
	}

	// Turn the body to face the wall. Smoothed rather than snapped: on uneven
	// rock the contact normal changes every tick, and following it exactly makes
	// the body jitter.
	private void AlignToWall(float dt)
	{
		// A contact normal that has gone near-vertical (the lip of an overhang)
		// has almost no horizontal component left, so its yaw is numerical noise.
		// Hold the current facing rather than spinning the body on rounding error.
		Vector2 flat = new(_climbNormal.X, _climbNormal.Z);
		if (flat.LengthSquared() < 1e-4f)
		{
			return;
		}

		float targetYaw = Mathf.Atan2(-_climbNormal.X, -_climbNormal.Z);
		float tau = data.climbFaceAlignTime;
		float yaw = tau > 0f
			? Mathf.LerpAngle(Rotation.Y, targetYaw, 1f - Mathf.Exp(-dt / tau))
			: targetYaw;
		Rotation = new Vector3(Rotation.X, yaw, Rotation.Z);
	}

	// Over the top of the wall, using the same band query and the same carry the
	// ledge affordance uses.
	private bool TryClimbTopOut(Vector3 pos)
	{
		if (!TryFindClimbTopOut(pos, out Vector3 landing))
		{
			return false;
		}
		BeginClimbExit(landing);
		return true;
	}

	private bool TryFindClimbTopOut(Vector3 pos, out Vector3 landing)
	{
		landing = default;
		Vector3 into = -_climbNormal;
		int wx = Mathf.FloorToInt(pos.X + into.X * data.climbReach);
		int wz = Mathf.FloorToInt(pos.Z + into.Z * data.climbReach);
		if (!_walkField.TryGetSurfaceInBand(wx, wz,
			pos.Y - data.climbStepOffDistance, pos.Y + data.mantleMaxRise, pos.Y,
			out float y, out bool _))
		{
			return false;
		}
		landing = new Vector3(wx + 0.5f, y, wz + 0.5f);
		return true;
	}

	// Off the face sideways or downwards onto something standable. One query
	// covers both, because both ask the same thing: is the column the body is
	// moving into standable near the height it is moving to.
	private bool TryClimbStepOff(Vector3 pos)
	{
		if (!TryFindClimbStepOff(pos, out Vector3 landing, out bool ontoWater))
		{
			return false;
		}
		// Water is a place to drop INTO, not a ledge to step onto. Carrying the
		// body to the water's surface and marking it grounded lands the player
		// exactly where they were already hanging, which is why releasing over
		// water looked like the button did nothing. Letting go instead drops them
		// in, and the swim state picks them up on the next ordinary tick.
		if (ontoWater)
		{
			ReleaseClimbIntoFall();
			return true;
		}
		BeginClimbExit(landing);
		return true;
	}

	private bool TryFindClimbStepOff(Vector3 pos, out Vector3 landing, out bool ontoWater)
	{
		landing = default;
		ontoWater = false;
		int wx = Mathf.FloorToInt(pos.X);
		int wz = Mathf.FloorToInt(pos.Z);
		if (!_walkField.TryGetSurface(wx, wz, pos.Y, out float y, out ontoWater, out bool _))
		{
			return false;
		}
		if (Mathf.Abs(y - pos.Y) > data.climbStepOffDistance)
		{
			return false;
		}
		landing = new Vector3(pos.X, y, pos.Z);
		return true;
	}

	// Let go with nothing to step onto — over a drop, or over water. Clears the
	// state by hand rather than through EndClimb, which lands the player grounded.
	private void ReleaseClimbIntoFall()
	{
		_climbPhase = EClimbPhase.None;
		_climbStartMs = 0;
		_climbEndMs = 0;
		_grounded = false;
		Velocity = Vector3.Zero;
		if (CVars.climbDebug.Value)
		{
			GD.Print("[climb] released into a fall");
		}
	}

	private void BeginClimbExit(Vector3 landing)
	{
		if (CVars.climbDebug.Value)
		{
			GD.Print($"[climb] release to=({landing.X:F2},{landing.Y:F2},{landing.Z:F2})");
		}
		_climbFrom = GlobalPosition;
		_climbTo = landing;
		_climbStartMs = _world.GameTimeMs;
		_climbEndMs = _climbStartMs + (ulong)(data.climbExitDuration * 1000f);
		_climbPhase = EClimbPhase.Exiting;
		_climbAnimSign = ClimbCarrySign(_climbFrom.Y, landing.Y);
		FaceAlong(landing - _climbFrom);
	}

	// Traversal, as an overload on the Dash button rather than a button of its
	// own. Order is by commitment, most-committed first:
	//   held wall   — release, since a press while hanging can mean nothing else
	//   ledge       — the short hop, nearly always what was meant where a mantle
	//                 and a climb are both offered
	//   wall ahead  — walk into a face and attach
	//   lip underfoot — back over the edge onto the face below
	// The last two cannot both be true (one needs rock ahead, the other air), so
	// their order is only a tie-break on paper. Returns false when none applied,
	// which is what lets the same press fall through and become an ordinary dash.
	public bool TryTraversalPress()
	{
		if (Climbing)
		{
			return TryReleaseClimb();
		}
		if (TryStartMantle())
		{
			return true;
		}
		if (TryStartClimb())
		{
			return true;
		}
		return TryStartClimbDescent();
	}

	// Voluntary let-go. Steps off onto anything standable within reach and
	// otherwise simply drops: a wall the player cannot leave is worse than a fall
	// they chose, and with no jump this press is the only exit from a face that
	// leads nowhere.
	private bool TryReleaseClimb()
	{
		bool trace = CVars.climbDebug.Value;

		// Mid-transition presses are swallowed, not queued. The carry owns position
		// for its span, and cutting it short leaves the body inside the wall it was
		// moving through.
		if (_climbPhase != EClimbPhase.Attached)
		{
			if (trace)
			{
				GD.Print($"[climb] release press ignored — phase is {_climbPhase}, not Attached");
			}
			return true;
		}

		TraversalProfile profile = TraversalProfileForQuery();
		_walkField.Refresh(_world.WorldState, _world, profile, GlobalPosition);

		// Near the top of a wall BOTH answer — the lip is over the head and the
		// ground is within step-off reach — so the stick decides. Holding into
		// the wall is the same input that climbs, and it means up; holding away
		// is the input that descends, and it means down. Neutral keeps the lip,
		// since a press with no input up there reads as pulling up.
		float into = -_inputMove.Dot(_climbNormal);
		bool wantsDown = into < -data.climbReleaseInputDeadzone;
		if (trace)
		{
			bool surfaceHere = _walkField.TryGetSurface(
				Mathf.FloorToInt(GlobalPosition.X), Mathf.FloorToInt(GlobalPosition.Z),
				GlobalPosition.Y, out float sy, out bool sWater, out bool _);
			GD.Print($"[climb] release at ({GlobalPosition.X:F2},{GlobalPosition.Y:F2},{GlobalPosition.Z:F2}) "
				+ $"into={into:F2} wantsDown={wantsDown}; column surface="
				+ (surfaceHere
					? $"y={sy:F2} dy={sy - GlobalPosition.Y:F2} water={sWater} (max {data.climbStepOffDistance:F2})"
					: "NONE"));
		}
		bool released = wantsDown
			? TryClimbStepOff(GlobalPosition) || TryClimbTopOut(GlobalPosition)
			: TryClimbTopOut(GlobalPosition) || TryClimbStepOff(GlobalPosition);
		if (released)
		{
			if (trace)
			{
				GD.Print($"  released to ({_climbTo.X:F2},{_climbTo.Y:F2},{_climbTo.Z:F2})");
			}
			return true;
		}

		ReleaseClimbIntoFall();
		return true;
	}

	// What a Dash press would traverse to from here, refreshed once per
	// tick and read by the ClimbHUD. Every branch runs the SAME find the press
	// runs, in the same order — a prompt that disagrees with the button is worse
	// than no prompt — so this is a preview, never a second opinion.
	private ETraversalPreview _traversalPreview;
	private Vector3 _traversalPromptAnchor;
	private bool _traversalPromptAnchorValid;

	public ETraversalPreview TraversalPreview => _traversalPreview;

	// Where the prompt is drawn. Meaningless while TraversalPreview is None.
	public Vector3 TraversalPromptPosition => _traversalPromptAnchor;

	// Called ahead of every early return in _PhysicsProcess, so riding a boat or
	// being carried through a traversal clears the prompt instead of freezing the
	// last one on screen.
	private void UpdateTraversalPreview(float dt)
	{
		ETraversalPreview preview = ETraversalPreview.None;
		// Nobody is looking at an inactive party member's affordances, and the
		// probes below are not free.
		if (!IsActive)
		{
			_traversalPreview = preview;
			_traversalPromptAnchorValid = false;
			return;
		}
		// Height of what the press would put the player on; the prompt floats
		// mantlePromptLift above it.
		float targetY = 0f;

		if (Climbing)
		{
			preview = PreviewClimbRelease(out targetY);
		}
		else if (TryFindMantle(out MantleProbe.Candidate candidate))
		{
			preview = candidate.rise >= 0f ? ETraversalPreview.Up : ETraversalPreview.Down;
			targetY = candidate.landing.Y;
		}
		else if (TryFindClimb(out ClimbProbe.Attachment _))
		{
			// A climb has no landing yet, so the prompt hangs at the grip — which
			// is where the hands go and reads as "up this face".
			preview = ETraversalPreview.Up;
			targetY = GlobalPosition.Y + data.climbGripHeight;
		}
		else if (TryFindClimbDescent(out ClimbProbe.Attachment _, out float feetY))
		{
			preview = ETraversalPreview.Down;
			targetY = feetY;
		}

		_traversalPreview = preview;
		if (preview == ETraversalPreview.None)
		{
			_traversalPromptAnchorValid = false;
			return;
		}

		// Placement is taken from the PLAYER — a fixed offset along body facing —
		// not from the target, whose XZ is a voxel centre and would step a metre
		// sideways as the target cell changes. Height has to sit at the target, so
		// it is the one term that steps, and the only one eased.
		Vector3 target = GlobalPosition + BodyForward() * data.mantlePromptForwardOffset;
		target.Y = targetY + data.mantlePromptLift;
		if (!_traversalPromptAnchorValid)
		{
			_traversalPromptAnchor = target;
			_traversalPromptAnchorValid = true;
			return;
		}
		_traversalPromptAnchor.X = target.X;
		_traversalPromptAnchor.Z = target.Z;
		float tau = data.mantlePromptSmoothTime;
		float k = tau > 0f ? 1f - Mathf.Exp(-dt / tau) : 1f;
		_traversalPromptAnchor.Y = Mathf.Lerp(_traversalPromptAnchor.Y, target.Y, k);
	}

	// On a wall the press is a release, so the preview answers the question
	// TryReleaseClimb asks — including the stick, which is what decides it where
	// both a lip and the ground are in reach. Topping out is the Up arrow;
	// stepping off the face is the Down one, whichever way the ground goes.
	private ETraversalPreview PreviewClimbRelease(out float targetY)
	{
		targetY = 0f;
		// Mid-carry presses are swallowed rather than queued, so there is nothing
		// to promise until the body is actually hanging.
		if (_climbPhase != EClimbPhase.Attached)
		{
			return ETraversalPreview.None;
		}

		_walkField.Refresh(_world.WorldState, _world, TraversalProfileForQuery(), GlobalPosition);
		float into = -_inputMove.Dot(_climbNormal);
		bool wantsDown = into < -data.climbReleaseInputDeadzone;

		if (!wantsDown && TryFindClimbTopOut(GlobalPosition, out Vector3 top))
		{
			targetY = top.Y;
			return ETraversalPreview.Up;
		}
		if (TryFindClimbStepOff(GlobalPosition, out Vector3 off, out bool _))
		{
			targetY = off.Y;
			return ETraversalPreview.Down;
		}
		if (wantsDown && TryFindClimbTopOut(GlobalPosition, out Vector3 topFallback))
		{
			targetY = topFallback.Y;
			return ETraversalPreview.Up;
		}
		// A release with nothing under us is a fall. It still happens on the
		// press; it just isn't an affordance to advertise.
		return ETraversalPreview.None;
	}

	// Whether the LedgeBarrier bit is currently set in our collision mask.
	// Tracked so the toggle is a comparison per tick rather than a native
	// property write, and so the mask authored in player.tscn is left alone
	// while the barriers are off.
	private bool _ledgeBarrierMaskOn;

	private void UpdateLedgeBarrierMask()
	{
		// Barriers exist to stop a WALKING body stepping off an edge, so they
		// apply only while grounded. Airborne they would be invisible walls in
		// mid-air — catching a fall against the cliff it just left, blocking a
		// knockback arc, or stopping a launch dead. Anything already off the
		// ground has committed to its trajectory and the barrier has no business
		// in it. Swimming is covered too, since that forces _grounded false.
		bool want = _grounded;
		if (want == _ledgeBarrierMaskOn)
		{
			return;
		}
		_ledgeBarrierMaskOn = want;
		uint bit = (uint)ECollisionLayer.LedgeBarrier;
		CollisionMask = want ? (CollisionMask | bit) : (CollisionMask & ~bit);
	}

	// Player-local standability memo over the shared WalkabilityGrid sampler.
	private readonly PlayerWalkField _walkField = new();
	private TraversalProfile _traversalProfile;
	private bool _traversalProfileBuilt;

	// Describes the player's body to WalkabilityGrid — the same sampler mobs
	// resolve standability through, so the guard can never disagree with where a
	// mob will path. maxStepHeight / maxFallHeight are read only by
	// LocalPathfinder's edge expansion, never by SampleColumn, so they don't
	// affect this query; they're set permissive so nothing is hidden from it.
	public TraversalProfile TraversalProfileForQuery()
	{
		if (!_traversalProfileBuilt)
		{
			_traversalProfile = new TraversalProfile(
				1,
				WalkabilityGrid.SurfaceSearchRadius,
				data.navClearanceRadius,
				data.navVerticalClearance,
				data.swimDepthThreshold);
			_traversalProfileBuilt = true;
		}
		return _traversalProfile;
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
			// Airborne isn't a slope — bleed the estimate to flat so a fall
			// doesn't leave a stale grade biasing the next grounded step.
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

	// Hand off from dash into normal motion. Velocity direction is preserved
	// — the magnitude is clamped horizontally to the dash-exit speed
	// (dashExitSpeed on land, swimDashExitSpeed in water). Airborne dashes
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
			cap = data.dashExitSpeed;
		}
		else if (_waterState == EWaterState.Swimming)
		{
			cap = data.swimDashExitSpeed;
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

		int voxelAtFeet = _world.WorldState.GetBlockWorld(fx, fy, fz);
		if (!Blocks.IsWater(voxelAtFeet) || data == null)
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
		while (Blocks.IsWater(_world.WorldState.GetBlockWorld(fx, topY + 1, fz)))
		{
			topY++;
		}
		int bottomY = fy;
		while (Blocks.IsWater(_world.WorldState.GetBlockWorld(fx, bottomY - 1, fz)))
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
