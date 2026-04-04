using System;
using Godot;

public partial class Loot : Area3D
{
	private PropSpawnState _spawnData;
	private CollisionShape3D _collisionShape;
	private AnimationPlayer _animationPlayer;
	private bool _pickedUp;
	private Action<Loot> _onPickedUp;

	public override void _Ready()
	{
		CollisionMask |= 2; // Layer 2 (bit 1) — detect players
		BodyEntered += OnBodyEntered;

		foreach (Node child in GetChildren())
		{
			if (child is CollisionShape3D col)
			{
				_collisionShape = col;
			}
			else if (child is AnimationPlayer anim)
			{
				_animationPlayer = anim;
			}
		}

		if (_animationPlayer != null)
		{
			_animationPlayer.Play("Bob");
		}
	}

	private void OnBodyEntered(Node3D body)
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

		if (_collisionShape != null)
		{
			_collisionShape.Disabled = true;
		}

		_onPickedUp?.Invoke(this);

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

	public static Loot Create(PropSpawnState data, Action<Loot> onPickedUp)
	{
		var instance = data.Scene.Instantiate<Loot>();
		instance.Position = data.WorldPosition;
		instance._spawnData = data;
		instance._onPickedUp = onPickedUp;
		return instance;
	}
}
