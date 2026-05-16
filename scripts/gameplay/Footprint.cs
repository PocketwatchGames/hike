using Godot;

// Ground decal left behind by a Player or Mob at footstep cadence.
//
// Visual is a Godot Decal — projects the actor's footprint texture down onto
// whatever geometry is below, so prints sit correctly on slopes, voxel
// step-ups, and uneven ground without per-actor projection math. Decals
// bypass our custom voxel lighting shader (they render in Godot's standard
// decal pass), so this script samples World.GetPerceivedLight at the print's
// position and modulates the decal tint to keep prints consistent with the
// rest of the world's day/night/cloud-shadow integration.
//
// Lifetime / discovery composition: lifetimeFade × discoveryFade × baseAlpha
// × worldLight, all written into the decal's Modulate alpha each frame.
// QueueFree's once lifetimeFade hits zero. Discoverable on the mob variant
// drives discoveryFade through its own perception state.
[GlobalClass]
public partial class Footprint : Node3D
{
	// The projected ground decal. Authored in the .tscn with a sensible
	// projection depth (Decal.Size.Y) and albedo_mix; this script writes
	// texture_albedo, the X/Z size (from the actor-supplied footprint size),
	// and modulate at runtime, so each actor can size their own prints
	// without needing a per-actor scene.
	[Export] private Decal _decal;
	// Optional perception gate for mob-laid prints. When wired, the
	// print is held invisible until the player notices it; player-print
	// scenes leave this null and the print is visible immediately.
	[Export] private Discoverable _discoverable;

	// Seconds for the discovery fade to traverse 0..1. Mirrors Discoverable's
	// own internal fade time so a footprint pop feels like a chest pop.
	private const float DiscoveryFadeTime = 0.4f;

	private Color _tintColor = new(1f, 1f, 1f, 1f);
	private float _durationSeconds = 15f;
	private ulong _spawnTimeMs;
	private World _world;
	// Smoothed 0..1 noticed-by-player factor. For player prints (no
	// Discoverable) this stays pinned at 1.
	private float _discoveryAlpha;

	public void Initialize(World world, Texture2D texture, Vector2 footprintSize, Color tint, float durationSeconds)
	{
		_world = world;
		_tintColor = tint;
		_durationSeconds = Mathf.Max(0.1f, durationSeconds);
		_spawnTimeMs = world?.GameTimeMs ?? 0;
		_discoveryAlpha = _discoverable == null ? 1f : 0f;
		if (_decal != null)
		{
			_decal.TextureAlbedo = texture;
			Vector3 size = _decal.Size;
			size.X = footprintSize.X;
			size.Z = footprintSize.Y;
			_decal.Size = size;
		}
		PushModulate(1f);
	}

	public override void _Process(double delta)
	{
		if (_world == null)
		{
			return;
		}

		ulong age = _world.GameTimeMs - _spawnTimeMs;
		float lifetimeAlpha = 1f - (age * 0.001f / _durationSeconds);
		if (lifetimeAlpha <= 0f)
		{
			QueueFree();
			return;
		}

		if (_discoverable != null)
		{
			float target = _discoverable.IsDiscovered ? 1f : 0f;
			_discoveryAlpha = Mathf.MoveToward(_discoveryAlpha, target, (float)delta / DiscoveryFadeTime);
		}

		PushModulate(lifetimeAlpha);
	}

	// Compose final modulate from baseline tint + lifetime + discovery + world
	// light. The decal pass doesn't read our voxel lightmap, so we sample it
	// here per-frame and tint by it; a print rendered indoors at night reads
	// dark, matching the surrounding voxel terrain.
	private void PushModulate(float lifetimeAlpha)
	{
		if (_decal == null)
		{
			return;
		}
		float worldLight = _world != null ? _world.GetPerceivedLight(GlobalPosition) : 1f;
		// SimData.TargetLightMax normalizes light into the same 0..1 band the
		// rest of the perception model uses. Above that the surface is at
		// "fully lit" already, so clamp to 1.
		float targetMax = _world?.SimData?.TargetLightMax ?? 0.75f;
		float lit = targetMax > 0f ? Mathf.Clamp(worldLight / targetMax, 0f, 1f) : 0f;
		float alpha = _tintColor.A * lifetimeAlpha * _discoveryAlpha;
		_decal.Modulate = new Color(_tintColor.R * lit, _tintColor.G * lit, _tintColor.B * lit, alpha);
	}
}
