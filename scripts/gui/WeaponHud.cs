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
		UpdateCounter();
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

	void UpdateCounter()
	{
		if (_item is WeaponState weapon && weapon.data is WeaponData weaponData && weaponData.maxAmmo > 0)
		{
			_ammoGroup.Visible = true;
			_ammoText.Text = weapon.ammo.ToString();
			UpdateAmmoProgress(weapon, weaponData);
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

	void UpdateAmmoProgress(WeaponState weapon, WeaponData weaponData)
	{
		_ammoProgress.MinValue = 0;
		_ammoProgress.MaxValue = 1;
		if (weapon.ammo >= weaponData.maxAmmo)
		{
			_ammoProgress.Value = 0;
			return;
		}
		float maxProgress = 0f;
		foreach (IWeaponArrow arrow in weapon.outstandingArrows)
		{
			float p = arrow.GetReplenishProgress();
			if (p > maxProgress)
			{
				maxProgress = p;
			}
		}
		_ammoProgress.Value = maxProgress;
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
