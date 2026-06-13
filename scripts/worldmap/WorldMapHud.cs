using Godot;

// Minimal status HUD for the world-map painter. Mirrors EditorHud — exported
// labels assigned in the scene, simple setters driven by the painter.
[GlobalClass]
public partial class WorldMapHud : CanvasLayer
{
    [Export] public Label viewLabel;
    [Export] public Label layerLabel;
    [Export] public Label toolLabel;
    [Export] public Label radiusLabel;
    [Export] public Label coordsLabel;
    [Export] public Label helpLabel;

    public override void _Ready()
    {
        if (helpLabel != null)
        {
            helpLabel.Text = "LMB: Paint | RMB: Erase/Lower | Tab: Layer | Space: 2D/3D | Q/E: Cycle Tool | [ ]: Brush Size | Ctrl+S: Save | Esc: Quit";
        }
    }

    public void SetView(bool preview)
    {
        if (viewLabel != null)
        {
            viewLabel.Text = preview ? "View: 3D Preview" : "View: 2D Map";
        }
    }

    public void SetLayer(string layer)
    {
        if (layerLabel != null)
        {
            layerLabel.Text = $"Layer: {layer}";
        }
    }

    public void SetTool(string tool)
    {
        if (toolLabel != null)
        {
            toolLabel.Text = $"Tool: {tool}";
        }
    }

    public void SetRadius(float radius)
    {
        if (radiusLabel != null)
        {
            radiusLabel.Text = $"Brush: {radius:F1}";
        }
    }

    public void SetCoords(Vector3 pos)
    {
        if (coordsLabel != null)
        {
            coordsLabel.Text = $"Cursor: ({pos.X:F0}, {pos.Y:F0}, {pos.Z:F0})";
        }
    }
}
