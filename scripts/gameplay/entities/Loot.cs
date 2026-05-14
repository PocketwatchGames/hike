using Godot;
using Godot.Collections;

// World pickup. The pickup model is decided at run time per (player,
// inventory) pair: if the player already has a same-kind non-full stack and
// the whole pile would top off into those existing stacks, walking near the
// pile is enough — InteractArea.BodyEntered fires and the loot deposits.
// Otherwise (fresh item, full stacks, non-stackable, or explicitly dropped by
// the player) the same area's interact-highlight path takes over and pickup
// runs through the action runner so the player has to press Interact. One
// Area3D drives both — the auto-pickup probe and the interact-highlight scan
// share the same volume so the two modes can't disagree on range.
[GlobalClass]
public partial class Loot : RigidBody3D, IInteractive, IWorldEntity
{
	[Export] private CollisionShape3D _collisionShape;
	[Export] private AnimationPlayer _animationPlayer;
	[Export] private Area3D _interactArea;
	[Export] private HurtBox _hurtBox;
	[Export] private Node3D _hudNode;
	[Export] private Sprite3D _sprite;
	[Export] private PackedScene _pickupEffectScene;
	[Export] private PackedScene _spawnEffectScene;
	// Played at the loot's position when it expires (LootData.removeTimeMs).
	// Same Fx.Create one-shot pattern as the pickup/spawn effects; null leaves
	// the despawn silent (e.g. test scenes that don't author a remove cue).
	[Export] private PackedScene _removeEffectScene;

	// Authored interaction list. The first entry's events should include an
	// OpenInteractive event that triggers Complete() — that's how the runner
	// signals "the loot has been collected." Break / Examine can be authored
	// later as the design evolves.
	[Export] private Array<InteractiveAction> _actions = new();

	private LootSimState _simState;
	private bool _pickedUp;
	private bool _removed;
	private World _world;
	private Vector3 _initialImpulse;
	private Player _picker;
	private bool _playSpawnEffects;
	// Elapsed time the pickup has been in the world, in seconds. Compared
	// against LootData.removeTimeMs (converted to seconds) to decide when to
	// fire the remove FX and despawn. Local to the live instance — re-enters
	// at 0 if the chunk unloads and re-streams the loot.
	private float _ageSeconds;

	public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

	public override void _Ready()
	{
		if (_interactArea != null)
		{
			_interactArea.BodyEntered += OnInteractAreaBodyEntered;
		}

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

	public override void _Process(double delta)
	{
		if (_pickedUp || _removed)
		{
			return;
		}
		ItemData data = _simState?.Item?.data ?? _simState?.Data;
		if (data is not LootData lootData || lootData.removeTimeMs <= 0)
		{
			return;
		}
		_ageSeconds += (float)delta;
		if (_ageSeconds * 1000f >= lootData.removeTimeMs)
		{
			Expire();
		}
	}

	private void Expire()
	{
		if (_removed || _pickedUp)
		{
			return;
		}
		_removed = true;
		// Reuse the PickedUp latch on the sim state — the only thing that
		// flag gates is LootSimState.CreateEntity returning null, which is
		// exactly the behavior we want for expired loot (don't respawn it
		// when the chunk re-streams).
		if (_simState != null)
		{
			_simState.PickedUp = true;
		}
		if (_removeEffectScene != null)
		{
			Fx.Create(_removeEffectScene, GetParent(), Position);
		}
		if (_collisionShape != null)
		{
			_collisionShape.Disabled = true;
		}
		if (_interactArea != null)
		{
			_interactArea.Monitoring = false;
		}
		_world?.RemoveEntity(this);
		QueueFree();
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
		// Enable monitoring after settle so the area only starts probing once
		// the loot is at rest — avoids spurious BodyEntered events from
		// graze-collisions during the post-spawn flight arc.
		if (_interactArea != null)
		{
			_interactArea.Monitoring = true;
		}
		// Loot that flew in (chest emission, player drop) bobs to read as
		// freshly arrived; loot that was already in the world at spawn (world
		// gen, LootSpawnEntry) sits idle so the chunk doesn't pulse.
		_animationPlayer?.Play(_initialImpulse != Vector3.Zero ? "Bob" : "Idle");
	}

	private void OnHurtBoxHit(HitInfo hit)
	{
	}

	private void OnInteractAreaBodyEntered(Node body)
	{
		if (_pickedUp || body is not Player player)
		{
			return;
		}
		// Body entry only acts when the inventory state allows auto-pickup.
		// Otherwise the same area's interact-highlight path is what the
		// player uses, via the action runner.
		if (!CanAutoPickup(player))
		{
			return;
		}
		_picker = player;
		FinalizePickup();
	}

	// Auto-pickup only fires when the entire pile would top off into existing
	// same-kind stacks. Fresh items (player has none of this kind) and items
	// that would need a new backpack slot fall through to the interactive
	// path so the player chooses to commit to a new slot.
	private bool CanAutoPickup(Player player)
	{
		if (_simState == null || _simState.RequireInteract)
		{
			return false;
		}
		if (player?.Inventory == null)
		{
			return false;
		}

		ItemData data = _simState.Item?.data ?? _simState.Data;
		if (data == null || !data.IsStackable)
		{
			return false;
		}

		int needed = _simState.Item?.stackCount ?? 1;
		int avail = 0;
		foreach (ItemState s in player.Inventory.EnumerateAll())
		{
			if (s.data != data)
			{
				continue;
			}
			avail += s.RemainingStackSpace();
			if (avail >= needed)
			{
				return true;
			}
		}
		return false;
	}

	public bool CanInteract() => !_pickedUp;
	public bool CanActorInteract(Player player)
	{
		if (_pickedUp || player?.Inventory == null)
		{
			return false;
		}
		// Auto-pickup loot suppresses its own interact highlight — body entry
		// will commit the pickup on the next physics frame, so showing the
		// "press to interact" affordance would just flicker.
		if (CanAutoPickup(player))
		{
			return false;
		}
		// If the loot carries an item, only allow interact when there's
		// space; otherwise the action would run to completion and silently
		// fail. Armor/weapons can land directly in an empty equip slot, so a
		// full backpack only blocks pickup when there's no slot to equip into.
		if (_simState?.Item != null && player.Inventory.BackpackCount >= player.Inventory.BackpackCapacity)
		{
			ItemData data = _simState.Item.data ?? _simState.Data;
			if (!HasEmptyEquipSlot(player.Inventory, data))
			{
				return false;
			}
		}
		return true;
	}

	private static bool HasEmptyEquipSlot(Inventory inv, ItemData data)
	{
		if (inv == null || data == null)
		{
			return false;
		}
		switch (data)
		{
			case ArmorData armor:
				return inv.GetEquipped(armor.armorSlot) == null;
			case WeaponData _:
				return inv.GetEquipped(EInventorySlot.WeaponLeft) == null
					|| inv.GetEquipped(EInventorySlot.WeaponRight) == null;
			default:
				return false;
		}
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
		if (_pickedUp || _picker == null)
		{
			return;
		}
		FinalizePickup();
	}

	private void FinalizePickup()
	{
		if (_pickedUp)
		{
			return;
		}
		if (!TryDepositItem(_picker))
		{
			return;
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

	// Returns true if the pickup should proceed. World-spawned loot with no
	// attached Item synthesizes a fresh ItemState from Data; legacy entries
	// with neither Data nor Item still pick up cleanly (deposit nothing).
	private bool TryDepositItem(Player player)
	{
		if (_simState == null)
		{
			return true;
		}
		ItemState toAdd = _simState.Item ?? _simState.Data?.CreateState();
		if (toAdd == null)
		{
			return true;
		}
		if (player?.Inventory == null)
		{
			return false;
		}
		Inventory inv = player.Inventory;

		// Armor/weapons land directly in an empty equip slot — bypasses the
		// backpack so the player can grab an obvious upgrade even when the
		// backpack is full.
		if (TryEquipToEmptySlot(inv, toAdd))
		{
			return true;
		}

		int initial = toAdd.stackCount;
		int added = inv.TryAdd(toAdd);
		if (added < initial)
		{
			return false;
		}

		// Consumables promote from the backpack into the first empty hotbar
		// slot. No-op when the item fully merged into an existing stack (the
		// move requires the item to be present in the backpack).
		if (toAdd.data is ConsumableData)
		{
			inv.TryMoveToConsumableSlot(toAdd);
		}
		return true;
	}

	private static bool TryEquipToEmptySlot(Inventory inv, ItemState item)
	{
		if (item?.data == null)
		{
			return false;
		}
		switch (item.data)
		{
			case ArmorData armor:
				if (inv.GetEquipped(armor.armorSlot) == null)
				{
					return inv.TryEquip(item, armor.armorSlot);
				}
				return false;
			case WeaponData _:
				if (inv.GetEquipped(EInventorySlot.WeaponLeft) == null)
				{
					return inv.TryEquip(item, EInventorySlot.WeaponLeft);
				}
				if (inv.GetEquipped(EInventorySlot.WeaponRight) == null)
				{
					return inv.TryEquip(item, EInventorySlot.WeaponRight);
				}
				return false;
			default:
				return false;
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

	public static Loot Create(World world, LootSimState data, PackedScene scene, Vector3 impulse = default)
	{
		var instance = scene.Instantiate<Loot>();
		instance.Position = data.WorldPosition;
		instance._simState = data;
		instance._world = world;
		instance._initialImpulse = impulse;
		instance._playSpawnEffects = true;
		// Swap the world-pickup sprite to the carried item's icon. Prefer the
		// item's worldSprite (authored at chunky-pixel resolution) and fall
		// back to inventorySprite when none is set — RegionEnabled=false makes
		// SpriteBase.Apply recompute the quad size + center offset for
		// whatever texture lands here.
		ItemData itemData = data.Item?.data ?? data.Data;
		Texture2D texture = itemData?.worldSprite ?? itemData?.inventorySprite;
		if (instance._sprite != null && texture != null)
		{
			instance._sprite.RegionEnabled = false;
			instance._sprite.Texture = texture;
		}
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
