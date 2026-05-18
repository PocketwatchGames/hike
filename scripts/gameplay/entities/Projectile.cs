using Godot;

// Authored cues + arrow-binding payload the projectile carries from its
// firing event. The fx fields (miss/environment/health/armor/lethal) come
// straight from the event's impact fields — same hit-result mapping
// DoHitscan uses. sourceWeapon, when non-null and carrying an arrowLootData,
// drives the StickArrow / SpawnArrowLoot follow-up: a hurtbox hit on a
// still-living mob sticks the arrow, anything else (env clip, lethal,
// miss-at-range) drops loose loot at the impact point.
public struct ProjectileImpact
{
	public PackedScene miss;
	public PackedScene environment;
	public PackedScene health;
	public PackedScene armor;
	public PackedScene lethal;
	public WeaponState sourceWeapon;
}

// In-flight arrow / bolt / magic missile. Spawned by ItemEventHandlers.DoProjectile
// from a weapon or mob action's timeline. Each physics tick the projectile
// integrates `_velocity * dt`, sweep-casts a ray from its previous position
// to the proposed next position, and resolves the first hit:
//   - hurtbox first (any matching collision mask) → HitInfo built from the
//     captured DamageData, hurtBox.Hit called, impact fx + arrow drop
//     resolved, projectile despawns.
//   - environment second → impact env fx + arrow drop, projectile despawns.
// Travelling past maxLifetimeSeconds without hitting anything fires the
// miss fx and drops the arrow at the projectile's current position, so a
// shot into empty space still returns recoverable ammo.
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
	private Godot.Collections.Array<Rid> _hurtBoxExclude;
	private Godot.Collections.Array<Rid> _bodyExclude;
	private ProjectileImpact _impact;
	private Fx _loopFx;

	public DamageData DamageData => _damageData;
	public Node Source => _source;

	public static Projectile Launch(
		Node parent,
		PackedScene scene,
		float speed,
		float maxLifetimeSeconds,
		PackedScene loopEffect,
		Vector3 origin,
		Vector3 direction,
		DamageData damageData,
		Node source,
		uint hurtboxMask,
		Rid? excludeHurtBox,
		Rid? excludeBody,
		ProjectileImpact impact)
	{
		if (scene == null || parent == null)
		{
			return null;
		}
		var inst = scene.Instantiate<Projectile>();
		inst._damageData = damageData;
		inst._source = source;
		inst._velocity = direction.Normalized() * speed;
		inst._maxLifetimeSeconds = maxLifetimeSeconds;
		inst._hurtboxMask = hurtboxMask;
		inst._impact = impact;
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
		if (direction.LengthSquared() > 1e-6f)
		{
			Vector3 fwd = direction.Normalized();
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
		Vector3 prev = GlobalPosition;
		Vector3 step = _velocity * dt;
		Vector3 next = prev + step;

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
					Vector3 hitPos = (Vector3)hurtResult["position"];
					var hit = new HitInfo(_damageData, _source, _velocity.Normalized());
					EHitResult hitResult = hurtBox.QueryHitType(hit);
					hurtBox.Hit(hit);
					GlobalPosition = hitPos;
					Despawn(hitResult, hurtBox, hitPos);
					return;
				}
			}

			if (envHit.HasValue)
			{
				GlobalPosition = envHit.Value;
				Despawn(EHitResult.Object, null, envHit.Value);
				return;
			}
		}

		GlobalPosition = next;
		if (_ageSeconds >= _maxLifetimeSeconds)
		{
			Despawn(EHitResult.None, null, GlobalPosition);
		}
	}

	// Shared end-of-life path: resolve impact fx + arrow recovery, tear
	// down the loop fx (reparented out so its tail fades naturally), then
	// free the projectile node.
	private void Despawn(EHitResult result, HurtBox hurtBox, Vector3 position)
	{
		ResolveImpact(result, hurtBox, position);
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
	// the hit type, then run the arrow-recovery logic (stick on a living
	// mob, otherwise drop loose loot). Environment hits and end-of-range
	// "miss"es both end up dropping the arrow at the projectile's last
	// position so a shot into empty space still returns recoverable ammo.
	private void ResolveImpact(EHitResult result, HurtBox hurtBox, Vector3 position)
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

		WeaponState weapon = _impact.sourceWeapon;
		if (weapon == null || weapon.data?.arrowLootData == null || World.Current == null)
		{
			return;
		}
		Mob targetMob = result != EHitResult.None ? ItemEventHandlers.FindOwningMob(hurtBox) : null;
		if (targetMob != null && targetMob.alive)
		{
			targetMob.StickArrow(weapon, weapon.data.arrowLootData, position);
		}
		else
		{
			World.Current.SpawnArrowLoot(position, ItemEventHandlers.BuildArrowEjectImpulse(), weapon.data.arrowLootData, weapon);
		}
	}
}
