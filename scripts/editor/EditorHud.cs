using Godot;

[GlobalClass]
public partial class EditorHud : CanvasLayer
{
    [Export] public Label modeLabel;
    [Export] public Label typeLabel;
    [Export] public Label heightLabel;
    [Export] public Label clipLabel;
    [Export] public Label coordsLabel;
    [Export] public Label helpLabel;

    public override void _Ready()
    {
        if (helpLabel != null)
        {
            helpLabel.Text = "LMB: Place | Ctrl+LMB: Delete | Alt+LMB: Replace | Q/E: Cycle | 0-9: Paint Height | Tab: Mode | R/F: Up/Down | Z/C: Rotate | Ctrl+S: Save | Esc: Quit";
        }
    }

    public void UpdatePaintHeight(int height)
    {
        if (heightLabel != null)
        {
            heightLabel.Text = $"Paint Height: {height}";
        }
    }

    public void UpdateVoxelMode(string typeName, int index, int total)
    {
        if (modeLabel != null)
        {
            modeLabel.Text = "Mode: Voxel";
        }
        if (typeLabel != null)
        {
            typeLabel.Text = $"{typeName} [{index + 1}/{total}]";
        }
    }

    public void UpdateEntityMode(string entityName, int index, int total)
    {
        if (modeLabel != null)
        {
            modeLabel.Text = "Mode: Entity";
        }
        if (typeLabel != null)
        {
            typeLabel.Text = $"{entityName} [{index + 1}/{total}]";
        }
    }

    public void UpdateClip(float clipY)
    {
        if (clipLabel != null)
        {
            if (clipY >= float.PositiveInfinity)
            {
                clipLabel.Text = "Clip: None";
            }
            else
            {
                clipLabel.Text = $"Clip: Y={clipY:F0}";
            }
        }
    }

    public void UpdatePosition(Vector3 pos)
    {
        if (coordsLabel != null)
        {
            coordsLabel.Text = $"Pos: ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})";
        }
    }
}
