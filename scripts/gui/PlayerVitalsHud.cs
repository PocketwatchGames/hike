using System.Collections.Generic;
using Godot;

// The bound party member's vitals: health (with its drained and bonus extensions),
// armor, block guard, the parry icon, lantern oil, stamina pips and the needs
// meters. One scene shared by the HUD and the inventory / stash screens, so every
// view of a member reads the same way. Ticks itself while bound and visible.
[GlobalClass]
public partial class PlayerVitalsHud : VBoxContainer
{
	// Value readouts on the health, armor, guard, oil and stamina rows —
	// on for the inventory screen's instance, off where the bars are glanced at
	// in play.
	[Export] bool _showValues;
	[Export] Label _healthValueLabel;
	[Export] Label _armorValueLabel;
	[Export] Label _blockArmorValueLabel;
	[Export] Label _oilValueLabel;
	[Export] Label _staminaValueLabel;
	[Export] ProgressBar _healthBar;
	[Export] TextureRect _parryIcon;
	// Layered UNDERNEATH _healthBar (placed earlier in the scene tree) with
	// a transparent background and value = (Health + DrainedHealth) / MaxHealth,
	// so the dark-red fill spans [0, Health + DrainedHealth] and the bright
	// health fill paints over [0, Health] on top — leaving only the
	// [Health, Health + DrainedHealth] tail visible as an apparent extension
	// past current HP. Player keeps the invariant
	// `Health + DrainedHealth <= MaxHealth` — Heal forgives any drain it
	// climbs into, and TickBloodDrain shrinks drain while growing health in
	// lockstep — so the visible tail shrinks cleanly as drain heals back.
	[Export] ProgressBar _drainedHealthBar;
	// Health past MaxHealth (Player.BonusHealth), a full bar whose length is the
	// bonus at the health scale. Hidden while there is none.
	[Export] ProgressBar _bonusHealthBar;
	[Export] ProgressBar _armorBar;
	// The melee weapon's block-guard pool, drawn as its own row above the
	// health/armor row. Tinted dark grey while dormant, dark blue while
	// sneaking (the only state in which block armor actually absorbs), and
	// bright blue while the parry window is open.
	[Export] ProgressBar _blockArmorBar;
	// The member's lantern oil, out of their MaxLanternOil (the lantern plus oil
	// flasks, so the bar lengthens as flasks are carried).
	[Export] ProgressBar _oilBar;
	[Export] Control _staminaContainer;
	[Export] PackedScene _staminaBarScene;
	// The member's needs meters, each a glyph (over) on a disc fill (progress)
	// whose level falls top to bottom, on a translucent disc (under). The fill
	// drains through each phase (Player.GetNeedPhaseRemaining); the last phase has
	// no end, so it shows full and the glyph flashes instead.
	[Export] TextureProgressBar _hungerMeter;
	[Export] TextureProgressBar _thirstMeter;
	[Export] TextureProgressBar _fatigueMeter;
	[Export] Color _needBackgroundColor = new Color(0f, 0f, 0f, 0.5f);
	[Export] Color _needFillColor = new Color(0.5f, 0.5f, 0.5f, 1f);
	// Glyph colour per phase (Player.GetNeedPhase).
	[Export] Color[] _needPhaseColors = { Colors.White, new Color(1f, 0.85f, 0.1f, 1f), new Color(1f, 0.1f, 0.1f, 1f) };
	// Last-phase glyph flash: seconds per on/off cycle, and the alpha it dips to.
	[Export] float _needFlashPeriod = 0.8f;
	[Export(PropertyHint.Range, "0,1,0.05")] float _needFlashMinAlpha = 0.2f;

	// Parry icon states. Hidden while no parry can land (weapon can't parry, or
	// the guard is on its recharge cooldown); a dim grey while a parry is
	// available; a brighter near-white while the parry window is actually open,
	// so the window's opening reads as a clear "now" flash. The icon's texture is
	// light, so these modulate colors carry both the grey/white and the alpha.
	[Export] Color _parryAvailableColor = new Color(0.6f, 0.6f, 0.6f, 0.5f);
	[Export] Color _parryActiveColor = new Color(1f, 1f, 1f, 0.85f);
	// Tints for the block-guard bar. The fill stylebox is white so these
	// multiply straight through: grey while the guard is dormant, saturated
	// blue while the player is sneaking and the pool is live.
	[Export] Color _blockArmorIdleColor = new Color(0.45f, 0.45f, 0.45f, 1f);
	[Export] Color _blockArmorActiveColor = new Color(0.2f, 0.5f, 1f, 1f);

	// Horizontal scale of the vitals bars: pixels of bar width per point of
	// the stat, so a bigger max reads as a physically longer bar (a health
	// buff or an armor equip visibly grows it). Health and armor share the
	// same 1000-points-per-full-bar calibration (PlayerData.maxHealth defaults
	// to 1000, the same damage units armor absorbs in), but stay separately
	// tunable since armor chips at (1 + blunt) per damage point. Stamina needs
	// no scale here: its pip row is already one pip per unit.
	// Defaults: 1000 points = 250px.
	[Export(PropertyHint.Range, "0.01,5,0.01")] float _pixelsPerHealthPoint = 0.25f;
	[Export(PropertyHint.Range, "0.01,5,0.01")] float _pixelsPerArmorPoint = 0.25f;
	// Oil's own scale (a default supply is 100): 100 oil = 250px.
	[Export(PropertyHint.Range, "0.01,10,0.01")] float _pixelsPerOilPoint = 2.5f;

	// One pip per unit of max stamina (1 unit = 1 dash), instanced from
	// _staminaBarScene into _staminaContainer. Filled left to right, so the
	// recharging unit reads as a partial fill on the first non-full pip.
	readonly List<ProgressBar> _staminaBars = new();
	// Full-unit pip size, read from the scene so the authored width stays the
	// source of truth; a fractional last pip is scaled down from it.
	Vector2 _staminaPipSize;
	// Float noise in MaxStamina (3.0000001) must not add a sliver pip.
	const float STAMINA_PIP_EPSILON = 0.01f;

	Player _player;

	public override void _Ready()
	{
		SetVisible(_healthValueLabel, _showValues);
		SetVisible(_armorValueLabel, _showValues);
		SetVisible(_blockArmorValueLabel, _showValues);
		SetVisible(_oilValueLabel, _showValues);
		SetVisible(_staminaValueLabel, _showValues);
	}

	// Null unbinds: the widget stops ticking and keeps its last frame.
	public void Bind(Player player)
	{
		_player = player;
	}

	public override void _Process(double delta)
	{
		if (_player == null || !IsInstanceValid(_player) || !IsVisibleInTree())
		{
			return;
		}

		float maxHealth = _player.MaxHealth;
		_healthBar.MinValue = 0;
		_healthBar.MaxValue = 1;
		_healthBar.Value = maxHealth > 0f ? _player.Health / maxHealth : 0f;
		// Bar length tracks the stat. The drained underlay spans the same
		// 0..MaxHealth range as the health bar, so it always shares its width.
		SetBarWidth(_healthBar, maxHealth * _pixelsPerHealthPoint);
		SetBarWidth(_drainedHealthBar, maxHealth * _pixelsPerHealthPoint);
		SetValueText(_healthValueLabel, Mathf.CeilToInt(_player.Health) + "/" + FormatValue(maxHealth));
		UpdateBonusBar(_bonusHealthBar, _player.BonusHealth, _pixelsPerHealthPoint);

		if (_drainedHealthBar != null)
		{
			float drained = _player.DrainedHealth;
			_drainedHealthBar.MinValue = 0;
			_drainedHealthBar.MaxValue = 1;
			_drainedHealthBar.Visible = drained > 0f;
			_drainedHealthBar.Value = maxHealth > 0f ? (_player.Health + drained) / maxHealth : 0f;
		}

		if (_oilBar != null)
		{
			_oilBar.MinValue = 0;
			_oilBar.MaxValue = _player.MaxLanternOil;
			_oilBar.Value = _player.LanternOil;
			SetBarWidth(_oilBar, _player.MaxLanternOil * _pixelsPerOilPoint);
			SetValueText(_oilValueLabel, Mathf.CeilToInt(_player.LanternOil) + "/" + FormatValue(_player.MaxLanternOil));
		}
		UpdateNeedMeter(_hungerMeter, ENeed.Hunger);
		UpdateNeedMeter(_thirstMeter, ENeed.Thirst);
		UpdateNeedMeter(_fatigueMeter, ENeed.Fatigue);

		float maxArmor = _player.MaxArmor;
		_armorBar.MinValue = 0;
		_armorBar.MaxValue = 1;
		_armorBar.Visible = maxArmor > 0f;
		_armorBar.Value = maxArmor > 0f ? _player.Armor / maxArmor : 0f;
		SetBarWidth(_armorBar, maxArmor * _pixelsPerArmorPoint);
		SetValueText(_armorValueLabel, FormatValue(maxArmor));

		UpdateBlockArmorBar();
		UpdateParryIcon();
		UpdateStaminaPips();
	}

	static void UpdateBonusBar(ProgressBar bar, float bonus, float pixelsPerPoint)
	{
		if (bar == null)
		{
			return;
		}
		bool show = bonus > 0f;
		bar.Visible = show;
		if (show)
		{
			bar.MinValue = 0;
			bar.MaxValue = 1;
			bar.Value = 1;
			SetBarWidth(bar, bonus * pixelsPerPoint);
		}
	}

	void UpdateNeedMeter(TextureProgressBar meter, ENeed need)
	{
		if (meter == null)
		{
			return;
		}
		meter.MaxValue = 1.0;
		meter.Value = _player.GetNeedPhaseRemaining(need);
		meter.TintUnder = _needBackgroundColor;
		meter.TintProgress = _needFillColor;
		int phase = _player.GetNeedPhase(need);
		if (_needPhaseColors == null || _needPhaseColors.Length == 0)
		{
			return;
		}
		Color color = _needPhaseColors[Mathf.Min(phase, _needPhaseColors.Length - 1)];
		if (phase >= Player.NeedLastPhase && _needFlashPeriod > 0f)
		{
			// Presentational, so wall clock: it keeps flashing through slow-mo.
			float t = (Time.GetTicksMsec() / 1000f) / _needFlashPeriod;
			float pulse = 0.5f + 0.5f * Mathf.Cos(t * Mathf.Tau);
			color.A *= Mathf.Lerp(_needFlashMinAlpha, 1f, pulse);
		}
		meter.TintOver = color;
	}

	// Health and oil read "current/max" (current rounded up, so a sliver left
	// reads as 1); stamina, armor and guard read just their max.
	void SetValueText(Label label, string text)
	{
		if (!_showValues || label == null)
		{
			return;
		}
		if (label.Text != text)
		{
			label.Text = text;
		}
	}

	// Up to one decimal place: "4", "4.5".
	static string FormatValue(float value)
	{
		return value.ToString("0.#");
	}

	static void SetVisible(CanvasItem item, bool visible)
	{
		if (item != null)
		{
			item.Visible = visible;
		}
	}

	// Sets a bar's minimum width in pixels (stat × pixels-per-point). Works for
	// both layouts in play: the container-driven health/drained pair (min size
	// feeds the MarginContainer) and the free-floating armor bars (a Control's
	// size is clamped up to its minimum; their authored offsets are 0).
	static void SetBarWidth(ProgressBar bar, float width)
	{
		if (bar == null)
		{
			return;
		}
		Vector2 size = bar.CustomMinimumSize;
		if (!Mathf.IsEqualApprox(size.X, width))
		{
			size.X = width;
			bar.CustomMinimumSize = size;
		}
	}

	// Sync the pip row against MaxStamina and fill pips left to right from current
	// stamina — full pips first, then the fractional remainder on the next pip. A
	// fractional max adds a last pip narrowed to that fraction of a unit, filling
	// over its own smaller capacity. Negative stamina (dash overdraw) just reads as
	// an empty row.
	void UpdateStaminaPips()
	{
		if (_staminaContainer == null || _staminaBarScene == null)
		{
			return;
		}
		float maxStamina = Mathf.Max(0f, _player.MaxStamina);
		int units = Mathf.CeilToInt(maxStamina - STAMINA_PIP_EPSILON);
		while (_staminaBars.Count < units)
		{
			ProgressBar bar = _staminaBarScene.Instantiate<ProgressBar>();
			if (_staminaBars.Count == 0)
			{
				_staminaPipSize = bar.CustomMinimumSize;
			}
			_staminaContainer.AddChild(bar);
			_staminaBars.Add(bar);
		}
		while (_staminaBars.Count > units)
		{
			ProgressBar bar = _staminaBars[^1];
			_staminaBars.RemoveAt(_staminaBars.Count - 1);
			bar.QueueFree();
		}
		_staminaContainer.Visible = units > 0;
		float stamina = _player.Stamina;
		SetValueText(_staminaValueLabel, FormatValue(maxStamina));
		for (int i = 0; i < _staminaBars.Count; i++)
		{
			ProgressBar bar = _staminaBars[i];
			float capacity = Mathf.Clamp(maxStamina - i, 0f, 1f);
			Vector2 size = new Vector2(_staminaPipSize.X * capacity, _staminaPipSize.Y);
			if (bar.CustomMinimumSize != size)
			{
				bar.CustomMinimumSize = size;
			}
			bar.MaxValue = capacity;
			bar.Value = Mathf.Clamp(stamina - i, 0f, capacity);
		}
	}

	// Drive the block-guard bar: the guard pool — live (blue) while the player
	// sneaks, a dormant grey reserve otherwise. Hidden when the guard has no pool
	// (a parry-only shield). Sized on the armor bar's pixels-per-point scale so
	// guard and armor capacities read in the same visual units.
	void UpdateBlockArmorBar()
	{
		if (_blockArmorBar == null)
		{
			return;
		}
		ShieldState shield = _player.GetShieldOrUnarmed();
		// A broken shield has no guard to show.
		float capacity = shield == null || shield.IsBroken ? 0f : shield.data?.guardArmor ?? 0f;
		if (capacity <= 0f)
		{
			_blockArmorBar.Visible = false;
			return;
		}
		_blockArmorBar.Visible = true;
		_blockArmorBar.MinValue = 0;
		_blockArmorBar.MaxValue = 1;
		_blockArmorBar.Value = shield.guard / capacity;
		SetBarWidth(_blockArmorBar, capacity * _pixelsPerArmorPoint);
		SetValueText(_blockArmorValueLabel, FormatValue(capacity));
		_blockArmorBar.Modulate = _player.IsSneaking ? _blockArmorActiveColor : _blockArmorIdleColor;
	}

	// Drive the parry icon: hidden when no parry can land, dim grey while a parry
	// is available (guard ready, shield can parry), brighter near-white while the
	// parry window is actually open.
	void UpdateParryIcon()
	{
		if (_parryIcon == null)
		{
			return;
		}
		if (_player.IsParryWindowActive)
		{
			_parryIcon.Visible = true;
			_parryIcon.Modulate = _parryActiveColor;
		}
		else if (_player.IsParryReady)
		{
			_parryIcon.Visible = true;
			_parryIcon.Modulate = _parryAvailableColor;
		}
		else
		{
			_parryIcon.Visible = false;
		}
	}
}
