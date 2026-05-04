using Godot;
using Godot.Collections;

// Interactive-pickup loot — the player must walk near AND press Interact to
// pick it up. Routes through the action runner via GetActions, so picking up
// runs an ItemActionProfile timeline (animation, sound, eventually a Diablo-
// style "loot the corpse" sequence). On OpenInteractive event firing, the
// loot is removed from the world and any carried ItemState is deposited into
// the player's inventory. The auto-pickup variant is AutoLoot.
[GlobalClass]
public partial class Loot : RigidBody3D, IInteractive, IWorldEntity
{
	[Export] private CollisionShape3D _collisionShape;
	[Export] private AnimationPlayer _animationPlayer;
	[Export] private Area3D _interactArea;
	[Export] private HurtBox _hurtBox;
	[Export] private Node3D _hudNode;
	[Export] private PackedScene _pickupEffectScene;
	[Export] private PackedScene _spawnEffectScene;

	// Authored interaction list. The first entry's events should include an
	// OpenInteractive event that triggers Complete() — that's how the runner
	// signals "the loot has been collected." Break / Examine can be authored
	// later as the design evolves.
	[Export] private Array<InteractiveAction> _actions = new();

	private PropSimState _simState;
	private bool _pickedUp;
	private World _world;
	private Vector3 _initialImpulse;
	private Player _picker;
	private bool _playSpawnEffects;

	public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

	public override void _Ready()
	{
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
		_animationPlayer?.Play("Bob");
	}

	private void OnHurtBoxHit(HitInfo hit)
	{
		GD.Print($"Loot hit for {hit.healthDamage} from {hit.source?.Name}");
	}

	public bool CanInteract() => !_pickedUp;
	public bool CanActorInteract(Player player)
	{
		if (_pickedUp || player?.Inventory == null)
		{
			return false;
		}
		// If the loot carries an item, only allow interact when there's
		// space; otherwise the action would run to completion and silently
		// fail. AutoLoot stays-on-ground when full; this matches.
		if (_simState?.Item != null && player.Inventory.BackpackCount >= player.Inventory.BackpackCapacity)
		{
			return false;
		}
		return true;
	}

	public Array<InteractiveAction> GetActions(Player player)
	{
		if (_actions == null || _actions.Count == 0)
		{
			return null;
		}
		_picker = player;
		return _actions;
	}

	// Called from the action's OpenInteractive event handler at the
	// authored t=N moment. Deposits the carried item into the picker's
	// inventory (if any) and removes the loot from the world.
	public void Complete(int actionIndex)
	{
		if (_pickedUp)
		{
			return;
		}
		if (_simState?.Item != null && _picker?.Inventory != null)
		{
			int added = _picker.Inventory.TryAdd(_simState.Item);
			if (added <= 0)
			{
				return;
			}
		}

		_pickedUp = true;
		if (_simState != null)
		{
			_simState.PickedUp = true;
		}
		if (_pickupEffectScene != null)
		{
			Fx.Create(_pickupEffectScene, GetParent(), Position);
		}
		_collisionShape.Disabled = true;
		_world?.RemoveEntity(this);
		if (_animationPlayer != null)
		{
			_animationPlayer.AnimationFinished += OnPickedUpFinished;
			_animationPlayer.Play("PickedUp");
		}
		else
		{
			QueueFree();
		}
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

	public static Loot Create(World world, PropSimState data, Vector3 impulse = default)
	{
		var instance = data.Scene.Instantiate<Loot>();
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
