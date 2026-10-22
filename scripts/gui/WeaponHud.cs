using Godot;

[GlobalClass]
public partial class WeaponHud : BoxContainer
{
	[Export] ProgressBar _cooldownBar;
	[Export] TextureRect _icon;
	[Export] Control _ammoGroup;
	[Export] Label _ammoText;
	// Optional hotbar overlays: shown on the hotbar cursor's slot, and on an
	// item that is active — the lit lantern.
	[Export] CanvasItem _selectedMarker;
	[Export] CanvasItem _activeMarker;
	// Gear condition overlays (GearState.Condition): warning while worn, broken
	// once it is.
	[Export] CanvasItem _durabilityWarning;
	[Export] CanvasItem _durabilityBroken;
	// Drawn while the slot holds no item (the armor slot's dash icon). Null = blank.
	[Export] Texture2D _emptyIcon;

	// Alpha applied to the guard gauge while the player isn't charging this
	// weapon — the guard is dormant, so it reads as a faint ghost rather than
	// a live bar.
	const float BlockArmorIdleAlpha = 0.25f;

	ItemState _item;
	// Whose rounds a ranged weapon's counter reads (Inventory.RoundsFor).
	Inventory _owner;
	bool _active;
	float _durabilityWarningFraction;

	public void SetItem(ItemState item, Inventory owner = null)
	{
		_item = item;
		_owner = owner;
		Refresh(0, false);
	}

	public void SetHotbarState(bool selected, bool active)
	{
		_active = active;
		UpdateIcon();
		if (_selectedMarker != null)
		{
			_selectedMarker.Visible = selected;
		}
		if (_activeMarker != null)
		{
			_activeMarker.Visible = active;
		}
	}

	// `durabilityWarningFraction` is SimData.durabilityWarningFraction.
	public void Tick(ulong nowMs, bool charging, float durabilityWarningFraction)
	{
		_durabilityWarningFraction = durabilityWarningFraction;
		Refresh(nowMs, charging);
	}

	void Refresh(ulong nowMs, bool charging)
	{
		UpdateIcon();
		UpdateCounter();
		UpdateCooldown(nowMs);
		UpdateDurability();
	}

	void UpdateDurability()
	{
		GearState.ECondition condition = _item is GearState gear
			? gear.Condition(_durabilityWarningFraction)
			: GearState.ECondition.Sound;
		if (_durabilityWarning != null)
		{
			_durabilityWarning.Visible = condition == GearState.ECondition.Worn;
		}
		if (_durabilityBroken != null)
		{
			_durabilityBroken.Visible = condition == GearState.ECondition.Broken;
		}
	}


	void UpdateIcon()
	{
		_icon.Texture = _item?.data != null ? _item.data.SlotIcon(_active) : _emptyIcon;
	}

	// A weapon that fires rounds shows how many its owner carries — the loaded
	// ammo of its type, or a thrown weapon's own stack — zero included; any other
	// stack shows its count past one.
	void UpdateCounter()
	{
		if (_owner != null && _item is WeaponState { data: WeaponData weaponData } weapon && (weaponData.NeedsAmmo || weaponData.thrown))
		{
			_ammoGroup.Visible = true;
			_ammoText.Text = _owner.RoundsFor(weapon).ToString();
			return;
		}
		if (_item != null && _item.data != null && _item.data.IsStackable && _item.stackCount > 1)
		{
			_ammoGroup.Visible = true;
			_ammoText.Text = _item.stackCount.ToString();
			return;
		}
		_ammoGroup.Visible = false;
	}

	void UpdateCooldown(ulong nowMs)
	{
		if (_item == null || _item.cooldownDurationMs == 0 || nowMs >= _item.cooldownExpireMs)
		{
			_cooldownBar.Visible = false;
			return;
		}
		ulong remaining = _item.cooldownExpireMs - nowMs;
		_cooldownBar.MinValue = 0;
		_cooldownBar.MaxValue = 1;
		_cooldownBar.Value = (double)remaining / _item.cooldownDurationMs;
		_cooldownBar.Visible = true;
	}
}
