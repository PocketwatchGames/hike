using Godot;

// Status HUD for the world-map painter. Exported labels assigned in the scene;
// the painter pushes the active tool's name, parameters, brush size, and the
// 2D/3D view state.
[GlobalClass]
public partial class WorldMapHud : CanvasLayer
{
    [Export] public Label viewLabel;
    [Export] public Label layerLabel;   // tool name
    [Export] public Label toolLabel;    // tool status + active level
    [Export] public Label radiusLabel;
    [Export] public Label coordsLabel;
    [Export] public Label helpLabel;

    public override void _Ready()
    {
        if (helpLabel != null)
        {
            helpLabel.Text = "LMB: Paint | RMB: Erase | Tab: Tool | Q/E: Param | R/F: Level | [ ]: Brush | Ctrl+S: Save | Esc: Quit";
        }
    }

    public void SetView(bool preview)
    {
        if (viewLabel != null)
        {
            viewLabel.Text = preview ? "View: 3D Preview" : "View: 2D Map";
        }
    }

    public void SetTool(string name)
    {
        if (layerLabel != null)
        {
            layerLabel.Text = $"Tool: {name}";
        }
    }

    public void SetStatus(string status)
    {
        if (toolLabel != null)
        {
            toolLabel.Text = status;
        }
    }

    public void SetRadius(float radius)
    {
        if (radiusLabel != null)
        {
            radiusLabel.Text = $"Brush: {radius:F1}";
        }
    }

    public void SetCoords(Vector2I texel)
    {
        if (coordsLabel != null)
        {
            coordsLabel.Text = $"Texel: ({texel.X}, {texel.Y})";
        }
    }
}
