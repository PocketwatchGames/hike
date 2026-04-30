using Godot;

// Per-event-type handlers split out of the runner so the runner stays free of
// combat-specific concerns and so future event types (ApplyEffect, PlayAnim,
// etc.) compose cleanly. Combat handlers read damage data from the action's
// primary weapon and physics queries from the actor's world.
public static class ItemEventHandlers
{
	public static void DoMelee(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		DamageData damage = ResolveDamage(ev, action);
		if (damage == null)
		{
			return;
		}

		World3D world3D = actor.AttackerNode?.GetWorld3D();
		if (world3D == null)
		{
			return;
		}

		Vector3 damagePos = actor.ActorWorldPosition + Vector3.Up + actor.ActorForward * ev.meleeRange;
		var query = new PhysicsShapeQueryParameters3D
		{
			Shape = new SphereShape3D() { Radius = ev.meleeRadius },
			Transform = new Transform3D(Basis.Identity, damagePos),
			CollisionMask = actor.AttackHurtboxMask,
			CollideWithAreas = true,
			CollideWithBodies = false,
		};

		var results = world3D.DirectSpaceState.IntersectShape(query);
		Rid? selfHurtBox = actor.SelfHurtBoxRid;
		foreach (var result in results)
		{
			var collider = result["collider"].Obj;
			if (collider is HurtBox hurtBox)
			{
				if (selfHurtBox.HasValue && hurtBox.GetRid() == selfHurtBox.Value)
				{
					continue;
				}
				hurtBox.Hit(damage, actor.AttackerNode);
			}
		}

		DebugDraw.Sphere(damagePos, ev.meleeRadius, new Color(1f, 0f, 0f, 0.3f), 0.15f);
	}

	public static void DoHitscan(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		DamageData damage = ResolveDamage(ev, action);
		if (damage == null)
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
		ChargedAction tier = action.selectedTier;
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
		var envQuery = PhysicsRayQueryParameters3D.Create(origin, rayEnd);
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
		var hurtQuery = PhysicsRayQueryParameters3D.Create(origin, hitPos);
		hurtQuery.CollisionMask = actor.AttackHurtboxMask;
		hurtQuery.CollideWithAreas = true;
		hurtQuery.CollideWithBodies = false;
		Rid? selfHurtBox = actor.SelfHurtBoxRid;
		if (selfHurtBox.HasValue)
		{
			hurtQuery.Exclude = new Godot.Collections.Array<Rid> { selfHurtBox.Value };
		}

		var hurtResult = spaceState.IntersectRay(hurtQuery);
		if (hurtResult.Count > 0)
		{
			var collider = hurtResult["collider"].Obj;
			if (collider is HurtBox hurtBox)
			{
				bool isSelf = selfHurtBox.HasValue && hurtBox.GetRid() == selfHurtBox.Value;
				if (!isSelf)
				{
					hurtBox.Hit(damage, actor.AttackerNode);
					hitPos = (Vector3)hurtResult["position"];
				}
			}
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
		if (item.stackCount > 0)
		{
			return;
		}
		// Stack hit zero — remove from inventory. The Player's Inventory
		// holds the canonical reference; route through it so equip/active-slot
		// fields clear too. For non-Player actors (mobs), the item simply
		// drops out of context with no further bookkeeping.
		if (actor is Player player && player.Inventory != null)
		{
			player.Inventory.Remove(item);
		}
	}

	public static void DoToggleCarrierLight(IActionActor actor, ItemEvent ev, ref PlayerAction action)
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
		player.SetCarrierLightActive(consumable.isActive);
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
		interactive.Complete();
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

	// Resolve the DamageData a Melee/Hitscan event should apply: prefer the
	// event's per-event override, then fall back to the driving weapon's
	// damageData (item-driven actions). Returns null if neither is set —
	// caller should early-out.
	private static DamageData ResolveDamage(ItemEvent ev, in PlayerAction action)
	{
		if (ev.damageData != null)
		{
			return ev.damageData;
		}
		if (action.context.primaryItem is WeaponState weapon)
		{
			return weapon.data?.damageData;
		}
		return null;
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
	private const float MAX_SPREAD_HALF_ANGLE = 0.18f;

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
