using Godot;

[GlobalClass]
public partial class TallGrass : Area3D, IWorldEntity
{
	[Export] public float speed = 0.5f;
	[Export] public float camouflage = 0.1f;

	public override void _Ready()
	{
		CollisionMask |= (uint)(ECollisionLayer.Player | ECollisionLayer.Mob);
		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;
	}

	public void OnSpawned(World world)
	{
		world.SetLightMapUniforms(this);
	}

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
