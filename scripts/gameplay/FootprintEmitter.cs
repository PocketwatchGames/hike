using Godot;

// Distance-based footprint spawner for moving entities (Player, Mob). Mirrors
// FootstepEmitter's stride logic — each holder ticks Update every physics
// frame, and one Footprint is spawned each time the entity has moved more
// than `stride` meters in XZ since the last emit.
//
// Per-ground tuning lives on SimData.FootprintColors (one tint Color per
// EGroundType, alpha encodes baseline visibility); the emitter looks the
// color up at emit time. Surfaces with no entry are no-emit.
public class FootprintEmitter
{
	private Vector2 _lastEmitXZ;
	private bool _hasLastEmit;

	public void Update(
		World world,
		Vector3 worldPos,
		float yaw,
		bool emitting,
		float stride,
		EGroundType ground,
		Texture2D texture,
		float alphaMultiplier,
		float durationMultiplier,
		bool gated)
	{
		if (!emitting)
		{
			_hasLastEmit = false;
			return;
		}
		Vector2 xz = new Vector2(worldPos.X, worldPos.Z);
		if (!_hasLastEmit)
		{
			_lastEmitXZ = xz;
			_hasLastEmit = true;
			return;
		}
		if (xz.DistanceSquaredTo(_lastEmitXZ) >= stride * stride)
		{
			Emit(world, worldPos, yaw, ground, texture, alphaMultiplier, durationMultiplier, gated);
			_lastEmitXZ = xz;
		}
	}

	private static void Emit(
		World world,
		Vector3 worldPos,
		float yaw,
		EGroundType ground,
		Texture2D texture,
		float alphaMultiplier,
		float durationMultiplier,
		bool gated)
	{
		if (world == null || texture == null)
		{
			return;
		}
		SimData sim = world.SimData;
		if (sim?.FootprintColors == null)
		{
			return;
		}
		if (!sim.FootprintColors.TryGetValue(ground, out Color tint))
		{
			return;
		}
		// Bake the alpha multiplier into the tint so World.SpawnFootprint
		// only has to deal with one composed Color.
		Color spawnTint = new(tint.R, tint.G, tint.B, Mathf.Clamp(tint.A * alphaMultiplier, 0f, 1f));
		float duration = sim.FootprintDurationSeconds * durationMultiplier;
		world.SpawnFootprint(texture, spawnTint, worldPos, yaw, duration, gated);
	}
}
