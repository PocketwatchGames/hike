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
		if (hit.healthDamage <= 0f && hit.buildups == null)
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
				HitPrediction prediction = hurtBox.QueryHit(hit);
				EHitResult r = prediction.Result;
				hurtBox.Hit(hit);
				if (r == EHitResult.Health || r == EHitResult.Lethal)
				{
					healthDamageDealt += hit.healthDamage;
				}
				if (HitPriority(r) > HitPriority(bestResult))
				{
					bestResult = r;
					bestTriggers = prediction.Triggers;
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
		// Item-side weapon-mod discharges (a Shocking weapon's chain lightning)
		// fire from the same impact point.
		TriggerWeaponModChains(actor, action, impactPos);
		// On-attack weapon-mod projectiles (a Seeking sword's homing missiles):
		// OnSwing-scoped mods fire on every swing; OnHit-scoped only when a
		// creature was struck.
		FireWeaponModAttackProjectiles(actor, ref action, bestResult != EHitResult.None);
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
		if (hit.healthDamage <= 0f && hit.buildups == null)
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
		Vector3 direction = ApplySpread(AimForward(actor, action.context, origin), spreadScale);
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
					HitPrediction prediction = hurtBox.QueryHit(hit);
					hitResult = prediction.Result;
					hitTriggers = prediction.Triggers;
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
		// traps) leave arrowLootData null and fire-and-forget. Sim.Current
		// is the active game world — used here rather than threading a Sim
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
			&& Sim.Current != null)
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
				Sim.Current.SpawnArrowLoot(hitPos, BuildArrowEjectImpulse(), shootingWeapon.data.arrowLootData, shootingWeapon);
			}
		}

		// Status-effect on-impact bursts fire at the resolved hit point —
		// hurtbox, environment clip, or the ray's air end. See DoMelee.
		actor.TriggerAttackImpact(hitPos);
		TriggerWeaponModChains(actor, action, hitPos);
		// On-attack weapon-mod projectiles (see DoMelee). OnHit fires when the
		// shot connected with a creature.
		FireWeaponModAttackProjectiles(actor, ref action, hitResult != EHitResult.None);

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

	// Chain lightning: from `origin`, repeatedly find a random enemy hurtbox
	// within `chainRange` of the current link and feed it Electrical BUILDUP,
	// hopping up to `maxChains` times. Each link is chosen uniformly at random
	// among all in-range targets not yet struck by THIS chain, so the arc forks
	// unpredictably through a crowd. A link only DISCHARGES (dischargeDamage — a
	// jolt of damage + Dizzy) and passes the arc onward when its shock meter
	// crosses the threshold; the buildup shrinks each hop (chainBuildupFalloff),
	// so the chain dies once a link can't cross. Wet targets are 2x vulnerable to
	// Electrical buildup, so arcs propagate freely through soaked crowds and
	// fizzle on dry ones. Shared by the Shocking weapon mod (player) and the
	// elite lightning aura (goblins); team scoping rides on the attacker's
	// AttackHurtboxMask + the receiver's CanBeHit, exactly like ApplyAreaDamage.
	// Does not re-enter the attack pipeline.
	public static void ApplyChainLightning(IActionActor attacker, ChainLightningData data, Vector3 origin)
	{
		if (attacker == null || data?.shockEffect == null || data.maxChains <= 0 || data.chainRange <= 0f)
		{
			return;
		}
		World3D world3D = attacker.AttackerNode?.GetWorld3D();
		if (world3D == null)
		{
			return;
		}
		Node fxHost = (Node)Sim.Current ?? attacker.AttackerNode?.GetParent();
		Rid? selfHurtBox = attacker.SelfHurtBoxRid;
		var struck = new System.Collections.Generic.HashSet<ulong>();
		var sphere = new SphereShape3D { Radius = data.chainRange };
		Vector3 current = origin;
		float buildup = data.buildupPerHit;

		// Bolt from the attacker (weapon) to the impact point — the "zap the thing
		// you hit" arc. Drawn even if no chain link is found.
		SpawnChainArc(fxHost, data.boltFx, attacker.ActorWorldPosition, origin);
		for (int hop = 0; hop < data.maxChains; hop++)
		{
			var query = new PhysicsShapeQueryParameters3D
			{
				Shape = sphere,
				Transform = new Transform3D(Basis.Identity, current),
				CollisionMask = attacker.AttackHurtboxMask,
				CollideWithAreas = true,
				CollideWithBodies = false,
			};
			var results = world3D.DirectSpaceState.IntersectShape(query, maxResults: 32);
			// Reservoir-sample one eligible target (k=1) so every in-range,
			// not-yet-struck, hittable enemy is equally likely without allocating
			// a candidate list.
			HurtBox pick = null;
			int seen = 0;
			foreach (var result in results)
			{
				if (result["collider"].Obj is not HurtBox hurtBox)
				{
					continue;
				}
				Rid rid = hurtBox.GetRid();
				if ((selfHurtBox.HasValue && rid == selfHurtBox.Value) || struck.Contains(rid.Id))
				{
					continue;
				}
				if (!CanChainTo(hurtBox, data, attacker))
				{
					continue;
				}
				seen++;
				if (GD.Randi() % (uint)seen == 0)
				{
					pick = hurtBox;
				}
			}
			if (pick == null)
			{
				break;
			}
			struck.Add(pick.GetRid().Id);
			Vector3 from = current;
			Vector3 targetPos = pick.GlobalPosition;
			// Bolt from the previous link (or the impact point on the first hop) to
			// this one — drawn whether or not the link crosses, so a fizzled arc
			// still visibly reaches the target before dying.
			SpawnChainArc(fxHost, data.boltFx, from, targetPos);
			current = targetPos;

			// Feed the link's shock meter (folding its Electrical resistance /
			// wetness) and discharge + continue only when it crosses.
			bool crossed = ApplyShockBuildup(pick, data.shockEffect, buildup);
			if (CVars.debugAoe.Value)
			{
				DebugDraw.Line(from, targetPos, new Color(0.6f, 0.85f, 1f, crossed ? 0.9f : 0.35f), 0.2f);
			}
			if (!crossed)
			{
				break;
			}
			if (data.dischargeDamage != null)
			{
				pick.Hit(new HitInfo(data.dischargeDamage, attacker.AttackerNode, Vector3.Zero, attacker.ActorTeam));
			}
			if (data.fx != null && fxHost != null)
			{
				Fx.Create(data.fx, fxHost, targetPos);
			}
			buildup *= data.chainBuildupFalloff;
		}
	}

	// A hurtbox is a valid chain link if the discharge (or, when none is
	// authored, a neutral electrical probe) would be accepted by its team filter.
	private static bool CanChainTo(HurtBox hurtBox, ChainLightningData data, IActionActor attacker)
	{
		if (data.dischargeDamage == null)
		{
			return true;
		}
		HitInfo probe = new HitInfo(data.dischargeDamage, attacker.AttackerNode, Vector3.Zero, attacker.ActorTeam);
		return hurtBox.CanBeHit(probe);
	}

	// Route a shock buildup contribution to whichever actor owns `hurtBox` and
	// report whether it crossed the threshold. Mob and Player both fold their own
	// Electrical resistance / wetness in AddCombatBuildup; ownerless hurtboxes
	// (props) can't be shocked, so the chain stops on them.
	private static bool ApplyShockBuildup(HurtBox hurtBox, StatusEffectData shockEffect, float amount)
	{
		Mob mob = FindOwningMob(hurtBox);
		if (mob != null)
		{
			return mob.ApplyCombatBuildup(shockEffect, amount);
		}
		Player player = FindOwningPlayer(hurtBox);
		return player != null && player.ApplyCombatBuildup(shockEffect, amount);
	}

	// Shortest arc we bother drawing — below this the two points are effectively
	// coincident (e.g. a chain link landing on the impact point) and a bolt would
	// just be a degenerate speck.
	private const float MinChainArcLength = 0.4f;

	// Spawn one chain-lightning bolt arc between two world points, skipping
	// degenerate near-zero arcs. No-op if the data has no boltFx wired.
	private static void SpawnChainArc(Node host, PackedScene boltFx, Vector3 from, Vector3 to)
	{
		if (boltFx == null || host == null || from.DistanceTo(to) <= MinChainArcLength)
		{
			return;
		}
		LightningBolt.CreateArc(host, boltFx, from, to);
	}

	// Fire the firing weapon's item-side chain-lightning mods at `position` (a
	// swing's impact point / a hitscan's hit point), scope-filtered to the firing
	// charge tier. Works for any attacker whose action carries a WeaponState as
	// primaryItem — the player's modded weapons and elite mobs (whose signature
	// Lightning mod is composed onto their natural weapon) alike.
	private static void TriggerWeaponModChains(IActionActor actor, in PlayerAction action, Vector3 position)
	{
		if (action.context.primaryItem is not WeaponState weapon)
		{
			return;
		}
		Godot.Collections.Array<ChainLightningData> chains = weapon.statusEffects.WeaponModChainLightning(FindChargeIndex(weapon, action.selectedTier));
		if (chains == null)
		{
			return;
		}
		for (int i = 0; i < chains.Count; i++)
		{
			ApplyChainLightning(actor, chains[i], position);
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

	// Player counterpart to FindOwningMob — walks up from a hurtbox to the owning
	// Player (the player authors its HurtBox as a child), or null for non-player
	// boxes. Used by the chain-lightning shock path to reach the receiver's
	// buildup meter regardless of whether the link is a mob or the player.
	public static Player FindOwningPlayer(Node node)
	{
		while (node != null)
		{
			if (node is Player player)
			{
				return player;
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
	public static void DoProjectile(IActionActor actor, ItemEvent ev, ref PlayerAction action, DamageData damageOverride = null, bool fireOnAttackMods = false)
	{
		if (ev.projectileScene == null)
		{
			return;
		}
		HitInfo hit = ResolveHit(ev, action, actor, damageOverride);
		if (hit.healthDamage <= 0f && hit.buildups == null)
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
		DamageData damageData = damageOverride ?? firingWeapon?.data?.GetDamage(ev.damageProfileKey);
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

		// Flat shots recompute their spread per shot inside the launch loop below;
		// arced lobs solve one launch velocity here and reuse it. flatSpreadScale
		// is captured here so the loop can re-sample ApplySpread for a volley.
		Vector3 velocity = Vector3.Zero;
		float flatSpreadScale = 0f;
		float lifetime;
		float gravity = 0f;
		bool noCollide = false;
		bool bounce = false;
		float bounciness = 0f;
		float friction = 0f;
		if (ev.projectileArcing)
		{
			// lifetime is the FUSE; the hump's shape/feel is the (shorter) arc
			// duration, so the lob keeps falling/bouncing past its landing. The
			// launch velocity is a fixed-shape hump along the actor's facing (or,
			// for a mob, toward its aim target) — see TrySolveArcLaunch.
			lifetime = ev.projectileLifetimeSeconds;
			// Charge ramp scales the throw's horizontal reach (and with it the
			// horizontal launch speed, reach / fuse).
			float arcRangeScale = ItemAction.SampleRangeScale(tier, action.chargeT);
			if (!TrySolveArcLaunch(actor, ev, action.context, origin, arcRangeScale, out velocity, out gravity, out Vector3 landing))
			{
				return;
			}
			// Bounces off solids it meets before the fuse, then detonates at
			// `lifetime` — so it can't pass through walls, and emergent hits (a
			// grenade rebounding off a wall around a corner) just work. Fragile
			// lobs drop out of bounce mode below.
			noCollide = false;
			bounce = true;
			bounciness = ev.projectileBounciness;
			friction = ev.projectileFriction;
			// Telegraph the landing point so the target can read and dodge the lob.
			if (ev.projectileTargetPreview != null)
			{
				SpawnArcTelegraph(ev.projectileTargetPreview, landing, parent, lifetime);
			}
		}
		else
		{
			// Flat flight: per-tier spread + range scalars (same convention as
			// DoHitscan). rangeScale shortens lifetime so reach drops
			// proportionally; speed stays constant so flight feel doesn't change.
			// The actual launch velocity is sampled per shot in the loop below.
			float chargeT = action.chargeT;
			flatSpreadScale = ItemAction.SampleAccuracySpread(tier, chargeT);
			float rangeScale = ItemAction.SampleRangeScale(tier, chargeT);
			lifetime = ev.projectileLifetimeSeconds * rangeScale;
		}

		// Compose this shot's weapon mods (loot / ItemDescriptor onto
		// WeaponState.statusEffects): the event's authored pierce base maxed
		// against every active mod that reaches this charge tier. A mod reaches
		// the shot when it's AllAttacks-scoped or its SpecificCharge index matches
		// the firing tier (resolved by position in the weapon's action profile).
		int pierceCount = Mathf.Max(0, ev.pierceCount);
		// Charge-tier id used to scope this shot's weapon mods; -1 (no weapon /
		// not found) matches only AllAttacks-scoped mods.
		int firingChargeIndex = firingWeapon != null ? FindChargeIndex(firingWeapon, tier) : -1;
		// Vampiric (lifesteal) fraction this shot carries — applied in-flight when
		// it deals health damage, healing the firer back.
		float lifestealFraction = 0f;
		// Flat stamina refunded to the firer on each creature this shot strikes
		// (the ranged stamina-recharge mod).
		float staminaOnHit = 0f;
		// On-hit effect contributions (a Flaming bow's Burning applied immediately,
		// a Venomous shot's Poison buildup) the shot adds to each creature it
		// strikes. The projectile rebuilds its HitInfo from the raw DamageData, so
		// these are passed in rather than riding the ResolveHit hit.
		Godot.Collections.Array<StatusEffectBuildup> onHitBuildups = null;
		// Chain-lightning mods (Shocking bow) discharge from each creature the
		// shot strikes.
		Godot.Collections.Array<ChainLightningData> chainLightning = null;
		// Knockback mod — extra shove + stagger added to each hit.
		float knockbackBonus = 0f;
		float knockbackTimeBonus = 0f;
		// Loop fx the mods attach to the shot (a Flaming bow's flaming arrows),
		// layered on top of any intrinsic trail authored as a child Fx of the
		// projectile scene.
		Godot.Collections.Array<PackedScene> projectileFx = null;
		// Composed weapon level doubles the shot's damage per level (2^level);
		// threaded through Launch since the projectile rebuilds its HitInfo from
		// raw DamageData and never sees ResolveHit's scaling. The per-level offense
		// scale (a Ranged forge upgrade's level, or a mob's Level) rides the same two
		// multipliers: damage folds into damageMultiplier, buildup into
		// buildupMultiplier, so a leveled ranged attack lands harder hits AND harder
		// status buildups on every creature the shot strikes.
		float levelScale = actor.OutgoingLevelScale(action.context.sourceSlot ?? EInventorySlot.None);
		float damageMultiplier = (firingWeapon?.DamageMultiplier ?? 1f) * levelScale;
		float buildupMultiplier = levelScale;
		if (firingWeapon != null)
		{
			pierceCount = System.Math.Max(pierceCount, firingWeapon.statusEffects.ProjectilePierceCount(firingChargeIndex));
			lifestealFraction = firingWeapon.statusEffects.Vampiric(firingChargeIndex);
			staminaOnHit = firingWeapon.statusEffects.StaminaOnHit(firingChargeIndex);
			onHitBuildups = firingWeapon.statusEffects.WeaponModOnHitBuildups(firingChargeIndex);
			chainLightning = firingWeapon.statusEffects.WeaponModChainLightning(firingChargeIndex);
			knockbackBonus = firingWeapon.statusEffects.WeaponModKnockbackBonus(firingChargeIndex);
			knockbackTimeBonus = firingWeapon.statusEffects.WeaponModKnockbackTimeBonus(firingChargeIndex);
			projectileFx = firingWeapon.statusEffects.WeaponModProjectileFx(firingChargeIndex);
		}

		// Fragile: a lob that would normally bounce and wait out its fuse instead
		// shatters on the first surface / creature it touches. Sourced from either
		// the event's intrinsic projectileFragile flag (mob attacks, born-fragile
		// weapons) or a Fragile weapon mod (ArcDetonatesOnContact unifies both).
		// Disabling bounce drops the projectile into its default
		// collision-detonation path (the first solid OR hurtbox ends it and fires
		// impactEvent there); the gravity arc is untouched.
		if (bounce && ArcDetonatesOnContact(ev, firingWeapon, firingChargeIndex))
		{
			bounce = false;
		}

		// Launch ("muzzle") cue, fired once at the origin regardless of shot count —
		// the per-event fx field (a fire hiss, a magic whoosh). Distinct from the
		// per-result impact fx the projectile spawns where it lands.
		if (ev.fx != null)
		{
			Fx.Create(ev.fx, parent, origin);
		}

		// Fire one shot, or a fanned volley when projectileCount > 1. A flat volley
		// re-samples the accuracy spread per shot so the shots spread out; an arced
		// lob reuses the single solved launch velocity.
		int projectileCount = Mathf.Max(1, ev.projectileCount);
		for (int shot = 0; shot < projectileCount; shot++)
		{
			Vector3 shotVelocity = velocity;
			if (!ev.projectileArcing)
			{
				Vector3 direction = ApplySpread(AimForward(actor, action.context, origin), flatSpreadScale);
				shotVelocity = direction * ev.projectileSpeed;
			}
			Projectile.Launch(
				parent,
				ev.projectileScene,
				lifetime,
				origin,
				shotVelocity,
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
				pierceCount,
				lifestealFraction,
				chainLightning,
				knockbackBonus,
				knockbackTimeBonus,
				projectileFx,
				onHitBuildups,
				ev.directHitEvent,
				ev.expirationEvent,
				damageMultiplier,
				buildupMultiplier,
				staminaOnHit);
		}

		// On-attack mods for a ranged-slot Fairy boon: a bow shot is a Projectile
		// attack, so DoProjectile is where its body-mod missiles get a chance to
		// fire. Gated by fireOnAttackMods, set only for the PRIMARY weapon attack
		// (ActionRunner's timeline dispatch) — the mod-spawned missiles re-enter
		// DoProjectile with the flag false, so they never spawn more missiles.
		// connected is unknown at launch, so only OnSwing-triggered mods fire here.
		if (fireOnAttackMods)
		{
			FireWeaponModAttackProjectiles(actor, ref action, false);
		}
	}

	// Single source of truth for "does this arced lob shatter on first contact?":
	// true when it carries an impactEvent (the shatter payload) AND is fragile
	// from either source — the event's intrinsic projectileFragile flag (works
	// for mobs, which have no WeaponState) or a Fragile weapon mod on the firing
	// weapon at the given charge tier. Shared by the throw (DoProjectile), the
	// reticle's dotted preview (AimingReticle.SolveArcForward), and the mob arc
	// path so all three agree. chargeIndex -1 matches only AllAttacks-scoped mods.
	public static bool ArcDetonatesOnContact(ItemEvent ev, WeaponState weapon, int chargeIndex)
	{
		if (ev == null || ev.impactEvent == null)
		{
			return false;
		}
		return ev.projectileFragile
			|| (weapon != null && weapon.statusEffects.ProjectilesDetonateOnContact(chargeIndex));
	}

	// Solve an arced lob's launch velocity (and report the predicted landing
	// point for telegraphs). The hump is fixed-shape: vertical = rise under
	// gravity, horizontal launch speed = (projectileMaxRange × rangeScale) /
	// fuse. A targeted throw (mob attacks set ActionContext.target) takes the
	// target's bearing and shortens to land ON the target when it's inside
	// reach; an untargeted throw (the player's aimed throw) flies the full
	// reach along the body's horizontal facing — the exact hump the reticle
	// previews (AimingReticle.SolveArcForward uses the same formula), so no
	// solved velocity needs to pass between the reticle and the throw. Returns
	// false when the arc can't be built (bad tuning, or no facing to fire along).
	private static bool TrySolveArcLaunch(IActionActor actor, ItemEvent ev, in ActionContext context,
		Vector3 origin, float rangeScale, out Vector3 velocity, out float gravity, out Vector3 landing)
	{
		velocity = Vector3.Zero;
		gravity = 0f;
		landing = origin;
		float fuse = ev.projectileLifetimeSeconds;
		if (fuse <= 0f || ev.projectileArcRise <= 0f || ev.projectileGravity <= 0f || ev.projectileMaxRange <= 0f)
		{
			return false;
		}
		gravity = ev.projectileGravity;
		float launchVy = AimingReticle.ArcLaunchVerticalSpeed(ev.projectileArcRise, gravity);
		float reach = ev.projectileMaxRange * rangeScale;
		Vector3 bearing;
		float landingY = origin.Y;
		if (context.target is IAimTarget aim && GodotObject.IsInstanceValid(context.target))
		{
			// Lob toward the target's body center, landing on it when inside reach.
			Vector3 target = aim.AimCenter;
			float dx = target.X - origin.X;
			float dz = target.Z - origin.Z;
			float rawDist = Mathf.Sqrt(dx * dx + dz * dz);
			bearing = rawDist > 1e-4f ? new Vector3(dx, 0f, dz) / rawDist : Vector3.Forward;
			reach = Mathf.Min(rawDist, reach);
			// Anchor the telegraph at the target's foot height under the throw's
			// XZ endpoint (the target stands on the ground the lob falls toward).
			landingY = context.target.GlobalPosition.Y;
		}
		else
		{
			// Untargeted: full reach along the horizontal facing (ActorForward
			// folds auto-aim pitch; the hump's vertical shape is authored).
			Vector3 f = actor.ActorForward;
			float fLen = Mathf.Sqrt(f.X * f.X + f.Z * f.Z);
			if (fLen <= 1e-4f)
			{
				return false;
			}
			bearing = new Vector3(f.X / fLen, 0f, f.Z / fLen);
		}
		velocity = bearing * (reach / fuse) + Vector3.Up * launchVy;
		landing = new Vector3(origin.X + bearing.X * reach, landingY, origin.Z + bearing.Z * reach);
		return true;
	}

	// Drop an arced shot's landing telegraph: instantiate the preview decal at
	// the landing point, parented to the Sim so it outlives the firing actor,
	// and arm its self-fade to roughly the lob's flight time. No-op if the scene
	// isn't a GroundDecalPreview.
	private static void SpawnArcTelegraph(PackedScene scene, Vector3 position, Node parent, float lifetimeSeconds)
	{
		Node host = (Node)Sim.Current ?? parent;
		if (scene == null || host == null)
		{
			return;
		}
		if (scene.Instantiate() is not GroundDecalPreview preview)
		{
			return;
		}
		preview.Initialize(lifetimeSeconds);
		host.AddChild(preview);
		preview.GlobalPosition = position;
	}

	// Lifesteal: heal the attacker by the firing weapon's vampiric fraction of
	// the health damage just dealt by a landed melee/hitscan hit, and refund the
	// weapon's flat stamina-on-hit amount. No-op for mob attacks (no WeaponState),
	// zero damage, or a weapon carrying neither mod at the firing charge tier. Read
	// off the weapon's composed mods the same way DoProjectile reads pierce / detonate.
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
		int chargeIndex = FindChargeIndex(weapon, action.selectedTier);
		float fraction = weapon.statusEffects.Vampiric(chargeIndex);
		if (fraction > 0f)
		{
			actor.Heal(healthDamageDealt * fraction);
		}
		float stamina = weapon.statusEffects.StaminaOnHit(chargeIndex);
		if (stamina > 0f)
		{
			actor.RestoreStamina(stamina);
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

	// Launch any attack-triggered on-attack-projectile mods for this attack: a
	// "Seeking" sword's missiles (composed onto the wielding weapon) and a Fairy
	// boon's missiles (carried on the actor's body, fired regardless of weapon).
	// `connected` is true when the attack landed on a creature — it adds the OnHit
	// trigger to the query; OnSwing-scoped mods fire either way. Each event is
	// dispatched through DoProjectile, reusing the full projectile path (spread,
	// aim, damage resolution, and the wielding weapon's other composed mods); the
	// body mods pass their own intrinsic damage since they may fire with no
	// weapon profile to resolve against. Called at the tail of DoMelee / DoHitscan
	// and (for the primary attack only) DoProjectile; the missiles it spawns
	// re-enter DoProjectile with fireOnAttackMods=false, so there's no recursion.
	private static void FireWeaponModAttackProjectiles(IActionActor actor, ref PlayerAction action, bool connected)
	{
		EWeaponModAttackTrigger trigger = EWeaponModAttackTrigger.OnSwing;
		if (connected)
		{
			trigger |= EWeaponModAttackTrigger.OnHit;
		}
		// Weapon-composed mods (a Seeking sword): the missiles resolve their damage
		// off the wielding weapon's own damageProfiles via the event's damageProfileKey.
		if (action.context.primaryItem is WeaponState weapon)
		{
			int chargeIndex = FindChargeIndex(weapon, action.selectedTier);
			Godot.Collections.Array<ItemEvent> events = weapon.statusEffects.WeaponModOnAttackEvents(chargeIndex, trigger);
			if (events != null)
			{
				for (int i = 0; i < events.Count; i++)
				{
					ItemEvent ev = events[i];
					if (ev != null)
					{
						DoProjectile(actor, ev, ref action);
					}
				}
			}
		}
		// Body-carried mods (a Fairy boon's homing missiles): fire regardless of
		// which weapon is wielded, but scoped to the slot this attack came from
		// (a melee-slot boon vs a ranged-slot boon), each carrying its own
		// intrinsic damage. sourceSlot is null for non-slot attacks (mobs) — those
		// only match a None-slot (any) body mod.
		EInventorySlot slot = action.context.sourceSlot ?? EInventorySlot.None;
		Godot.Collections.Array<WeaponModData> bodyMods = actor.BodyOnAttackMods(trigger, slot);
		if (bodyMods != null)
		{
			for (int i = 0; i < bodyMods.Count; i++)
			{
				WeaponModData mod = bodyMods[i];
				if (mod?.onAttackEvent != null)
				{
					DoProjectile(actor, mod.onAttackEvent, ref action, mod.projectileDamage);
				}
			}
		}
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
			Node host = (Node)Sim.Current ?? parent;
			if (host != null)
			{
				Node3D instance = ev.areaEffectScene.Instantiate<Node3D>();
				// Apply weapon-side overrides BEFORE AddChild — DamageZone's
				// _Ready builds its interval HitInfos from the (possibly
				// overridden) damageIntervals, so the override has to land
				// first.
				if (instance is GasCloud cloud)
				{
					ResolveAreaPayload(ev, sourceWeaponData,
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
	// the actor's feet otherwise. The scene is parented to the Sim so it
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
		Node parent = (Node)Sim.Current ?? actor.AttackerNode?.GetParent();
		if (parent == null)
		{
			return;
		}
		Node3D instance = ev.areaEffectScene.Instantiate<Node3D>();
		if (instance is GasCloud cloud)
		{
			WeaponData weaponData = (action.context.primaryItem as WeaponState)?.data;
			ResolveAreaPayload(ev, weaponData,
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
		Sim sim = Sim.Current;
		if (sim == null)
		{
			return;
		}
		Mob minion = sim.SpawnMob(ev.minionData, ResolveAimPoint(actor));
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
		Node parent = (Node)Sim.Current ?? actor.AttackerNode?.GetParent();
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
	// weapon's continuousProfiles / damageProfiles dictionaries (the mob's
	// attack weapon, for mob-spawned zones). weaponData may be null — keys
	// that miss yield no profile and are silently skipped. The returned
	// `intervals` array contains one IntervalDamageEntry per AreaIntervalSpec
	// whose key resolved to a non-null DamageData.
	private static void ResolveAreaPayload(
		ItemEvent ev,
		WeaponData weaponData,
		out ContinuousDamageData continuous,
		out Godot.Collections.Array<IntervalDamageEntry> intervals)
	{
		continuous = null;
		if (!string.IsNullOrEmpty(ev.areaContinuousKey.ToString()))
		{
			continuous = weaponData?.GetContinuousDamage(ev.areaContinuousKey);
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
				DamageData damage = weaponData?.GetDamage(spec.damageProfileKey);
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

	// One-shot full-screen flash toward ev.screenFlashColor, decayed by the
	// ScreenEffectsController. The main-timeline counterpart to the projectile-
	// impact ScreenFlash in DispatchAtPosition — author it on any tier / charge
	// event that should punch a screenspace flash (a spell going off). No-op
	// when there's no controller (headless).
	public static void DoScreenFlash(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		ScreenEffectsController.Current?.Flash(ev.screenFlashColor, ev.screenFlashIntensity, ev.screenFlashFadeSeconds);
	}

	// Fires a one-shot controller-rumble impulse anchored at the actor's
	// position. Same distance-falloff semantics as DoCameraShake (range == 0 =
	// full magnitude, the right default for player-sourced actions since the
	// player holds the pad). No-op when no GameClient (e.g. headless).
	public static void DoControllerRumble(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		GameClient client = GameClient.Current;
		if (client == null) { return; }
		Vector3 playerPos = client.Player?.GlobalPosition ?? actor.ActorWorldPosition;
		client.Rumble.AddImpulse(ev.controllerRumbleWeak, ev.controllerRumbleStrong, ev.controllerRumbleDuration, actor.ActorWorldPosition, ev.controllerRumbleRange, playerPos);
	}

	// Digs at the player's aim cursor (positional-aim tiers) or a short reach
	// in front of the actor (directional / no cursor). Routes to Sim.TryDig,
	// which uncovers the nearest buried-item spot in range — or, failing that,
	// forces the nearest burrowed mob to the surface. Only the player digs;
	// mob actors have no shovel.
	public static void DoDig(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (actor is not Player player)
		{
			return;
		}
		Sim sim = Sim.Current;
		if (sim == null)
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
		EDigResult result = sim.TryDig(center, ev.digRadius, player);
		PackedScene effect = result switch
		{
			EDigResult.Treasure => ev.digTreasureEffect,
			EDigResult.Common => ev.digCommonEffect,
			_ => ev.digNothingEffect,
		};
		if (effect != null)
		{
			Node parent = (Node)Sim.Current ?? player.AttackerNode?.GetParent();
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

	// A targeted rally: pulse-applies each entry in ev.effects to the crier
	// itself (always, for free) plus up to ev.areaMaxTargets allied Mobs picked
	// from an ev.areaRadius sphere around the actor. Eligibility is allied
	// (same ActorTeam side, per Teams.AreAllied) AND either the crier's own
	// species (same base MobData) or an already-triggered mob of another species
	// — so an idle lurker standing near a goblin warband is never rallied, but a
	// lurker already in the fight can be. Recipients are chosen closest-first,
	// own-species before other-species. ev.fx fires once at the actor as the
	// source-side audiovisual cue. The handler no-ops when the actor isn't a Mob
	// rather than guess at "Player friendlies" semantics — flip it on by adding a
	// Player branch when a player-sourced cry is wanted.
	private static readonly System.Collections.Generic.List<Mob> _areaBuffScratch = new();
	private static readonly System.Collections.Generic.List<Mob> _areaBuffEligible = new();
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
		// The crier always buffs itself, and it does NOT count against the ally
		// cap — ev.areaMaxTargets bounds how many OTHERS it rallies.
		ApplyAreaBuff(sourceMob, ev, ref action);
		float radius = ev.areaRadius;
		if (radius <= 0f)
		{
			return;
		}
		MobSpatialHash hash = sourceMob.Sim?.MobSpatialHash;
		if (hash == null)
		{
			return;
		}
		ETeam sourceTeam = sourceMob.ActorTeam;
		MobData sourceSpecies = sourceMob.mobData;
		Vector3 origin = actor.ActorWorldPosition;
		_areaBuffScratch.Clear();
		hash.QueryRadius(origin, radius, _areaBuffScratch);
		_areaBuffEligible.Clear();
		for (int i = 0; i < _areaBuffScratch.Count; i++)
		{
			Mob target = _areaBuffScratch[i];
			if (target == null || target == sourceMob || !target.alive || target.mobData == null)
			{
				continue;
			}
			if (!Teams.AreAllied(sourceTeam, target.ActorTeam))
			{
				continue;
			}
			// Own species always qualifies; another species only while it's
			// already engaged (triggered).
			bool sameSpecies = target.mobData == sourceSpecies;
			if (!sameSpecies && !target.IsTriggered)
			{
				continue;
			}
			_areaBuffEligible.Add(target);
		}
		// Closest-first, own species before other species.
		_areaBuffEligible.Sort((a, b) =>
		{
			bool aSame = a.mobData == sourceSpecies;
			bool bSame = b.mobData == sourceSpecies;
			if (aSame != bSame)
			{
				return aSame ? -1 : 1;
			}
			float da = (a.GlobalPosition - origin).LengthSquared();
			float db = (b.GlobalPosition - origin).LengthSquared();
			return da.CompareTo(db);
		});
		int cap = ev.areaMaxTargets > 0 ? ev.areaMaxTargets : _areaBuffEligible.Count;
		int applied = 0;
		for (int i = 0; i < _areaBuffEligible.Count && applied < cap; i++, applied++)
		{
			ApplyAreaBuff(_areaBuffEligible[i], ev, ref action);
		}
		_areaBuffScratch.Clear();
		_areaBuffEligible.Clear();
	}

	private static void ApplyAreaBuff(Mob target, ItemEvent ev, ref PlayerAction action)
	{
		for (int j = 0; j < ev.effects.Count; j++)
		{
			ItemEffect effect = ev.effects[j];
			if (effect != null)
			{
				effect.Apply(target, action.context);
			}
		}
	}

	public static void DoDecrementStack(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		ConsumeOneFromStack(actor, action.context.primaryItem);
	}

	// Consume one unit of `item` from the using actor's stack: identify-on-first-
	// use, decrement, and remove from inventory at zero. The canonical "actually
	// consumed" hook. Split out of DoDecrementStack so the boon path — which must
	// hold off consuming until the player commits to a pick — can call it from its
	// own selection callback rather than via the synchronous DecrementStack event.
	public static void ConsumeOneFromStack(IActionActor actor, ItemState item)
	{
		if (item == null)
		{
			return;
		}
		Player castPlayer = actor as Player;
		Inventory inv = castPlayer?.Inventory;
		// Alchemy spell cast: the attuned cast instance is not a stack. Casting
		// spends one cast's worth of reagents from the party pool (backpack + stash)
		// and the instance persists — it is never removed, so this returns before
		// the identify/decrement/remove path below.
		if (inv != null && inv.AttunedSpell != null && item == inv.GetActiveConsumable())
		{
			// SpendReagents notifies the inventory itself on a successful spend.
			castPlayer.SpendReagents(inv.AttunedSpell.reagents);
			return;
		}
		// Reveal the item's real name on first successful use. Decrement is
		// the canonical "actually consumed" hook — only consumables flow
		// through here, and only ones whose timeline reached this event.
		// Identification is shared across all stacks/recipes of the same
		// ItemData; the read-side (SimState.GetItemDisplayName) picks it
		// up immediately so the inventory row, recipe button, and cook
		// announcement all reveal in lockstep.
		if (actor is Player identifyingPlayer && identifyingPlayer.Sim?.WorldState?.SimState?.IdentifyItem(item.data) == true)
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
		// A fuel-empty lantern can't be relit — fuel only comes back at a sunrise,
		// on respawn, or at a fountain. Refuse the light half of the toggle;
		// dousing is always allowed.
		if (!consumable.isActive && consumable is LanternState lantern && !lantern.HasFuel)
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
		actor.ApplyMotion(ev.motionForwardSpeed, ev.motionDuration, ev.motionFreezeGravity, ev.motionDirection);
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
	// event's damageProfileKey on the driving weapon's damageProfiles dict.
	// Both player and mob attacks carry a WeaponState as primaryItem (the mob's
	// is its authored attack weapon), so damage always comes from the weapon.
	// Source is the actor so receivers see the attacker. Returns a default
	// HitInfo (no damage) if the lookup fails or there's no source — caller
	// should early-out. Conditional crit / dizzy behavior rides on
	// `template.modifiers`, no separate parameter needed here.
	private static HitInfo ResolveHit(ItemEvent ev, in PlayerAction action, IActionActor actor, DamageData damageOverride = null)
	{
		// damageOverride wins when set (a body boon's on-attack projectile carries
		// its own damage, since there's no wielding-weapon profile to resolve
		// against); otherwise resolve the event's key off the driving weapon.
		DamageData template = damageOverride;
		if (template == null && action.context.primaryItem is WeaponState weapon)
		{
			template = weapon.data?.GetDamage(ev.damageProfileKey);
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
		// Melee-only strength scale (the player's PlayerState.strength). Gated on
		// the event's Melee flag so ranged / thrown swings are unaffected; mobs
		// return 1.0. Layered on top of the status-effect OutgoingDamageMultiplier.
		if ((ev.type & EItemEventType.Melee) != 0)
		{
			float meleeMul = actor.MeleeDamageMultiplier;
			if (meleeMul != 1f)
			{
				hit.healthDamage *= meleeMul;
			}
		}
		// Per-level offense scale (player: the forge upgrade on this weapon's slot;
		// mob: its difficulty Level). Scales healthDamage AND every buildup the hit
		// delivers (buildupAmountMultiplier), so a leveled attacker lands its status
		// effects harder too. For projectiles this same scale is folded into the
		// threaded damage/buildup multipliers in DoProjectile — the rebuilt HitInfo
		// there ignores this one — so ranged upgrades aren't double-counted.
		float levelScale = actor.OutgoingLevelScale(action.context.sourceSlot ?? EInventorySlot.None);
		if (levelScale != 1f)
		{
			hit.healthDamage *= levelScale;
			hit.buildupAmountMultiplier *= levelScale;
		}
		// Weapon-mod payloads scope-filtered to the firing tier: on-hit enchants
		// (a Flaming weapon's Burning) ride on top of the template's statusEffects,
		// and the Knockback mod adds shove + stagger to the hit.
		if (action.context.primaryItem is WeaponState weapon2)
		{
			// Composed weapon level doubles outgoing damage per level (2^level).
			hit.healthDamage *= weapon2.DamageMultiplier;
			// Per-swing repeat-combo multiplier (a final-swing haymaker, etc.).
			// Resolved at activation into action.repeatIndex; null / 1 for
			// non-repeat swings leaves the damage unchanged.
			ActionRepeatOverride swing = action.selectedTier?.GetRepeat(action.repeatIndex);
			if (swing != null && swing.damageMultiplier != 1f)
			{
				hit.healthDamage *= swing.damageMultiplier;
			}
			int chargeIndex = FindChargeIndex(weapon2, action.selectedTier);
			hit.AddBuildups(weapon2.statusEffects.WeaponModOnHitBuildups(chargeIndex));
			hit.knockbackDistance += weapon2.statusEffects.WeaponModKnockbackBonus(chargeIndex);
			hit.knockbackTime += weapon2.statusEffects.WeaponModKnockbackTimeBonus(chargeIndex);
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
	// once a hurtbox hit lands; the receiver's HurtBox.QueryHit reports
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

	// Un-spread firing direction for a directional shot. With an explicit aim
	// target (mob attacks set ActionContext.target) the shot heads at the
	// target's body center — a real 3D heading, so a spit from a low spider or a
	// perched archer leads in pitch rather than firing flat along the body's yaw.
	// The player leaves target null and keeps firing along ActorForward (auto-aim
	// pitch already folded in), so this never touches the player aim path.
	private static Vector3 AimForward(IActionActor actor, in ActionContext context, Vector3 origin)
	{
		if (context.target is IAimTarget aimTarget)
		{
			Vector3 toCenter = aimTarget.AimCenter - origin;
			if (toCenter.LengthSquared() > 1e-4f)
			{
				return toCenter.Normalized();
			}
		}
		return actor.ActorForward;
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
