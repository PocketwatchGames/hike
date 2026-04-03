using Godot;

public partial class TallGrass : Area3D
{
	[Export] public float speed = 0.5f;

	public override void _Ready()
	{
		CollisionMask |= 2; // Layer 2 (bit 1) — detect players
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

	public static TallGrass Create(PropGenData data, float spriteYScale)
	{
		var instance = data.Scene.Instantiate<TallGrass>();
		instance.Position = data.WorldPosition;
		foreach (Node child in instance.GetChildren())
		{
			if (child is Sprite3D sprite)
			{
				sprite.Scale = new Vector3(1, spriteYScale, 1);
				break;
			}
		}
		return instance;
	}
}
