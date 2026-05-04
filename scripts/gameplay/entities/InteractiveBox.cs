using Godot;

[GlobalClass]
public partial class InteractiveBox : Area3D
{
    [Export] private Node _interactiveNode;

    public IInteractive Interactive => _interactiveNode as IInteractive;
}
