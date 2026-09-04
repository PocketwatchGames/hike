using Godot;

// A named list of prop scenes the world-map painter fills a painted region with.
//
// The fill is SIZE-ORDERED: the largest things go down first and everywhere,
// spaced by their drawn canopies rather than by their trunks, then each smaller
// class in turn, then a last pass seals the region's edge band with the widest
// thing that fits. So a list is read as a set of size classes, and what an
// author puts in it decides the passes — see scripts/worldmap/docs/prop-fill.md.
//
// COVERAGE IS THE EDGE BAND'S CONTRACT AND ONLY THE EDGE BAND'S. Painting a
// region says "you cannot walk through here", and a barrier with a lane through
// it is not a barrier — but that is a claim about the rim, which is the only
// part anyone can reach. Behind it the region is furnished to look right and
// nothing more, at interiorDensity.
//
// Two layers paint from this same palette, and the difference is what the props
// are for rather than what they are (see PropPaintTool):
//   COLLIDABLE   — trees, boulders, walls. A wall of the world.
//   DESTRUCTIBLE — thickets, brambles, crates. A wall until it is cleared.
// Which list suits which layer is the author's call, so any list can be painted
// on either — a boulder field is a hard barrier and a bramble a soft one, and
// nothing about the resource has to know which use it was put to.
//
// Distinct from SpawnSetData, which is the GENERATOR's ambient scatter (kits
// reference one, and its noise fields are what shape a wood) — that is scenery
// grown by rule, this is furniture placed by hand.
[GlobalClass]
public partial class PropListData : Resource
{
    // Shown on the painter's palette button and in the HUD.
    [Export] public string displayName = "";

    // The palette button's swatch. NOT the map dot: a dot is inked by which
    // LAYER it belongs to (collidable or destructible), because what a painted
    // region does to movement is the question the map has to answer at a
    // glance, and which list furnishes it is the palette's answer.
    [Export] public Color mapColor = new Color(0.4f, 0.8f, 0.4f);

    // Rows, not bare scenes: a row carries which storey this LIST puts the
    // scene in (PropListEntry.tier). Typed as the base so the nine lists
    // authored before the tier existed still load; a plain WeightedScene reads
    // as EPropTier.Auto.
    [Export] public WeightedScene[] scenes = System.Array.Empty<WeightedScene>();

    // --- The interior of a region ------------------------------------------
    //
    // Only the EDGE of a painted region has to be solid. Past a few metres in,
    // the ground behind the barrier is ground nobody can reach, and packing it
    // buys nothing but entities — a 40m thicket spends nearly all of itself on
    // an inside no one will stand in.
    //
    // So the seal pass covers this band at the region's edge and nothing else
    // does. The band has to be deep enough that nothing squeezes through it,
    // which is a property of the props in the list: a stand of 0.3m trunks needs
    // more metres of it than a field of boulders.
    [Export(PropertyHint.Range, "1,8,1")] public int barrierDepthMeters = 3;

    // How densely the interior is furnished, as a fraction of the edge band's
    // own density. 1 fills a region solid; 0 leaves everything behind the band
    // to the largest pass alone, which is a stand of trees over open ground.
    //
    // It is a DENSITY and not a hole chance because a barrier's inside is not a
    // barrier: nobody can reach it, so nothing there has to touch anything else,
    // and the only question left is how the ground behind the treeline reads
    // from a camera that can see over it.
    [Export(PropertyHint.Range, "0,1,0.05")] public float interiorDensity = 0.35f;

    // How far the interior's density takes to arrive, in metres past the band.
    // The band is dense because it has to seal; the inside is sparse because it
    // does not. A hard switch between them draws a line around every region at
    // exactly barrierDepthMeters, which is the tell that gives a painted wood
    // away — so the two densities are blended across this distance instead.
    [Export(PropertyHint.Range, "0,24,1")] public int densityRampMeters = 8;

    // --- Two storeys, two reservation models --------------------------------
    //
    // CANOPY and UNDERSTORY reserve room from THEIR OWN KIND only: a bush does
    // not push a tree away and a tree does not push a bush away. That is what an
    // understory is, and it is why one shared spacing rule could never express
    // both - it had to either forbid undergrowth beneath trees or let a redwood
    // stand in a maple's canopy.
    //
    // UNDERSTORY needs no number at all. A low prop's honest reservation is its
    // own COLLISION radius: two bushes reserving that stand exactly touching,
    // which is what a thicket is, and it is measured rather than authored.
    //
    // Only the CANOPY needs its own, because a canopy is many times wider than
    // the trunk holding it up and nothing about the collider says how much room
    // the crown wants.

    // Drawn height above which a prop is CANOPY rather than understory, for a
    // row left on EPropTier.Auto. Height and not collider width: a pine's trunk
    // collider is as wide as its crown and a willow's is a tenth of it, so width
    // says nothing, while nothing low is a tree and nothing tall is undergrowth.
    [Export(PropertyHint.Range, "0.5,12,0.25")] public float canopyHeightMeters = 3f;

    // What a canopy reserves, as a fraction of its drawn radius. Two of them
    // stand at least the SUM of their reservations apart, so a 3 m reservation
    // keeps another 3 m tree six metres away. 1 means crowns just touch.
    [Export(PropertyHint.Range, "0.1,2,0.05")] public float canopySpacing = 1.2f;

    // The most any canopy may reserve. A cap rather than a taste knob: without
    // one the widest tree in a list monopolises the region. An oak draws 4.62 m
    // against a birch's 2.56 m, so at canopySpacing 1.2 it reserved 5.5 m and
    // pushed every birch 8.6 m away - eight oaks blanketed a 360 m2 wood and not
    // one birch could be placed in it.
    //
    // It is also the tree-count dial, and the arithmetic is unforgiving: a
    // reservation of r puts trees 2r apart, about one per pi*r^2. 3 m gives one
    // tree per ~31 m2, 2.5 m one per ~22 m2, 2 m one per ~14 m2.
    [Export(PropertyHint.Range, "1,12,0.25")] public float canopyMaxReservation = 3f;

    // How many times each size class is thrown at the region. One round places
    // a maximal independent set, which settles at roughly two thirds of what the
    // same minimum separation would allow; each further round throws into the
    // gaps the last one left and is refused by what it reserved. Raising this
    // packs a class denser WITHOUT letting anything closer than its reservation.
    [Export(PropertyHint.Range, "1,8,1")] public int spacingRounds = 4;

    // Metres a prop may wander off its column's centre. The fill measures each
    // prop at the pose it will actually stand in, so this costs nothing in
    // accuracy — it is what stops a filled region reading as a lattice.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float positionJitter = 0.35f;

    // Uniform scale variation, applied UPWARD only: a prop stands somewhere in
    // 1 .. 1 + this. One-directional on purpose — the fill measures footprints
    // at scale 1, so growing a prop can only make it cover MORE than was
    // claimed, and over-covering is the harmless direction for a barrier.
    // Shrinking would let a column the map calls blocked come back open.
    [Export(PropertyHint.Range, "0,1,0.05")] public float scaleJitter = 0.2f;

    // How hard the fill spreads itself across the whole list. 0 takes the
    // authored weights as they are, which lets one prop win most rolls in any
    // patch small enough to notice; higher values push each pass toward scenes
    // it has not used yet in this chunk.
    [Export(PropertyHint.Range, "0,4,0.1")] public float varietyPressure = 1.5f;

    // One measured shape per scene — its static collision in the scene's own
    // space, plus how far it REACHES both as collision and as drawn canopy.
    // Built on demand, then kept for the life of the resource — which is the
    // life of the PROCESS, since Godot's resource cache hands every loader the
    // same instance. That is fine for a *Data resource, whose contents cannot
    // change while the game runs, and it is exactly why Refresh exists: the
    // collider these are measured off lives in a .tscn an author edits in the
    // editor beside the running game.
    //
    // Per SCENE and not per (scene, yaw): a measured shape rasterizes at any
    // pose, so the yaws and the sub-metre offsets the fill wants cost arithmetic
    // rather than another instantiate.
    private PropFootprint.Shape[] _shapes;
    private PackedScene[] _sceneMirror;
    private EPropTier[] _tierMirror;
    private float[] _weightMirror;
    private float _totalWeight;

    // The bake runs on a worker thread while the painter draws on the main one,
    // and both ask for shapes. Measuring one instantiates a scene and calls
    // PropFootprint, which keeps a shared gather buffer — so the build is
    // serialized here rather than left to chance.
    private readonly object _shapeLock = new();

    public int SceneCount
    {
        get
        {
            EnsureMirrors();
            return _sceneMirror.Length;
        }
    }

    public PackedScene SceneAt(int index)
    {
        EnsureMirrors();
        return index >= 0 && index < _sceneMirror.Length ? _sceneMirror[index] : null;
    }

    // Does this scene fill the given storey? A row left on Auto is decided by
    // the measured drawn height, which is right for everything but a tree whose
    // branches reach the ground - see EPropTier.
    public bool FillsTier(int scene, bool canopy)
    {
        EnsureMirrors();
        if (scene < 0 || scene >= _tierMirror.Length)
        {
            return false;
        }
        return _tierMirror[scene] switch
        {
            EPropTier.Canopy => canopy,
            EPropTier.Understory => !canopy,
            EPropTier.Both => true,
            _ => (ShapeOf(scene)?.VisualHeight ?? 0f) >= canopyHeightMeters == canopy,
        };
    }

    // What this scene reserves around itself in the given storey. The two
    // storeys answer differently on purpose: see the note on the knobs.
    public float Reservation(int scene, bool canopy)
    {
        PropFootprint.Shape shape = ShapeOf(scene);
        if (shape == null)
        {
            return 0f;
        }
        return canopy
            ? Mathf.Min(shape.VisualRadius * canopySpacing, canopyMaxReservation)
            : shape.CollisionRadius;
    }

    // This scene's authored weight, for a caller doing its own weighted pick
    // over a subset of the list.
    public float WeightOf(int scene)
    {
        EnsureMirrors();
        return scene >= 0 && scene < _weightMirror.Length ? _weightMirror[scene] : 0f;
    }

    // Weighted pick from a 0..1 roll, so the caller's hash decides and this
    // holds no sequential state.
    public int ChooseScene(float roll01)
    {
        EnsureMirrors();
        if (_sceneMirror.Length == 0)
        {
            return -1;
        }
        float target = Mathf.Clamp(roll01, 0f, 0.999999f) * _totalWeight;
        for (int i = 0; i < _weightMirror.Length; i++)
        {
            target -= _weightMirror[i];
            if (target < 0f)
            {
                return i;
            }
        }
        return _sceneMirror.Length - 1;
    }

    // This scene's measured collision, or null for an index out of range. The
    // shape carries its own reach, which is what the fill spaces props by.
    public PropFootprint.Shape ShapeOf(int scene)
    {
        EnsureMirrors();
        if (scene < 0 || scene >= _sceneMirror.Length)
        {
            return null;
        }
        lock (_shapeLock)
        {
            return _shapes[scene] ??= Measure(_sceneMirror[scene]);
        }
    }

    // The columns a prop covers standing in a column at `offset` metres off its
    // centre, turned to `yaw` — as offsets FROM that column. Its own column is
    // unioned in whatever the answer: a thin trunk standing dead centre does
    // cover it, but a collider modelled off-origin might not, and a placed prop
    // that fails to occupy the column it was placed in would leave the fill
    // claiming ground it never filled.
    public void Rasterize(int scene, float yaw, Vector2 offset, System.Collections.Generic.List<Vector2I> into)
    {
        into.Clear();
        PropFootprint.Shape shape = ShapeOf(scene);
        shape?.Rasterize(PoseIn(yaw, offset), into);
        if (!into.Contains(Vector2I.Zero))
        {
            into.Add(Vector2I.Zero);
        }
    }

    // Where a prop stands inside its own column: the centre, plus its jitter,
    // turned to its yaw. Column-local, so the columns the rasterizer reports
    // come back as offsets from the column the prop was placed in.
    // Free rotation, not one of a handful of steps. It was quantized only so
    // that a footprint could be cached per (scene, yaw); a measured Shape
    // rasterizes at any pose, so there is nothing left to quantize FOR.
    public static Transform3D PoseIn(float yaw, Vector2 offset)
        => new(Basis.FromEuler(new Vector3(0f, yaw, 0f)),
            new Vector3(0.5f + offset.X, 0f, 0.5f + offset.Y));

    // The prop's static collision, rasterized into the 1 m columns it covers —
    // the same question and the same code the minimap asks of a placed prop, so
    // a painted barrier and the map of it cannot disagree about what blocks.
    //
    // Its own column is unioned in whatever the answer: a thin trunk standing
    // dead centre does cover it, but a collider modelled off-origin might not,
    // and a placed prop that fails to occupy the column it was placed in would
    // leave the fill claiming ground it never filled.
    private static readonly PropFootprint.Shape EmptyShape = PropFootprint.EmptyShape();

    private static PropFootprint.Shape Measure(PackedScene scene)
    {
        if (scene?.Instantiate() is not Node3D root)
        {
            return EmptyShape;
        }
        PropFootprint.Shape shape = PropFootprint.Measure(root);
        // Free, not QueueFree: nothing here entered the tree, so there is no
        // frame to defer to and the nodes would simply leak.
        root.Free();
        return shape;
    }

    private void EnsureMirrors()
    {
        if (_sceneMirror != null)
        {
            return;
        }
        var kept = new System.Collections.Generic.List<PackedScene>();
        var weights = new System.Collections.Generic.List<float>();
        var tiers = new System.Collections.Generic.List<EPropTier>();
        foreach (WeightedScene entry in scenes ?? System.Array.Empty<WeightedScene>())
        {
            if (entry?.scene != null && entry.weight > 0f)
            {
                kept.Add(entry.scene);
                weights.Add(entry.weight);
                tiers.Add((entry as PropListEntry)?.tier ?? EPropTier.Auto);
                _totalWeight += entry.weight;
            }
        }
        _tierMirror = tiers.ToArray();
        _weightMirror = weights.ToArray();
        _shapes = new PropFootprint.Shape[kept.Count];
        _sceneMirror = kept.ToArray();
    }

    // Re-read this list and the scenes in it FROM DISK, and drop everything
    // measured off them. The bake calls it, so a collider widened in the editor
    // reaches the world by re-saving the map rather than by restarting the game.
    //
    // CacheMode.Replace for this list's OWN .tres, so re-reading it picks up a
    // scene added to the list or a band retuned. Safe here because the file is a
    // flat resource: Replace re-applies the file's properties onto the live
    // cached object, which is only a problem for an object that refuses a setter.
    //
    // The SCENES are loaded with CacheMode.Ignore and adopted, because they are
    // exactly that problem. **Never Replace a scene containing a MultiMesh**:
    // `transform_format`, `use_colors` and `use_custom_data` all fail their
    // `instance_count > 0` guard against the object already in the cache, so a
    // replace prints three errors per scene AND silently keeps the old flags —
    // failing at the one job it was chosen for. Every tree in the project is a
    // MultiMesh of leaf cards, so this is most of what a prop list holds.
    //
    // Ignore hands back a fresh copy instead of refilling the cached one, so the
    // copy has to be adopted into `scenes` for the re-measure to see it. That
    // does not update a reference held elsewhere (a live PropSimState's scene),
    // which is the price: the painter and the bake re-measure from disk, and a
    // running world keeps whatever it instanced.
    public void Refresh()
    {
        if (!string.IsNullOrEmpty(ResourcePath))
        {
            ResourceLoader.Load<Resource>(ResourcePath, cacheMode: ResourceLoader.CacheMode.Replace);
        }
        foreach (WeightedScene entry in scenes ?? System.Array.Empty<WeightedScene>())
        {
            string path = entry?.scene?.ResourcePath;
            if (!string.IsNullOrEmpty(path))
            {
                var fresh = ResourceLoader.Load<PackedScene>(
                    path, cacheMode: ResourceLoader.CacheMode.Ignore);
                if (fresh != null)
                {
                    entry.scene = fresh;
                }
            }
        }
        lock (_shapeLock)
        {
            _sceneMirror = null;
            _tierMirror = null;
            _weightMirror = null;
            _shapes = null;
            _totalWeight = 0f;
        }
    }

    public string Label => string.IsNullOrEmpty(displayName)
        ? (string.IsNullOrEmpty(ResourcePath) ? "Props" : ResourcePath.GetFile().GetBaseName())
        : displayName;
}
