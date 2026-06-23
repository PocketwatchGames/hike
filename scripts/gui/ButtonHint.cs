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
	// Used for both devices unless InputActionGamepad is set.
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

	// Optional gamepad-specific action. When set, takes precedence over
	// InputAction on gamepad. Use when the K&M and gamepad bindings live on
	// different actions (e.g. weapon attacks: AttackContextSensitive on K&M,
	// AttackMelee/AttackRanged on gamepad).
	string _inputActionGamepad = string.Empty;
	[Export]
	public string InputActionGamepad
	{
		get => _inputActionGamepad;
		set
		{
			_inputActionGamepad = value ?? string.Empty;
			UpdateButtonText();
		}
	}

	// Optional K&M-only modifier action whose glyph is prepended (joined with
	// "+") to InputAction's glyph. Use for chorded inputs like Aim+LMB. Not
	// applied on gamepad — gamepad uses InputActionGamepad as the full glyph.
	string _inputActionModifier = string.Empty;
	[Export]
	public string InputActionModifier
	{
		get => _inputActionModifier;
		set
		{
			_inputActionModifier = value ?? string.Empty;
			UpdateButtonText();
		}
	}

	// Optional literal glyph shown on keyboard/mouse instead of resolving
	// InputAction. Use when the K&M control is a RANGE of keys that no single
	// action glyph expresses — e.g. the consumable hotbar's "1-4". Ignored on
	// gamepad, which still resolves its own glyph from the input action.
	string _glyphOverrideKeyboard = string.Empty;
	[Export]
	public string GlyphOverrideKeyboard
	{
		get => _glyphOverrideKeyboard;
		set
		{
			_glyphOverrideKeyboard = value ?? string.Empty;
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
		_inputActionGamepad = string.Empty;
		_inputActionModifier = string.Empty;
		_glyphOverrideKeyboard = string.Empty;
		ActionName = hint;
		UpdateButtonText();
	}

	public void SetHint(string inputAction, string inputActionGamepad, string inputActionModifier, string hint)
	{
		_inputAction = inputAction ?? string.Empty;
		_inputActionGamepad = inputActionGamepad ?? string.Empty;
		_inputActionModifier = inputActionModifier ?? string.Empty;
		_glyphOverrideKeyboard = string.Empty;
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
		_buttonText.Text = ResolveGlyph(InputDevice.Current);
	}

	string ResolveGlyph(InputDevice.EDevice device)
	{
		if (device == InputDevice.EDevice.KeyboardMouse && !string.IsNullOrEmpty(_glyphOverrideKeyboard))
		{
			return _glyphOverrideKeyboard;
		}
		if (device == InputDevice.EDevice.Gamepad && !string.IsNullOrEmpty(_inputActionGamepad))
		{
			return InputGlyph.Resolve(_inputActionGamepad, device);
		}
		string main = InputGlyph.Resolve(_inputAction, device);
		if (device == InputDevice.EDevice.KeyboardMouse && !string.IsNullOrEmpty(_inputActionModifier))
		{
			string mod = InputGlyph.Resolve(_inputActionModifier, device);
			if (!string.IsNullOrEmpty(mod))
			{
				return mod + "+" + main;
			}
		}
		return main;
	}
}
