using System.Collections.Generic;
using Godot;

// The resident half of a trigger (TriggerSimState). Draws nothing and runs no
// process callback of its own: it exists so chunk streaming has a node to
// create and free, and while it is in the tree its box is tested by
// Sim.TickTriggers. Who is inside is runtime-only — a trigger streamed in with
// an admitted actor already standing in it counts that as entering.
[GlobalClass]
public partial class TriggerZone : Node3D
{
    public TriggerSimState State { get; private set; }

    // Every admitted actor currently inside, with the kind it was admitted as.
    // Mobs are only re-tested on the slower mob scan, so their entries stand
    // between scans.
    private readonly Dictionary<Node3D, ETriggerActors> _inside = new();

    public bool IsOccupied => _inside.Count > 0;

    public static TriggerZone Create(Sim sim, TriggerSimState state)
    {
        var zone = new TriggerZone { State = state, Name = "Trigger" };
        state.SeatTransform(zone);
        sim.AddChild(zone);
        return zone;
    }

    public override void _EnterTree()
    {
        Sim.Current?.RegisterTrigger(this);
    }

    public override void _ExitTree()
    {
        Sim.Current?.UnregisterTrigger(this);
        _inside.Clear();
    }

    // One pass over the candidate actors. `mobsScanned` says whether the mob
    // entries of `actors` are fresh this tick; when they are not, the mobs this
    // zone already holds are left as they were. Fires nothing itself: an
    // occupancy change is appended to `fires`, so Sim can fire every zone's
    // exits before any zone's enters.
    public void Evaluate(List<(Node3D Node, ETriggerActors Kind)> actors, bool mobsScanned, List<TriggerFire> fires)
    {
        bool wasOccupied = _inside.Count > 0;
        Node3D entered = null;
        Node3D left = null;

        // Drop what is gone or has walked out, before adding what walked in, so
        // `left` names the last one out.
        _scratch.Clear();
        foreach ((Node3D node, ETriggerActors kind) in _inside)
        {
            bool isMob = IsMobKind(kind);
            if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree())
            {
                _scratch.Add(node);
                continue;
            }
            if (isMob && !mobsScanned)
            {
                continue;
            }
            if (!Contains(actors, node, kind) || !State.Box.HasPoint(node.GlobalPosition))
            {
                _scratch.Add(node);
            }
        }
        ETriggerActors leftKind = ETriggerActors.None;
        foreach (Node3D node in _scratch)
        {
            leftKind = _inside[node];
            _inside.Remove(node);
            left = node;
        }

        foreach ((Node3D node, ETriggerActors kind) in actors)
        {
            if (!State.Wants(kind) || _inside.ContainsKey(node))
            {
                continue;
            }
            if (State.Box.HasPoint(node.GlobalPosition))
            {
                _inside[node] = kind;
                entered ??= node;
            }
        }

        bool occupied = _inside.Count > 0;
        if (!wasOccupied && occupied)
        {
            fires.Add(new TriggerFire(this, true, entered, _inside[entered]));
        }
        else if (wasOccupied && !occupied)
        {
            fires.Add(new TriggerFire(this, false, GodotObject.IsInstanceValid(left) ? left : null, leftKind));
        }
    }

    private readonly List<Node3D> _scratch = new();

    // One occupancy change, waiting for Sim to fire it.
    public readonly struct TriggerFire
    {
        public readonly TriggerZone Zone;
        public readonly bool Enter;
        public readonly Node3D Who;
        public readonly ETriggerActors Kind;

        public TriggerFire(TriggerZone zone, bool enter, Node3D who, ETriggerActors kind)
        {
            Zone = zone;
            Enter = enter;
            Who = who;
            Kind = kind;
        }
    }

    private static bool Contains(List<(Node3D Node, ETriggerActors Kind)> actors, Node3D node, ETriggerActors kind)
    {
        for (int i = 0; i < actors.Count; i++)
        {
            if (actors[i].Node == node)
            {
                return actors[i].Kind == kind;
            }
        }
        return false;
    }

    public static bool IsMobKind(ETriggerActors kind)
        => (kind & (ETriggerActors.HostileMob | ETriggerActors.FriendlyMob
            | ETriggerActors.NeutralMob | ETriggerActors.PreyMob)) != 0;
}
