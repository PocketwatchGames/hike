using Godot;

// A coil of rope sitting at the top of a drop. Interacting throws it over the
// edge, and the rope that unrolls is climbed exactly the way a dressed cliff is
// — the climb's contact test asks the ClimbableSurface the rope carries instead
// of marching into rock that is not there (Player.TryResolveHold).
//
// The drop direction is the coil's AUTHORED facing, not the player's: an author
// places a coil at the edge it belongs to, and a rope that could be thrown any
// way would need the edge re-checked every frame just to keep the prompt honest.
//
// One-way. Once thrown the rope stays, which is the whole point — it is a
// shortcut the player has opened in that place, and it persists through save and
// chunk streaming as a single bool on the sim state.
[GlobalClass]
public partial class CoiledRope : Node3D, IInteractive, IWorldEntity
{
    [Export] private Node3D _hudNode;
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    // Offered by the RopE once it is down — taking hold of it. Separate from
    // _actions, which is the coil's "throw it over".
    [Export] private Godot.Collections.Array<InteractiveAction> _climbActions = new();
    // The coil model, hidden once the rope is out — a coil still sitting there
    // beside its own unrolled rope reads as a second, unused one.
    [Export] private Node3D _coilVisual;
    // Shown once the rope is out: the stake it is tied off to. The coil is gone
    // by then, so without this the rope hangs from nothing.
    [Export] private Node3D _anchorVisual;

    [Export] private Material _ropeMaterial;
    // Drawn thickness. The grab volume below is deliberately fatter.
    [Export(PropertyHint.Range, "0.01,0.3,0.005")] private float _ropeRadius = 0.05f;
    [Export(PropertyHint.Range, "3,16,1")] private int _ropeSides = 6;
    // Radius of the climbable volume. Wider than the drawn rope so grabbing one
    // is forgiving — a climber is aiming at a line a few centimetres across.
    [Export(PropertyHint.Range, "0.05,0.5,0.01")] private float _gripRadius = 0.14f;
    // How far the rope stands ABOVE the ledge it is tied to. Must clear the
    // player's grip height, or someone walking up to the edge has nothing at
    // hand height to take hold of and the rope can only be entered from below.
    [Export] private float _gripStubHeight = 1.3f;
    // Gap between the rock face and the rope's line, so a climber hanging off
    // the rope is not inside the cliff.
    [Export] private float _wallClearance = 0.15f;
    // How many cells out from the coil to look for the edge.
    [Export(PropertyHint.Range, "1,8,1")] private int _edgeSearchCells = 3;
    // Drops outside this band are not offered: shorter than this is a mantle,
    // longer than this has no rope.
    [Export] private float _minDrop = 3f;
    [Export] private float _maxDrop = 40f;
    // Half-width of the box you have to be inside to be offered the climb. Wide
    // enough to cover a player held back from the lip by the ledge barrier,
    // which is where they stand when they mean to go down.
    [Export] private float _interactBoxHalfWidth = 1.3f;
    // How far the box runs ABOVE the rope's anchor, so someone standing ON the
    // ledge — whose feet are level with it and whose body is above it — is
    // inside the box rather than just over its lid.
    [Export] private float _interactBoxAbove = 2f;
    // ...and below its foot, for a player walking up to the bottom of it.
    [Export] private float _interactBoxBelow = 0.5f;
    // Whether a coil that faces no edge falls back to whichever side of it does.
    //
    // OFF by default: the facing IS the authoring, and it should mean what it
    // says — the authored direction is also the only thing that can disambiguate
    // a coil on a corner, where two drops are in reach and only the author knows
    // which one is the route. This exists because the failure is silent: a coil
    // that resolves nothing shows no prompt at all, so a 90-degree slip and a
    // broken feature look alike. Turn it on if that trade is worth it; either
    // way `rope_probe` reports which facings would work, so a mis-aimed coil is
    // a five-second diagnosis.
    [Export] private bool _autoAimAtNearestEdge = false;

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    private CoiledRopeSimState _simState;
    private Sim _world;
    private Node3D _rope;

    // The drop, resolved once and kept. CanInteract is polled by the HUD every
    // frame for every interactive in range, and a resolve walks a voxel column —
    // cheap once, not sixty times a second. Nothing but a world edit can change
    // the answer, and an edit already reloads the chunk.
    private bool _dropResolved;
    private bool _dropValid;
    private RopeDrop.Result _drop;
    // Why the drop did or did not resolve, for `rope_probe`. A coil that
    // resolves nothing shows no prompt at all, so without this every cause —
    // aimed at a wall, edge out of reach, drop too short — is one symptom.
    private string _dropReason = "not resolved yet";

    public void OnSpawned(Sim sim)
    {
        _world = sim;
    }

    private bool Deployed => _simState != null && _simState.Deployed;

    // Refused once thrown, and refused where the throw would find no edge — a
    // prompt that runs and does nothing is worse than no prompt.
    public bool CanInteract()
    {
        return !Deployed && EnsureDrop(out RopeDrop.Result _);
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract();
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        if (!CanActorInteract(player))
        {
            return null;
        }
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    public void Complete(int actionIndex)
    {
        if (Deployed || !Deploy())
        {
            return;
        }
        if (_simState != null)
        {
            _simState.Deployed = true;
        }
    }

    private bool EnsureDrop(out RopeDrop.Result drop)
    {
        if (_dropResolved)
        {
            drop = _drop;
            return _dropValid;
        }
        drop = default;
        WorldState ws = _world?.WorldState;
        if (ws == null)
        {
            // Not an answer yet, so nothing is cached — asked again next frame.
            return false;
        }
        // +Z, NOT the stock Godot -Z: this project's facing convention is
        // Player.BodyForward / FaceAlong, which are (sin yaw, 0, cos yaw) — the
        // Y-rotation basis' +Z column. Reading it the other way points every
        // coil at whatever is BEHIND the edge it was aimed at, and a coil facing
        // solid ground resolves no drop and shows no prompt, so the mistake
        // reads exactly like the feature being broken.
        _dropValid = RopeDrop.Resolve(ws, GlobalPosition, GlobalBasis.Z,
            _edgeSearchCells, _maxDrop, _wallClearance, _minDrop, out _drop, out _dropReason);
        if (!_dropValid && _autoAimAtNearestEdge)
        {
            _dropValid = TryAutoAim(ws, out _drop, out string autoReason);
            _dropReason = $"{_dropReason}; auto-aim: {autoReason}";
        }
        _dropResolved = true;
        drop = _drop;
        return _dropValid;
    }

    // The best drop on any side, for a coil whose authored facing found none.
    // Deepest wins rather than first-found: where two sides both fall away, the
    // taller one is the cliff the coil was put there for, and the shallow one is
    // usually the slope leading to it. Ties break on a fixed direction order, so
    // the same coil always picks the same edge.
    private bool TryAutoAim(WorldState ws, out RopeDrop.Result best, out string reason)
    {
        best = default;
        var probes = new Vector3[]
        {
            new Vector3(0f, 0f, -1f), new Vector3(1f, 0f, 0f),
            new Vector3(0f, 0f, 1f), new Vector3(-1f, 0f, 0f),
        };
        bool found = false;
        int tried = 0;
        foreach (Vector3 dir in probes)
        {
            tried++;
            if (!RopeDrop.Resolve(ws, GlobalPosition, dir, _edgeSearchCells, _maxDrop,
                _wallClearance, _minDrop, out RopeDrop.Result r, out string _))
            {
                continue;
            }
            if (!found || r.Length > best.Length)
            {
                best = r;
                found = true;
            }
        }
        reason = found
            ? $"took ({best.Outward.X:F0},{best.Outward.Z:F0}) instead, {best.Length:F1}m — "
                + "TURN THE COIL to face this way so the authoring matches what it does"
            : $"no edge on any of the {tried} sides either";
        return found;
    }

    // Unrolls the rope: the drawn tube, and the climbable volume alongside it.
    // False when the drop does not resolve, which is how a reloaded world whose
    // ledge has been carved away comes back with the coil still coiled.
    private bool Deploy()
    {
        if (_rope != null || !EnsureDrop(out RopeDrop.Result drop))
        {
            return false;
        }

        // Turn the coil to match the drop it actually took, so an auto-aimed one
        // does not sit pointing away from its own rope. Must happen BEFORE the
        // container below is seated: it is a child, so turning this node after
        // placing it swings the rope off the line with it.
        Rotation = new Vector3(Rotation.X,
            Mathf.Atan2(drop.Outward.X, drop.Outward.Z), Rotation.Z);

        float length = drop.Length + _gripStubHeight;
        ArrayMesh mesh = RopeMeshBuilder.Build(length, _ropeRadius, _ropeSides);
        if (mesh == null)
        {
            return false;
        }

        // One container seated at the top of the line, so the tube and the grab
        // volume cannot drift apart. Parented here rather than made top-level:
        // this node carries only a yaw, and a vertical tube is symmetric about
        // it, so no orientation correction is needed.
        var container = new Node3D();
        container.Name = "Rope";
        AddChild(container);
        container.GlobalPosition = new Vector3(drop.Line.X, drop.TopY + _gripStubHeight, drop.Line.Z);

        var visual = new MeshInstance3D();
        visual.Mesh = mesh;
        visual.MaterialOverride = _ropeMaterial;
        visual.Layers = GameCamera.MainSceneLayer;
        container.AddChild(visual);

        // Seated at the MIDDLE of the line, not its top: the interact highlight
        // ranks candidates by node distance, so a node parked at the anchor
        // makes a climber at the foot of a tall rope lose the prompt to whatever
        // else is nearby.
        var surface = new ClimbableSurface();
        surface.Name = "Grip";
        surface.Position = new Vector3(0f, -length * 0.5f, 0f);
        surface.CollisionLayer = (uint)ECollisionLayer.Climbable;
        surface.CollisionMask = 0;
        surface.GripRadius = _gripRadius;
        surface.Actions = _climbActions;
        surface.LineTopY = drop.TopY + _gripStubHeight;
        surface.LineBottomY = drop.BottomY;
        // Held from the outboard side however the climber reached it, so the
        // body faces the cliff and the animation runs the right way round.
        surface.GripNormalOverride = drop.Outward;
        surface.allowsLateral = false;
        // Back across the clearance and half the lip cell: the centre of the
        // column the rope is tied to.
        surface.TopOutTarget = new Vector3(
            drop.Line.X - drop.Outward.X * (0.5f + _wallClearance),
            drop.TopY,
            drop.Line.Z - drop.Outward.Z * (0.5f + _wallClearance));
        container.AddChild(surface);

        var shapeNode = new CollisionShape3D();
        var cylinder = new CylinderShape3D();
        cylinder.Radius = _gripRadius;
        cylinder.Height = length;
        shapeNode.Shape = cylinder;
        // The surface node is already at the line's middle, which is where a
        // cylinder shape centres itself.
        surface.AddChild(shapeNode);

        // The box you interact with. Deliberately NOT the climbing collider:
        // that one is a thin line the climb sweeps against, and reaching it
        // takes a ray. This is the volume that means "you are at the rope", so
        // it spans the whole drop and stands proud of the ledge at the top.
        var box = new InteractiveBox();
        box.Name = "InteractiveBox";
        box.CollisionLayer = (uint)ECollisionLayer.Interactive;
        box.CollisionMask = 0;
        box.Monitoring = false;
        surface.AddChild(box);
        box.SetInteractive(surface);

        var boxShapeNode = new CollisionShape3D();
        var boxShape = new BoxShape3D();
        float boxHeight = length + _interactBoxAbove + _interactBoxBelow;
        boxShape.Size = new Vector3(_interactBoxHalfWidth * 2f, boxHeight, _interactBoxHalfWidth * 2f);
        boxShapeNode.Shape = boxShape;
        // Measured from the line's middle, where the surface node sits: the
        // above and below margins are unequal, so the box's centre is not.
        boxShapeNode.Position = new Vector3(0f, (_interactBoxAbove - _interactBoxBelow) * 0.5f, 0f);
        box.AddChild(boxShapeNode);

        _rope = container;
        if (_coilVisual != null)
        {
            _coilVisual.Visible = false;
        }
        if (_anchorVisual != null)
        {
            _anchorVisual.Visible = true;
        }
        return true;
    }

    // Console `rope_probe`: walks the whole pipeline from authored state to
    // interact prompt and reports each stage, because a rope that does not
    // highlight looks identical whether it never spawned, spawned without its
    // script, sits outside the interact area, or simply resolves no drop.
    // Mirrors climb_probe and nav_column: the answer is never "it does not
    // work", it is one stage, and this names it.
    public static void Probe()
    {
        Sim sim = Sim.Current;
        Player player = sim?.player;
        WorldState ws = sim?.WorldState;
        if (sim == null || ws == null)
        {
            GD.Print("[rope_probe] no running game");
            return;
        }

        // --- A: what the world says was authored --------------------------
        GD.Print("[rope_probe] === A: authored sim states ===");
        var states = new System.Collections.Generic.List<CoiledRopeSimState>();
        foreach (System.Collections.Generic.List<EntitySimState> bucket in ws._entities.Values)
        {
            foreach (EntitySimState e in bucket)
            {
                if (e is CoiledRopeSimState rope)
                {
                    states.Add(rope);
                }
            }
        }
        GD.Print($"  {states.Count} CoiledRopeSimState in WorldState");
        for (int i = 0; i < states.Count; i++)
        {
            CoiledRopeSimState st = states[i];
            Node3D node = st.RuntimeNode;
            GD.Print($"  [{i}] pos=({st.WorldPosition.X:F2},{st.WorldPosition.Y:F2},{st.WorldPosition.Z:F2}) "
                + $"yaw={Mathf.RadToDeg(st.RotationY):F0}deg deployed={st.Deployed} "
                + $"scene='{st.Scene?.ResourcePath ?? "NULL"}' "
                + $"node={(node == null ? "NOT SPAWNED" : node.GetType().Name)}");
        }
        if (states.Count == 0)
        {
            GD.Print("  -> nothing was placed, or the placement did not reach WorldState.");
            return;
        }

        // --- B: what actually spawned -------------------------------------
        GD.Print("[rope_probe] === B: spawned nodes ===");
        var nodes = new System.Collections.Generic.List<CoiledRope>();
        foreach (CoiledRope r in sim.GetEntities<CoiledRope>())
        {
            nodes.Add(r);
        }
        GD.Print($"  Sim.GetEntities<CoiledRope>() = {nodes.Count}");
        if (nodes.Count == 0)
        {
            GD.Print("  -> the state exists but no node. Either its chunk is not loaded, or the "
                + "scene did not instantiate AS CoiledRope (script not attached / cast failed).");
            return;
        }

        CoiledRope rope2 = nodes[0];
        if (player != null)
        {
            float best = float.MaxValue;
            foreach (CoiledRope r in nodes)
            {
                float d = player.GlobalPosition.DistanceSquaredTo(r.GlobalPosition);
                if (d < best) { best = d; rope2 = r; }
            }
        }
        Vector3 p = rope2.GlobalPosition;
        GD.Print($"  nearest at ({p.X:F2},{p.Y:F2},{p.Z:F2}) cell "
            + $"({Mathf.FloorToInt(p.X)},{Mathf.FloorToInt(p.Y)},{Mathf.FloorToInt(p.Z)})");

        // --- C: is the scene wired -----------------------------------------
        GD.Print("[rope_probe] === C: scene wiring ===");
        GD.Print($"  hudNode={(rope2._hudNode != null ? "ok" : "NULL")} "
            + $"actions={(rope2._actions?.Count ?? -1)} "
            + $"coilVisual={(rope2._coilVisual != null ? "ok" : "NULL")} "
            + $"anchorVisual={(rope2._anchorVisual != null ? "ok" : "NULL")} "
            + $"ropeMaterial={(rope2._ropeMaterial != null ? "ok" : "NULL")}");
        InteractiveBox box = null;
        foreach (Node child in rope2.GetChildren())
        {
            if (child is InteractiveBox b) { box = b; break; }
        }
        if (box == null)
        {
            GD.Print("  InteractiveBox: MISSING -> nothing can ever detect this rope.");
        }
        else
        {
            GD.Print($"  InteractiveBox: layer={box.CollisionLayer} monitorable={box.Monitorable} "
                + $"interactive={(box.Interactive == null ? "NULL (the _interactiveNode export is unset)" : box.Interactive.GetType().Name)}");
        }

        // --- D: does the player see it -------------------------------------
        GD.Print("[rope_probe] === D: discovery ===");
        if (player == null)
        {
            GD.Print("  no player (editor mode?) — the interact HUD does not exist here.");
        }
        else
        {
            Vector3 pp = player.GlobalPosition;
            GD.Print($"  player=({pp.X:F2},{pp.Y:F2},{pp.Z:F2}) dist={pp.DistanceTo(p):F2}m");
            Area3D area = player.interactArea;
            if (area == null)
            {
                GD.Print("  player.interactArea is NULL");
            }
            else
            {
                Godot.Collections.Array<Area3D> overlaps = area.GetOverlappingAreas();
                GD.Print($"  interactArea monitoring={area.Monitoring} mask={area.CollisionMask} "
                    + $"overlapping={overlaps.Count}");
                bool physicsSeesIt = false;
                int overlapCount = overlaps.Count;
                for (int i = 0; i < overlapCount; i++)
                {
                    if (overlaps[i] == box) { physicsSeesIt = true; }
                    GD.Print($"    overlap[{i}] {overlaps[i].GetParent()?.Name} ({overlaps[i].Name}) "
                        + $"layer={overlaps[i].CollisionLayer}");
                }
                GD.Print($"  physics overlap with this rope's box = {physicsSeesIt}");
                GD.Print($"  tracked in player collision list = {player.HasInteractiveForDebug(rope2)}");
            }
        }

        // --- E: the gate ----------------------------------------------------
        GD.Print("[rope_probe] === E: gate ===");
        Vector3 facing = rope2.GlobalBasis.Z;
        Vector3I outStep = VoxelFaces.Delta(VoxelFaces.Opposite(ClimbProbe.FacingBack(facing)));
        // Asked fresh, so a cached refusal cannot make the probe disagree with
        // what the coil would answer now.
        rope2._dropResolved = false;
        bool ok = rope2.CanInteract();
        GD.Print($"  facing=({facing.X:F2},{facing.Z:F2}) yaw={Mathf.RadToDeg(rope2.Rotation.Y):F0}deg "
            + $"-> drops toward ({outStep.X},{outStep.Z})");
        GD.Print($"  deployed={rope2.Deployed} searchCells={rope2._edgeSearchCells} "
            + $"minDrop={rope2._minDrop:F1} maxDrop={rope2._maxDrop:F0}");
        GD.Print($"  resolve: {rope2._dropReason}");
        GD.Print($"  VERDICT CanActorInteract={ok}"
            + (ok ? "" : "  <- no prompt is shown while this is false"));

        // --- F: what the coil COULD do -------------------------------------
        // "No edge that way" is true but not actionable on its own: the author
        // still has to work out which way to turn it, or whether this spot has
        // a drop at all. Both answers are one query each, so give them.
        GD.Print("[rope_probe] === F: every facing from this spot ===");
        Vector3I current = outStep;
        var probes = new Vector3[]
        {
            new Vector3(0f, 0f, -1f), new Vector3(1f, 0f, 0f),
            new Vector3(0f, 0f, 1f), new Vector3(-1f, 0f, 0f),
        };
        for (int i = 0; i < probes.Length; i++)
        {
            Vector3 dir = probes[i];
            bool can = RopeDrop.Resolve(ws, p, dir, rope2._edgeSearchCells, rope2._maxDrop,
                rope2._wallClearance, rope2._minDrop, out RopeDrop.Result _, out string why);
            bool isCurrent = Mathf.RoundToInt(dir.X) == current.X && Mathf.RoundToInt(dir.Z) == current.Z;
            GD.Print($"  ({dir.X,2:F0},{dir.Z,2:F0}) yaw={Mathf.RadToDeg(Mathf.Atan2(dir.X, dir.Z)),4:F0}deg : "
                + $"{(can ? "WORKS" : "no")} — {why}"
                + (isCurrent ? "   <- the coil currently faces this way" : (can ? "   <- TURN IT THIS WAY" : "")));
        }

        PrintHeightMap(ws, p);
    }

    // Ground height around the coil, relative to the ground it stands on, so an
    // author can see where the drop actually is rather than turning the coil
    // four times to find out. Digits are metres DOWN; '+' is higher ground.
    private static void PrintHeightMap(WorldState ws, Vector3 coil)
    {
        const int Radius = 6;
        // Deep enough that a real cliff reads as a cliff rather than as "no
        // ground", and shallow enough to stay one screen.
        const int ScanUp = 4;
        const int ScanDown = 24;

        int cx = Mathf.FloorToInt(coil.X);
        int cz = Mathf.FloorToInt(coil.Z);
        if (!TryGroundTop(ws, cx, Mathf.FloorToInt(coil.Y), cz, ScanUp, ScanDown, out int coilTop))
        {
            GD.Print("[rope_probe] height map: no ground under the coil");
            return;
        }

        GD.Print($"[rope_probe] ground around the coil (top y={coilTop}); "
            + "'C'=coil  '.'=level  '+'=higher  1-9=metres down  '*'=10+ down  '#'=no ground");
        GD.Print($"  -X {new string(' ', Radius * 2 - 2)}+X   (rows run -Z at top to +Z at bottom)");
        for (int dz = -Radius; dz <= Radius; dz++)
        {
            var row = new System.Text.StringBuilder("  ");
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                if (dx == 0 && dz == 0)
                {
                    row.Append('C');
                    continue;
                }
                if (!TryGroundTop(ws, cx + dx, coilTop, cz + dz, ScanUp, ScanDown, out int top))
                {
                    row.Append('#');
                    continue;
                }
                int drop = coilTop - top;
                row.Append(drop <= -1 ? '+'
                    : drop == 0 ? '.'
                    : drop >= 10 ? '*'
                    : (char)('0' + drop));
            }
            GD.Print(row.ToString() + $"   z={cz + dz}");
        }
    }

    // Topmost solid surface in a column, as the Y of its top face.
    private static bool TryGroundTop(WorldState ws, int x, int fromY, int z, int up, int down, out int topY)
    {
        for (int y = fromY + up; y >= fromY - down; y--)
        {
            if (Blocks.IsSolid(ws.GetBlockWorld(x, y, z)))
            {
                topY = y + 1;
                return true;
            }
        }
        topY = 0;
        return false;
    }

    public static CoiledRope Create(Sim sim, CoiledRopeSimState data)
    {
        var instance = data.Scene.Instantiate<CoiledRope>();
        data.SeatTransform(instance);
        instance._simState = data;
        instance._world = sim;
        sim.AddChild(instance);
        // After AddChild: the drop resolves against world position, which only
        // means anything once the node is in the tree.
        if (data.Deployed && !instance.Deploy())
        {
            // The edge it was tied to is gone. Coming back coiled is the honest
            // outcome — a rope hanging off nothing is a climb into a wall.
            data.Deployed = false;
        }
        if (!data.Deployed && instance._anchorVisual != null)
        {
            instance._anchorVisual.Visible = false;
        }
        return instance;
    }
}
