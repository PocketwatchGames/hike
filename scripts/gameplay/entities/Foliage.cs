using Godot;

[GlobalClass]
public partial class Foliage : Area3D, IWorldEntity
{
	[Export] public float speed = 0.5f;
	[Export] public float camouflage = 0.1f;

	public override void _Ready()
	{
		CollisionMask |= (uint)(ECollisionLayer.Player | ECollisionLayer.Mob);
		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;

		// Track the props_visible bisection toggle. Setting Visible=false on
		// the Area3D propagates render-side to the MultimeshPropSprite child
		// (which draws via WorldPropScatter, so its Visible flag is moot —
		// the multimesh bucket itself respects propsVisible). Collision still
		// works either way, so the player still picks up camo/slow when the
		// debug toggle is hiding visuals.
		Visible = CVars.propsVisible.Value;
		CVars.propsVisible.OnChanged += OnPropsVisibleChanged;
		TreeExiting += () => CVars.propsVisible.OnChanged -= OnPropsVisibleChanged;
	}

	private void OnPropsVisibleChanged(CVar cvar)
	{
		Visible = ((CVarBool)cvar).Value;
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

	public static Foliage Create(World world, PropSimState data)
	{
		var instance = data.Scene.Instantiate<Foliage>();
		instance.Position = data.WorldPosition;
		instance.Rotation = new Vector3(0f, data.RotationY, 0f);
		world.AddChild(instance);
		return instance;
	}
}
