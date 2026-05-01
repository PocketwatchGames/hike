using Godot;

public partial class Hud : CanvasLayer
{
	[Export] public GameClient gameClient;
	[Export] PackedScene _statusEffectHudScene;
	[Export] WeaponHud _weaponLeftHud;
	[Export] WeaponHud _weaponRightHud;
	[Export] WeaponHud _consumableHud;
	[Export] ButtonHint _weaponLeftButtonHint;
	[Export] ButtonHint _weaponRightButtonHint;
	[Export] ButtonHint _consumableButtonHint;
	[Export] Control _statusEffectContainer;
	[Export] ProgressBar _healthBar;
	[Export] ProgressBar _armorBar;

	Player _player;
	Inventory _inventory;

	public override void _Ready()
	{
		gameClient.onPlayerSpawned += OnPlayerSpawned;
		_weaponLeftButtonHint.SetHint("AttackMelee", string.Empty);
		_weaponRightButtonHint.SetHint("AttackRanged", string.Empty);
		_consumableButtonHint.SetHint("UseItem", string.Empty);
	}

	public override void _ExitTree()
	{
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
		}
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventorySlotChanged;
			_inventory.onActiveConsumableChanged -= OnActiveConsumableChanged;
		}
	}

	void OnPlayerSpawned(Player player)
	{
		_player = player;
		_inventory = player.Inventory;
		_inventory.onSlotChanged += OnInventorySlotChanged;
		_inventory.onActiveConsumableChanged += OnActiveConsumableChanged;
		RefreshSlot(EInventorySlot.WeaponLeft);
		RefreshSlot(EInventorySlot.WeaponRight);
		RefreshSlot(EInventorySlot.Consumable);
	}

	void OnInventorySlotChanged(EInventorySlot slot)
	{
		RefreshSlot(slot);
	}

	void OnActiveConsumableChanged(int index)
	{
		RefreshSlot(EInventorySlot.Consumable);
	}

	void RefreshSlot(EInventorySlot slot)
	{
		ItemState item = _inventory?.GetEquipped(slot);
		switch (slot)
		{
			case EInventorySlot.WeaponLeft:
				_weaponLeftHud.SetItem(item);
				_weaponLeftButtonHint.Visible = item != null;
				_weaponLeftButtonHint.ActionName = item?.data?.displayName ?? string.Empty;
				break;
			case EInventorySlot.WeaponRight:
				_weaponRightHud.SetItem(item);
				_weaponRightButtonHint.Visible = item != null;
				_weaponRightButtonHint.ActionName = item?.data?.displayName ?? string.Empty;
				break;
			case EInventorySlot.Consumable:
				_consumableHud.SetItem(item);
				_consumableButtonHint.Visible = item != null;
				break;
		}
	}

	public override void _Process(double delta)
	{
		if (_player == null)
		{
			return;
		}

		float maxHealth = _player.MaxHealth;
		_healthBar.MinValue = 0;
		_healthBar.MaxValue = 1;
		_healthBar.Value = maxHealth > 0f ? _player.Health / maxHealth : 0f;

		ulong now = gameClient.World?.GameTimeMs ?? 0;
		_weaponLeftHud.Tick(now);
		_weaponRightHud.Tick(now);
		_consumableHud.Tick(now);

		_weaponLeftButtonHint.SetProgress(GetSlotProgress(EInventorySlot.WeaponLeft, now));
		_weaponRightButtonHint.SetProgress(GetSlotProgress(EInventorySlot.WeaponRight, now));
		_consumableButtonHint.SetProgress(GetSlotProgress(EInventorySlot.Consumable, now));
	}

	// Charge progress takes precedence: while the player is holding the slot's
	// input toward the next charged tier, fill toward that tier's chargeTime.
	// Otherwise fall back to the post-fire cooldown timer.
	float GetSlotProgress(EInventorySlot slot, ulong nowMs)
	{
		ItemState item = _inventory?.GetEquipped(slot);
		if (item == null)
		{
			return 0f;
		}
		float charge = GetChargeProgress(item, nowMs);
		if (charge > 0f)
		{
			return charge;
		}
		return GetCooldownProgress(item, nowMs);
	}

	float GetChargeProgress(ItemState item, ulong nowMs)
	{
		if (_player == null || _player.Runner == null)
		{
			return 0f;
		}
		ref readonly PlayerAction action = ref _player.Runner.Current;
		if (action.phase != EActionPhase.Charging)
		{
			return 0f;
		}
		if (action.context.primaryItem != item)
		{
			return 0f;
		}
		ItemActionProfile profile = action.profile;
		if (profile == null || profile.chargedActions == null || profile.chargedActions.Count == 0)
		{
			return 0f;
		}

		float elapsed = (nowMs - action.pressMs) / 1000f;
		int nextIndex = action.selectedTierIndex + 1;
		if (nextIndex >= profile.chargedActions.Count)
		{
			return 1f;
		}
		ChargedAction nextTier = profile.chargedActions[nextIndex];
		if (nextTier == null)
		{
			return 1f;
		}
		float prevChargeTime = action.selectedTierIndex >= 0
			? profile.chargedActions[action.selectedTierIndex].chargeTime
			: 0f;
		float span = nextTier.chargeTime - prevChargeTime;
		if (span <= 0f)
		{
			return 1f;
		}
		return Mathf.Clamp((elapsed - prevChargeTime) / span, 0f, 1f);
	}

	float GetCooldownProgress(ItemState item, ulong nowMs)
	{
		if (item.cooldownDurationMs == 0 || nowMs >= item.cooldownExpireMs)
		{
			return 0f;
		}
		ulong remaining = item.cooldownExpireMs - nowMs;
		return (float)remaining / item.cooldownDurationMs;
	}
}
