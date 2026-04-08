using Godot;

[GlobalClass]
public partial class TallGrass : Area3D
{
	[Export] public float speed = 0.5f;

	public override void _Ready()
	{
		CollisionMask |= (uint)ECollisionLayer.Player;
		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;
	}

	private void OnBodyEntered(Node3D body)
	{
		if (body is Player player)
		{
			player.AddTerrainModifier(this);
		}
	}

	private void OnBodyExited(Node3D body)
	{
		if (body is Player player)
		{
			player.RemoveTerrainModifier(this);
		}
	}

	public static TallGrass Create(World world, PropSpawnState data)
	{
		var instance = data.Scene.Instantiate<TallGrass>();
		instance.Position = data.WorldPosition;
		world.AddChild(instance);
		return instance;
	}
}
