using Godot;

[GlobalClass]
public partial class WeaponHud : BoxContainer
{
	[Export] ProgressBar _cooldownBar;
	[Export] TextureRect _icon;
	[Export] Control _ammoGroup;
	[Export] ProgressBar _ammoProgress;
	[Export] Label _ammoText;
	// Block-armor guard widget — a darker "capacity" bar (fixed full value)
	// with a brighter fill painted over the current pool on top, so the
	// recharging deficit reads as a dark span exactly like the health bar's
	// blood. _blockArmorGroup carries the modulate alpha that fades the whole
	// gauge to a faint ghost when this weapon isn't being charged.
	[Export] Control _blockArmorGroup;
	[Export] ProgressBar _blockArmorBar;

	// Alpha applied to the guard gauge while the player isn't charging this
	// weapon — the guard is dormant, so it reads as a faint ghost rather than
	// a live bar.
	const float BlockArmorIdleAlpha = 0.25f;

	ItemState _item;

	public void SetItem(ItemState item)
	{
		_item = item;
		Refresh(0, false);
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
		UpdateBlockArmor(charging);
	}

	void UpdateBlockArmor(bool charging)
	{
		if (_blockArmorGroup == null)
		{
			return;
		}
		if (_item is WeaponState weapon && weapon.data is WeaponData weaponData && weaponData.blockArmor > 0f)
		{
			_blockArmorGroup.Visible = true;
			if (_blockArmorBar != null)
			{
				_blockArmorBar.MinValue = 0;
				_blockArmorBar.MaxValue = 1;
				_blockArmorBar.Value = weapon.blockArmor / weaponData.blockArmor;
			}
			Color modulate = _blockArmorGroup.Modulate;
			modulate.A = charging ? 1f : BlockArmorIdleAlpha;
			_blockArmorGroup.Modulate = modulate;
			return;
		}
		_blockArmorGroup.Visible = false;
	}

	void UpdateIcon()
	{
		if (_item is ConsumableState consumable && consumable.isActive && consumable.data is ConsumableData cd && cd.activeSprite != null)
		{
			_icon.Texture = cd.activeSprite;
			return;
		}
		_icon.Texture = _item?.data?.inventorySprite;
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
		if (_item is TorchState torch && torch.data is TorchData torchData && torchData.HasLimitedFuel)
		{
			_cooldownBar.MinValue = 0;
			_cooldownBar.MaxValue = 1;
			_cooldownBar.Value = (double)torch.FuelRemainingMs / torchData.BurnTimeMs;
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
