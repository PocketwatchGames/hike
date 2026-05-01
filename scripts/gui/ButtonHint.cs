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

	// Input action whose first matching key/button glyph drives the button
	// label. Resolved from InputMap so rebinding takes effect without code.
	string _inputAction = string.Empty;
	[Export]
	public string InputAction
	{
		get => _inputAction;
		set
		{
			_inputAction = value ?? string.Empty;
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

	public void SetHint(string inputAction, string hint)
	{
		_inputAction = inputAction ?? string.Empty;
		ActionName = hint;
		UpdateButtonText();
	}

	public void SetProgress(float value)
	{
		if (_progressBar == null)
		{
			return;
		}
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
		if (_buttonText == null)
		{
			return;
		}
		_buttonText.Text = InputGlyph.Resolve(_inputAction, InputDevice.Current);
	}
}
