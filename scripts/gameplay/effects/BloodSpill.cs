using Godot;

// A struck body bleeding: the BloodData burst for the hit's severity — a
// splatter thrown along the hit, a mist left at the hit point, and stains on
// the ground where the splatter lands. Called by the receiver (Mob, Player),
// which alone knows for certain whether the hit was a crit, a backstab or
// lethal. Purely presentational: nothing here touches sim state.
public static class BloodSpill
{
	// Downward reach of the stain ground probe, so a flier struck in the air
	// still marks the floor beneath it.
	private const float STAIN_PROBE_DEPTH = 8f;
	private const float STAIN_PROBE_LIFT = 0.5f;
	// Sideways scatter of a stain off the hit line, as a fraction of the throw.
	private const float STAIN_SIDE_SPREAD = 0.35f;

	public static void Spill(BloodData blood, Node3D parent, in HitInfo hit, Vector3 bodyCenter, bool lethal)
	{
		if (blood == null || parent == null)
		{
			return;
		}
		BloodBurstData burst = blood.PickBurst(lethal, hit.triggers);
		if (burst == null)
		{
			return;
		}

		Vector3 point = hit.hitPoint ?? bodyCenter;
		Vector3 dir = HitDirection(in hit, bodyCenter);

		if (burst.splatter != null)
		{
			Fx.Create(burst.splatter, parent, new Transform3D(ThrowBasis(dir), point), blood.color);
		}
		if (burst.mist != null)
		{
			Fx.Create(burst.mist, parent, new Transform3D(Basis.Identity, point), blood.color);
		}
		LayStains(blood, burst, parent, point, dir);
	}

	// The sender's direction, else away from whoever dealt it, else none.
	private static Vector3 HitDirection(in HitInfo hit, Vector3 bodyCenter)
	{
		if (hit.hitDirection.LengthSquared() > 1e-6f)
		{
			return hit.hitDirection.Normalized();
		}
		if (hit.source is Node3D source && GodotObject.IsInstanceValid(source))
		{
			Vector3 away = bodyCenter - source.GlobalPosition;
			if (away.LengthSquared() > 1e-6f)
			{
				return away.Normalized();
			}
		}
		return Vector3.Zero;
	}

	// Turns the scene's -Z along the hit; with no direction, straight up.
	private static Basis ThrowBasis(Vector3 dir)
	{
		Vector3 forward = dir == Vector3.Zero ? Vector3.Up : dir;
		Vector3 up = Mathf.Abs(forward.Dot(Vector3.Up)) > 0.99f ? Vector3.Back : Vector3.Up;
		return Basis.LookingAt(forward, up);
	}

	private static void LayStains(BloodData blood, BloodBurstData burst, Node3D parent, Vector3 point, Vector3 dir)
	{
		Sim sim = Sim.Current;
		int textureCount = blood.stainTextures?.Count ?? 0;
		if (sim == null || burst.stainCount <= 0 || textureCount == 0)
		{
			return;
		}
		PhysicsDirectSpaceState3D space = parent.GetWorld3D()?.DirectSpaceState;
		if (space == null)
		{
			return;
		}

		Vector3 along = new Vector3(dir.X, 0f, dir.Z);
		along = along.LengthSquared() > 1e-6f ? along.Normalized() : Vector3.Zero;
		Vector3 side = along == Vector3.Zero ? Vector3.Zero : along.Cross(Vector3.Up);

		for (int i = 0; i < burst.stainCount; i++)
		{
			Vector3 offset;
			if (along == Vector3.Zero)
			{
				// No direction: a pool around the point.
				float angle = GD.Randf() * Mathf.Tau;
				offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (GD.Randf() * burst.stainThrow * STAIN_SIDE_SPREAD);
			}
			else
			{
				float t = GD.Randf() * burst.stainThrow;
				float s = (GD.Randf() * 2f - 1f) * burst.stainThrow * STAIN_SIDE_SPREAD;
				offset = along * t + side * s;
			}
			Vector3 from = point + offset + Vector3.Up * STAIN_PROBE_LIFT;
			using var query = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * STAIN_PROBE_DEPTH, (uint)ECollisionLayer.Solid);
			var result = space.IntersectRay(query);
			if (result.Count == 0)
			{
				continue;
			}
			Vector3 ground = (Vector3)result["position"];
			Texture2D texture = blood.stainTextures[(int)(GD.Randi() % (uint)textureCount)];
			float size = Mathf.Lerp(burst.stainSizeMin, burst.stainSizeMax, GD.Randf());
			sim.SpawnGroundMark(texture, new Vector2(size, size), blood.color, ground, GD.Randf() * Mathf.Tau,
				burst.stainHoldSeconds, burst.stainFadeSeconds, false);
		}
	}
}
