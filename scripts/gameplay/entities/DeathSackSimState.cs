using System.Collections.Generic;
using Godot;

// A fallen party member's gear, left where they died (Sim.DropDeathSack). It is not
// dropped loot, so the sunrise reset leaves it alone, and a save carries it like any
// other entity in a chunk the run changed. Opening it spills the contents and
// removes it for good.
public class DeathSackSimState : EntitySimState
{
	// Live ItemStates, written whole (EntitySerializer.WriteItemList), so a weapon's
	// mods and a stack's spoil cohorts come back exactly as the member carried them.
	public readonly List<ItemState> Contents = new();

	public DeathSackSimState(Vector3 worldPosition, PackedScene scene)
		: base(worldPosition, scene)
	{
	}

	public override Node3D CreateEntity(Sim sim) => DeathSack.Create(sim, this);
}
