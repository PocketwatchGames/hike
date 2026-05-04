#if TOOLS

using Godot;
using System;

// Compact dropdown replacement for Godot's default flags-grid editor.
// Hint string format matches Godot's PropertyHint.Flags: comma-separated
// "Name" entries (one per consecutive bit) or "Name:value" entries with
// explicit bit values.
[Tool]
public partial class FlagsPropertyEditor : EditorProperty
{
	private MenuButton _button;
	private PopupMenu _menu;
	private int _currentValue;
	private string[] _names;
	private int[] _flags;
	private bool _updating;

	public FlagsPropertyEditor(string hintString)
	{
		_button = new MenuButton { Alignment = HorizontalAlignment.Left, ClipText = true };
		AddChild(_button);
		AddFocusable(_button);

		_menu = _button.GetPopup();
		// IdPressed (not IndexPressed) so the menu hands us the bit value we
		// stamped onto each item — avoids an index→bit indirection that breaks
		// if menu items are ever reordered.
		_menu.IdPressed += MenuIdPressed;
		_menu.HideOnCheckableItemSelection = false;

		var options = hintString.Split(',', StringSplitOptions.RemoveEmptyEntries);
		_names = new string[options.Length];
		_flags = new int[options.Length];
		for (int i = 0; i < options.Length; i++)
		{
			var ss = options[i].Split(':', StringSplitOptions.RemoveEmptyEntries);
			_names[i] = ss[0];
			_flags[i] = 1 << i;
			if (ss.Length > 1 && int.TryParse(ss[1], out int j))
			{
				_flags[i] = j;
			}
			_menu.AddCheckItem(ss[0], _flags[i]);
		}
	}

	private void MenuIdPressed(long id)
	{
		if (_updating) { return; }
		int bit = (int)id;

		if ((_currentValue & bit) == bit)
		{
			_currentValue &= ~bit;
		}
		else
		{
			_currentValue |= bit;
		}

		// EmitChanged is the canonical EditorProperty path — it tells the
		// inspector to push the value into the object and refresh dependents,
		// without us calling Set ourselves (which would re-enter through the
		// property setter mid-callback).
		EmitChanged(GetEditedProperty(), _currentValue);
		UpdateButtonText();
	}

	public override void _UpdateProperty()
	{
		_updating = true;

		var property = GetEditedProperty();
		_currentValue = GetEditedObject().Get(property).AsInt32();

		UpdateButtonText();

		_updating = false;
	}

	private void UpdateButtonText()
	{
		var text = string.Empty;
		for (int i = 0; i < _flags.Length; i++)
		{
			if ((_currentValue & _flags[i]) == _flags[i])
			{
				if (text.Length > 0) { text += ", "; }
				text += _names[i];
				_menu.SetItemChecked(i, true);
			}
			else
			{
				_menu.SetItemChecked(i, false);
			}
		}

		_button.Text = text;
	}
}

#endif
