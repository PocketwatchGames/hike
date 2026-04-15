using Godot;

[GlobalClass]
public partial class Loot : RigidBody3D, IWorldEntity
{
	[Export] private CollisionShape3D _collisionShape;
	[Export] private AnimationPlayer _animationPlayer;
	[Export] private Area3D _pickupArea;
	[Export] private HurtBox _hurtBox;

	private PropSimState _simState;
	private bool _pickedUp;
	private World _world;
	private Vector3 _initialImpulse;

	public override void _Ready()
	{
		_pickupArea.BodyEntered += OnBodyEntered;
		_pickupArea.Monitoring = false;

		if (_hurtBox != null)
		{
			_hurtBox.OnHit = OnHurtBoxHit;
		}

		if (_initialImpulse != Vector3.Zero)
		{
			CanSleep = false;
			ContactMonitor = true;
			MaxContactsReported = 1;
			ApplyCentralImpulse(_initialImpulse);
		}
		else
		{
			Settle();
		}
	}

	public override void _IntegrateForces(PhysicsDirectBodyState3D state)
	{
		if (_pickedUp || Freeze)
		{
			return;
		}

		if (state.LinearVelocity.LengthSquared() < 0.25f && state.GetContactCount() > 0)
		{
			Settle();
		}
	}

	private void Settle()
	{
		Freeze = true;
		_pickupArea.Monitoring = true;
		_animationPlayer.Play("Bob");
	}

	private void OnHurtBoxHit(DamageData data, Node source)
	{
		GD.Print($"Loot hit for {data?.healthDamage} from {source?.Name}");
	}

	private void OnBodyEntered(Node body)
	{
		if (_pickedUp)
		{
			return;
		}

		if (body is Player player)
		{
			player.OnLootCollision(this);
		}
	}

	public void PickUp()
	{
		if (_pickedUp)
		{
			return;
		}

		_pickedUp = true;

		if (_simState != null)
		{
			_simState.PickedUp = true;
		}

		_collisionShape.Disabled = true;
		_world?.RemoveEntity(this);
		_animationPlayer.AnimationFinished += OnPickedUpFinished;
		_animationPlayer.Play("PickedUp");
	}

	private void OnPickedUpFinished(StringName animName)
	{
		QueueFree();
	}

	public void OnSpawned(World world) { }

	public static Loot Create(World world, PropSimState data, Vector3 impulse = default)
	{
		var instance = data.Scene.Instantiate<Loot>();
		instance.Position = data.WorldPosition;
		instance._simState = data;
		instance._world = world;
		instance._initialImpulse = impulse;
		world.AddChild(instance);
		return instance;
	}

	public override void _ExitTree()
	{
		// Sync the physics-driven position back to the persistent sim state so
		// chunk unload / save captures where the loot actually settled.
		if (_simState != null && !_pickedUp)
		{
			_simState.WorldPosition = Position;
		}
		base._ExitTree();
	}
}
