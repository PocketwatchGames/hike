using Godot;

[GlobalClass]
public partial class TallGrass : Area3D, IWorldEntity
{
	[Export] public float speed = 0.5f;
	[Export] public float camouflage = 0.1f;

	// Blend between upright billboarding (0) and terrain-aligned billboarding
	// (1). Upright = yaw-to-camera with world-up as the sprite's up axis.
	// Terrain-aligned = yaw-to-camera, but rolled within the sprite plane so
	// the sprite's up axis follows the terrain normal under its anchor (the
	// same roll math detail_sprite.gdshader applies to scattered grass), with
	// a vertical stretch that compensates so the quad keeps its authored
	// aspect ratio as it rolls.
	[Export(PropertyHint.Range, "0,1")] public float AlignToTerrain = 0f;

	// Sprite child the shader uniforms are pushed onto. Assigned in the
	// scene file so we don't GetNode() by name at runtime.
	[Export] private LitSprite _sprite;

	public override void _Ready()
	{
		CollisionMask |= (uint)(ECollisionLayer.Player | ECollisionLayer.Mob);
		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;

		// Push AlignToTerrain and a sampled terrain normal down to the sprite's
		// shader. Sampled via a short downward raycast so the grass leans with
		// the actual visible slope (which the DC mesher may interpolate a bit
		// off the voxel grid — see ChunkMesherDC's shallow-Y smoothing). When
		// the ray misses (grass floating over a carved hole, physics not yet
		// ready, etc.) fall back to world-up so the sprite stays upright.
		if (_sprite != null)
		{
			_sprite.AlignToTerrain = AlignToTerrain;
			if (AlignToTerrain > 0f)
			{
				_sprite.TerrainNormal = SampleTerrainNormal();
			}
		}
	}

	private Vector3 SampleTerrainNormal()
	{
		var space = GetWorld3D().DirectSpaceState;
		if (space == null)
		{
			return Vector3.Up;
		}
		var from = GlobalPosition + Vector3.Up * 0.1f;
		var to = GlobalPosition - Vector3.Up * 2.0f;
		var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Environment);
		var result = space.IntersectRay(query);
		if (result.Count > 0 && result.TryGetValue("normal", out var normal))
		{
			return (Vector3)normal;
		}
		return Vector3.Up;
	}

	public void OnSpawned(World world) { }

	private void OnBodyEntered(Node3D body)
	{
		if (body is Player player)
		{
			player.AddTerrainModifier(this);
		}
		else if (body is Mob mob)
		{
			mob.AddTerrainModifier(this);
		}
	}

	private void OnBodyExited(Node3D body)
	{
		if (body is Player player)
		{
			player.RemoveTerrainModifier(this);
		}
		else if (body is Mob mob)
		{
			mob.RemoveTerrainModifier(this);
		}
	}

	public static TallGrass Create(World world, PropSimState data)
	{
		var instance = data.Scene.Instantiate<TallGrass>();
		instance.Position = data.WorldPosition;
		world.AddChild(instance);
		return instance;
	}
}
