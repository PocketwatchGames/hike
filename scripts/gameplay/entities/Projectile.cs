using Godot;

// Authored cues + arrow-binding payload the projectile carries from its
// firing event. The fx fields (miss/environment/health/armor/lethal) come
// straight from the event's impact fields — same hit-result mapping
// DoHitscan uses. arrowLootData, when non-null, drives the StickArrow /
// SpawnArrowLoot follow-up: a hurtbox hit on a still-living mob sticks the
// arrow, anything else (env clip, lethal, miss-at-range) drops loose loot
// at the impact point. The firing handler decides at fire time whether to
// populate it (weapon authors arrowLootData AND the firing tier flags
// useAmmo), so the projectile doesn't need to peek back at the action.
// sourceWeapon is the ammo-bookkeeping target — arrows that resolve into
// loot or stuck-on-mob register back to this WeaponState for recovery.
public struct ProjectileImpact
{
	public PackedScene miss;
	public PackedScene environment;
	public PackedScene health;
	public PackedScene armor;
	public PackedScene lethal;
	// Per-tier overlay fx layered on top of health/armor/lethal when the
	// receiver's HurtBox.QueryHitTriggers reports the matching condition.
	// Carried by value (rather than holding the ItemAction ref) so the
	// projectile doesn't outlive the action's runtime context.
	public PackedScene crit;
	public PackedScene backstab;
	public WeaponState sourceWeapon;
	public ArrowLootData arrowLootData;
}

// In-flight arrow / bolt / magic missile. Spawned by ItemEventHandlers.DoProjectile
// from a weapon or mob action's timeline. Each physics tick the projectile
// integrates `_velocity * dt`, optionally accumulates gravity (Y-) into
// velocity for ballistic arcs, and (unless _noCollide is set) sweep-casts a
// ray from its previous position to the proposed next position to resolve
// the first hit:
//   - hurtbox first (any matching collision mask) → HitInfo built from the
//     captured DamageData, hurtBox.Hit called, impact fx + arrow drop
//     resolved, projectile despawns.
//   - environment second → impact env fx + arrow drop, projectile despawns.
// Travelling past maxLifetimeSeconds without hitting anything fires the
// miss fx and drops the arrow at the projectile's current position, so a
// shot into empty space still returns recoverable ammo.
//
// In `_noCollide` mode (used by arcing delivery projectiles), both sweeps
// are skipped and the projectile only despawns when its lifetime expires —
// it passes harmlessly through walls and creatures. Pair with `_gravity`
// for a true arcing flight; pair with `_impactEvent` to have the landing
// spawn a follow-up effect (e.g. rain-of-arrows AoE).
//
// The source actor's own hurtbox + body are excluded from the sweep so the
// shooter doesn't self-hit on the first tick.
[GlobalClass]
public partial class Projectile : Node3D
{
	private DamageData _damageData;
	// Who fired this projectile. Goes into HitInfo.source so the receiver
	// (mob, player) sees the attacker for retaliation / aggro / etc.
	private Node _source;
	private Vector3 _velocity;
	private float _ageSeconds;
	private float _maxLifetimeSeconds;
	private uint _hurtboxMask;
	// Downward acceleration applied to velocity each physics tick. 0 = flat
	// flight (default for hitscan-replacement arrows). Positive values curve
	// the trajectory downward; the arcing-projectile path solves for the
	// launch velocity that lands at the aim cursor after maxLifetimeSeconds
	// given this gravity.
	private float _gravity;
	// Skip both sweep queries; the projectile only ends via lifetime expiry.
	// Used by arcing delivery projectiles whose impact is the landing point,
	// not a collision.
	private bool _noCollide;
	// Optional follow-up event fired at the despawn position. Currently
	// supports SpawnAreaEffect (drops areaEffectScene where the projectile
	// landed). See ItemEventHandlers.DispatchAtPosition.
	private ItemEvent _impactEvent;
	// Team of the firing actor. With _friendlyFire false, a hurtbox hit that
	// resolves to a mob on this team is added to _hurtBoxExclude and the
	// projectile passes through instead of detonating.
	private ETeam _attackerTeam;
	private bool _friendlyFire;
	private Godot.Collections.Array<Rid> _hurtBoxExclude;
	private Godot.Collections.Array<Rid> _bodyExclude;
	private ProjectileImpact _impact;
	private Fx _loopFx;

	public DamageData DamageData => _damageData;
	public Node Source => _source;

	public static Projectile Launch(
		Node parent,
		PackedScene scene,
		float maxLifetimeSeconds,
		PackedScene loopEffect,
		Vector3 origin,
		Vector3 velocity,
		DamageData damageData,
		Node source,
		uint hurtboxMask,
		Rid? excludeHurtBox,
		Rid? excludeBody,
		ProjectileImpact impact,
		float gravity = 0f,
		bool noCollide = false,
		ItemEvent impactEvent = null,
		ETeam attackerTeam = ETeam.Hostile,
		bool friendlyFire = false)
	{
		if (scene == null || parent == null)
		{
			return null;
		}
		var inst = scene.Instantiate<Projectile>();
		inst._damageData = damageData;
		inst._source = source;
		inst._velocity = velocity;
		inst._maxLifetimeSeconds = maxLifetimeSeconds;
		inst._hurtboxMask = hurtboxMask;
		inst._impact = impact;
		inst._gravity = gravity;
		inst._noCollide = noCollide;
		inst._impactEvent = impactEvent;
		inst._attackerTeam = attackerTeam;
		inst._friendlyFire = friendlyFire;
		if (excludeHurtBox.HasValue)
		{
			inst._hurtBoxExclude = new Godot.Collections.Array<Rid> { excludeHurtBox.Value };
		}
		if (excludeBody.HasValue)
		{
			inst._bodyExclude = new Godot.Collections.Array<Rid> { excludeBody.Value };
		}
		parent.AddChild(inst);
		inst.GlobalPosition = origin;
		// Orient the visual along the flight direction. LookAt requires a
		// non-zero direction and a non-parallel up vector; guard for both.
		// Re-orientation as gravity pitches the arc down happens each tick
		// in _PhysicsProcess when gravity > 0.
		if (velocity.LengthSquared() > 1e-6f)
		{
			Vector3 fwd = velocity.Normalized();
			Vector3 up = Mathf.Abs(fwd.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
			inst.LookAt(origin + fwd, up);
		}
		// Parent the loop fx to the projectile so its emitter follows the
		// flight naturally. Despawn reparents it out and calls Stop() so
		// trailing particles can fade for their authored Lifetime after the
		// projectile node frees.
		if (loopEffect != null)
		{
			inst._loopFx = Fx.Create(loopEffect, inst, Vector3.Zero);
		}
		return inst;
	}

	public override void _PhysicsProcess(double delta)
	{
		float dt = (float)delta;
		_ageSeconds += dt;
		// Gravity accumulates BEFORE the position step so the per-tick
		// trajectory uses the current (post-acceleration) velocity. With
		// gravity=0 this is a no-op and flat flight is preserved.
		if (_gravity != 0f)
		{
			_velocity.Y -= _gravity * dt;
		}
		Vector3 prev = GlobalPosition;
		Vector3 step = _velocity * dt;
		Vector3 next = prev + step;

		// Arcing / delivery projectiles skip collision entirely — only
		// lifetime expiry ends them.
		if (!_noCollide)
		{
			World3D world3D = GetWorld3D();
			if (world3D != null)
			{
				var spaceState = world3D.DirectSpaceState;

				// Environment clip first — gives us the wall position the
				// projectile would have impacted if no hurtbox were in the way.
				Vector3 endPoint = next;
				Vector3? envHit = null;
				using (var envQuery = PhysicsRayQueryParameters3D.Create(prev, next))
				{
					envQuery.CollisionMask = (uint)ECollisionLayer.Environment;
					envQuery.CollideWithBodies = true;
					envQuery.CollideWithAreas = false;
					if (_bodyExclude != null)
					{
						envQuery.Exclude = _bodyExclude;
					}
					var envResult = spaceState.IntersectRay(envQuery);
					if (envResult.Count > 0)
					{
						envHit = (Vector3)envResult["position"];
						endPoint = envHit.Value;
					}
				}

				// Hurtbox sweep up to the (possibly clipped) end point. Query
				// hit type first so a Lethal result sees pre-damage state — same
				// rationale as DoMelee / DoHitscan.
				using (var hurtQuery = PhysicsRayQueryParameters3D.Create(prev, endPoint))
				{
					hurtQuery.CollisionMask = _hurtboxMask;
					hurtQuery.CollideWithAreas = true;
					hurtQuery.CollideWithBodies = false;
					if (_hurtBoxExclude != null)
					{
						hurtQuery.Exclude = _hurtBoxExclude;
					}
					var hurtResult = spaceState.IntersectRay(hurtQuery);
					if (hurtResult.Count > 0 && hurtResult["collider"].Obj is HurtBox hurtBox)
					{
						// Friendly-fire skip: same-team hurtbox is added to the
						// exclude list and the projectile keeps flying. Falls
						// through to the env-clip / continue branches below.
						if (!_friendlyFire)
						{
							Mob owner = ItemEventHandlers.FindOwningMob(hurtBox);
							if (owner?.mobData != null && owner.mobData.team == _attackerTeam)
							{
								if (_hurtBoxExclude == null)
								{
									_hurtBoxExclude = new Godot.Collections.Array<Rid>();
								}
								_hurtBoxExclude.Add(hurtBox.GetRid());
								goto AfterHurtSweep;
							}
						}
						Vector3 hitPos = (Vector3)hurtResult["position"];
						var hit = new HitInfo(_damageData, _source, _velocity.Normalized());
						EHitResult hitResult = hurtBox.QueryHitType(hit);
						EDamageTriggerFlags hitTriggers = hurtBox.QueryHitTriggers(hit);
						hurtBox.Hit(hit);
						GlobalPosition = hitPos;
						Despawn(hitResult, hitTriggers, hurtBox, hitPos);
						return;
					}
				}

				AfterHurtSweep:
				if (envHit.HasValue)
				{
					GlobalPosition = envHit.Value;
					Despawn(EHitResult.Object, EDamageTriggerFlags.None, null, envHit.Value);
					return;
				}
			}
		}

		GlobalPosition = next;
		// Re-aim the visual along the current velocity as gravity tilts the
		// arc downward. Flat flight (gravity=0) skips this — orientation was
		// fixed at Launch.
		if (_gravity != 0f && _velocity.LengthSquared() > 1e-6f)
		{
			Vector3 fwd = _velocity.Normalized();
			Vector3 up = Mathf.Abs(fwd.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
			LookAt(next + fwd, up);
		}
		if (_ageSeconds >= _maxLifetimeSeconds)
		{
			Despawn(EHitResult.None, EDamageTriggerFlags.None, null, GlobalPosition);
		}
	}

	// Shared end-of-life path: resolve impact fx + arrow recovery, fire the
	// authored impactEvent at the landing position (if any), tear down the
	// loop fx (reparented out so its tail fades naturally), then free the
	// projectile node. `triggers` is non-zero only on the hurtbox-hit path —
	// env clips and lifetime expiry pass None since there's no receiver to
	// query.
	private void Despawn(EHitResult result, EDamageTriggerFlags triggers, HurtBox hurtBox, Vector3 position)
	{
		ResolveImpact(result, triggers, hurtBox, position);
		ItemEventHandlers.DispatchAtPosition(_impactEvent, position, GetParent(), _impact.sourceWeapon?.data);
		StopLoopFx();
		QueueFree();
	}

	// Hand the loop fx off to the projectile's parent so its trailing
	// particles can fade after our QueueFree resolves. Stop() flips
	// Emitting=false and halts audio; the Fx node frees itself once the
	// longest particle Lifetime has elapsed.
	private void StopLoopFx()
	{
		if (_loopFx == null || !GodotObject.IsInstanceValid(_loopFx))
		{
			_loopFx = null;
			return;
		}
		Node parent = GetParent();
		if (parent != null && _loopFx.GetParent() == this)
		{
			_loopFx.Reparent(parent, true);
		}
		_loopFx.Stop();
		_loopFx = null;
	}

	// Branch shared by every despawn path: pick the right impact fx for
	// the hit type, then run the arrow-recovery logic. An arrow that
	// landed on a living mob's health sticks; everything else (armor
	// bounce, environment clip, end-of-range miss) drops loose loot at
	// the projectile's last position so a shot into empty space — or one
	// that glanced off armor — still returns recoverable ammo. Pierce is
	// already folded in upstream: a pierced hit skips armor and resolves
	// to EHitResult.Health, so it sticks like any other health hit.
	private void ResolveImpact(EHitResult result, EDamageTriggerFlags triggers, HurtBox hurtBox, Vector3 position)
	{
		PackedScene fx = result switch
		{
			EHitResult.Lethal => _impact.lethal ?? _impact.health,
			EHitResult.Health => _impact.health,
			EHitResult.Armor => _impact.armor,
			EHitResult.Object => _impact.environment,
			_ => _impact.miss,
		};
		Node parent = GetParent();
		if (fx != null && parent != null)
		{
			Fx.Create(fx, parent, position);
		}
		// Crit / backstab overlays — only meaningful on a hurtbox-hit despawn
		// (triggers is None for env / lifetime exits). Layered on top of the
		// base impact fx selected above, matching the Melee / Hitscan paths.
		if (parent != null && triggers != EDamageTriggerFlags.None)
		{
			if ((triggers & EDamageTriggerFlags.Crit) != 0 && _impact.crit != null)
			{
				Fx.Create(_impact.crit, parent, position);
			}
			if ((triggers & EDamageTriggerFlags.Backstab) != 0 && _impact.backstab != null)
			{
				Fx.Create(_impact.backstab, parent, position);
			}
		}

		WeaponState weapon = _impact.sourceWeapon;
		ArrowLootData arrowLootData = _impact.arrowLootData;
		if (weapon == null || arrowLootData == null || World.Current == null)
		{
			return;
		}
		Mob targetMob = result == EHitResult.Health ? ItemEventHandlers.FindOwningMob(hurtBox) : null;
		if (targetMob != null && targetMob.alive)
		{
			targetMob.StickArrow(weapon, arrowLootData, position);
		}
		else
		{
			World.Current.SpawnArrowLoot(position, ItemEventHandlers.BuildArrowEjectImpulse(), arrowLootData, weapon);
		}
	}
}
