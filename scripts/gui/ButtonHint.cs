using Godot;

[Tool]
[GlobalClass]
public partial class ButtonHint : BoxContainer
{
	[Export] Label _buttonText;
	[Export] Control _label;
	[Export] Label _labelText;
	[Export] ProgressBar _progressBar;

	string _actionName = string.Empty;
	[Export]
	public string ActionName
	{
		get => _actionName;
		set
		{
			_actionName = value ?? string.Empty;
			ApplyActionName();
		}
	}

	string _gamepadButton = string.Empty;
	[Export]
	public string GamepadButton
	{
		get => _gamepadButton;
		set
		{
			_gamepadButton = value ?? string.Empty;
			UpdateButtonText();
		}
	}

	string _keyboardButton = string.Empty;
	[Export]
	public string KeyboardButton
	{
		get => _keyboardButton;
		set
		{
			_keyboardButton = value ?? string.Empty;
			UpdateButtonText();
		}
	}

	public override void _Ready()
	{
		ApplyActionName();
		if (Engine.IsEditorHint())
		{
			return;
		}
		InputDevice.OnChanged += OnInputDeviceChanged;
		UpdateButtonText();
	}

	public override void _ExitTree()
	{
		if (Engine.IsEditorHint())
		{
			return;
		}
		InputDevice.OnChanged -= OnInputDeviceChanged;
	}

	public void SetHint(string gamepadButton, string keyboardButton, string hint)
	{
		_gamepadButton = gamepadButton ?? string.Empty;
		_keyboardButton = keyboardButton ?? string.Empty;

		ActionName = hint;

		UpdateButtonText();
	}

	public void SetProgress(float value)
	{
		float clamped = Mathf.Clamp(value, 0f, 1f);
		_progressBar.MinValue = 0;
		_progressBar.MaxValue = 1;
		_progressBar.Value = clamped;
		_progressBar.Visible = clamped > 0f;
	}

	void ApplyActionName()
	{
		if (_labelText == null || _label == null)
		{
			return;
		}
		bool hasHint = !string.IsNullOrEmpty(_actionName);
		_labelText.Text = hasHint ? _actionName : string.Empty;
		_label.Visible = hasHint;
	}

	void OnInputDeviceChanged(InputDevice.EDevice device)
	{
		UpdateButtonText();
	}

	void UpdateButtonText()
	{
		_buttonText.Text = InputDevice.Current == InputDevice.EDevice.Gamepad
			? _gamepadButton
			: _keyboardButton;
	}
}
