using Godot;

// Ground mark left behind by a Player or Mob at footstep cadence.
//
// Visual is a flat quad on the ground-stain projector layer (see
// GroundStainProjector): the GroundStainProjector renders it from straight
// above into a world-space texture, and the lit ground shaders composite that
// into the surface BASE color before the lighting split. So prints read in any
// lighting (sun, shade, swamp, torchlight) AND the shader handles light-
// matching for free — unlike the old Godot Decal, which only modified ALBEDO
// (the direct-sun fraction) and washed out wherever the EMISSION term
// dominated, so we no longer sample perceived light here.
//
// Lifetime / discovery composition: lifetimeFade × discoveryFade × baseAlpha,
// written into the quad material's albedo alpha each frame. QueueFree's once
// lifetimeFade hits zero. Discoverable on the mob variant drives discoveryFade
// through its own perception state.
[GlobalClass]
public partial class Footprint : Node3D
{
	// The stain proxy quad. Authored in the .tscn on the stain projector layer
	// (layer 5) with a resource_local_to_scene StandardMaterial3D so each
	// instance gets its own material copy — this script writes the albedo
	// texture (per actor), the albedo color (tint + animated alpha), and the
	// quad scale (per-actor footprint size) without cross-talk between prints.
	[Export] private MeshInstance3D _quad;
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
	// Per-instance quad material (unique via resource_local_to_scene).
	private StandardMaterial3D _material;

	public void Initialize(World world, Texture2D texture, Vector2 footprintSize, Color tint, float durationSeconds)
	{
		_world = world;
		_tintColor = tint;
		_durationSeconds = Mathf.Max(0.1f, durationSeconds);
		_spawnTimeMs = world?.GameTimeMs ?? 0;
		_discoveryAlpha = _discoverable == null ? 1f : 0f;
		if (_quad != null)
		{
			_material = _quad.MaterialOverride as StandardMaterial3D;
			if (_material != null)
			{
				_material.AlbedoTexture = texture;
			}
			// The authored PlaneMesh is unit-sized; scale the quad to the
			// actor's footprint size (X = width, Z = stride length). Rotation
			// (facing) lives on the Footprint root, so scaling the child stays
			// shear-free.
			Vector3 scale = _quad.Scale;
			scale.X = footprintSize.X;
			scale.Z = footprintSize.Y;
			_quad.Scale = scale;
		}
		// Seed the alpha so a pre-discovery footprint doesn't render a frame at
		// the material's authored color before _Process runs.
		PushModulate(1f);
	}

	public override void _Process(double delta)
	{
		using var _prof = Profiler.Sample("Footprint.Process");
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

		// Only animate the discovery fade while a transition is in
		// progress — once _discoveryAlpha hits target (0 = pre-discovery,
		// 1 = fully visible) MoveToward would be a no-op every frame.
		if (_discoverable != null)
		{
			float target = _discoverable.IsDiscovered ? 1f : 0f;
			if (_discoveryAlpha != target)
			{
				_discoveryAlpha = Mathf.MoveToward(_discoveryAlpha, target, (float)delta / DiscoveryFadeTime);
			}
		}

		// Pre-discovery mob prints sit at _discoveryAlpha = 0 → alpha = 0, the
		// quad contributes nothing to the stain texture, so skip the write. The
		// material already carries an alpha=0 color from Initialize.
		float alpha = _tintColor.A * lifetimeAlpha * _discoveryAlpha;
		if (alpha <= 0f)
		{
			return;
		}

		PushModulate(lifetimeAlpha);
	}

	// Compose the quad albedo from baseline tint + lifetime + discovery. The
	// ground-stain shader lights the composited surface, so we no longer pre-dim
	// the tint by perceived light — a print on dark ground reads dark because
	// the surface it stains is dark, not because we darkened the print.
	private void PushModulate(float lifetimeAlpha)
	{
		if (_material == null)
		{
			return;
		}
		float alpha = _tintColor.A * lifetimeAlpha * _discoveryAlpha;
		_material.AlbedoColor = new Color(_tintColor.R, _tintColor.G, _tintColor.B, alpha);
	}
}
