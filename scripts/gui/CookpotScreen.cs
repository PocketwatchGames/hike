using Godot;
using System;

// A lit cookpot's screen: what is in the pot, and two buttons.
//   Eat [meal] — the member who opened the pot eats from it (MealData.Eat).
//                Eating never empties the pot; only time does. Hovering or
//                focusing it shows the meal's effects (MealInfoPanel).
//   Cook       — opens the CookingScreen over this pot; cooking a meal, or
//                backing out, returns here.
// ui_cancel closes the screen.
[GlobalClass]
public partial class CookpotScreen : Control
{
	[Export] private CookingScreen _cookingScreen;
	[Export] private Label _contentsLabel;
	[Export] private Button _eatButton;
	[Export] private Button _cookButton;
	// Shown while the Eat button is hovered or focused.
	[Export] private MealInfoPanel _mealInfoPanel;

	Player _player;
	Cookpot _pot;
	Action _onClose;
	// The meal the buttons were last built for, so a meal going off while the
	// screen is up repaints it.
	MealData _shownMeal;

	public override void _Ready()
	{
		Visible = false;
		if (_eatButton != null)
		{
			_eatButton.Pressed += OnEat;
			_eatButton.FocusEntered += ShowMealInfo;
			_eatButton.MouseEntered += _eatButton.GrabFocus;
			_eatButton.FocusExited += HideMealInfo;
		}
		if (_cookButton != null)
		{
			_cookButton.Pressed += OnCook;
			_cookButton.MouseEntered += _cookButton.GrabFocus;
		}
		_mealInfoPanel?.SetMeal(null);
	}

	public override void _ExitTree()
	{
		if (_eatButton != null)
		{
			_eatButton.Pressed -= OnEat;
			_eatButton.FocusEntered -= ShowMealInfo;
			_eatButton.FocusExited -= HideMealInfo;
		}
		if (_cookButton != null)
		{
			_cookButton.Pressed -= OnCook;
		}
	}

	// The opener owns the global gating (input, HUD, mouse).
	public void Open(Player player, Cookpot pot, Action onClose = null)
	{
		_player = player;
		_pot = pot;
		_onClose = onClose;
		Visible = true;
		Refresh();
		Callable.From(ApplyInitialFocus).CallDeferred();
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		Visible = false;
		_mealInfoPanel?.SetMeal(null);
		_player = null;
		_pot = null;
		_shownMeal = null;
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	// Eat when there is something to eat, else Cook. GrabFocus needs the button
	// visible-in-tree, hence deferred from Open.
	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		Button target = _eatButton != null && !_eatButton.Disabled ? _eatButton : _cookButton;
		target?.GrabFocus();
	}

	void Refresh()
	{
		MealData meal = _pot?.Meal;
		_shownMeal = meal;
		if (_contentsLabel != null)
		{
			_contentsLabel.Text = meal != null
				? Loc.Format(Loc.Keys.cookpot_contents, meal.displayName.ToString())
				: Loc.Get(Loc.Keys.cookpot_empty);
		}
		if (_eatButton != null)
		{
			_eatButton.Text = meal != null
				? Loc.Format(Loc.Keys.cookpot_eat, meal.displayName.ToString())
				: Loc.Get(Loc.Keys.cookpot_eat_nothing);
			_eatButton.Disabled = meal == null;
			if (meal == null && _eatButton.HasFocus())
			{
				_cookButton?.GrabFocus();
			}
		}
		if (_cookButton != null)
		{
			_cookButton.Text = Loc.Get(Loc.Keys.station_cook);
		}
		if (_eatButton != null && _eatButton.HasFocus())
		{
			ShowMealInfo();
		}
		else
		{
			HideMealInfo();
		}
	}

	void ShowMealInfo()
	{
		_mealInfoPanel?.SetMeal(_pot?.Meal);
	}

	void HideMealInfo()
	{
		_mealInfoPanel?.SetMeal(null);
	}

	void OnEat()
	{
		MealData meal = _pot?.Meal;
		if (meal == null || _player == null)
		{
			return;
		}
		meal.Eat(_player);
		GameClient.Current?.onHudText?.Invoke(_player.GlobalPosition, Loc.Format(Loc.Keys.cookpot_ate, meal.displayName.ToString()), EHudTextType.Info);
	}

	// The cooking screen takes over; backing out of it lands back here.
	void OnCook()
	{
		if (_cookingScreen == null || _pot == null || _player == null)
		{
			return;
		}
		Visible = false;
		_cookingScreen.Open(_player, _pot, onClose: ReturnFromCooking);
	}

	// Lands on Eat when the pot holds a meal (always, after a cook), else Cook.
	void ReturnFromCooking()
	{
		Visible = true;
		Refresh();
		Callable.From(ApplyInitialFocus).CallDeferred();
	}

	public override void _Process(double delta)
	{
		if (Visible && _pot != null && _pot.Meal != _shownMeal)
		{
			Refresh();
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (Visible && e.IsActionPressed("ui_cancel"))
		{
			Close();
			GetViewport().SetInputAsHandled();
		}
	}
}
