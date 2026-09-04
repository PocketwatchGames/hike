using Godot;

[GlobalClass]
public partial class InteractiveBox : Area3D
{
    [Export] private Node _interactiveNode;

    public IInteractive Interactive => _interactiveNode as IInteractive;

    // For a box built at runtime rather than authored, where there is no .tscn
    // to carry the NodePath (a dropped rope sizes its own box to its length).
    public void SetInteractive(Node node)
    {
        _interactiveNode = node;
    }
}
