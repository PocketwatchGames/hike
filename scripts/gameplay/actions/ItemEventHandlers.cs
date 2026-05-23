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
		EDamageTriggerFlags bestTriggers = EDamageTriggerFlags.None;
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
				// the post-damage zero). Trigger flags ride alongside so a
				// crit/backstab swing layers its tier overlay on the best
				// hurtbox of a multi-target swing.
				EHitResult r = hurtBox.QueryHitType(hit);
				EDamageTriggerFlags t = hurtBox.QueryHitTriggers(hit);
				hurtBox.Hit(hit);
				if (HitPriority(r) > HitPriority(bestResult))
				{
					bestResult = r;
					bestTriggers = t;
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
			SpawnTriggerOverlays(actor, action.selectedTier, bestTriggers, impactPos);
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
		EDamageTriggerFlags hitTriggers = EDamageTriggerFlags.None;
		HurtBox hitHurtBox = null;
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
					hitTriggers = hurtBox.QueryHitTriggers(hit);
					hurtBox.Hit(hit);
					hitPos = (Vector3)hurtResult["position"];
					hitHurtBox = hurtBox;
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
				targetMob.StickArrow(shootingWeapon, shootingWeapon.data.arrowLootData, hitPos);
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

		DebugDraw.Line(origin, hitPos, new Color(1f, 0f, 0f, 0.3f), 0.15f);
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
	// Arcing (`projectileArcing = true`): no in-flight collision, gravity-
	// driven arc, lands at the player's Positional aim cursor at exactly
	// `projectileLifetimeSeconds`. Velocity is solved for from origin →
	// cursor over the authored lifetime under world gravity — author sets
	// lifetime + the arcing flag, the math finds the pitch/speed. Used for
	// delivery-style attacks (rain of arrows, thrown explosive); pairs with
	// an authored `impactEvent` that fires at the landing point.
	public static void DoProjectile(IActionActor actor, ItemEvent ev, ref PlayerAction action)
	{
		if (ev.projectileScene == null)
		{
			return;
		}
		HitInfo hit = ResolveHit(ev, action, actor);
		if (hit.healthDamage <= 0f && hit.statusEffects == null && hit.stun <= 0f)
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
		Vector3 origin = actor.ActorWorldPosition + Vector3.Up;

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
		if (ev.projectileArcing)
		{
			// Arcing requires a positional aim cursor — without one (mob
			// attacks, weapon with no AimingReticle) there's nowhere to aim,
			// so the firing event silently no-ops. Position-aim tiers always
			// have a valid cursor by the time release fires.
			if (actor is not Player player || player.AimingReticle == null || !player.AimingReticle.HasAimWorldPosition)
			{
				return;
			}
			Vector3 target = player.AimingReticle.AimWorldPosition;
			lifetime = ev.projectileLifetimeSeconds;
			if (lifetime <= 0f)
			{
				return;
			}
			gravity = ev.projectileGravity > 0f
				? ev.projectileGravity
				: (World.Current?.SimData?.Gravity ?? 9.8f);
			// Solve ballistic launch: horizontal velocity is delta.xz / t;
			// vertical solves dy = v0y*t - 0.5*g*t^2 → v0y = (dy + 0.5*g*t^2) / t.
			Vector3 delta = target - origin;
			float vx = delta.X / lifetime;
			float vz = delta.Z / lifetime;
			float vy = (delta.Y + 0.5f * gravity * lifetime * lifetime) / lifetime;
			velocity = new Vector3(vx, vy, vz);
			noCollide = true;
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
			ev.impactEvent);
	}

	// Position-aware sub-dispatcher for projectile impactEvents (and any
	// future "fire at a point" sources). Subset of DispatchEvent because
	// most handlers need an action context (selectedTier, primaryItem,
	// chargeT, etc.) we don't have here. Currently supports SpawnAreaEffect
	// — the canonical "arcing arrow lands → spawn AoE at the landing point"
	// path. Other handlers no-op silently; their authored fields on the
	// nested event just get ignored.
	public static void DispatchAtPosition(ItemEvent ev, Vector3 position, Node parent, WeaponData sourceWeaponData)
	{
		if (ev == null) { return; }
		if ((ev.type & EItemEventType.SpawnAreaEffect) != 0 && ev.areaEffectScene != null)
		{
			Node host = (Node)World.Current ?? parent;
			if (host != null)
			{
				Node3D instance = ev.areaEffectScene.Instantiate<Node3D>();
				// Apply weapon-side overrides BEFORE AddChild — DamageZone's
				// _Ready builds its HitInfo from the (possibly overridden)
				// damage field, so the override has to land first.
				if (instance is GasCloud cloud)
				{
					DamageData damage = sourceWeaponData?.GetDamage(ev.damageProfileKey);
					cloud.Initialize(ev, damage);
				}
				host.AddChild(instance);
				instance.GlobalPosition = position;
			}
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
			WeaponState firingWeapon = action.context.primaryItem as WeaponState;
			DamageData damage = firingWeapon?.data?.GetDamage(ev.damageProfileKey);
			cloud.Initialize(ev, damage);
		}
		parent.AddChild(instance);
		instance.GlobalPosition = position;
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
	// crit / stun behavior rides on `template.modifiers`, no separate
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
		HitInfo hit = new HitInfo(template, actor.AttackerNode, actor.ActorForward);
		// Source-side buffs / debuffs scale the swing's healthDamage at fire
		// time. Only healthDamage is scaled — stun / hitstun / knockback keep
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
