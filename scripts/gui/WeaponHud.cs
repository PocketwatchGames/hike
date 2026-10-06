using Godot;

[GlobalClass]
public partial class WeaponHud : BoxContainer
{
	[Export] ProgressBar _cooldownBar;
	[Export] TextureRect _icon;
	[Export] Control _ammoGroup;
	[Export] ProgressBar _ammoProgress;
	[Export] Label _ammoText;
	// Optional hotbar overlays: shown on the hotbar cursor's slot, and on an
	// item that is currently equipped.
	[Export] CanvasItem _selectedMarker;
	[Export] CanvasItem _equippedMarker;

	// Alpha applied to the guard gauge while the player isn't charging this
	// weapon — the guard is dormant, so it reads as a faint ghost rather than
	// a live bar.
	const float BlockArmorIdleAlpha = 0.25f;

	ItemState _item;
	bool _equipped;

	public void SetItem(ItemState item)
	{
		_item = item;
		Refresh(0, false);
	}

	public void SetHotbarState(bool selected, bool equipped)
	{
		_equipped = equipped;
		UpdateIcon();
		if (_selectedMarker != null)
		{
			_selectedMarker.Visible = selected;
		}
		if (_equippedMarker != null)
		{
			_equippedMarker.Visible = equipped;
		}
	}

	public void Tick(ulong nowMs, bool charging)
	{
		Refresh(nowMs, charging);
	}

	void Refresh(ulong nowMs, bool charging)
	{
		UpdateIcon();
		UpdateCounter(nowMs);
		UpdateCooldown(nowMs);
	}


	void UpdateIcon()
	{
		_icon.Texture = _item?.data?.SlotIcon(_equipped);
	}

	void UpdateCounter(ulong nowMs)
	{
		if (_item is WeaponState weapon && weapon.data is WeaponData weaponData && weaponData.maxAmmo > 0)
		{
			_ammoGroup.Visible = true;
			_ammoText.Text = weapon.ammo.ToString();
			UpdateAmmoProgress(weapon, weaponData, nowMs);
			return;
		}
		if (_item != null && _item.data != null && _item.data.IsStackable && _item.stackCount > 1)
		{
			_ammoGroup.Visible = true;
			_ammoText.Text = _item.stackCount.ToString();
			_ammoProgress.Value = 0;
			return;
		}
		_ammoGroup.Visible = false;
	}

	// Progress toward the next ammo unit, driven by the weapon's single central
	// recharge timer (the same one whether the weapon self-recharges like the
	// bomb or reclaims arrows like the bow). 0 when at full ammo or the timer
	// isn't currently armed.
	void UpdateAmmoProgress(WeaponState weapon, WeaponData weaponData, ulong nowMs)
	{
		_ammoProgress.MinValue = 0;
		_ammoProgress.MaxValue = 1;
		ulong durationMs = (ulong)(weaponData.ammoRechargeSeconds * 1000f);
		if (weapon.ammo >= weaponData.maxAmmo || weapon.ammoRechargeReadyMs == 0 || durationMs == 0)
		{
			_ammoProgress.Value = 0;
			return;
		}
		ulong remaining = weapon.ammoRechargeReadyMs > nowMs ? weapon.ammoRechargeReadyMs - nowMs : 0;
		_ammoProgress.Value = Mathf.Clamp(1.0 - (double)remaining / durationMs, 0.0, 1.0);
	}

	void UpdateCooldown(ulong nowMs)
	{
		// A fuel-limited lantern repurposes the cooldown bar as a fuel gauge —
		// always shown, tracking the remaining burn budget as a fraction rather
		// than a cooldown countdown.
		if (_item is LanternState lantern && lantern.data is LanternData lanternData && lanternData.HasLimitedFuel)
		{
			_cooldownBar.MinValue = 0;
			_cooldownBar.MaxValue = 1;
			_cooldownBar.Value = (double)lantern.FuelRemainingMs / lanternData.BurnTimeMs;
			_cooldownBar.Visible = true;
			return;
		}
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
