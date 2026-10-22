using Godot;

// Persistent state for drops on the ground. All loot uses one scene (Loot) and
// one runtime class — the auto-pickup vs interact-pickup decision happens at
// run time inside Loot based on the player's inventory state. Item is the
// optional carried ItemState; world-spawned loot leaves it null (pickup
// synthesizes a fresh ItemState from Data), player drops set it so pickup
// deposits the original stack. RequireInteract latches the loot into "press to
// pick up" mode regardless of inventory state — set on player-initiated drops
// so freshly dropped piles don't immediately re-enter the inventory.
// RequireInteract is in-memory only (inventory is not yet serialized).
public class LootSimState : EntitySimState
{
    public readonly ItemData Data;
    public bool PickedUp;
    public ItemState Item;
    public bool RequireInteract;
    // True for loot that dropped at runtime (mob kills, dig yields, player drops)
    // rather than authored worldgen ground loot. A full spawn-state reset
    // (Sim.ResetSpawns) sweeps these so a revived encounter doesn't leave
    // the last life's spoils lying around, while authored loot (Dropped == false)
    // and a dropped sack stay put. In-memory only, like RequireInteract.
    public bool Dropped;
    // A round the player fired that landed here (Sim.EjectFiredRound). Collected
    // on contact whenever it fits, even with none of its kind left in the pack.
    // In-memory only: the rest sweep clears it before any save could hold it.
    public bool FiredRound;
    // Whether the next materialization should play the spawn puff + sound.
    // CreateEntity runs on every chunk stream-in, not just when loot first
    // appears, so without consuming this the whole ground inventory of an area
    // re-poofs (and re-chimes) each time the player walks back into it — 64
    // concurrent loot_spawn effects showed up in a node census this way.
    // Starts true so genuinely new loot announces itself once. In-memory only,
    // like the flags above; a reload replays each pile's puff once, which is
    // indistinguishable from the loot having just appeared.
    public bool PlaySpawnEffects = true;

    public LootSimState(Vector3 worldPosition, ItemData data)
        : base(worldPosition, scene: null)
    {
        Data = data;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        if (PickedUp)
        {
            return null;
        }
        GameClient gc = GameClient.Current;
        PackedScene scene = gc?.lootScene;
        if (scene == null)
        {
            return null;
        }
        Loot loot = Loot.Create(sim, this, scene);
        PlaySpawnEffects = false;
        return loot;
    }

    // Called from Loot after the world-removal commit (PickedUp set true)
    // for any removal cause — player pickup, ItemData.removeTimeMs timeout,
    // future causes. Not invoked by deserialization; load-time PickedUp
    // values from disk skip this hook.
    public virtual void OnRemovedFromWorld() { }
}
