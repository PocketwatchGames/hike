using Godot;

[GlobalClass]
public partial class WeaponHud : BoxContainer
{
	[Export] ProgressBar _cooldownBar;
	[Export] TextureRect _icon;
	[Export] Control _ammoGroup;
	[Export] ProgressBar _ammoProgress;
	[Export] Label _ammoText;

	ItemState _item;

	public void SetItem(ItemState item)
	{
		_item = item;
		Refresh(0);
	}

	public void Tick(ulong nowMs)
	{
		Refresh(nowMs);
	}

	void Refresh(ulong nowMs)
	{
		UpdateIcon();
		UpdateCounter();
		UpdateCooldown(nowMs);
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
