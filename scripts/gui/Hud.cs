using Godot;
using System.Collections.Generic;

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
	[Export] HudRegionBanner _regionBanner;

	Player _player;
	Inventory _inventory;
	// Active status-effect HUD nodes keyed by their data. Multiple stacked
	// instances of the same data show as one HUD entry — count is set on the
	// existing entry instead of spawning duplicates. Entries are added /
	// removed each tick as the player's status-effect list changes.
	readonly Dictionary<StatusEffectData, StatusEffectHud> _statusEffectHuds = new();
	// Reused per-frame so the per-data instance counts don't churn the GC.
	// Cleared at the top of UpdateStatusEffects.
	readonly Dictionary<StatusEffectData, int> _statusEffectCounts = new();
	readonly Dictionary<StatusEffectData, ulong> _statusEffectShortestRemainingMs = new();
	readonly List<StatusEffectData> _statusEffectsToRemove = new();

	public override void _Ready()
	{
		gameClient.onPlayerSpawned += OnPlayerSpawned;
		gameClient.onRegionEntered += OnRegionEntered;
		_weaponLeftButtonHint.SetHint("AttackMelee", string.Empty);
		_weaponRightButtonHint.SetHint("AttackRanged", string.Empty);
		_consumableButtonHint.SetHint("UseItem", string.Empty);
	}

	public override void _ExitTree()
	{
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
			gameClient.onRegionEntered -= OnRegionEntered;
		}
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventorySlotChanged;
			_inventory.onActiveConsumableChanged -= OnActiveConsumableChanged;
		}
	}

	void OnRegionEntered(RegionData region)
	{
		_regionBanner?.Show(region);
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

		float maxArmor = _player.MaxArmor;
		_armorBar.MinValue = 0;
		_armorBar.MaxValue = 1;
		_armorBar.Visible = maxArmor > 0f;
		_armorBar.Value = maxArmor > 0f ? _player.Armor / maxArmor : 0f;

		ulong now = gameClient.World?.GameTimeMs ?? 0;
		_weaponLeftHud.Tick(now);
		_weaponRightHud.Tick(now);
		_consumableHud.Tick(now);

		UpdateStatusEffects(now);

		_weaponLeftButtonHint.SetProgress(GetChargeProgress(EInventorySlot.WeaponLeft, now));
		_weaponRightButtonHint.SetProgress(GetChargeProgress(EInventorySlot.WeaponRight, now));
		_consumableButtonHint.SetProgress(GetChargeProgress(EInventorySlot.Consumable, now));
	}

	// Sync the strip of status-effect icons against the player's current list.
	// Effects with the same data stack into one entry whose count badge shows
	// stack size; the progress bar tracks the timer of the instance closest to
	// expiry (or hides if every instance in the stack is persistent).
	void UpdateStatusEffects(ulong now)
	{
		_statusEffectCounts.Clear();
		_statusEffectShortestRemainingMs.Clear();

		IReadOnlyList<StatusEffectState> effects = _player.StatusEffects;
		for (int i = 0; i < effects.Count; i++)
		{
			StatusEffectState s = effects[i];
			if (s?.data == null)
			{
				continue;
			}
			_statusEffectCounts.TryGetValue(s.data, out int prevCount);
			_statusEffectCounts[s.data] = prevCount + 1;
			if (s.IsTimed)
			{
				ulong remaining = s.RemainingMs(now);
				if (!_statusEffectShortestRemainingMs.TryGetValue(s.data, out ulong prevShortest)
					|| remaining < prevShortest)
				{
					_statusEffectShortestRemainingMs[s.data] = remaining;
				}
			}
		}

		// Drop HUD entries whose data no longer appears in the player's list.
		_statusEffectsToRemove.Clear();
		foreach (var kv in _statusEffectHuds)
		{
			if (!_statusEffectCounts.ContainsKey(kv.Key))
			{
				kv.Value.QueueFree();
				_statusEffectsToRemove.Add(kv.Key);
			}
		}
		for (int i = 0; i < _statusEffectsToRemove.Count; i++)
		{
			_statusEffectHuds.Remove(_statusEffectsToRemove[i]);
		}

		// Add / refresh entries for everything currently held.
		foreach (var kv in _statusEffectCounts)
		{
			StatusEffectData data = kv.Key;
			int count = kv.Value;
			if (!_statusEffectHuds.TryGetValue(data, out StatusEffectHud hud))
			{
				hud = _statusEffectHudScene.Instantiate<StatusEffectHud>();
				_statusEffectContainer.AddChild(hud);
				_statusEffectHuds[data] = hud;
			}
			bool hasTimer = _statusEffectShortestRemainingMs.TryGetValue(data, out ulong shortestRemaining);
			float progress = 0f;
			if (hasTimer)
			{
				float totalMs = data.duration * 1000f;
				progress = totalMs > 0f ? shortestRemaining / totalMs : 0f;
			}
			hud.Set(data, count, progress, hasTimer);
		}
	}

	// Charge fill toward the next tier's chargeTime while the slot's item is
	// in the runner's Charging phase. Cooldown is shown by WeaponHud, not here.
	float GetChargeProgress(EInventorySlot slot, ulong nowMs)
	{
		ItemState item = _inventory?.GetEquipped(slot);
		if (item == null || _player == null || _player.Runner == null)
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
		ItemAction nextTier = profile.chargedActions[nextIndex];
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
}
