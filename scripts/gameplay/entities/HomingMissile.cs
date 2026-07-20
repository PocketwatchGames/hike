using Godot;
using System.Collections.Generic;

// Movement-override Projectile. Fired with the normal flat-flight random spread
// (its launch direction/speed come from the firing event), it bleeds that speed
// with drag while a homing impulse toward an acquired enemy ramps in from 0 to
// its max over homingRampSeconds, corkscrews along the way, can switch targets,
// and misses infrequently. Environment collisions only destroy it after an
// initial grace window (environmentArmSeconds) so a shot that briefly clips
// terrain while settling onto its target survives; a valid target is always hit
// regardless. Everything else — collision response, hurtbox damage, weapon mods
// (pierce / lifesteal / chain-lightning / knockback / on-hit buildups), impact
// fx, stuck-arrow loot, and the per-cause follow-up events — runs through the
// base Projectile unchanged.
//
// Spawned through the normal DoProjectile path: point a Projectile ItemEvent's
// projectileScene at a scene rooted on this class. To make a sword swing emit
// missiles, add such a Projectile event to the swing tier's timeline — no code.
// Lifetime-based, not range-based: despawn is driven by projectileLifetimeSeconds.
[GlobalClass]
public partial class HomingMissile : Projectile
{
	// Velocity bleed per second (exponential decay rate). The shot launches at
	// the event's projectileSpeed in its spread direction; drag pulls that
	// initial speed down so the ramped homing impulse dominates mid-flight.
	[Export(PropertyHint.Range, "0,10,0.01")] public float dragPerSecond = 1.5f;

	// Peak homing acceleration (m/s^2) toward the target, reached after
	// homingRampSeconds. Ramps in from 0 so the missile drifts on its spread
	// launch first, then pulls onto the target.
	[Export] public float maxHomingAccel = 60f;
	[Export(PropertyHint.Range, "0.01,2,0.01")] public float homingRampSeconds = 0.5f;

	// Grace window before solid hits destroy the missile. Within it the missile
	// passes through terrain; after it, an environment hit despawns it (firing
	// the impactEvent). Hurtbox hits always apply, armed or not.
	[Export(PropertyHint.Range, "0,2,0.01")] public float environmentArmSeconds = 0.5f;

	// Corkscrew: lateral acceleration (m/s^2) oscillating around the velocity
	// axis, at corkscrewFrequencyHz revolutions per second. 0 amplitude = no
	// corkscrew (straight homing).
	[Export] public float corkscrewAccel = 25f;
	[Export(PropertyHint.Range, "0,10,0.05")] public float corkscrewFrequencyHz = 2f;
	// Per-missile randomization of the corkscrew so a volley doesn't spiral in
	// lockstep: each missile rolls its own phase, frequency, amplitude, and a
	// slight ellipse on spawn, each varied by ±this fraction. 0 = identical
	// uniform circles; ~0.4 = lively, varied spirals.
	[Export(PropertyHint.Range, "0,1,0.01")] public float corkscrewRandomness = 0.4f;

	// Target acquisition: candidates within retargetRadiusMeters are considered;
	// the nearest enemy within acquireConeDegrees of the current heading wins.
	// Re-run every retargetIntervalSeconds so the missile can switch onto a
	// closer / newly-valid target mid-flight.
	[Export] public float retargetRadiusMeters = 18f;
	[Export(PropertyHint.Range, "0,180,1")] public float acquireConeDegrees = 100f;
	[Export(PropertyHint.Range, "0.05,2,0.01")] public float retargetIntervalSeconds = 0.25f;

	// Miss model: a small constant aim wobble (aimJitterDegrees), plus a per-
	// acquisition chance (missChance) to commit to a deliberately deflected aim
	// (missDeflectionDegrees) so the missile cleanly misses now and then.
	// Together with the capped impulse these let a fast crossing target evade.
	[Export(PropertyHint.Range, "0,30,0.5")] public float aimJitterDegrees = 4f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float missChance = 0.1f;
	[Export(PropertyHint.Range, "0,45,0.5")] public float missDeflectionDegrees = 14f;

	private IAimTarget _target;
	private float _retargetTimer;
	// Aim deflection (radians) frozen for the current acquisition: a deliberate
	// miss when this acquisition rolled one, else 0. Re-rolled on each reacquire.
	private float _missYaw;
	private float _missPitch;
	// Per-instance corkscrew randomization, rolled on spawn (see corkscrewRandomness).
	private float _phaseOffset;
	private float _corkAccelScale = 1f;
	private float _corkFreqScale = 1f;
	private float _corkEllipseX = 1f;
	private float _corkEllipseY = 1f;
	// Shared scratch for the spatial-hash query — these tick one-at-a-time on the
	// physics thread, so a single static buffer is safe and avoids per-shot alloc.
	private static List<Mob> _scratch;

	public override void _Ready()
	{
		base._Ready();
		_phaseOffset = GD.Randf() * Mathf.Tau;
		float r = corkscrewRandomness;
		_corkAccelScale = 1f + (float)GD.RandRange(-r, r);
		_corkFreqScale = 1f + (float)GD.RandRange(-r, r);
		_corkEllipseX = 1f + (float)GD.RandRange(-r, r);
		_corkEllipseY = 1f + (float)GD.RandRange(-r, r);
	}

	protected override bool EnvironmentCollisionArmed => _ageSeconds >= environmentArmSeconds;
	protected override bool ReorientToVelocity => true;

	protected override void UpdateVelocity(float dt)
	{
		// Base applies optional gravity (normally 0 for a missile).
		base.UpdateVelocity(dt);

		// Drag — frame-rate-independent exponential bleed of the launch speed.
		if (dragPerSecond > 0f)
		{
			_velocity *= Mathf.Exp(-dragPerSecond * dt);
		}

		// Refresh the target on cadence (or when the current one is gone) so the
		// missile can switch targets mid-flight.
		_retargetTimer -= dt;
		if (_target == null || !IsTargetValid(_target) || _retargetTimer <= 0f)
		{
			AcquireTarget();
			_retargetTimer = retargetIntervalSeconds;
		}

		// Ramped homing impulse toward the (jittered / occasionally missed) target.
		if (_target != null)
		{
			Vector3 toTarget = _target.AimCenter - GlobalPosition;
			if (toTarget.LengthSquared() > 1e-4f)
			{
				Vector3 dir = ApplyAimError(toTarget.Normalized());
				float ramp = Mathf.Min(1f, _ageSeconds / Mathf.Max(homingRampSeconds, 1e-3f));
				_velocity += dir * (maxHomingAccel * ramp * dt);
			}
		}

		// Corkscrew — oscillating lateral accel in the plane perpendicular to the
		// current heading.
		if (corkscrewAccel > 0f && _velocity.LengthSquared() > 1e-4f)
		{
			Vector3 fwd = _velocity.Normalized();
			Vector3 up = Mathf.Abs(fwd.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
			Vector3 right = fwd.Cross(up).Normalized();
			up = right.Cross(fwd).Normalized();
			float phase = _ageSeconds * corkscrewFrequencyHz * _corkFreqScale * Mathf.Tau + _phaseOffset;
			Vector3 lateral = (right * (Mathf.Cos(phase) * _corkEllipseX)) + (up * (Mathf.Sin(phase) * _corkEllipseY));
			_velocity += lateral * (corkscrewAccel * _corkAccelScale * dt);
		}
	}

	// Rotate the ideal homing direction by this acquisition's frozen miss
	// deflection plus a small oscillating wobble, so the missile is allowed to
	// miss infrequently rather than tracking perfectly.
	private Vector3 ApplyAimError(Vector3 dir)
	{
		Vector3 up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
		Vector3 right = dir.Cross(up).Normalized();
		up = right.Cross(dir).Normalized();
		float wobble = Mathf.DegToRad(aimJitterDegrees) * Mathf.Sin(_ageSeconds * Mathf.Tau);
		return dir.Rotated(up, _missYaw + wobble).Rotated(right, _missPitch).Normalized();
	}

	// Pick the nearest enemy within range AND within the acquisition cone of the
	// current heading. Mirrors LightningStrike.TryFindClosestTarget /
	// PlayerWeapon aim-assist: spatial-hash the mobs, also consider the player,
	// filter to enemies of the firer's team (alive, not burrowed). Then roll this
	// acquisition's deliberate-miss deflection.
	private void AcquireTarget()
	{
		_target = null;
		Sim sim = Sim.Current;
		if (sim == null)
		{
			return;
		}
		Vector3 origin = GlobalPosition;
		Vector3 heading = _velocity.LengthSquared() > 1e-4f ? _velocity.Normalized() : Vector3.Forward;
		float cosCone = Mathf.Cos(Mathf.DegToRad(acquireConeDegrees));

		IAimTarget best = null;
		float bestDistSq = float.PositiveInfinity;
		void Consider(IAimTarget cand, Vector3 center)
		{
			Vector3 to = center - origin;
			float distSq = to.LengthSquared();
			if (distSq < 1e-4f)
			{
				return;
			}
			Vector3 dir = to / Mathf.Sqrt(distSq);
			if (dir.Dot(heading) < cosCone)
			{
				return;
			}
			if (distSq < bestDistSq)
			{
				bestDistSq = distSq;
				best = cand;
			}
		}

		Player player = sim.player;
		if (player != null && IsEnemy(ETeam.Player))
		{
			Consider(player, player.AimCenter);
		}

		_scratch ??= new List<Mob>(32);
		_scratch.Clear();
		sim.MobSpatialHash?.QueryRadius(origin, retargetRadiusMeters, _scratch);
		for (int i = 0; i < _scratch.Count; i++)
		{
			Mob m = _scratch[i];
			if (m == null || !GodotObject.IsInstanceValid(m) || !m.alive || m.burrowed)
			{
				continue;
			}
			// Only home onto enemies the player has actually discovered — a missile
			// won't seek a mob the player can't see / hasn't spotted yet.
			if (m.playerPerceptionState != EPlayerPerceptionState.Discovered)
			{
				continue;
			}
			if (!IsEnemy(m.ActorTeam))
			{
				continue;
			}
			Consider(m, m.AimCenter);
		}

		_target = best;

		if (_target != null && missChance > 0f && GD.Randf() < missChance)
		{
			float defl = Mathf.DegToRad(missDeflectionDegrees);
			_missYaw = (float)GD.RandRange(-defl, defl);
			_missPitch = (float)GD.RandRange(-defl, defl);
		}
		else
		{
			_missYaw = 0f;
			_missPitch = 0f;
		}
	}

	// Enemy of the firer when the two teams aren't allied (same friendly-fire
	// rule the hurtbox sweep uses), so a player missile seeks hostiles and a
	// hostile missile seeks the player side.
	private bool IsEnemy(ETeam other)
	{
		return !Teams.AreAllied(_attackerTeam, other);
	}

	private static bool IsTargetValid(IAimTarget t)
	{
		if (t is not GodotObject obj || !GodotObject.IsInstanceValid(obj))
		{
			return false;
		}
		if (t is Mob m)
		{
			return m.alive && !m.burrowed;
		}
		return true;
	}
}
