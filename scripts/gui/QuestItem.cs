using Godot;

// One quest's HUD row. Pure view: bound to a runtime QuestState by the Hud,
// which drives Refresh each frame so counters / countdowns stay live. The quest
// lifecycle itself is sim-driven (World.Quests) — this only renders it.
public partial class QuestItem : HBoxContainer
{
    [Export] Label _questLabel;
    [Export] TextureRect _icon;

    // Set the (optional) icon once and render the first frame.
    public void Bind(QuestState quest)
    {
        if (_icon != null && quest?.Data?.icon != null)
        {
            _icon.Texture = quest.Data.icon;
        }
        Refresh(quest != null ? quest.GetDisplay(0) : default);
    }

    public void Refresh(QuestDisplay display)
    {
        if (_questLabel != null)
        {
            _questLabel.Text = display.Text ?? "";
        }
    }
}
