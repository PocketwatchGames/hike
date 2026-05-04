using Godot;

// Auto-pickup loot — the player walks over it and the carried ItemState is
// deposited into their inventory automatically. Used for player drops and for
// any world-spawn loot that should be no-friction. The interactive variant
// (require press to pick up) is `Loot`, in Loot.cs.
[GlobalClass]
public partial class AutoLoot : RigidBody3D, IWorldEntity
{
	[Export] private CollisionShape3D _collisionShape;
	[Export] private AnimationPlayer _animationPlayer;
	[Export] private Area3D _pickupArea;
	[Export] private HurtBox _hurtBox;
	[Export] private PackedScene _pickupEffectScene;
	[Export] private PackedScene _spawnEffectScene;

	private PropSimState _simState;
	private bool _pickedUp;
	private World _world;
	private Vector3 _initialImpulse;
	private bool _playSpawnEffects;

	public override void _Ready()
	{
		_pickupArea.BodyEntered += OnBodyEntered;
		_pickupArea.Monitoring = false;

		if (_hurtBox != null)
		{
			_hurtBox.OnHit = OnHurtBoxHit;
			_hurtBox.GetHitType = _ => EHitResult.Object;
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

	private void OnHurtBoxHit(HitInfo hit)
	{
		GD.Print($"AutoLoot hit for {hit.healthDamage} from {hit.source?.Name}");
	}

	private void OnBodyEntered(Node body)
	{
		if (_pickedUp)
		{
			return;
		}

		if (body is Player player)
		{
			player.OnAutoLootCollision(this);
		}
	}

	// Returns true if pickup succeeded. When the loot carries an ItemState
	// (player drops or authored drops), the item is deposited into the
	// player's inventory; if it doesn't fit, pickup is refused so the loot
	// stays on the ground. World-spawned loot with no attached ItemState
	// still picks up unconditionally (legacy behavior).
	public bool PickUp(Player picker = null)
	{
		if (_pickedUp)
		{
			return false;
		}

		if (_simState != null && _simState.Item != null && picker != null)
		{
			int added = picker.Inventory?.TryAdd(_simState.Item) ?? 0;
			if (added <= 0)
			{
				return false;
			}
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
		if (_pickupEffectScene != null)
		{
			Fx.Create(_pickupEffectScene, GetParent(), Position);
		}
		return true;
	}

	private void OnPickedUpFinished(StringName animName)
	{
		QueueFree();
	}

	public void OnSpawned(World world)
	{
		if (_playSpawnEffects && _spawnEffectScene != null)
		{
			Fx.Create(_spawnEffectScene, GetParent(), Position);
		}
	}

	public static AutoLoot Create(World world, PropSimState data, Vector3 impulse = default)
	{
		var instance = data.Scene.Instantiate<AutoLoot>();
		instance.Position = data.WorldPosition;
		instance._simState = data;
		instance._world = world;
		instance._initialImpulse = impulse;
		instance._playSpawnEffects = true;
		world.AddChild(instance);
		return instance;
	}

	public override void _ExitTree()
	{
		if (_simState != null && !_pickedUp)
		{
			_simState.WorldPosition = Position;
		}
		base._ExitTree();
	}
}
