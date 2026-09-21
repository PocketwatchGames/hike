using Godot;
using System;

// One save slot on the profile screen: the world a save plays, or "New Game"
// for an empty slot. The whole panel is one button; Delete only shows on a
// slot that holds a save, and takes a second press to confirm.
[GlobalClass]
public partial class ProfilePanel : PanelContainer
{
	[Export] Button _selectButton;
	[Export] Label _nameLabel;
	[Export] Button _deleteButton;

	public event Action Selected;
	public event Action DeleteConfirmed;

	public string SavePath { get; private set; }
	public bool HasSave { get; private set; }

	bool _confirmingDelete;

	public override void _Ready()
	{
		if (_selectButton != null)
		{
			_selectButton.Pressed += () => Selected?.Invoke();
			_selectButton.FocusExited += CancelDelete;
		}
		if (_deleteButton != null)
		{
			_deleteButton.Pressed += RequestDelete;
			_deleteButton.FocusExited += CancelDelete;
		}
	}

	// worldName is null for a slot whose file exists but can't be read by this
	// build — it can still be deleted, but not played.
	public void Fill(string savePath, bool hasSave, string worldName)
	{
		SavePath = savePath;
		HasSave = hasSave;
		if (_nameLabel != null)
		{
			_nameLabel.Text = !hasSave ? Loc.Get(Loc.Keys.profile_new_game)
				: worldName ?? Loc.Get(Loc.Keys.profile_unreadable);
		}
		if (_selectButton != null)
		{
			_selectButton.Disabled = hasSave && worldName == null;
		}
		if (_deleteButton != null)
		{
			_deleteButton.Visible = hasSave;
		}
		CancelDelete();
	}

	public void GrabSelectFocus()
	{
		_selectButton?.GrabFocus();
	}

	public bool HasFocusWithin()
	{
		return (_selectButton?.HasFocus() ?? false) || (_deleteButton?.HasFocus() ?? false);
	}

	// First press arms, second press deletes: a save is the whole run.
	public void RequestDelete()
	{
		if (!HasSave)
		{
			return;
		}
		if (!_confirmingDelete)
		{
			_confirmingDelete = true;
			if (_deleteButton != null)
			{
				_deleteButton.Text = Loc.Get(Loc.Keys.profile_delete_confirm);
			}
			return;
		}
		_confirmingDelete = false;
		DeleteConfirmed?.Invoke();
	}

	void CancelDelete()
	{
		_confirmingDelete = false;
		if (_deleteButton != null)
		{
			_deleteButton.Text = Loc.Get(Loc.Keys.profile_delete);
		}
	}
}
