using System;
using System.Collections.Generic;

// Crafting benches' queues run on the in-world clock wherever the party is, so
// they are settled from the sim — on every deadline sweep — not by the bench's
// node, which exists only while its chunk is resident.
public partial class Sim
{
    // Every crafting bench in the world, resident or not. Derived from the entity
    // buckets in Initialize; benches are placed by the world, never at runtime.
    private readonly List<CraftingBenchSimState> _craftingBenches = new();

    // A finished job was handed to the stash.
    public event Action<CraftingBenchSimState, CraftJob> onCraftJobDelivered;

    private readonly List<CraftJob> _deliveredScratch = new();

    private void IndexCraftingBenches()
    {
        _craftingBenches.Clear();
        foreach (EntitySimState state in _worldState.AllChunkEntities())
        {
            if (state is CraftingBenchSimState bench)
            {
                _craftingBenches.Add(bench);
            }
        }
    }

    private void SettleCraftQueues()
    {
        for (int i = 0; i < _craftingBenches.Count; i++)
        {
            SettleCraftQueue(_craftingBenches[i]);
        }
    }

    // Deliver what `bench` has finished by now into the party stash, announcing
    // each job. The crafting screen calls this every frame for its own bench so
    // its progress bar never sits at full waiting for the next sweep.
    public void SettleCraftQueue(CraftingBenchSimState bench)
    {
        ItemGrid stash = _worldState?.SimState?.PartyStash;
        if (bench == null || bench.Queue.IsEmpty || stash == null)
        {
            return;
        }
        _deliveredScratch.Clear();
        bench.Queue.Settle(_worldState.WorldClockDays, stash, _deliveredScratch);
        for (int i = 0; i < _deliveredScratch.Count; i++)
        {
            CraftJob job = _deliveredScratch[i];
            GameClient.Current?.Announce(new Announcement
            {
                type = EAnnouncementType.Notice,
                title = DeliveredText(job),
                icon = job.IsRepair ? job.gear.data.inventorySprite : job.made.inventorySprite,
            });
            onCraftJobDelivered?.Invoke(bench, job);
        }
        _deliveredScratch.Clear();
    }

    // "Crafted Caltrops (3)" / "Repaired Iron Sword".
    public string DeliveredText(CraftJob job)
    {
        SimState simState = _worldState?.SimState;
        if (job.IsRepair)
        {
            string gearName = simState?.GetItemDisplayName(job.gear) ?? job.gear.data.displayName.ToString();
            return Loc.Format(Loc.Keys.item_repaired, gearName);
        }
        string name = simState?.GetItemDisplayName(job.Preview()) ?? job.made.displayName.ToString();
        return job.count > 1
            ? Loc.Format(Loc.Keys.station_crafted_count, name, job.count)
            : Loc.Format(Loc.Keys.station_crafted, name);
    }
}
