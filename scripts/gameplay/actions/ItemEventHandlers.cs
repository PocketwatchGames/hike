using Godot;

// Per-event-type handlers split out of the runner so the runner stays free of
// combat-specific concerns and so future event types (ApplyEffect, PlayAnim,
// etc.) compose cleanly. Combat handlers read damage data from the action's
// primary weapon and physics queries from the actor's world.
public static class ItemEventHandlers
{
	public static void DoMelee(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		// Effective attack shape — single source of truth shared by the damage
		// query and the smear visual, so a future status effect that scales the
		// swing's reach / width feeds both together.
		float shapeRange = ev.range;
		float shapeNearWidth = ev.nearWidth;
		float shapeFarWidth = ev.farWidth;

		// Swing smear, sized to that live shape. Fired on every swing — a
		// blocked or zero-damage swing still swooshes — so it spawns before the
		// damage early-out below.
		SpawnSmear(actor, ev, shapeRange, shapeNearWidth, shapeFarWidth);

		HitInfo hit = ResolveHit(ev, action, actor);
		if (hit.healthDamage <= 0f && hit.statusEffects == null && hit.buildups == null)
		{
			return;
		}

		World3D world3D = actor.AttackerNode?.GetWorld3D();
		if (world3D == null)
		{
			return;
		}

		float halfHeight = ev.meleeHeight * 0.5f;
		Vector3 basePos = actor.ActorWorldPosition + Vector3.Up * halfHeight;
		Vector3 forward = actor.ActorForward;
		float nearRadius = shapeNearWidth * 0.5f;
		float farRadius = shapeFarWidth * 0.5f;
		Vector3 nearCenter = basePos + forward * nearRadius;
		Vector3 farCenter = basePos + forward * (shapeRange - farRadius);
		// Damage volume is the convex hull of the two disks (the swept fan)
		// extruded vertically into a flat-topped/-bottomed prism over
		// `meleeHeight`. The physics query uses the convex hull of horizontal
		// ring points sampled on each disk at the top and bottom planes — the
		// hull spans both clusters so the tangent connection between them is
		// filled in automatically, while the shared top/bottom rings give flat
		// caps rather than a rounded capsule. The far disk center is the
		// canonical impact point for whiff / environment fallbacks.
		ConvexPolygonShape3D shape = BuildSweepShape(nearCenter, nearRadius, farCenter, farRadius, halfHeight);
		Vector3 damagePos = farCenter;
		var query = new PhysicsShapeQueryParameters3D
		{
			Shape = shape,
			CollisionMask = actor.AttackHurtboxMask,
			CollideWithAreas = true,
			CollideWithBodies = false,
		};

		var results = world3D.DirectSpaceState.IntersectShape(query);
		Rid? selfHurtBox = actor.SelfHurtBoxRid;
		EHitResult bestResult = EHitResult.None;
		EDamageTriggerFlags bestTriggers = EDamageTriggerFlags.None;
		Vector3 impactPos = damagePos;
		// Total health damage this swing lands on flesh — drives lifesteal. Armor
		// pings and prop hits don't leech.
		float healthDamageDealt = 0f;
		foreach (var result in results)
		{
			var collider = result["collider"].Obj;
			if (collider is HurtBox hurtBox)
			{
				if (selfHurtBox.HasValue && hurtBox.GetRid() == selfHurtBox.Value)
				{
					continue;
				}
				if (!hurtBox.CanBeHit(hit))
				{
					continue;
				}
				// Query first so the impact effect reflects the pre-hit state
				// (e.g. Lethal needs to see the target's current health, not
				// the post-damage zero). Trigger flags ride alongside so a
				// crit/backstab swing layers its tier overlay on the best
				// hurtbox of a multi-target swing.
				EHitResult r = hurtBox.QueryHitType(hit);
				EDamageTriggerFlags t = hurtBox.QueryHitTriggers(hit);
				hurtBox.Hit(hit);
				if (r == EHitResult.Health || r == EHitResult.Lethal)
				{
					healthDamageDealt += hit.healthDamage;
				}
				if (HitPriority(r) > HitPriority(bestResult))
				{
					bestResult = r;
					bestTriggers = t;
					impactPos = hurtBox.GlobalPosition;
				}
			}
		}
		ApplyLifesteal(actor, action, healthDamageDealt);

		// No hurtbox hit — fall back to environment so a swing into a wall
		// still gets a thunk rather than reading as a whiff.
		if (bestResult == EHitResult.None)
		{
			var envQuery = new PhysicsShapeQueryParameters3D
			{
				Shape = shape,
				CollisionMask = (uint)ECollisionLayer.Solid,
				CollideWithAreas = false,
				CollideWithBodies = true,
			};
			if (world3D.DirectSpaceState.IntersectShape(envQuery, maxResults: 1).Count > 0)
			{
				SpawnImpact(actor, ev.impactEnvironmentEffect, damagePos);
			}
			else
			{
				SpawnImpact(actor, ev.impactMissEffect, damagePos);
			}
		}
		else
		{
			SpawnImpact(actor, PickImpactScene(ev, bestResult), impactPos);
			SpawnTriggerOverlays(actor, action.selectedTier, bestTriggers, impactPos);
		}

		// Status-effect on-impact bursts (elite lightning aura, shock enchant,
		// etc.) fire at the swing's resolved impact point — the best hurtbox
		// when one was hit, else the swing center. See StatusEffectController.
		actor.TriggerAttackImpact(impactPos);
	}

	// Spawn the Melee event's authored smear scene, sized to the live attack
	// shape via WeaponSmear.Initialize so status-effect range / width changes
	// carry through to the visual. Parented to the actor so it inherits facing
	// (the mesh is built in actor-local space pointing along +Z). Returns the
	// spawned smear so future status-driven trail effects (fire, lightning) can
	// be attached as children of it.
	private static WeaponSmear SpawnSmear(IActionActor actor, ItemEvent ev, float range, float nearWidth, float farWidth)
	{
		if (ev.smearEffect == null)
		{
			return null;
		}
		Node3D parent = actor.AttackerNode;
		if (parent == null)
		{
			return null;
		}
		WeaponSmear smear = ev.smearEffect.Instantiate<WeaponSmear>();
		// Configure before AddChild so the geometry is set when _Ready builds
		// the mesh.
		smear.Initialize(range, nearWidth, farWidth, ev.smearClockwise);
		parent.AddChild(smear);
		return smear;
	}

	// Horizontal sample directions for the disk rings (y = 0). More points give
	// a smoother polygonal approximation of each disk; the convex hull of the
	// near + far rings (taken at both the top and bottom planes) is the
	// flat-capped, tangent-joined prism the swing damages.
	private const int SweepRingPoints = 12;
	private static readonly Vector3[] RingDirs = BuildRingDirs();

	private static Vector3[] BuildRingDirs()
	{
		var dirs = new Vector3[SweepRingPoints];
		for (int i = 0; i < SweepRingPoints; i++)
		{
			float a = Mathf.Tau * i / SweepRingPoints;
			dirs[i] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
		}
		return dirs;
	}

	private static ConvexPolygonShape3D BuildSweepShape(Vector3 nearCenter, float nearRadius, Vector3 farCenter, float farRadius, float halfHeight)
	{
		int n = RingDirs.Length;
		var points = new Vector3[n * 4];
		Vector3 vh = Vector3.Up * halfHeight;
		int p = 0;
		for (int i = 0; i < n; i++)
		{
			Vector3 nearRing = nearCenter + RingDirs[i] * nearRadius;
			Vector3 farRing = farCenter + RingDirs[i] * farRadius;
			points[p++] = nearRing + vh;
			points[p++] = nearRing - vh;
			points[p++] = farRing + vh;
			points[p++] = farRing - vh;
		}
		return new ConvexPolygonShape3D { Points = points };
	}

	// A horizontal ring polyline for debug visualization of one flat cap.
	private static void DebugDrawDisk(Vector3 center, float radius, Color color)
	{
		var ring = new Vector3[SweepRingPoints + 1];
		for (int i = 0; i < SweepRingPoints; i++)
		{
			ring[i] = center + RingDirs[i] * radius;
		}
		ring[SweepRingPoints] = ring[0];
		DebugDraw.Lines(ring, color, 0.15f);
	}

	public static void DoHitscan(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		HitInfo hit = ResolveHit(ev, action, actor);
		if (hit.healthDamage <= 0f && hit.statusEffects == null && hit.buildups == null)
		{
			return;
		}

		World3D world3D = actor.AttackerNode?.GetWorld3D();
		if (world3D == null)
		{
			return;
		}

		// Per-tier scalars — sampled at activation, stashed on chargeT.
		// Range scales the event's authored hitScanRange (chargedRangeScale
		// multiplier, lerp from 1 at chargeT=0 to chargedRangeScale at
		// chargeT=1). Spread perturbs the aim direction uniformly within a
		// cone whose half-angle = MAX_SPREAD_HALF_ANGLE * spreadFraction,
		// with spread = accuracySpread01 / lerp(1, chargedAccuracyScale,
		// chargeT) so holding tightens.
		ItemAction tier = action.selectedTier;
		float chargeT = action.chargeT;
		float rangeScale = ItemAction.SampleRangeScale(tier, chargeT);
		float spreadScale = ItemAction.SampleAccuracySpread(tier, chargeT);

		Vector3 origin = actor.ActorWorldPosition + Vector3.Up;
		Vector3 direction = ApplySpread(actor.ActorForward, spreadScale);
		Vector3 rayEnd = origin + direction * ev.hitScanRange * rangeScale;

		var spaceState = world3D.DirectSpaceState;

		Godot.Collections.Array<Rid> bodyExclude = new();
		if (actor.AttackerNode is CollisionObject3D body)
		{
			bodyExclude.Add(body.GetRid());
		}

		// Clip against environment.
		using var envQuery = PhysicsRayQueryParameters3D.Create(origin, rayEnd);
		envQuery.CollisionMask = (uint)ECollisionLayer.Solid;
		envQuery.CollideWithAreas = false;
		envQuery.CollideWithBodies = true;
		envQuery.Exclude = bodyExclude;
		var envResult = spaceState.IntersectRay(envQuery);

		Vector3 hitPos = rayEnd;
		if (envResult.Count > 0)
		{
			hitPos = (Vector3)envResult["position"];
		}

		// Cast against hurtboxes up to the clipped end point.
		using var hurtQuery = PhysicsRayQueryParameters3D.Create(origin, hitPos);
		hurtQuery.CollisionMask = actor.AttackHurtboxMask;
		hurtQuery.CollideWithAreas = true;
		hurtQuery.CollideWithBodies = false;
		Rid? selfHurtBox = actor.SelfHurtBoxRid;
		if (selfHurtBox.HasValue)
		{
			hurtQuery.Exclude = new Godot.Collections.Array<Rid> { selfHurtBox.Value };
		}

		var hurtResult = spaceState.IntersectRay(hurtQuery);
		EHitResult hitResult = EHitResult.None;
		EDamageTriggerFlags hitTriggers = EDamageTriggerFlags.None;
		HurtBox hitHurtBox = null;
		if (hurtResult.Count > 0)
		{
			var collider = hurtResult["collider"].Obj;
			if (collider is HurtBox hurtBox)
			{
				bool isSelf = selfHurtBox.HasValue && hurtBox.GetRid() == selfHurtBox.Value;
				if (!isSelf && hurtBox.CanBeHit(hit))
				{
					// Query before Hit so Lethal sees pre-damage state. See DoMelee.
					hitResult = hurtBox.QueryHitType(hit);
					hitTriggers = hurtBox.QueryHitTriggers(hit);
					hurtBox.Hit(hit);
					hitPos = (Vector3)hurtResult["position"];
					hitHurtBox = hurtBox;
					if (hitResult == EHitResult.Health || hitResult == EHitResult.Lethal)
					{
						ApplyLifesteal(actor, action, hit.healthDamage);
					}
				}
			}
		}

		// Resolve impact: hurtbox first, then environment clip, then air.
		if (hitResult != EHitResult.None)
		{
			SpawnImpact(actor, PickImpactScene(ev, hitResult), hitPos);
			SpawnTriggerOverlays(actor, tier, hitTriggers, hitPos);
		}
		else if (envResult.Count > 0)
		{
			SpawnImpact(actor, ev.impactEnvironmentEffect, hitPos);
		}
		else
		{
			SpawnImpact(actor, ev.impactMissEffect, hitPos);
		}

		// Recoverable arrow. Triggers only for weapons that author an
		// arrowLootData reference (currently the bow) AND on tiers that flag
		// useAmmo — a non-ammo tier on the same weapon (e.g. a melee-bash
		// with a bow) skips the drop. Other hitscan sources (mob attacks,
		// traps) leave arrowLootData null and fire-and-forget. World.Current
		// is the active game world — used here rather than threading a World
		// through IActionActor since the hitscan handler already targets the
		// player's running game.
		//
		// Branch: a hit that landed on a still-living mob's hurtbox sticks the
		// arrow on that mob (Mob.Die later drops it as loose loot with an
		// outward impulse). Everything else — environment clip, miss, or a
		// lethal hit that just killed the mob — drops the arrow at the impact
		// point.
		if (action.context.primaryItem is WeaponState shootingWeapon
			&& shootingWeapon.data?.arrowLootData != null
			&& action.selectedTier?.useAmmo == true
			&& World.Current != null)
		{
			Mob targetMob = hitResult != EHitResult.None ? FindOwningMob(hitHurtBox) : null;
			if (targetMob != null && targetMob.alive)
			{
				targetMob.StickArrow(shootingWeapon, shootingWeapon.data.arrowLootData, hitPos, direction);
			}
			else
			{
				// Chest-style 45° pop on a random horizontal heading. Same
				// arc Mob.EjectLoot / EjectStuckArrows use, so arrows landing
				// on the ground vs popping off a corpse have a consistent
				// "freshly dropped" read. The launch also kicks the bob
				// animation in Loot.Settle on rest (the AnimationPlayer
				// branch keys on _initialImpulse != Vector3.Zero).
				World.Current.SpawnArrowLoot(hitPos, BuildArrowEjectImpulse(), shootingWeapon.data.arrowLootData, shootingWeapon);
			}
		}

		// Status-effect on-impact bursts fire at the resolved hit point —
		// hurtbox, environment clip, or the ray's air end. See DoMelee.
		actor.TriggerAttackImpact(hitPos);

		DebugDraw.Line(origin, hitPos, new Color(1f, 0f, 0f, 0.3f), 0.15f);
	}

	// Apply a single-frame AoE hit to every hurtbox inside `radius` of
	// `center`. Position-anchored sibling of DoMelee's sphere query, factored
	// out so status-effect on-impact bursts (elite lightning aura, etc.) reuse
	// the exact same hurtbox resolution — self-exclusion, friendly-fire skip,
	// one Hit per target. Friendly-fire policy rides on the DamageData (same as
	// every other hit), so allies are spared unless the payload opts in. No
	// charge-tier scaling and no per-target impact overlay: the burst's own fx
	// (spawned by the caller at `center`) is the visual, and the payload is
	// authored flat on the DamageData. Not a DoT — each call lands exactly once
	// per affected target. Does NOT re-enter the attack pipeline (it calls
	// HurtBox.Hit directly), so an on-impact burst can't recursively re-trigger
	// itself.
	public static void ApplyAreaDamage(IActionActor attacker, DamageData damage, Vector3 center, float radius, bool radialKnockback = false)
	{
		if (attacker == null || damage == null || radius <= 0f)
		{
			return;
		}
		World3D world3D = attacker.AttackerNode?.GetWorld3D();
		if (world3D == null)
		{
			return;
		}
		var sphere = new SphereShape3D() { Radius = radius };
		var query = new PhysicsShapeQueryParameters3D
		{
			Shape = sphere,
			Transform = new Transform3D(Basis.Identity, center),
			CollisionMask = attacker.AttackHurtboxMask,
			CollideWithAreas = true,
			CollideWithBodies = false,
		};
		var results = world3D.DirectSpaceState.IntersectShape(query, maxResults: 32);
		Rid? selfHurtBox = attacker.SelfHurtBoxRid;
		foreach (var result in results)
		{
			if (result["collider"].Obj is not HurtBox hurtBox)
			{
				continue;
			}
			if (selfHurtBox.HasValue && hurtBox.GetRid() == selfHurtBox.Value)
			{
				continue;
			}
			// Hit direction. Default is zero — the receiver applies no
			// knockback when it's zero (an elite's lightning discharge authors
			// none anyway). When `radialKnockback` is set (a shockwave like the
			// fairy dash burst), push each target directly away from the blast
			// center so the crowd scatters outward; a target sitting exactly on
			// center falls back to zero (no usable axis). The attacker node is
			// the source so the receiver still attributes the hit correctly.
			Vector3 hitDir = Vector3.Zero;
			if (radialKnockback)
			{
				Vector3 away = hurtBox.GlobalPosition - center;
				away.Y = 0f;
				if (away.LengthSquared() > 0.0001f)
				{
					hitDir = away.Normalized();
				}
			}
			HitInfo hit = new HitInfo(damage, attacker.AttackerNode, hitDir, attacker.ActorTeam);
			if (!hurtBox.CanBeHit(hit))
			{
				continue;
			}
			hurtBox.Hit(hit);
		}
		if (CVars.debugAoe.Value)
		{
			DebugDraw.Sphere(center, radius, new Color(0.6f, 0.85f, 1f, 0.3f), 0.15f);
		}
	}


	// Walks up from a HurtBox's tree position looking for the owning Mob.
	// Mobs author the HurtBox as a child Area3D, so walking GetParent() will
	// always find the Mob within a handful of hops. Non-mob hurtboxes (the
	// player's own, environmental damageables) return null and fall through
	// to the loose-loot path.
	public static Mob FindOwningMob(Node node)
	{
		while (node != null)
		{
			if (node is Mob mob)
			{
				return mob;
			}
			node = node.GetParent();
		}
		return null;
	}

	// 45° upward pop on a random horizontal heading at chest-eject speed.
	// Shared by the env-hit and miss paths so both produce the same
	// "freshly dropped" arc through Loot's physics; Mob.EjectStuckArrows
	// uses the same shape inline for arrows scattering off a corpse.
	private const float ARROW_EJECT_SPEED = 5f;
	public static Vector3 BuildArrowEjectImpulse()
	{
		float horizontalSpeed = ARROW_EJECT_SPEED * Mathf.Cos(Mathf.Pi / 4f);
		float verticalSpeed = ARROW_EJECT_SPEED * Mathf.Sin(Mathf.Pi / 4f);
		float angle = (float)GD.RandRange(0.0, Mathf.Tau);
		return new Vector3(
			horizontalSpeed * Mathf.Cos(angle),
			verticalSpeed,
			horizontalSpeed * Mathf.Sin(angle)
		);
	}

	// Spawns a Projectile at the actor's position. Two flight modes:
	//
	// Flat (default, `projectileArcing = false`): flies along the actor's
	// forward with the tier's accuracy spread applied. Damage on impact
	// comes from ResolveHit — same template precedence as melee/hitscan.
	// Mirrors DoHitscan's spread/range scaling so a single weapon can swap
	// between hitscan and projectile without re-authoring its curves.
	//
	// Arcing (`projectileArcing = true`): a gravity-driven, COLLISION-RESPECTING
	// lob. For the player this fires the exact arc the Arced-aim reticle
	// previewed (it solved launch speed + pitch and published the velocity);
	// `projectileLifetimeSeconds` is the fuse cap, not an exact flight time. The
	// projectile detonates on the first surface / creature it meets or at the
	// fuse, whichever is first, so it can't pass through walls or bury itself.
	// Used for delivery-style attacks (thrown explosive); pairs with an authored
	// `impactEvent` that fires at the landing point.
	public static void DoProjectile(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (ev.projectileScene == null)
		{
			return;
		}
		HitInfo hit = ResolveHit(ev, action, actor);
		if (hit.healthDamage <= 0f && hit.statusEffects == null && hit.buildups == null)
		{
			return;
		}

		Node3D attacker = actor.AttackerNode;
		Node parent = attacker?.GetParent();
		if (parent == null)
		{
			return;
		}

		ItemAction tier = action.selectedTier;
		// Projectiles launch from the actor's chest, ArcLaunchHeight above the feet
		// — also the drop an arced hump uses to bottom out at foot level.
		Vector3 origin = actor.ActorWorldPosition + Vector3.Up * ArcLaunchHeight;

		WeaponState firingWeapon = action.context.primaryItem as WeaponState;
		DamageData damageData = firingWeapon?.data?.GetDamage(ev.damageProfileKey);
		Rid? excludeBody = (attacker is CollisionObject3D body) ? body.GetRid() : null;
		// Arrow-recovery binding is decided here at fire time: only populate
		// arrowLootData if the firing tier flags useAmmo. A non-ammo tier on
		// the same weapon (e.g. a melee-bash with a bow) leaves it null and
		// the projectile skips the drop even though the weapon authors an
		// arrowLootData reference.
		ArrowLootData arrowLootData = (tier?.useAmmo == true) ? firingWeapon?.data?.arrowLootData : null;
		ProjectileImpact impact = new ProjectileImpact
		{
			miss = ev.impactMissEffect,
			environment = ev.impactEnvironmentEffect,
			health = ev.impactHealthEffect,
			armor = ev.impactArmorEffect,
			lethal = ev.impactLethalEffect,
			crit = tier?.impactCritEffect,
			backstab = tier?.impactBackstabEffect,
			sourceWeapon = firingWeapon,
			arrowLootData = arrowLootData,
		};

		Vector3 velocity;
		float lifetime;
		float gravity = 0f;
		bool noCollide = false;
		bool bounce = false;
		float bounciness = 0f;
		float friction = 0f;
		if (ev.projectileArcing)
		{
			// Arcing is a player throw — it needs the aiming reticle (mob attacks
			// / weapons with no reticle have nowhere to lob, so the event no-ops).
			if (actor is not Player player || player.AimingReticle == null)
			{
				return;
			}
			AimingReticle reticle = player.AimingReticle;
			// lifetime is the FUSE; the hump's shape/feel is the (shorter) arc
			// duration, so the lob keeps falling/bouncing past its landing.
			lifetime = ev.projectileLifetimeSeconds;
			if (lifetime <= 0f || ev.projectileArcRise <= 0f || ev.projectileGravity <= 0f)
			{
				return;
			}
			if (reticle.HasArcLaunch)
			{
				// Fire the EXACT hump the reticle previewed.
				velocity = reticle.ArcLaunchVelocity;
				gravity = reticle.ArcLaunchGravity;
			}
			else if (reticle.HasAimWorldPosition)
			{
				// Fallback when no Arced solve is published (e.g. fired the instant
				// aim began): rebuild the same hump toward the aim cursor. Vertical is
				// rise + gravity; horizontal covers the aim distance over the fuse.
				gravity = ev.projectileGravity;
				float launchVy = AimingReticle.ArcLaunchVerticalSpeed(ev.projectileArcRise, gravity);
				Vector3 delta = reticle.AimWorldPosition - origin;
				Vector3 horiz = new Vector3(delta.X, 0f, delta.Z);
				float horizDist = horiz.Length();
				Vector3 horizDir = horizDist > 1e-4f ? horiz / horizDist : Vector3.Zero;
				velocity = horizDir * (horizDist / lifetime) + Vector3.Up * launchVy;
			}
			else
			{
				return;
			}
			// Bounces off solids it meets before the fuse, then detonates at
			// `lifetime` — so it can't pass through walls, and emergent hits (a
			// grenade rebounding off a wall around a corner) just work.
			noCollide = false;
			bounce = true;
			bounciness = ev.projectileBounciness;
			friction = ev.projectileFriction;
		}
		else
		{
			// Flat flight: per-tier spread + range scalars (same convention as
			// DoHitscan). rangeScale shortens lifetime so reach drops
			// proportionally; speed stays constant so flight feel doesn't change.
			float chargeT = action.chargeT;
			float spreadScale = ItemAction.SampleAccuracySpread(tier, chargeT);
			float rangeScale = ItemAction.SampleRangeScale(tier, chargeT);
			Vector3 direction = ApplySpread(actor.ActorForward, spreadScale);
			velocity = direction * ev.projectileSpeed;
			lifetime = ev.projectileLifetimeSeconds * rangeScale;
		}

		// Compose this shot's weapon mods (loot / ItemDescriptor onto
		// WeaponState.statusEffects): the event's authored pierce base maxed
		// against every active mod that reaches this charge tier, and whether any
		// such mod makes the shot detonate on contact. A mod reaches the shot when
		// it's AllAttacks-scoped or its SpecificCharge index matches the firing
		// tier (resolved by position in the weapon's action profile).
		int pierceCount = Mathf.Max(0, ev.pierceCount);
		bool detonateOnContact = false;
		if (firingWeapon != null)
		{
			int firingChargeIndex = FindChargeIndex(firingWeapon, tier);
			pierceCount = System.Math.Max(pierceCount, firingWeapon.statusEffects.ProjectilePierceCount(firingChargeIndex));
			detonateOnContact = firingWeapon.statusEffects.ProjectilesDetonateOnContact(firingChargeIndex);
		}

		// "Fragile" weapon mod: a projectile that would normally bounce and wait
		// out its fuse instead shatters on the first surface / creature it
		// touches. Only meaningful when the projectile carries an impactEvent
		// (the payload fired on completion, e.g. the explosion) — otherwise the
		// bounce-and-fuse flight is left intact. Disabling bounce drops the
		// projectile into its default collision-detonation path (the first solid
		// OR hurtbox ends it and fires impactEvent there); the gravity arc is
		// untouched. See StatusEffectData.projectilesDetonateOnContact.
		if (bounce && ev.impactEvent != null && detonateOnContact)
		{
			bounce = false;
		}

		Projectile.Launch(
			parent,
			ev.projectileScene,
			lifetime,
			ev.projectileLoopEffect,
			origin,
			velocity,
			damageData,
			attacker,
			actor.AttackHurtboxMask,
			actor.SelfHurtBoxRid,
			excludeBody,
			impact,
			gravity,
			noCollide,
			ev.impactEvent,
			actor.ActorTeam,
			hit.friendlyFire,
			bounce,
			bounciness,
			friction,
			pierceCount);
	}

	// Lifesteal: heal the attacker by the firing weapon's vampiric fraction of
	// the health damage just dealt by a landed melee/hitscan hit. No-op for mob
	// attacks (no WeaponState), zero damage, or a weapon carrying no vampiric mod
	// that reaches the firing charge tier. Read off the weapon's composed mods
	// the same way DoProjectile reads pierce / detonate.
	private static void ApplyLifesteal(IActionActor actor, in PlayerAction action, float healthDamageDealt)
	{
		if (healthDamageDealt <= 0f)
		{
			return;
		}
		if (action.context.primaryItem is not WeaponState weapon)
		{
			return;
		}
		float fraction = weapon.statusEffects.Vampiric(FindChargeIndex(weapon, action.selectedTier));
		if (fraction > 0f)
		{
			actor.Heal(healthDamageDealt * fraction);
		}
	}

	// Index of `tier` within the weapon's action profile (the charge-tier id
	// used to scope weapon mods), or -1 if the weapon has no profile or the tier
	// isn't found. Compared by reference — `tier` is one of the profile's own
	// ItemAction instances.
	private static int FindChargeIndex(WeaponState weapon, ItemAction tier)
	{
		Godot.Collections.Array<ItemAction> tiers = weapon?.data?.actionProfile?.chargedActions;
		if (tiers == null)
		{
			return -1;
		}
		for (int i = 0; i < tiers.Count; i++)
		{
			if (tiers[i] == tier)
			{
				return i;
			}
		}
		return -1;
	}

	// Position-aware sub-dispatcher for projectile impactEvents (and any
	// future "fire at a point" sources). Subset of DispatchEvent because
	// most handlers need an action context (selectedTier, primaryItem,
	// chargeT, etc.) we don't have here. Currently supports SpawnAreaEffect
	// — the canonical "arcing arrow lands → spawn AoE at the landing point"
	// path. Other handlers no-op silently; their authored fields on the
	// nested event just get ignored.
	public static void DispatchAtPosition(ItemEvent ev, Vector3 position, Node parent, WeaponData sourceWeaponData, ETeam attackerTeam)
	{
		if (ev == null) { return; }
		if ((ev.type & EItemEventType.SpawnAreaEffect) != 0 && ev.areaEffectScene != null)
		{
			Node host = (Node)World.Current ?? parent;
			if (host != null)
			{
				Node3D instance = ev.areaEffectScene.Instantiate<Node3D>();
				// Apply weapon-side overrides BEFORE AddChild — DamageZone's
				// _Ready builds its interval HitInfos from the (possibly
				// overridden) damageIntervals, so the override has to land
				// first.
				if (instance is GasCloud cloud)
				{
					ResolveAreaPayload(ev, sourceWeaponData, null,
						out ContinuousDamageData continuous,
						out Godot.Collections.Array<IntervalDamageEntry> intervals);
					cloud.Initialize(ev, continuous, intervals, attackerTeam);
				}
				host.AddChild(instance);
				instance.GlobalPosition = position;
			}
		}
		if ((ev.type & EItemEventType.CameraShake) != 0)
		{
			GameCamera cam = GameCamera.Current;
			if (cam != null)
			{
				Vector3 playerPos = GameClient.Current?.Player?.GlobalPosition ?? position;
				cam.Shake.AddImpulse(ev.cameraShakeMagnitude, ev.cameraShakeDuration, position, ev.cameraShakeRange, playerPos);
			}
		}
		if ((ev.type & EItemEventType.ScreenFlash) != 0)
		{
			ScreenEffectsController.Current?.Flash(ev.screenFlashColor, ev.screenFlashIntensity, ev.screenFlashFadeSeconds);
		}
	}

	// Spawns ev.areaEffectScene at the player's aim cursor (when valid) or
	// the actor's feet otherwise. The scene is parented to the World so it
	// outlives the actor and stays put as the actor keeps moving. Used for
	// positional-aim AoEs (rain of arrows, fire patch, etc.) whose lifetime
	// and damage ticking live on the spawned scene itself.
	public static void DoSpawnAreaEffect(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (ev.areaEffectScene == null)
		{
			return;
		}
		Vector3 position = actor.ActorWorldPosition;
		if (actor is Player player && player.AimingReticle != null && player.AimingReticle.HasAimWorldPosition)
		{
			position = player.AimingReticle.AimWorldPosition;
		}
		Node parent = (Node)World.Current ?? actor.AttackerNode?.GetParent();
		if (parent == null)
		{
			return;
		}
		Node3D instance = ev.areaEffectScene.Instantiate<Node3D>();
		if (instance is GasCloud cloud)
		{
			WeaponData weaponData = (action.context.primaryItem as WeaponState)?.data;
			MobData mobData = (actor as Mob)?.mobData;
			ResolveAreaPayload(ev, weaponData, mobData,
				out ContinuousDamageData continuous,
				out Godot.Collections.Array<IntervalDamageEntry> intervals);
			cloud.Initialize(ev, continuous, intervals, actor.ActorTeam);
		}
		parent.AddChild(instance);
		instance.GlobalPosition = position;
	}

	// Summons ev.minionData at the actor's aim point (positional cursor when
	// active, else the actor position) and hands it to the summoning weapon,
	// which owns the minion's lifetime — recycling the oldest past its cap and
	// destroying all of them when the weapon is unequipped/removed. The minion
	// spawns on the player team (authored on its MobData) and self-drains via
	// its MobData.spawnStatusEffect, so this handler only spawns + registers.
	// Player-only — mob actors don't summon, and the weapon ownership requires a
	// WeaponState in hand.
	public static void DoSummonMinion(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (ev.minionData == null || actor is not Player)
		{
			return;
		}
		if (action.context.primaryItem is not WeaponState weapon)
		{
			return;
		}
		World world = World.Current;
		if (world == null)
		{
			return;
		}
		Mob minion = world.SpawnMob(ev.minionData, ResolveAimPoint(actor));
		if (minion != null)
		{
			weapon.AddMinion(minion);
		}
	}

	// The actor's current aim point: the player's positional aim cursor when
	// one is active, otherwise the actor's own world position. Shared by the
	// positional-aim handlers (area effect, dig, summon) and ActionRunner's
	// channeled-charge zone so they all read the same ground target.
	public static Vector3 ResolveAimPoint(IActionActor actor)
	{
		if (actor is Player player && player.AimingReticle != null && player.AimingReticle.HasAimWorldPosition)
		{
			return player.AimingReticle.AimWorldPosition;
		}
		return actor.ActorWorldPosition;
	}

	// Spawns a channeled-charge zone (GasCloud) at the actor's aim point and
	// returns it so ActionRunner can reposition + free it over the channel's
	// lifetime. The scene's damage is authored in the .tscn; we only stamp the
	// caster's team so the channel spares the caster + allies. Mirrors
	// DoSpawnAreaEffect's parenting (World so it outlives the actor) and the
	// before-AddChild Initialize ordering DamageZone requires.
	public static GasCloud SpawnChannelZone(IActionActor actor, PackedScene scene, float radius)
	{
		if (scene == null)
		{
			return null;
		}
		Node parent = (Node)World.Current ?? actor.AttackerNode?.GetParent();
		if (parent == null)
		{
			return null;
		}
		Node3D instance = scene.Instantiate<Node3D>();
		if (instance is not GasCloud cloud)
		{
			instance.QueueFree();
			return null;
		}
		cloud.InitializeChannel(actor.ActorTeam, radius);
		parent.AddChild(cloud);
		cloud.GlobalPosition = ResolveAimPoint(actor);
		return cloud;
	}

	// Resolves an ItemEvent's SpawnAreaEffect payload against the firing
	// entity's continuousProfiles / damageProfiles dictionaries. Weapon-
	// driven hits prefer the WeaponData; mob-driven hits fall back to
	// MobData. Either may be null — keys that miss yield no profile and
	// are silently skipped. The returned `intervals` array contains one
	// IntervalDamageEntry per AreaIntervalSpec whose key resolved to a
	// non-null DamageData.
	private static void ResolveAreaPayload(
		ItemEvent ev,
		WeaponData weaponData,
		MobData mobData,
		out ContinuousDamageData continuous,
		out Godot.Collections.Array<IntervalDamageEntry> intervals)
	{
		continuous = null;
		if (!string.IsNullOrEmpty(ev.areaContinuousKey.ToString()))
		{
			continuous = weaponData?.GetContinuousDamage(ev.areaContinuousKey)
				?? mobData?.GetContinuousDamage(ev.areaContinuousKey);
		}
		intervals = null;
		if (ev.areaIntervals != null && ev.areaIntervals.Count > 0)
		{
			intervals = new Godot.Collections.Array<IntervalDamageEntry>();
			for (int i = 0; i < ev.areaIntervals.Count; i++)
			{
				AreaIntervalSpec spec = ev.areaIntervals[i];
				if (spec == null)
				{
					continue;
				}
				DamageData damage = weaponData?.GetDamage(spec.damageProfileKey)
					?? mobData?.GetDamage(spec.damageProfileKey);
				if (damage == null)
				{
					continue;
				}
				intervals.Add(new IntervalDamageEntry
				{
					damage = damage,
					tickInterval = spec.tickInterval,
					tickOnEnter = spec.tickOnEnter,
				});
			}
		}
	}

	// Fires a one-shot camera-shake impulse anchored at the actor's position.
	// Range > 0 lets the driver apply a distance falloff against the player
	// (useful for mob-sourced hits — a far-away ogre stomp shakes less than
	// one in your face). Range == 0 fires at full magnitude regardless of
	// the actor's location, which is the right default for player-sourced
	// melee/hitscan because the actor IS the camera target.
	public static void DoCameraShake(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		GameCamera cam = GameCamera.Current;
		if (cam == null) { return; }
		Vector3 playerPos = GameClient.Current?.Player?.GlobalPosition ?? actor.ActorWorldPosition;
		cam.Shake.AddImpulse(ev.cameraShakeMagnitude, ev.cameraShakeDuration, actor.ActorWorldPosition, ev.cameraShakeRange, playerPos);
	}

	// Digs at the player's aim cursor (positional-aim tiers) or a short reach
	// in front of the actor (directional / no cursor). Routes to World.TryDig,
	// which uncovers the nearest buried-item spot in range — or, failing that,
	// forces the nearest burrowed mob to the surface. Only the player digs;
	// mob actors have no shovel.
	public static void DoDig(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (actor is not Player player)
		{
			return;
		}
		World world = World.Current;
		if (world == null)
		{
			return;
		}
		Vector3 center;
		if (player.AimingReticle != null && player.AimingReticle.HasAimWorldPosition)
		{
			center = player.AimingReticle.AimWorldPosition;
		}
		else
		{
			Vector3 forward = player.ActorForward;
			forward.Y = 0f;
			center = player.ActorWorldPosition + forward.Normalized() * ev.digReach;
		}
		EDigResult result = world.TryDig(center, ev.digRadius, player);
		PackedScene effect = result switch
		{
			EDigResult.Treasure => ev.digTreasureEffect,
			EDigResult.Common => ev.digCommonEffect,
			_ => ev.digNothingEffect,
		};
		if (effect != null)
		{
			Node parent = (Node)World.Current ?? player.AttackerNode?.GetParent();
			if (parent != null)
			{
				Fx.Create(effect, parent, center);
			}
		}
	}

	public static void DoUseAmmo(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (action.context.primaryItem is WeaponState weapon && weapon.ammo > 0)
		{
			weapon.ammo--;
		}
	}

	public static void DoApplyEffect(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (ev.effects == null)
		{
			return;
		}
		for (int i = 0; i < ev.effects.Count; i++)
		{
			ItemEffect effect = ev.effects[i];
			if (effect != null)
			{
				effect.Apply(actor, action.context);
			}
		}
	}

	// Pulse-applies each entry in ev.effects to every alive same-team Mob
	// inside an ev.areaRadius sphere around the actor (the source mob itself
	// is included — a battle cry buffs the crier too). ev.fx fires once at
	// the actor as the source-side audiovisual cue. Player-sourced cries are
	// not authored today; the handler no-ops when the actor isn't a Mob
	// rather than guess at "Player friendlies" semantics — flip it on by
	// adding a Player.Team branch when a player buff is wanted.
	private static readonly System.Collections.Generic.List<Mob> _areaBuffScratch = new();
	public static void DoApplyAreaStatusEffect(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (ev.effects == null || ev.effects.Count == 0)
		{
			return;
		}
		if (ev.fx != null)
		{
			SpawnOnActor(actor, ev.fx);
		}
		if (actor is not Mob sourceMob || sourceMob.mobData == null)
		{
			return;
		}
		float radius = ev.areaRadius;
		if (radius <= 0f)
		{
			return;
		}
		MobSpatialHash hash = sourceMob.World?.MobSpatialHash;
		if (hash == null)
		{
			return;
		}
		ETeam team = sourceMob.mobData.team;
		_areaBuffScratch.Clear();
		hash.QueryRadius(actor.ActorWorldPosition, radius, _areaBuffScratch);
		for (int i = 0; i < _areaBuffScratch.Count; i++)
		{
			Mob target = _areaBuffScratch[i];
			if (target == null || !target.alive || target.mobData == null || target.mobData.team != team)
			{
				continue;
			}
			for (int j = 0; j < ev.effects.Count; j++)
			{
				ItemEffect effect = ev.effects[j];
				if (effect != null)
				{
					effect.Apply(target, action.context);
				}
			}
		}
		_areaBuffScratch.Clear();
	}

	public static void DoDecrementStack(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		ItemState item = action.context.primaryItem;
		if (item == null)
		{
			return;
		}
		Inventory inv = (actor is Player player) ? player.Inventory : null;
		// Reveal the item's real name on first successful use. Decrement is
		// the canonical "actually consumed" hook — only consumables flow
		// through here, and only ones whose timeline reached this event.
		// Identification is shared across all stacks/recipes of the same
		// ItemData; the read-side (WorldSimState.GetItemDisplayName) picks it
		// up immediately so the inventory row, recipe button, and cook
		// announcement all reveal in lockstep.
		if (actor is Player identifyingPlayer && identifyingPlayer.World?.WorldState?.SimState?.IdentifyItem(item.data) == true)
		{
			inv?.NotifyChanged();
		}
		item.stackCount--;
		// We mutated stackCount directly — Inventory has no other way to
		// learn that an item changed under it. Fire its onChanged signal so
		// any listening UI (e.g. the inventory screen's stack badge) can
		// refresh without polling.
		if (item.stackCount > 0)
		{
			inv?.NotifyChanged();
			return;
		}
		// Stack hit zero — remove from inventory. The Player's Inventory
		// holds the canonical reference; route through it so equip/active-slot
		// fields clear too (Remove fires onChanged itself). For non-Player
		// actors (mobs), the item simply drops out of context with no further
		// bookkeeping.
		inv?.Remove(item);
	}

	public static void DoToggleMovingLight(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (actor is not Player player)
		{
			return;
		}
		if (action.context.primaryItem is not ConsumableState consumable)
		{
			return;
		}
		consumable.isActive = !consumable.isActive;
		player.RefreshCarriedLight();
	}

	public static void DoOpenInteractive(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		IInteractive interactive = action.context.primaryInteractive;
		if (interactive == null)
		{
			return;
		}
		if (!interactive.CanInteract())
		{
			return;
		}
		// Capture position/parent BEFORE Complete() — Loot.Complete QueueFrees
		// the node, which would leave us no parent to spawn into.
		Node3D node = interactive as Node3D;
		Node parent = node?.GetParent();
		Vector3 pos = node?.Position ?? Vector3.Zero;
		interactive.Complete(action.context.interactiveActionIndex);
		if (ev.fx != null && parent != null)
		{
			Fx.Create(ev.fx, parent, pos);
		}
	}

	public static void DoApplyMotion(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		actor.ApplyMotion(ev.motionSpeed, ev.motionDuration, ev.motionFreezeGravity);
	}

	// Grants ev.languageComponents of ev.language to the learner — for
	// sources whose language is uniform across all uses (consumables, mob
	// dialogue). LearnLanguageComponents returns true only when this call
	// newly added at least one bit, so the firstLearnEffect doesn't fire on
	// re-trigger of a fully-known language (or on a partial event whose
	// pieces the player already had). Non-Player actors don't have a
	// learned-language set — they no-op. Per-instance teaching
	// (KnowledgeStone) doesn't go through this flag — it handles its own
	// learn-and-display inside Complete() to guarantee ordering against the
	// same-tick text reveal.
	public static void DoLearnLanguage(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (ev.language == null || actor is not Player player)
		{
			return;
		}
		if (player.LearnLanguageComponents(ev.language, ev.languageComponents))
		{
			SpawnOnActor(actor, ev.firstLearnEffect);
		}
	}

	// Generalized teach event — dispatches a TeachableConcept against the
	// player's world-state collections (language pieces, recipe discovery
	// set, region discovery set, ...). First-learn fx gates on concept.Teach
	// returning true, matching DoLearnLanguage's silent-on-re-teach contract.
	//
	// Concept resolution prefers the event-authored ref (`ev.concept`); when
	// that's null, falls back to the consuming item's concept if it's a
	// ScrollData. This lets every scroll variant share a single action
	// profile — the profile fires a concept-less LearnConcept event and the
	// handler pulls the specific concept off the scroll being used, keeping
	// ScrollData.concept the single source of truth for both the displayed
	// name and the granted concept.
	public static void DoLearnConcept(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (actor is not Player player)
		{
			return;
		}
		TeachableConcept concept = ev.concept;
		if (concept == null && action.context.primaryItem?.data is ScrollData scroll)
		{
			concept = scroll.concept;
		}
		if (concept == null)
		{
			return;
		}
		if (concept.Teach(player))
		{
			SpawnOnActor(actor, ev.firstLearnEffect);
		}
	}

	public static void DoConsumeFromInventory(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (ev.reagent == null || action.context.supportingItems == null)
		{
			return;
		}
		int remaining = ev.consumeAmount;
		Player player = actor as Player;
		// Walk supporting items, decrement matching stacks until consumeAmount
		// is fulfilled. Stack→0 removes from inventory via Player.Inventory.
		for (int i = 0; i < action.context.supportingItems.Count && remaining > 0; i++)
		{
			ItemState item = action.context.supportingItems[i];
			if (item == null || item.data != ev.reagent)
			{
				continue;
			}
			int take = System.Math.Min(remaining, item.stackCount);
			item.stackCount -= take;
			remaining -= take;
			if (item.stackCount <= 0 && player?.Inventory != null)
			{
				player.Inventory.Remove(item);
			}
		}
	}

	// Build the HitInfo a Melee/Hitscan event should apply: looks up the
	// event's damageProfileKey on the driving weapon's damageProfiles dict,
	// or on the firing mob's MobData.damageProfiles when the actor is a Mob
	// (mob attacks have no WeaponState). Source is the actor so receivers
	// see the attacker. Returns a default HitInfo (no damage) if the lookup
	// fails or there's no source — caller should early-out. Conditional
	// crit / dizzy behavior rides on `template.modifiers`, no separate
	// parameter needed here.
	private static HitInfo ResolveHit(ItemEvent ev, in PlayerAction action, IActionActor actor)
	{
		DamageData template = null;
		if (action.context.primaryItem is WeaponState weapon)
		{
			template = weapon.data?.GetDamage(ev.damageProfileKey);
		}
		else if (actor is Mob mob)
		{
			template = mob.mobData?.GetDamage(ev.damageProfileKey);
		}
		if (template == null)
		{
			return default;
		}
		// Hit direction = attacker's forward. Knockback uses this to push
		// the target along the swing axis; senders that need a different
		// direction (e.g. radial pop-up from a trap) build HitInfo directly.
		HitInfo hit = new HitInfo(template, actor.AttackerNode, actor.ActorForward, actor.ActorTeam);
		// Source-side buffs / debuffs scale the swing's healthDamage at fire
		// time. Only healthDamage is scaled — buildups / hitstun / knockback keep
		// their authored CC pattern so a damage-only buff doesn't accidentally
		// turn into a stagger-cannon.
		float mul = actor.OutgoingDamageMultiplier;
		if (mul != 1f)
		{
			hit.healthDamage *= mul;
		}
		return hit;
	}

	// Maximum spread cone half-angle, in radians, when the tier's spread
	// fraction is 1.0. Tuned so an early-release bow shot is visibly
	// inaccurate without being absurd. Per-tier accuracySpread01 scales this.
	public const float MAX_SPREAD_HALF_ANGLE = 0.18f;

	// Height above the actor's feet that projectiles launch from (the chest). For
	// arced lobs it's also the drop used so the hump bottoms out at foot level.
	private const float ArcLaunchHeight = 1f;

	// Best-of priority for melee swings that overlap multiple hurtboxes:
	// a real damageable hit beats an absorbed hit beats a prop ping.
	private static int HitPriority(EHitResult r)
	{
		return r switch
		{
			EHitResult.Lethal => 4,
			EHitResult.Health => 3,
			EHitResult.Armor => 2,
			EHitResult.Object => 1,
			_ => 0,
		};
	}

	public static PackedScene PickImpactScene(ItemEvent ev, EHitResult result)
	{
		return result switch
		{
			// Lethal falls back to Health if no kill-specific scene is wired —
			// most weapons don't ship a unique kill sound.
			EHitResult.Lethal => ev.impactLethalEffect ?? ev.impactHealthEffect,
			EHitResult.Health => ev.impactHealthEffect,
			EHitResult.Armor => ev.impactArmorEffect,
			EHitResult.Object => ev.impactEnvironmentEffect,
			_ => null,
		};
	}

	private static void SpawnImpact(IActionActor actor, PackedScene scene, Vector3 position)
	{
		SpawnAtWorld(actor, scene, position);
	}

	// Spawn the tier's crit / backstab overlays on top of the base impact fx.
	// Called by Melee, Hitscan, and (via ProjectileImpact) the projectile path
	// once a hurtbox hit lands; the receiver's HurtBox.QueryHitTriggers reports
	// which trigger conditions held. Null tier or null scenes silently skip.
	public static void SpawnTriggerOverlays(IActionActor actor, ItemAction tier, EDamageTriggerFlags triggers, Vector3 position)
	{
		if (tier == null || triggers == EDamageTriggerFlags.None) { return; }
		if ((triggers & EDamageTriggerFlags.Crit) != 0)
		{
			SpawnAtWorld(actor, tier.impactCritEffect, position);
		}
		if ((triggers & EDamageTriggerFlags.Backstab) != 0)
		{
			SpawnAtWorld(actor, tier.impactBackstabEffect, position);
		}
	}

	// World-parented one-shot at a fixed world position — matches the puff /
	// blood / footstep convention so the effect stays put as the actor keeps
	// moving. Returns the spawned Fx, or null if nothing spawned.
	public static Fx SpawnAtWorld(IActionActor actor, PackedScene scene, Vector3 position)
	{
		if (scene == null)
		{
			return null;
		}
		Node parent = actor.AttackerNode?.GetParent();
		if (parent == null)
		{
			return null;
		}
		return Fx.Create(scene, parent, position);
	}

	// Actor-parented effect — tracks the actor as it moves. Use for charge
	// loops and any sound that should follow the wielder.
	public static Fx SpawnOnActor(IActionActor actor, PackedScene scene)
	{
		if (scene == null || actor.AttackerNode == null)
		{
			return null;
		}
		return Fx.Create(scene, actor.AttackerNode, Vector3.Zero);
	}

	private static Vector3 ApplySpread(Vector3 forward, float spread01)
	{
		if (spread01 <= 0f)
		{
			return forward;
		}
		float halfAngle = Mathf.Clamp(spread01, 0f, 1f) * MAX_SPREAD_HALF_ANGLE;
		// Uniform jitter on a cone around the forward axis. Use the actor's
		// up as the rotation axis for the random heading; the elevation jitter
		// rotates around an in-plane perpendicular axis.
		Vector3 axisUp = Vector3.Up;
		Vector3 right = forward.Cross(axisUp);
		if (right.LengthSquared() < 1e-4f)
		{
			right = Vector3.Right;
		}
		else
		{
			right = right.Normalized();
		}
		float yaw = (float)GD.RandRange(-halfAngle, halfAngle);
		float pitch = (float)GD.RandRange(-halfAngle, halfAngle);
		Vector3 dir = forward.Rotated(axisUp, yaw).Rotated(right, pitch);
		return dir.Normalized();
	}
}
