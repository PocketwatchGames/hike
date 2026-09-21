using Godot;
using System;

// The Play screen: a fixed row of save slots. A slot holding a save loads it;
// an empty one starts a new game whose autosaves land in that slot. A slot IS
// its save file — there is no profile record beside it.
[GlobalClass]
public partial class ProfileScreen : Control
{
	[Export] PackedScene profileScene;
	[Export] Container profileContainer;
	[Export(PropertyHint.Range, "1,8,1")] int slotCount = 4;
	// {0} is the slot index.
	[Export] string slotPathFormat = "user://profile_{0}.sav";
	[Export] StringName deleteAction = "MenuSecondary";

	// Chosen slot and whether it already holds a save.
	public event Action<string, bool> SlotChosen;
	public event Action Closed;

	public void Open()
	{
		Visible = true;
		Rebuild();
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		Visible = false;
		Closed?.Invoke();
	}

	// _Input rather than _UnhandledInput so a focused Button can't swallow the
	// delete action first.
	public override void _Input(InputEvent e)
	{
		if (!IsVisibleInTree())
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			Close();
			GetViewport().SetInputAsHandled();
		}
		else if (deleteAction != null && !deleteAction.IsEmpty && e.IsActionPressed(deleteAction))
		{
			ProfilePanel focused = FocusedPanel();
			if (focused != null)
			{
				focused.RequestDelete();
				GetViewport().SetInputAsHandled();
			}
		}
	}

	void Rebuild()
	{
		if (profileContainer == null || profileScene == null)
		{
			return;
		}
		foreach (Node child in profileContainer.GetChildren())
		{
			profileContainer.RemoveChild(child);
			child.QueueFree();
		}
		ProfilePanel first = null;
		for (int i = 0; i < slotCount; i++)
		{
			string path = string.Format(slotPathFormat, i);
			bool hasSave = SaveGame.Exists(path);
			ProfilePanel panel = profileScene.Instantiate<ProfilePanel>();
			profileContainer.AddChild(panel);
			panel.Fill(path, hasSave, hasSave ? SaveGame.ReadWorldName(path) : null);
			panel.Selected += () => SlotChosen?.Invoke(panel.SavePath, panel.HasSave);
			panel.DeleteConfirmed += () => DeleteSlot(panel);
			first ??= panel;
		}
		// Deferred: a freshly added panel isn't focusable until it's laid out.
		first?.CallDeferred(ProfilePanel.MethodName.GrabSelectFocus);
	}

	void DeleteSlot(ProfilePanel panel)
	{
		SaveGame.Delete(panel.SavePath);
		panel.Fill(panel.SavePath, false, null);
		panel.GrabSelectFocus();
	}

	ProfilePanel FocusedPanel()
	{
		foreach (Node child in profileContainer.GetChildren())
		{
			if (child is ProfilePanel panel && panel.HasFocusWithin())
			{
				return panel;
			}
		}
		return null;
	}
}
