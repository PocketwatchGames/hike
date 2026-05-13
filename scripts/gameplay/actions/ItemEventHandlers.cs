using Godot;

// Per-event-type handlers split out of the runner so the runner stays free of
// combat-specific concerns and so future event types (ApplyEffect, PlayAnim,
// etc.) compose cleanly. Combat handlers read damage data from the action's
// primary weapon and physics queries from the actor's world.
public static class ItemEventHandlers
{
	public static void DoMelee(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		HitInfo hit = ResolveHit(ev, action, actor);
		if (hit.healthDamage <= 0f && hit.statusEffects == null && hit.stun <= 0f)
		{
			return;
		}

		World3D world3D = actor.AttackerNode?.GetWorld3D();
		if (world3D == null)
		{
			return;
		}

		Vector3 damagePos = actor.ActorWorldPosition + Vector3.Up + actor.ActorForward * ev.meleeRange;
		var sphere = new SphereShape3D() { Radius = ev.meleeRadius };
		var query = new PhysicsShapeQueryParameters3D
		{
			Shape = sphere,
			Transform = new Transform3D(Basis.Identity, damagePos),
			CollisionMask = actor.AttackHurtboxMask,
			CollideWithAreas = true,
			CollideWithBodies = false,
		};

		var results = world3D.DirectSpaceState.IntersectShape(query);
		Rid? selfHurtBox = actor.SelfHurtBoxRid;
		EHitResult bestResult = EHitResult.None;
		Vector3 impactPos = damagePos;
		foreach (var result in results)
		{
			var collider = result["collider"].Obj;
			if (collider is HurtBox hurtBox)
			{
				if (selfHurtBox.HasValue && hurtBox.GetRid() == selfHurtBox.Value)
				{
					continue;
				}
				// Query first so the impact effect reflects the pre-hit state
				// (e.g. Lethal needs to see the target's current health, not
				// the post-damage zero). Then apply.
				EHitResult r = hurtBox.QueryHitType(hit);
				hurtBox.Hit(hit);
				if (HitPriority(r) > HitPriority(bestResult))
				{
					bestResult = r;
					impactPos = hurtBox.GlobalPosition;
				}
			}
		}

		// No hurtbox hit — fall back to environment so a swing into a wall
		// still gets a thunk rather than reading as a whiff.
		if (bestResult == EHitResult.None)
		{
			var envQuery = new PhysicsShapeQueryParameters3D
			{
				Shape = sphere,
				Transform = new Transform3D(Basis.Identity, damagePos),
				CollisionMask = (uint)ECollisionLayer.Environment,
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
		}

		DebugDraw.Sphere(damagePos, ev.meleeRadius, new Color(1f, 0f, 0f, 0.3f), 0.15f);
	}

	public static void DoHitscan(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		HitInfo hit = ResolveHit(ev, action, actor);
		if (hit.healthDamage <= 0f && hit.statusEffects == null && hit.stun <= 0f)
		{
			return;
		}

		World3D world3D = actor.AttackerNode?.GetWorld3D();
		if (world3D == null)
		{
			return;
		}

		// Per-tier charge curves — sampled at activation, stashed on chargeT.
		// Range scales the authored hitScanRange; spread perturbs the aim
		// direction uniformly within a cone whose half-angle scales with the
		// accuracy curve. Null curves = 1.0 (no scaling) for range and
		// "fully accurate" (0.0 spread) for accuracy.
		ItemAction tier = action.selectedTier;
		float chargeT = action.chargeT;
		float rangeScale = SampleCurve(tier?.rangeScaleCurve, chargeT, 1f);
		float spreadScale = SampleCurve(tier?.accuracyScaleCurve, chargeT, 0f);

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
		envQuery.CollisionMask = (uint)ECollisionLayer.Environment;
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
		if (hurtResult.Count > 0)
		{
			var collider = hurtResult["collider"].Obj;
			if (collider is HurtBox hurtBox)
			{
				bool isSelf = selfHurtBox.HasValue && hurtBox.GetRid() == selfHurtBox.Value;
				if (!isSelf)
				{
					// Query before Hit so Lethal sees pre-damage state. See DoMelee.
					hitResult = hurtBox.QueryHitType(hit);
					hurtBox.Hit(hit);
					hitPos = (Vector3)hurtResult["position"];
				}
			}
		}

		// Resolve impact: hurtbox first, then environment clip, then air.
		if (hitResult != EHitResult.None)
		{
			SpawnImpact(actor, PickImpactScene(ev, hitResult), hitPos);
		}
		else if (envResult.Count > 0)
		{
			SpawnImpact(actor, ev.impactEnvironmentEffect, hitPos);
		}
		else
		{
			SpawnImpact(actor, ev.impactMissEffect, hitPos);
		}

		DebugDraw.Line(origin, hitPos, new Color(1f, 0f, 0f, 0.3f), 0.15f);
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

	public static void DoDecrementStack(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		ItemState item = action.context.primaryItem;
		if (item == null)
		{
			return;
		}
		item.stackCount--;
		// We mutated stackCount directly — Inventory has no other way to
		// learn that an item changed under it. Fire its onChanged signal so
		// any listening UI (e.g. the inventory screen's stack badge) can
		// refresh without polling.
		Inventory inv = (actor is Player player) ? player.Inventory : null;
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
		PackedScene scene = (consumable.data as TorchData)?.movingLightScene;
		player.SetMovingLightActive(consumable.isActive, scene);
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

	// Build the HitInfo a Melee/Hitscan event should apply: prefer the
	// event's per-event DamageData override, then fall back to the driving
	// weapon's damageData (item-driven actions). Source is the actor so
	// receivers see the attacker. Returns a default HitInfo (no damage) if
	// neither template is set — caller should early-out.
	private static HitInfo ResolveHit(ItemEvent ev, in PlayerAction action, IActionActor actor)
	{
		DamageData template = ev.damageData;
		DamageData crit = null;
		if (action.context.primaryItem is WeaponState weapon)
		{
			if (template == null)
			{
				template = weapon.data?.damageData;
			}
			crit = weapon.data?.critDamageData;
		}
		if (template == null)
		{
			return default;
		}
		return new HitInfo(template, actor.AttackerNode, default, crit);
	}

	private static float SampleCurve(Curve curve, float t, float fallback)
	{
		if (curve == null)
		{
			return fallback;
		}
		return curve.Sample(Mathf.Clamp(t, 0f, 1f));
	}

	// Maximum spread cone half-angle, in radians, when the accuracy curve
	// outputs 1.0. Tuned so an early-release bow shot is visibly inaccurate
	// without being absurd. Curve outputs in [0, 1] scale this.
	public const float MAX_SPREAD_HALF_ANGLE = 0.18f;

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

	private static PackedScene PickImpactScene(ItemEvent ev, EHitResult result)
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
