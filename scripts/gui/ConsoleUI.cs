using Godot;
using System;
using System.Collections.Generic;

public partial class ConsoleUI : CanvasLayer
{
	private const int ConsoleLayer = 100;
	private const float ConsoleHeightRatio = 0.4f;

	private PanelContainer _panel;
	private RichTextLabel _output;
	private LineEdit _input;

	private string _completionPrefix;
	private List<string> _completionMatches;
	private int _completionIndex;
	private bool _isTabCompleting;

	private List<string> _history = new List<string>();
	private int _historyIndex;

	private static ConsoleUI _instance;

	public static bool IsOpen => _instance != null && _instance.Visible;

	public override void _Ready()
	{
		Layer = ConsoleLayer;
		Visible = false;
		BuildUI();
	}

	public override void _EnterTree()
	{
		_instance = this;
	}

	public override void _ExitTree()
	{
		if (_instance == this)
		{
			_instance = null;
		}
	}

	private void BuildUI()
	{
		_panel = new PanelContainer();
		_panel.AnchorRight = 1.0f;
		_panel.AnchorBottom = ConsoleHeightRatio;

		var style = new StyleBoxFlat();
		style.BgColor = new Color(0.05f, 0.05f, 0.1f, 0.85f);
		style.ContentMarginLeft = 8;
		style.ContentMarginRight = 8;
		style.ContentMarginTop = 8;
		style.ContentMarginBottom = 8;
		_panel.AddThemeStyleboxOverride("panel", style);

		var vbox = new VBoxContainer();
		vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_panel.AddChild(vbox);

		_output = new RichTextLabel();
		_output.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
		_output.ScrollFollowing = true;
		_output.BbcodeEnabled = true;
		_output.SelectionEnabled = true;
		vbox.AddChild(_output);

		_input = new LineEdit();
		_input.PlaceholderText = "Enter command...";
		_input.TextSubmitted += OnTextSubmitted;
		_input.TextChanged += OnTextChanged;
		vbox.AddChild(_input);

		AddChild(_panel);
	}

	public override void _Input(InputEvent e)
	{
		if (e.IsActionPressed("ToggleConsole"))
		{
			Visible = !Visible;
			if (Visible)
			{
				_input.GrabFocus();
				_input.Clear();
			}

			GetViewport().SetInputAsHandled();
			return;
		}

		if (!Visible)
		{
			return;
		}

		if (e is InputEventKey keyEvent && keyEvent.Pressed)
		{
			if (keyEvent.Keycode == Key.Tab)
			{
				HandleTabCompletion();
				GetViewport().SetInputAsHandled();
				return;
			}

			if (keyEvent.Keycode == Key.Up)
			{
				NavigateHistory(-1);
				GetViewport().SetInputAsHandled();
				return;
			}

			if (keyEvent.Keycode == Key.Down)
			{
				NavigateHistory(1);
				GetViewport().SetInputAsHandled();
				return;
			}
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (Visible)
		{
			GetViewport().SetInputAsHandled();
		}
	}

	private void OnTextSubmitted(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			_input.Clear();
			return;
		}

		_history.Add(text);
		_historyIndex = _history.Count;

		AppendOutput($"> {text}");

		string result = CVarRegistry.ProcessCommand(text);
		if (!string.IsNullOrEmpty(result))
		{
			AppendOutput(result);
		}

		_input.Clear();
		ResetCompletion();
	}

	private void OnTextChanged(string newText)
	{
		if (!_isTabCompleting)
		{
			ResetCompletion();
		}
	}

	private void HandleTabCompletion()
	{
		string currentText = _input.Text;

		if (_completionMatches == null || _completionPrefix == null)
		{
			_completionPrefix = currentText;
			_completionMatches = CVarRegistry.GetCompletions(_completionPrefix);
			_completionIndex = 0;
		}
		else
		{
			_completionIndex = (_completionIndex + 1) % _completionMatches.Count;
		}

		if (_completionMatches.Count > 0)
		{
			_isTabCompleting = true;
			_input.Text = _completionMatches[_completionIndex];
			_input.CaretColumn = _input.Text.Length;
			_isTabCompleting = false;
		}
	}

	private void ResetCompletion()
	{
		_completionPrefix = null;
		_completionMatches = null;
		_completionIndex = 0;
	}

	private void NavigateHistory(int direction)
	{
		if (_history.Count == 0)
		{
			return;
		}

		_historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
		if (_historyIndex < _history.Count)
		{
			_isTabCompleting = true;
			_input.Text = _history[_historyIndex];
			_input.CaretColumn = _input.Text.Length;
			_isTabCompleting = false;
		}
		else
		{
			_input.Clear();
		}
	}

	public void AppendOutput(string text)
	{
		_output.AppendText(text + "\n");
	}

	public static void Print(string text)
	{
		_instance?.AppendOutput(text);
	}
}
