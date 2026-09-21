using Godot;

// An instant area blast — an explosion, a spit glob bursting, a dash shockwave.
// Lands exactly once, in the frame it fires, on every hurtbox inside the burst
// shape, and leaves nothing behind but its visual. This is a shape query, not a
// DamageZone: an Area3D only learns its overlaps on the physics step after it is
// added, so a zone can never hit "now" — keeping one alive long enough to register
// is exactly the window that lets a late arrival walk into a finished blast.
//
// DamageZone (a hazard that LINGERS) finds its targets by Area3D overlap instead,
// but applies each hit through TryHit here, so the two agree on who is hit and how.
public static class AreaBurst
{
	// Lift off the blast center before the occlusion ray, so a burst resting on
	// the ground doesn't start inside the floor voxel and block every target.
	private const float LOS_ORIGIN_HEIGHT = 0.5f;

	// Spawn `burst.fx` at `center` and apply its damage. `host` parents the fx and
	// supplies the physics world. `source` is who the hit is attributed to (may be
	// null — a projectile whose shooter died); `exclude` skips the firer's own
	// hurtbox.
	public static void Fire(AreaBurstData burst, Node3D host, Vector3 center, Node source, ETeam attackerTeam, Rid? exclude = null)
	{
		if (burst == null || host == null)
		{
			return;
		}
		if (burst.fx != null)
		{
			Fx.Create(burst.fx, host, center);
		}
		ApplyDamage(host.GetWorld3D(), burst, center, source, attackerTeam, exclude);
	}

	private static void ApplyDamage(World3D world3D, AreaBurstData burst, Vector3 center, Node source, ETeam attackerTeam, Rid? exclude)
	{
		if (world3D == null || burst.damage == null || burst.radius <= 0f)
		{
			return;
		}
		// A column reaches `radius` below the center and `height` above it, so a
		// ground blast catches airborne targets without reaching further down
		// than a sphere would.
		Shape3D shape;
		Vector3 shapeCenter = center;
		if (burst.height > 0f)
		{
			float span = burst.radius + burst.height;
			shape = new CylinderShape3D { Radius = burst.radius, Height = span };
			shapeCenter.Y += (burst.height - burst.radius) * 0.5f;
		}
		else
		{
			shape = new SphereShape3D { Radius = burst.radius };
		}
		var query = new PhysicsShapeQueryParameters3D
		{
			Shape = shape,
			Transform = new Transform3D(Basis.Identity, shapeCenter),
			// Blasts scatter loose loot — see the melee sweep for why Debris is
			// safe in an every-overlap query and nowhere else.
			CollisionMask = (uint)(ECollisionLayer.HurtBox | ECollisionLayer.Debris),
			CollideWithAreas = true,
			CollideWithBodies = false,
		};
		var results = world3D.DirectSpaceState.IntersectShape(query, maxResults: 32);
		Vector3 losOrigin = center + Vector3.Up * LOS_ORIGIN_HEIGHT;
		HitInfo hit = new HitInfo(burst.damage, source, Vector3.Zero, attackerTeam);
		foreach (var result in results)
		{
			if (result["collider"].Obj is not HurtBox hurtBox)
			{
				continue;
			}
			if (exclude.HasValue && hurtBox.GetRid() == exclude.Value)
			{
				continue;
			}
			TryHit(hurtBox, hit, center, radial: true, world3D, losOrigin);
		}
		if (CVars.debugAoe.Value)
		{
			DebugDraw.Sphere(center, burst.radius, new Color(0.6f, 0.85f, 1f, 0.3f), 0.15f);
		}
	}

	// The per-target rule every area hit shares: the receiver's team filter, then
	// occlusion, then the hit. `radial` pushes the target directly away from
	// `center` (a blast); without it only loose debris is pushed — a lingering
	// zone damages from no particular side, but a dropped item still has to be
	// thrown somewhere or the hit reads as passing straight through it. A target
	// sitting on the center gets no direction, which the receiver reads as no
	// knockback. `losWorld` null skips the occlusion check.
	public static void TryHit(HurtBox hurtBox, HitInfo hit, Vector3 center, bool radial, World3D losWorld, Vector3 losOrigin)
	{
		if (!hurtBox.CanBeHit(hit))
		{
			return;
		}
		if (losWorld != null && !HasLineOfSight(losWorld, losOrigin, hurtBox))
		{
			return;
		}
		bool isDebris = (hurtBox.CollisionLayer & (uint)ECollisionLayer.Debris) != 0;
		if ((radial || isDebris) && hit.hitDirection.LengthSquared() < 0.0001f)
		{
			Vector3 away = hurtBox.GlobalPosition - center;
			away.Y = 0f;
			if (away.LengthSquared() > 0.0001f)
			{
				hit.hitDirection = away.Normalized();
			}
		}
		hurtBox.Hit(hit);
	}

	// Raycast from `from` to the target's hurtbox; blocked by solid terrain/props
	// so a blast can't reach through walls or floors. Mirrors the perception LOS
	// query (ECollisionLayer.Solid, bodies only). Areas are ignored so the HurtBox
	// areas themselves don't register as occluders. Aimed at HurtBox.Center, never
	// the hurtbox node: that sits at the target's feet, so the ray would end on
	// the ground the target is standing on and every ground-resting blast would
	// self-block. A target with a solid body of its own (a barrel's PorousBody)
	// is reached when the first thing the ray meets is that body — anything
	// genuinely in the way is met before it.
	private static bool HasLineOfSight(World3D world, Vector3 from, HurtBox hurtBox)
	{
		using var query = PhysicsRayQueryParameters3D.Create(from, hurtBox.Center, (uint)ECollisionLayer.Solid);
		query.CollideWithAreas = false;
		query.CollideWithBodies = true;
		var result = world.DirectSpaceState.IntersectRay(query);
		if (result.Count == 0)
		{
			return true;
		}
		Node target = hurtBox.GetParent();
		return target != null && result["collider"].Obj is Node collider
			&& (collider == target || target.IsAncestorOf(collider));
	}
}
