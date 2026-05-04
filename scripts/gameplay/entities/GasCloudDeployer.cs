using Godot;

// Spawns a GasCloud (or any Node3D scene, really) at the deployer's
// position when triggered. The companion to SpikeDeployer for hazard
// firers that don't need TriggerSource.BodiesInArea — the cloud's own
// DamageZone handles who gets hit. Wire it into a Chest's
// _onOpenTargets, a Trap's _deployers, or any other firer.
//
// The cloud is parented to the deployer's parent (typically the host
// scene root, e.g. the chest) so it survives independently of the
// deployer being pooled or re-streamed; if the host scene unloads,
// everything goes together. World-parenting can be added later if
// clouds need to outlive their host (set _parentToWorld true and
// hand a World ref via OnSpawned-style plumbing).
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
