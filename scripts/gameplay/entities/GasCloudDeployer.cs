using Godot;

// Spawns a GasCloud (or any Node3D scene) at the deployer's position when
// triggered. The cloud's own DamageZone handles who gets hit.
//
// The cloud is parented to the deployer's parent (typically the host scene
// root, e.g. the chest) so it survives the deployer being pooled or
// re-streamed; if the host scene unloads, everything goes together.
[GlobalClass]
public partial class GasCloudDeployer : Node3D, ITriggerable
{
    [Export] public PackedScene cloudScene;

    public void Trigger(Node source)
    {
        if (cloudScene == null)
        {
            return;
        }
        var cloud = cloudScene.Instantiate<Node3D>();
        Node parent = GetParent() ?? this;
        parent.AddChild(cloud);
        cloud.GlobalPosition = GlobalPosition;
    }
}
