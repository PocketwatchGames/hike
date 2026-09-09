using Godot;

// One brush entry in the editor's tool palette: display name plus the icon
// shown on its button. Icon may be null (entity brushes, and voxel types with
// no atlas tile such as Barrier) — the button falls back to its name label.
public readonly struct EditorBrushEntry
{
    public readonly string Name;
    public readonly Texture2D Icon;
    // Which entity palette tab the button belongs in, by NAME — the section its
    // palette root declared (AuthoringPaletteSource.PaletteRoot.Section), or a
    // prop category. A name rather than an enum because the tab strip is built
    // from whatever sections turn up, so adding a palette root is a line in one
    // table and not a scene edit. Ignored by voxel brushes, which share one grid.
    public readonly string Section;

    public EditorBrushEntry(string name, Texture2D icon, string section = "")
    {
        Name = name;
        Icon = icon;
        Section = section;
    }
}

// A single toggle button in the editor's tool palette (scenes/gui/
// editor_tool_button.tscn), instanced once per brush. The name label only
// shows when there's no icon to show instead; the tooltip always carries it.
[GlobalClass]
public partial class EditorToolButton : TextureButton
{
    [Export] public Label nameLabel;

    public void Bind(EditorBrushEntry entry)
    {
        TextureNormal = entry.Icon;
        TooltipText = entry.Name;
        if (nameLabel != null)
        {
            nameLabel.Text = entry.Name;
            nameLabel.Visible = entry.Icon == null;
        }
    }
}
