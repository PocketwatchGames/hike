using Godot;

[GlobalClass]
public partial class Loot : RigidBody3D
{
	[Export] private CollisionShape3D _collisionShape;
	[Export] private AnimationPlayer _animationPlayer;
	[Export] private Area3D _pickupArea;

	private PropSpawnState _spawnData;
	private bool _pickedUp;
	private World _world;
	private Vector3 _initialImpulse;

	public override void _Ready()
	{
		_pickupArea.BodyEntered += OnBodyEntered;
		_pickupArea.Monitoring = false;

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

		if (_spawnData != null)
		{
			_spawnData.PickedUp = true;
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

	public static Loot Create(PropSpawnState data, World world, Vector3 impulse = default)
	{
		var instance = data.Scene.Instantiate<Loot>();
		instance.Position = data.WorldPosition;
		instance._spawnData = data;
		instance._world = world;
		instance._initialImpulse = impulse;
		return instance;
	}
}
