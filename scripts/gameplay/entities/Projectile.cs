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
	// How far along the surface normal a bounced projectile is nudged after a
	// reflection, so the following tick's sweep doesn't immediately re-hit the
	// surface it just bounced off.
	private const float BounceSurfaceOffset = 0.03f;

	private DamageData _damageData;
	// Who fired this projectile. Goes into HitInfo.source so the receiver
	// (mob, player) sees the attacker for retaliation / aggro / etc. Protected
	// so a homing subclass can read the firer when picking targets.
	protected Node _source;
	// Protected so movement-override subclasses (HomingMissile) can steer it.
	protected Vector3 _velocity;
	// Protected so subclasses can ramp/arm behavior off the projectile's age
	// and lifetime (homing impulse ramp, environment-collision arming).
	protected float _ageSeconds;
	protected float _maxLifetimeSeconds;
	private uint _hurtboxMask;
	// Downward acceleration applied to velocity each physics tick. 0 = flat
	// flight (default for hitscan-replacement arrows). Positive values curve
	// the trajectory downward; the arcing-projectile path solves for the
	// launch velocity that lands at the aim cursor after maxLifetimeSeconds
	// given this gravity. Protected so a subclass's UpdateVelocity can read it.
	protected float _gravity;
	// Skip both sweep queries; the projectile only ends via lifetime expiry.
	// Used by arcing delivery projectiles whose impact is the landing point,
	// not a collision.
	private bool _noCollide;
	// Bounce mode (arced lobs): env hits reflect the velocity (scaled by
	// _restitution) and the projectile keeps flying instead of detonating;
	// hurtboxes are ignored in flight. It only ends at lifetime expiry, which is
	// where its `_impactEvent` (the explosion) fires. Mutually meaningful only
	// when _noCollide is false.
	private bool _bounce;
	// Normal-direction restitution (fraction of into-surface speed kept on the
	// rebound) and tangential friction (fraction of along-surface speed shed) on
	// each bounce. Split so walls (mostly normal impact) stay bouncy while the
	// ground (repeated grazing impacts) rolls the projectile to a stop.
	private float _restitution;
	private float _friction;
	// Optional follow-up event fired at the despawn position. Currently
	// supports SpawnAreaEffect (drops areaEffectScene where the projectile
	// landed). See ItemEventHandlers.DispatchAtPosition. This is the catch-all:
	// it fires on every despawn cause, and is the fallback when no cause-specific
	// event below is authored.
	private ItemEvent _impactEvent;
	// Cause-specific follow-up events, each falling back to _impactEvent when
	// null (so a projectile authored with only _impactEvent behaves exactly as
	// before). _directHitEvent fires when the shot ends on a creature
	// (Health/Armor/Lethal); _expirationEvent fires when it ends on lifetime
	// expiry (None). Environment clips (Object) always use _impactEvent.
	private ItemEvent _directHitEvent;
	private ItemEvent _expirationEvent;
	// Team of the firing actor. With _friendlyFire false, a hurtbox hit that
	// resolves to a mob on this team is added to _hurtBoxExclude and the
	// projectile passes through instead of detonating. Protected so a homing
	// subclass can filter target candidates to enemies of this team.
	protected ETeam _attackerTeam;
	private bool _friendlyFire;
	// Creatures this shot may still pass THROUGH. Starts at the composed pierce
	// count (event base maxed against weapon mods). A hit while this is > 0 passes
	// through (decrementing it, adding the struck hurtbox to _hurtBoxExclude so it
	// isn't hit twice); a hit while it's 0 ends the projectile. 0 = a normal
	// single-target shot that stops on the first creature.
	private int _pierceRemaining;
	// Fraction of the health damage this shot deals that is returned to the firer
	// (_source) as healing. Composed at fire time from the weapon's vampiric mods.
	// 0 = no lifesteal.
	private float _lifestealFraction;
	// On-hit effect contributions (weapon-mod enchants — Burning applied
	// immediately, Poison buildup) added to every creature this shot strikes.
	// Null when the shot carries no enchant.
	private Godot.Collections.Array<StatusEffectBuildup> _onHitBuildups;
	// Chain-lightning mods (Shocking bow) that discharge from each creature this
	// shot strikes. Null when the shot carries none.
	private Godot.Collections.Array<ChainLightningData> _chainLightning;
	// Knockback-mod shove (m/s) + stagger (s) added to each hit. 0 = none.
	private float _knockbackBonus;
	private float _knockbackTimeBonus;
	private Godot.Collections.Array<Rid> _hurtBoxExclude;
	private Godot.Collections.Array<Rid> _bodyExclude;
	private ProjectileImpact _impact;
	// Loop Fx riding the projectile: the event's authored projectileLoopEffect
	// plus any weapon-mod projectileFx (a Flaming bow's flaming arrows). All are
	// reparented out and Stop()ped at despawn so their trailing particles fade.
	private readonly System.Collections.Generic.List<Fx> _loopFx = new();

	public DamageData DamageData => _damageData;
	public Node Source => _source;
	// Current world velocity (m/s) and firing team, read by ProjectileRegistry
	// so mobs can decide whether an in-flight shot is an incoming threat and
	// which way it's travelling. Velocity changes each tick (gravity / homing);
	// the registry samples it live at query time.
	public Vector3 Velocity => _velocity;
	public ETeam AttackerTeam => _attackerTeam;

	// Self-register with the world's projectile registry for the mob dodge /
	// perch-flee reaction. Symmetric add/remove on tree enter/exit so a despawn
	// (QueueFree) or chunk evict drops it cleanly — mirrors Perch.
	public override void _Ready()
	{
		World.Current?.Projectiles?.Add(this);
	}

	public override void _ExitTree()
	{
		World.Current?.Projectiles?.Remove(this);
	}

	public static Projectile Launch(
		Node parent,
		PackedScene scene,
		float maxLifetimeSeconds,
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
		bool friendlyFire = false,
		bool bounce = false,
		float bounciness = 0f,
		float friction = 0f,
		int pierceCount = 0,
		float lifestealFraction = 0f,
		Godot.Collections.Array<ChainLightningData> chainLightning = null,
		float knockbackBonus = 0f,
		float knockbackTimeBonus = 0f,
		Godot.Collections.Array<PackedScene> projectileFx = null,
		Godot.Collections.Array<StatusEffectBuildup> onHitBuildups = null,
		ItemEvent directHitEvent = null,
		ItemEvent expirationEvent = null)
	{
		if (scene == null || parent == null)
		{
			return null;
		}
		var inst = scene.Instantiate<Projectile>();
		inst._pierceRemaining = Mathf.Max(0, pierceCount);
		inst._lifestealFraction = lifestealFraction;
		inst._onHitBuildups = onHitBuildups;
		inst._chainLightning = chainLightning;
		inst._knockbackBonus = knockbackBonus;
		inst._knockbackTimeBonus = knockbackTimeBonus;
		inst._damageData = damageData;
		inst._source = source;
		inst._velocity = velocity;
		inst._maxLifetimeSeconds = maxLifetimeSeconds;
		inst._hurtboxMask = hurtboxMask;
		inst._impact = impact;
		inst._gravity = gravity;
		inst._noCollide = noCollide;
		inst._bounce = bounce;
		inst._restitution = bounciness;
		inst._friction = friction;
		inst._impactEvent = impactEvent;
		inst._directHitEvent = directHitEvent;
		inst._expirationEvent = expirationEvent;
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
		// Loop fx riding the projectile come from two sources, both joining the
		// same _loopFx teardown: intrinsic trails authored as child Fx of the
		// projectile scene (adopted here), and compositional weapon-mod fx (a
		// Flaming bow) injected via projectileFx below. Despawn reparents them out
		// and calls Stop() so trailing particles fade for their authored Lifetime
		// after the projectile frees — without that a scene-child trail would be
		// freed instantly by our QueueFree and snap off at impact. Safe to scan
		// here: the AddChild above already ran the children's Fx._Ready (which
		// starts their emission).
		foreach (Node child in inst.GetChildren())
		{
			if (child is Fx childFx)
			{
				inst._loopFx.Add(childFx);
			}
		}
		if (projectileFx != null)
		{
			for (int i = 0; i < projectileFx.Count; i++)
			{
				if (projectileFx[i] == null)
				{
					continue;
				}
				Fx fx = Fx.Create(projectileFx[i], inst, Vector3.Zero);
				if (fx != null)
				{
					inst._loopFx.Add(fx);
				}
			}
		}
		return inst;
	}

	// Per-tick velocity integration, the one seam a movement-override subclass
	// replaces. Base = gravity accumulation (Y-), a no-op at gravity 0 so flat
	// flight is preserved. HomingMissile overrides this with drag + ramped
	// homing impulse + corkscrew (and chains to base for optional gravity).
	protected virtual void UpdateVelocity(float dt)
	{
		if (_gravity != 0f)
		{
			_velocity.Y -= _gravity * dt;
		}
	}

	// Whether environment (solid) collisions end the projectile this tick. Base
	// = always armed. A homing missile leaves this false for an initial grace
	// window so it can clip terrain while it settles onto its target, then arms.
	// The hurtbox sweep is unaffected — a valid target is always hit.
	protected virtual bool EnvironmentCollisionArmed => true;

	// Whether the visual is re-aimed along the current velocity each tick. Base
	// = only when gravity is bending the arc (flat shots keep their launch
	// orientation). A homing missile returns true so it tracks its curve.
	protected virtual bool ReorientToVelocity => _gravity != 0f;

	public override void _PhysicsProcess(double delta)
	{
		float dt = (float)delta;
		_ageSeconds += dt;
		// Per-tick velocity update runs BEFORE the position step so the
		// trajectory uses the current (post-acceleration) velocity. The base
		// applies gravity; a movement-override subclass (HomingMissile) adds
		// drag / homing impulse / corkscrew here.
		UpdateVelocity(dt);
		Vector3 prev = GlobalPosition;
		Vector3 step = _velocity * dt;
		Vector3 next = prev + step;

		// Arcing / delivery projectiles skip collision entirely — only
		// lifetime expiry ends them.
		if (!_noCollide && _bounce)
		{
			// Bounce mode: reflect off solids and keep flying; ignore hurtboxes.
			// Only lifetime ends it (where the explosion fires).
			World3D bounceWorld = GetWorld3D();
			if (bounceWorld != null)
			{
				using var envQuery = PhysicsRayQueryParameters3D.Create(prev, next);
				envQuery.CollisionMask = (uint)ECollisionLayer.Solid;
				envQuery.CollideWithBodies = true;
				envQuery.CollideWithAreas = false;
				if (_bodyExclude != null)
				{
					envQuery.Exclude = _bodyExclude;
				}
				var envResult = bounceWorld.DirectSpaceState.IntersectRay(envQuery);
				if (envResult.Count > 0)
				{
					Vector3 hitPos = (Vector3)envResult["position"];
					Vector3 normal = (Vector3)envResult["normal"];
					// Split into normal + tangential: the normal component reverses
					// scaled by restitution (wall bounce), the tangential component is
					// shed by friction (rolls to rest on the ground). Nudge off the
					// surface so the next tick's sweep doesn't immediately re-hit it.
					Vector3 vNormal = _velocity.Dot(normal) * normal;
					Vector3 vTangent = _velocity - vNormal;
					_velocity = (-vNormal * _restitution) + (vTangent * (1f - _friction));
					GlobalPosition = hitPos + normal * BounceSurfaceOffset;
					if (_velocity.LengthSquared() > 1e-6f)
					{
						Vector3 fwd = _velocity.Normalized();
						Vector3 up = Mathf.Abs(fwd.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
						LookAt(GlobalPosition + fwd, up);
					}
					if (_ageSeconds >= _maxLifetimeSeconds)
					{
						Despawn(EHitResult.None, EDamageTriggerFlags.None, null, GlobalPosition);
					}
					return;
				}
			}
		}
		else if (!_noCollide)
		{
			World3D world3D = GetWorld3D();
			if (world3D != null)
			{
				var spaceState = world3D.DirectSpaceState;

				// Environment clip first — gives us the wall position the
				// projectile would have impacted if no hurtbox were in the way.
				// Skipped while environment collision is unarmed (a homing
				// missile's grace window) so the shot passes through terrain; the
				// hurtbox sweep below still runs, so valid targets are always hit.
				Vector3 endPoint = next;
				Vector3? envHit = null;
				if (EnvironmentCollisionArmed)
				{
					using var envQuery = PhysicsRayQueryParameters3D.Create(prev, next);
					envQuery.CollisionMask = (uint)ECollisionLayer.Solid;
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
						var hit = new HitInfo(_damageData, _source, _velocity.Normalized(), _attackerTeam);
						hit.friendlyFire = _friendlyFire;
						// Weapon-mod on-hit effects (Burning applied immediately,
						// Poison buildup) the shot carries, on top of the
						// DamageData's own buildups.
						hit.AddBuildups(_onHitBuildups);
						// Knockback mod — extra shove + stagger. The shot's flight
						// direction is already the hit direction, so the push aligns
						// with the arrow's travel.
						hit.knockbackDistance += _knockbackBonus;
						hit.knockbackTime += _knockbackTimeBonus;
						// Team skip via the receiver's filter: an allied hurtbox is
						// added to the exclude list and the projectile keeps flying.
						// Falls through to the env-clip / continue branches below.
						if (!hurtBox.CanBeHit(hit))
						{
							if (_hurtBoxExclude == null)
							{
								_hurtBoxExclude = new Godot.Collections.Array<Rid>();
							}
							_hurtBoxExclude.Add(hurtBox.GetRid());
							goto AfterHurtSweep;
						}
						Vector3 hitPos = (Vector3)hurtResult["position"];
						EHitResult hitResult = hurtBox.QueryHitType(hit);
						EDamageTriggerFlags hitTriggers = hurtBox.QueryHitTriggers(hit);
						hurtBox.Hit(hit);
						GlobalPosition = hitPos;
						// Record this creature so the shot can't strike it again as
						// it passes through.
						if (_hurtBoxExclude == null)
						{
							_hurtBoxExclude = new Godot.Collections.Array<Rid>();
						}
						_hurtBoxExclude.Add(hurtBox.GetRid());
						// Lifesteal leeches off every creature the shot wounds —
						// both the terminal hit and each pierce-through.
						ApplyLifesteal(hitResult, hit.healthDamage);
						// Chain-lightning mods arc off the struck creature.
						if (_chainLightning != null && GodotObject.IsInstanceValid(_source) && _source is IActionActor chainActor)
						{
							for (int ci = 0; ci < _chainLightning.Count; ci++)
							{
								ItemEventHandlers.ApplyChainLightning(chainActor, _chainLightning[ci], hitPos);
							}
						}
						if (_pierceRemaining <= 0)
						{
							// No pierce budget left — this hit ends the shot (impact
							// fx, arrow drop/stick, impactEvent).
							Despawn(hitResult, hitTriggers, hurtBox, hitPos);
							return;
						}
						// Pierce through: spend a point of budget, show the impact,
						// and keep flying from the hit point so the next tick
						// continues the sweep (catching a wall or a further creature).
						_pierceRemaining--;
						SpawnImpactVisuals(hitResult, hitTriggers, hitPos);
						if (ReorientToVelocity && _velocity.LengthSquared() > 1e-6f)
						{
							Vector3 fwd = _velocity.Normalized();
							Vector3 up = Mathf.Abs(fwd.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
							LookAt(hitPos + fwd, up);
						}
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
		// Re-aim the visual along the current velocity (gravity tilting the arc
		// down, or a homing missile curving). Flat flight skips this —
		// orientation was fixed at Launch.
		if (ReorientToVelocity && _velocity.LengthSquared() > 1e-6f)
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

	// Heal the firing actor by the composed vampiric fraction of the health
	// damage this shot deals. Health/Lethal hits only — armor pings and prop
	// hits don't leech. No-op when the shot carries no vampiric mod or the firer
	// is gone; _source is the actor's AttackerNode, which is the IActionActor
	// itself for both Player and Mob.
	private void ApplyLifesteal(EHitResult result, float healthDamage)
	{
		if (_lifestealFraction <= 0f || healthDamage <= 0f)
		{
			return;
		}
		if (result != EHitResult.Health && result != EHitResult.Lethal)
		{
			return;
		}
		if (GodotObject.IsInstanceValid(_source) && _source is IActionActor actor)
		{
			actor.Heal(healthDamage * _lifestealFraction);
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
		// Cause-specific follow-up: a direct creature hit fires _directHitEvent,
		// lifetime expiry fires _expirationEvent, an environment clip fires
		// _impactEvent. Each cause-specific event falls back to _impactEvent, so
		// a projectile authored with only _impactEvent fires it on every cause
		// exactly as before.
		ItemEvent followUp = result switch
		{
			EHitResult.Health or EHitResult.Armor or EHitResult.Lethal => _directHitEvent ?? _impactEvent,
			EHitResult.None => _expirationEvent ?? _impactEvent,
			_ => _impactEvent,
		};
		ItemEventHandlers.DispatchAtPosition(followUp, position, GetParent(), _impact.sourceWeapon?.data, _attackerTeam);
		StopLoopFx();
		QueueFree();
	}

	// Hand the loop fx off to the projectile's parent so their trailing
	// particles can fade after our QueueFree resolves. Stop() flips
	// Emitting=false and halts audio; each Fx node frees itself once its
	// longest particle Lifetime has elapsed.
	private void StopLoopFx()
	{
		Node parent = GetParent();
		for (int i = 0; i < _loopFx.Count; i++)
		{
			Fx fx = _loopFx[i];
			if (fx == null || !GodotObject.IsInstanceValid(fx))
			{
				continue;
			}
			if (parent != null && fx.GetParent() == this)
			{
				fx.Reparent(parent, true);
			}
			fx.Stop();
		}
		_loopFx.Clear();
	}

	// Spawn the impact one-shot for a hurtbox/environment hit plus any crit /
	// backstab overlays the receiver flagged. Shared by the terminal despawn
	// (ResolveImpact) and the pierce-through path, so every creature a piercing
	// shot passes through gets the same blood/hit cue the final stop would.
	// `triggers` is None for env clips and lifetime exits (no receiver to query).
	private void SpawnImpactVisuals(EHitResult result, EDamageTriggerFlags triggers, Vector3 position)
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
		// Crit / backstab overlays — only meaningful on a hurtbox hit (triggers
		// is None for env / lifetime exits). Layered on top of the base impact
		// fx selected above, matching the Melee / Hitscan paths.
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
	}

	// Branch shared by every despawn path: pick the right impact fx for
	// the hit type, then run the arrow-recovery logic. An arrow that
	// landed on a living mob's health sticks; everything else (armor
	// bounce, environment clip, end-of-range miss) drops loose loot at
	// the projectile's last position so a shot into empty space — or one
	// that glanced off armor — still returns recoverable ammo. Armor
	// penetration is already folded in upstream: a penetrating hit skips armor and resolves
	// to EHitResult.Health, so it sticks like any other health hit.
	private void ResolveImpact(EHitResult result, EDamageTriggerFlags triggers, HurtBox hurtBox, Vector3 position)
	{
		SpawnImpactVisuals(result, triggers, position);

		WeaponState weapon = _impact.sourceWeapon;
		ArrowLootData arrowLootData = _impact.arrowLootData;
		if (weapon == null || arrowLootData == null || World.Current == null)
		{
			return;
		}
		Mob targetMob = result == EHitResult.Health ? ItemEventHandlers.FindOwningMob(hurtBox) : null;
		if (targetMob != null && targetMob.alive)
		{
			targetMob.StickArrow(weapon, arrowLootData, position, _velocity);
		}
		else
		{
			World.Current.SpawnArrowLoot(position, ItemEventHandlers.BuildArrowEjectImpulse(), arrowLootData, weapon);
		}
	}
}
