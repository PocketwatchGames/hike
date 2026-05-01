using Godot;

[GlobalClass]
public partial class WeaponHud : BoxContainer
{
	[Export] ProgressBar _cooldownBar;
	[Export] TextureRect _icon;
	[Export] Control _ammoGroup;
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
		if (_item is WeaponState weapon && weapon.data is WeaponData weaponData && weaponData.useAmmo)
		{
			_ammoGroup.Visible = true;
			_ammoText.Text = weapon.ammo.ToString();
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
