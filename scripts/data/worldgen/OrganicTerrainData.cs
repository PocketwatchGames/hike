using Godot;

// Tuning for the ORGANIC terrain approach (see OrganicTerrainGen). Assigning
// one of these to WorldGenData.terrain selects that approach; nothing here is
// read by any other approach, and no other approach's knobs are visible from
// here.
//
// Heights are in VOXELS relative to sea level (unlike the legacy path, which
// counts plateau steps). The one exception is the per-zone ZoneGenData
// elevation / elevationRange pair, which stays in its authored unit and is
// converted by ZoneElevationUnit so existing zone assets keep their meaning.
//
// The shape is built in three passes (see OrganicTerrainGen):
//   1. a continuous field  — domain warp, continental base, ridged relief
//      gated by a "hill country" mask, roughness, soft basin floor;
//   2. bench + cliff shaping — strata benching applied only where the field
//      is already steep, so cliffs land on flanks and benches on shoulders;
//   3. talus relaxation   — enforces the invariant that every adjacent column
//      pair is either walkable (<= MaxWalkableStep) or a wall (>= CliffMinDrop),
//      never an in-between slope that would harden into visible stairs.
[GlobalClass]
public partial class OrganicTerrainData : TerrainGenData
{
    public override ITerrainGenerator CreateGenerator(WorldGenData genData, int worldSeed)
    {
        return new OrganicTerrainGen(this, genData, worldSeed);
    }

    // Voxels per unit of ZoneGenData.elevation / elevationRange. Those fields
    // are authored in legacy plateau-step units, so keeping this at the legacy
    // plateauStep (4) means an existing zone sits at the same world height it
    // did before.
    [Export(PropertyHint.Range, "1,16,1")] public int zoneElevationUnit = 4;

    // The world's vertical lattice for ENCLOSED space, in voxels: building
    // floors, and cave / tunnel ceilings once carving returns. The open-air
    // surface is continuous on this path and ignores it — but every interior
    // ceiling must sit on a shared Y grid or the camera cutaway slices through
    // rooms at arbitrary heights. Note the per-region bench step is NOT usable
    // for this: it varies across the world by design, which is the opposite of
    // what a lattice is for.
    [Export(PropertyHint.Range, "1,16,1")] public int interiorLevelStep = 4;

    [ExportGroup("Domain Warp")]
    // Every downstream channel is sampled at coordinates displaced by this
    // field, which is what turns symmetric noise blobs into sinuous, bent
    // landforms. Shared by all channels so features stay registered with each
    // other. Amplitude is in voxels — roughly how far a coastline or ridge
    // wanders from where unwarped noise would have put it.
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float warpFrequency = 0.004f;
    [Export(PropertyHint.Range, "0,256,0.5")] public float warpAmplitude = 60f;

    [ExportGroup("Continental Base")]
    // Broad basins and swells the rest of the terrain rides on. This is the
    // only channel with real reach, so keep the frequency very low.
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float macroFrequency = 0.005f;
    [Export(PropertyHint.Range, "1,6,1")] public int macroOctaves = 3;
    [Export(PropertyHint.Range, "0,64,0.5")] public float macroAmplitude = 9f;

    [ExportGroup("Relief")]
    // Ridged relief: 1 - |fbm|, so the field peaks along the noise's zero
    // crossings. Those crossings form branching lines rather than blobs, which
    // is what makes the result read as eroded ridge-and-valley country. Relief
    // only ever ADDS to the base, so valley floors sit at the base level and
    // ridges rise off it.
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float reliefFrequency = 0.012f;
    [Export(PropertyHint.Range, "1,8,1")] public int reliefOctaves = 4;
    // Exponent on the ridged field. 1 = rounded whalebacks; higher pinches the
    // crests into narrow ridges with wide valleys between them.
    [Export(PropertyHint.Range, "0.5,6,0.05")] public float reliefSharpness = 2f;
    // Peak relief height = zone elevationRange * ZoneElevationUnit * this.
    [Export(PropertyHint.Range, "0,4,0.05")] public float reliefAmplitudeScale = 1.3f;

    // The hill-country mask. Relief is multiplied by smoothstep(MaskLow,
    // MaskHigh, maskNoise), so entire regions come out genuinely flat instead
    // of the whole world being uniformly bumpy. Raise MaskLow for more plains,
    // lower it for more highlands.
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float reliefMaskFrequency = 0.006f;
    [Export(PropertyHint.Range, "-1,1,0.01")] public float reliefMaskLow = -0.3f;
    [Export(PropertyHint.Range, "-1,1,0.01")] public float reliefMaskHigh = 0.1f;

    [ExportGroup("Roughness")]
    // Fine surface detail, scaled by the same hill mask so plains stay smooth
    // and hillsides get texture. Keep the amplitude low — anything past a few
    // voxels turns into noise the talus pass then has to grind back down.
    [Export(PropertyHint.Range, "0,0.5,0.00005")] public float roughnessFrequency = 0.03f;
    [Export(PropertyHint.Range, "1,6,1")] public int roughnessOctaves = 3;
    [Export(PropertyHint.Range, "0,16,0.1")] public float roughnessAmplitude = 0.6f;

    [ExportGroup("Basin Floors")]
    // Soft floor under each column at (base elevation + this offset). Anything
    // dipping below flattens into it, which is where genuinely flat ground
    // comes from — meadows, floodplains, lake beds — with a soft margin rather
    // than a hard clip. Softness is the blend width in voxels.
    [Export(PropertyHint.Range, "-32,32,0.5")] public float basinFloorOffset = 0f;
    [Export(PropertyHint.Range, "0.1,16,0.1")] public float basinSoftness = 2f;

    [ExportGroup("Drainage")]
    // Flow-accumulation valley carving: every column sheds one unit of rain
    // downhill (steepest of 8 neighbours), and the surface is then cut in
    // proportion to how much flow crosses it. This is the one pass whose shape
    // comes from the terrain rather than from a noise field, which is what
    // makes its valleys branch like real ones and meet at real confluences.
    // It also steepens the ground either side of a channel, so the bench pass
    // downstream turns the bigger valleys into walled gorges.
    // 0 disables the pass.
    [Export(PropertyHint.Range, "0,32,0.5")] public float drainageCarveDepth = 4.5f;
    // Flow (in contributing columns) at which carving reaches full depth. The
    // response is logarithmic — flow is heavy-tailed, so a linear map would put
    // the entire visible effect in the few trunk channels.
    [Export(PropertyHint.Range, "2,100000,1")] public float drainageFlowReference = 4000f;
    // Grade (voxels of rise per column) at which carving reaches full depth.
    // Water cuts where it runs fast, so incision scales with this as well as
    // with flow; without the term a wide flat basin accumulates huge flow and
    // sinks bodily, which near sea level simply drowns it.
    [Export(PropertyHint.Range, "0.01,2,0.01")] public float drainageSlopeReference = 0.13f;

    [ExportGroup("Faults")]
    // Fault blocks: the world is partitioned into irregular cells, each lifted
    // or dropped bodily, so every cell boundary is a scarp. Unlike benching,
    // this does not need a slope to act on — it puts real walls into flat and
    // gently sloping country, which is where noise-derived cliffs never reach.
    // Share of blocks left UNRAISED. The rest step up by one drawn wall
    // height, so this sets how much of the world the fault network walls off;
    // the height itself comes from the wall distribution below. 0 disables.
    [Export(PropertyHint.Range, "0,0.95,0.01")] public float faultRaisedFraction = 0.5f;
    // Cell size: lower = larger blocks and rarer, longer scarps. This is the
    // main control on how far the player can walk before meeting a wall.
    [Export(PropertyHint.Range, "0.0005,0.1,0.00005")] public float faultFrequency = 0.05f;
    // Breach field: scales each block's throw locally, so a scarp fades out
    // along its length instead of walling a block off completely. This is what
    // leaves passes through a fault line — without it, blocks are sealed. Its
    // slow variation also tilts blocks slightly, which reads as natural.
    // How far a block edge wanders, in voxels. 0 leaves the raw cell diagram,
    // whose straight edges read as map polygons rather than as terrain.
    [Export(PropertyHint.Range, "0,64,0.5")] public float faultEdgeWarp = 14f;
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float faultBreachFrequency = 0.006f;
    [Export(PropertyHint.Range, "-1,1,0.01")] public float faultBreachLow = -0.35f;
    [Export(PropertyHint.Range, "-1,1,0.01")] public float faultBreachHigh = 0.1f;

    [ExportGroup("Benching / Cliffs")]
    // Strata benching: within each band of BenchStep voxels the surface is
    // pushed toward a flat bench with a steep riser at the band's top. Gated by
    // slope (so flat ground is never carved into contour rings) and by a patchy
    // bedrock mask whose coverage is the zone's benchedFraction.
    //
    // The mask FREQUENCY is what enforces intermixing, and it is the field to
    // reach for when terrain plays badly: it sets the size of a terraced patch,
    // so it also sets how far the player can walk on unbroken slope. Keep the
    // patches well under the distance a region reads at.
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float benchMaskFrequency = 0.03f;
    // Half-width of the mask's smoothstep. Small keeps a patch decisively
    // terraced or decisively open; widening it produces half-benched ground,
    // whose risers land in the mantleable range and get ground back into slope.
    [Export(PropertyHint.Range, "0.01,0.5,0.01")] public float benchMaskEdge = 0.06f;
    // How far the zone's benchedFraction may shift the mask threshold. The
    // noise is ~zero-mean, so +/-0.35 spans nearly-no-terracing to
    // nearly-all-terracing across the authored 0..1 range.
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float benchMaskCenterRange = 0.35f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float benchMaxStrength = 1f;
    // Slope gate, in voxels of rise per horizontal column. Below Low no
    // benching at all; above High full strength.
    [Export(PropertyHint.Range, "0,4,0.01")] public float benchSlopeLow = 0.013f;
    [Export(PropertyHint.Range, "0,4,0.01")] public float benchSlopeHigh = 0.045f;
    // Fraction of each band spent on the riser; the rest is dead-flat bench.
    // This is the cliff knob — it compresses a band's whole rise into that
    // fraction of the horizontal run, multiplying the local slope by its
    // reciprocal, so 0.15 turns a gentle 0.3-voxel-per-column grade into a
    // 2-voxel-per-column wall. 1 = a plain ramp, i.e. no benching.
    [Export(PropertyHint.Range, "0.02,1,0.01")] public float benchRiserFraction = 0.15f;
    // Phase offset added to the height BEFORE banding (and not subtracted
    // after, so bench tops stay flat and lattice-aligned). Without it a band
    // boundary is an iso-height contour, which wraps any dome in concentric
    // rings — the artifact that makes a terraced mountain read as a contour map
    // instead of a landscape. Offsetting by a low-frequency field makes the
    // boundaries wander across the contours instead of tracing them. Amplitude
    // is in voxels; around half a bench step is a good starting point.
    [Export(PropertyHint.Range, "0,32,0.5")] public float benchPhaseAmplitude = 3.5f;
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float benchPhaseFrequency = 0.01f;

    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float benchStepFrequency = 0.002f;

    [ExportGroup("Walkability")]
    // The invariant the talus pass enforces: an adjacent column pair either
    // differs by at most MaxWalkableStep (a grade — walkable, and meshed
    // smooth because it matches WorldGenData.maxGradeStep) or by at least
    // CliffMinDrop (a wall — meshed crisp). Anything between is shaved back
    // into a grade, which also builds scree at the foot of steep ground.
    //
    // CliffMinDrop is a GAMEPLAY threshold, not an aesthetic one: a drop the
    // player can simply mantle back up is not a wall, it is decoration that also
    // happens to break up the ground. Keep it well clear of the mantle reach
    // (~2m) so every cliff the generator makes actually constrains movement, and
    // the in-between heights become honest walkable slope instead.
    [Export(PropertyHint.Range, "1,4,1")] public int maxWalkableStep = 1;

    // Steepest SUSTAINED grade an open slope may hold, as rise over run.
    // MaxWalkableStep alone cannot express this: it caps a single pair at one
    // voxel, so one-voxel-per-column — a 100% grade, 45 degrees — is legal for
    // as long as the hill lasts, and that plays badly. Ground steeper than this
    // is terraced into bench-and-cliff instead of left as a ramp.
    //
    // Voxels are integers, so the achievable sustained grades are 1/2 (50%),
    // 1/3 (33%), 1/4 (25%)...; a cap of 0.45 therefore resolves to at most one
    // step per three columns. Set it just above a fraction you want allowed,
    // not just below.
    [Export(PropertyHint.Range, "0.1,1,0.01")] public float maxWalkableGrade = 0.45f;
    [Export(PropertyHint.Range, "2,16,1")] public int cliffMinDrop = 4;

    // Tallest wall the world should contain — three storeys. Past this a cliff
    // stops reading as architecture the player can judge and becomes a sheer
    // face with no sense of scale. Enforced at the source (the bench step and
    // the fault throw are clamped into [CliffMinDrop, CliffMaxDrop], both after
    // the zone's cliffScale) and again in the relaxation, which catches the
    // leftovers where two wall-makers land on the same column.
    [Export(PropertyHint.Range, "4,64,1")] public int cliffMaxDrop = 12;

    // Shape of the wall-height distribution between the two bounds above.
    // 1 spreads walls evenly across the band; higher piles them onto the
    // floor and thins the tall tail. Around 3 puts ~40% of walls at exactly
    // CliffMinDrop, falling off to a rare few at CliffMaxDrop.
    //
    // This is the ONLY place wall height is decided — bench risers and fault
    // scarps both draw from it, so the world has one wall-height distribution
    // rather than one per system. A zone's cliffScale leans its draws toward
    // the tall end without leaving the band.
    [Export(PropertyHint.Range, "0.5,8,0.05")] public float wallHeightFalloff = 3f;
    [Export(PropertyHint.Range, "0,16,1")] public int talusPasses = 10;

    [ExportGroup("Coast")]
    // Far east of the world drops to ocean over this many chunks, reaching
    // OceanDepth voxels below sea level at the edge. Same shape as the legacy
    // path's shoreline falloff, in voxels instead of plateau steps.
    [Export(PropertyHint.Range, "0,32,1")] public int shorelineChunks = 4;
    [Export(PropertyHint.Range, "0,64,0.5")] public float oceanDepth = 5.5f;
}
