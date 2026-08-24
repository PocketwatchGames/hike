using Godot;

// Tuning for the CELLULAR terrain approach (see CellularTerrainGen). Assigning
// one of these to WorldGenData.terrain selects that approach; nothing here is
// read by any other approach.
//
// Heights are in VOXELS relative to sea level. The one exception is the
// inherited per-zone elevation / elevationRange pair, which stays in its
// authored unit and is converted by zoneElevationUnit, so a zone asset means
// the same height it did under the other approaches.
//
// The shape is built in five passes (see CellularTerrainGen):
//   1. a continuous field — domain-warped continental base plus ridged relief
//      scaled by the zone's elevationRange, dropped to the sea floor wherever a
//      SEPARATE continent field puts the coast;
//   2. cellular partition — jittered-grid Voronoi cells, each subdivided again
//      wherever the field inside it spans more than subdivideRange, so busy
//      country ends up with small cells and quiet country with large ones;
//   3. flattening — every cell takes the MEDIAN of the field over its own
//      columns, quantized to quantizeStep, then relaxed so no cell stands more
//      than maxCellStep above a neighbour. The coast is inside the field by
//      now, so the beach terraces on the same lattice as everything else;
//   4. a spanning network over the cell graph — the routes roads will take.
//      Cells along it subdivide to the ramp's own width;
//   5. ramps — each network edge with a wall to climb gets one narrow cutting,
//      sloped along its length and flat across its width;
//   6. rivers and lakes — humidity-weighted rain routed over the FINISHED
//      terraces. Channels are cut where the flow runs over ground at its own
//      water level, sinks either fill as lakes or get their rim breached, and
//      every water surface lands on the same lattice the land does.
//
// The result is mesa country: flat terraces separated by walls, with a
// connected network of narrow ramps threading between them. Ramps are the ONLY
// sloped ground; everywhere else a column sits at a multiple of quantizeStep
// and every adjacent pair is either equal or a whole-step wall. That is the
// point of it, not an incidental property — a terrace the player can read at a
// glance is worth more than a hillside.
[GlobalClass]
public partial class CellularTerrainData : TerrainGenData
{
    public override ITerrainGenerator CreateGenerator(WorldGenData genData, int worldSeed)
    {
        return new CellularTerrainGen(this, genData, worldSeed);
    }

    // Voxels per unit of the inherited ZoneTerrainData elevation /
    // elevationRange. This scales the BASE height a zone sits at — deliberately
    // small, because the island is meant to lie near sea level and take its
    // relief from reliefAmplitudeScale instead. Every non-swamp zone is authored
    // at elevation 1, so this alone decides how far the whole landmass floats
    // above the waterline; at 2 that is one quantize step.
    [Export(PropertyHint.Range, "1,16,1")] public int zoneElevationUnit = 2;

    // The world's vertical lattice for ENCLOSED space, in voxels: building
    // floors, and cave / tunnel ceilings once carving returns. Independent of
    // quantizeStep on purpose — the open-air terraces and the interior lattice
    // answer to different consumers, and tying them together means retuning the
    // terrain moves every building floor in the world.
    [Export(PropertyHint.Range, "1,16,1")] public int interiorLevelStep = 4;

    [ExportGroup("Domain Warp")]
    // Every channel — the continental base, the relief, and the cell lookup
    // itself — is sampled at coordinates displaced by this field. Warping the
    // CELL lookup is what turns the Voronoi diagram from a map of polygons into
    // landforms with wandering, interlocking borders. Amplitude is in voxels.
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float warpFrequency = 0.004f;
    [Export(PropertyHint.Range, "0,256,0.5")] public float warpAmplitude = 60f;

    [ExportGroup("Continental Base")]
    // Broad basins and swells the rest of the terrain rides on. The only
    // channel with real reach, so keep the frequency very low.
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float macroFrequency = 0.005f;
    [Export(PropertyHint.Range, "1,6,1")] public int macroOctaves = 3;
    [Export(PropertyHint.Range, "0,64,0.5")] public float macroAmplitude = 4f;

    [ExportGroup("Relief")]
    // Ridged relief: 1 - |fbm|, so the field peaks along the noise's zero
    // crossings. Those crossings branch like a drainage divide, which is what
    // stops the cell medians from reading as a field of unrelated plateaus —
    // the terraces inherit the ridge lines running under them. Relief only ever
    // ADDS, so valley floors sit at the base level and ridges rise off it.
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float reliefFrequency = 0.035f;
    [Export(PropertyHint.Range, "1,8,1")] public int reliefOctaves = 4;

    // Exponent on the ridged field. 1 = rounded whalebacks; higher pinches the
    // crests into narrow ridges with wide valleys between them.
    [Export(PropertyHint.Range, "0.5,6,0.05")] public float reliefSharpness = 2f;
    // Peak relief height = zone elevationRange^reliefRangeExponent *
    // zoneElevationUnit * this.
    [Export(PropertyHint.Range, "0,4,0.05")] public float reliefAmplitudeScale = 3.4f;

    // Exponent on the zone's elevationRange before it becomes relief height.
    // Sets how sharply height is RESERVED for the mountains. The authored ranges
    // only span 2 (lowland) to 4 (mountain); at 1 that is a 2:1 ratio, at 2 it
    // is 4:1. High values leave the lowlands within a step or two of sea level —
    // which reads as an island with one interesting region and three flat ones,
    // so keep it low enough that the lowlands still get slopes of their own and
    // raise reliefAmplitudeScale to hold the mountains up.
    [Export(PropertyHint.Range, "0.5,4,0.05")] public float reliefRangeExponent = 1.2f;

    // Highland mask: a low-frequency field that modulates how much relief a
    // part of the island gets, so some of it is plain and some is broken
    // country. It scales an AMPLITUDE and never adds a step of its own, which
    // is the design rule here — a wall has to come from the cells quantizing a
    // genuinely steep patch of field, so the way to get more walls is to make
    // the field steeper (reliefAmplitudeScale, reliefFrequency), never to inject
    // an edge. Raise MaskLow for more plains, lower it for more highlands; the
    // gap between Low and High sets how quickly one becomes the other.
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float reliefMaskFrequency = 0.0045f;
    [Export(PropertyHint.Range, "-1,1,0.01")] public float reliefMaskLow = -0.5f;
    [Export(PropertyHint.Range, "-1,1,0.01")] public float reliefMaskHigh = -0.05f;

    [ExportGroup("Cells")]
    // Edge length of a level-0 cell, in voxels (= metres). This is the coarsest
    // terrace the world contains and the main control on how far the player
    // walks before meeting a wall; subdivision only ever makes cells smaller.
    [Export(PropertyHint.Range, "8,256,1")] public float cellSizeMeters = 72f;

    // How far a cell's site wanders from its grid slot, as a fraction of the
    // spacing. 0 gives a regular lattice of near-identical cells (reads as a
    // grid however much the coordinates are warped); 1 lets sites reach their
    // slot's edge, which is where the irregular, interlocking shapes come from.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cellJitter = 0.85f;

    // Smallest terrace the VARIANCE subdivision may produce, in voxels. Cells
    // halve until either they are honest about the ground under them or they
    // reach this, so it is the floor on ordinary terrace size. Ramp corridors
    // subdivide past it — that is their own target, below.
    [Export(PropertyHint.Range, "4,128,1")] public float minCellSizeMeters = 36f;

    // A cell subdivides when the underlying field inside it spans more than
    // this many voxels — i.e. when one flat top would misrepresent the ground
    // it covers. This is the adaptive part: quiet country keeps big cells,
    // and a mountain flank (which spans far more than this over one cell)
    // subdivides until each piece is honest about the slope it sits on.
    [Export(PropertyHint.Range, "1,64,0.5")] public float subdivideRange = 6f;

    // Smallest cell allowed to keep its own elevation, in columns. Anything
    // under this is folded into the neighbour it shares the longest border
    // with. The nesting rule splits a fine cell at its coarse parent's border,
    // and the far piece can be a single column — which then takes its own
    // median and quantizes independently, leaving a one-voxel spike standing in
    // open ground. 16 columns is a 4 m cell, the smallest that reads as ground.
    [Export(PropertyHint.Range, "1,256,1")] public int minCellColumns = 16;

    // How much a cell's own appetite for splitting varies, 0..1. At 0 cell size
    // is a pure function of how busy the ground is, so a region of even relief
    // comes out as a field of near-identical terraces — regular in a way that
    // reads as procedural. Raising it scatters the threshold per cell, so quiet
    // country still holds a mix of big and small tops. 1 spans thresholds from
    // zero (always splits) to double (rarely splits).
    [Export(PropertyHint.Range, "0,1,0.01")] public float cellSizeRandomness = 0.8f;

    [ExportGroup("Drainage")]
    // Flow-accumulation valley carving: every column sheds one unit of rain
    // downhill (steepest of 8), and the surface is cut in proportion to the flow
    // crossing it. This is the one channel whose shape comes from the terrain
    // rather than from a noise field, which is what makes its valleys branch and
    // meet the way real ones do — and what stops the cell field reading as a
    // collection of unrelated plateaus. 0 disables the pass.
    [Export(PropertyHint.Range, "0,32,0.5")] public float drainageCarveDepth = 3.5f;

    // Flow (in contributing columns) at which carving reaches full depth. The
    // response is logarithmic — flow is heavy-tailed, so a linear map would put
    // the entire visible effect in the few trunk channels.
    [Export(PropertyHint.Range, "2,100000,1")] public float drainageFlowReference = 4000f;

    // Grade (voxels of rise per column) at which carving reaches full depth.
    // Water cuts where it runs fast, so incision scales with this as well as
    // with flow; without the term a wide flat basin accumulates huge flow and
    // sinks bodily, which near sea level simply drowns it.
    [Export(PropertyHint.Range, "0.01,2,0.01")] public float drainageSlopeReference = 0.13f;

    // Catchment, in contributing columns, a channel must carry before it incises
    // anything. This is the knob that makes the pass a river network rather than
    // a general lowering: without it every column carries flow >= 1 and carves a
    // little, and since all of an island's flow converges on its coast — where
    // the shelf is also the steepest ground in the world — the heaviest flow met
    // the steepest grade and shredded the whole shoreline into radial gullies.
    // Raise it for fewer, larger gorges.
    [Export(PropertyHint.Range, "1,20000,1")] public float drainageMinFlow = 400f;

    // Incision depth, in voxels, at which a cell counts as eroded and refines to
    // erosionCellSizeMeters. A valley is where a flat top is most obviously
    // wrong — channel and banks land in one cell, the median picks whichever
    // won, and the valley either fills in or swallows its banks. Refining turns
    // it into a terraced gorge instead.
    [Export(PropertyHint.Range, "0.1,16,0.1")] public float erosionSubdivideDepth = 1.5f;
    [Export(PropertyHint.Range, "4,128,1")] public float erosionCellSizeMeters = 24f;

    // How strongly a zone's authored humidity (ZoneData.weather.humidity)
    // weights the rain each of its columns sheds — the same weights the river
    // pass below routes. 0 gives every column one unit, as before. 1 makes the
    // weight proportional to humidity, so the swamp (0.95) sheds nineteen times
    // what the desert (0.05) does and grows the trunk channels to match.
    //
    // The field is NORMALISED to a mean of 1 over land before it accumulates,
    // so raising this REDISTRIBUTES flow between wet and dry country without
    // changing the world's total — which is what lets drainageMinFlow and
    // riverMinFlow keep their meaning when it is retuned.
    [Export(PropertyHint.Range, "0,1,0.01")] public float rainfallHumidityWeight = 0.8f;

    [ExportGroup("Rivers & Lakes")]
    // Real water voxels, cut into the FINISHED terraces (unlike the drainage
    // pass above, which only shapes valleys in the pre-cell field). Rain routed
    // over a depression-filled copy of the final heights gives both at once: a
    // channel where the flow runs over ground, and a flooded basin wherever it
    // runs into a sink.
    //
    // The water surface is always a multiple of quantizeStep, never sloped. On
    // a terrace top that makes the river a flat channel at the terrace's level;
    // where it meets a wall the fill behind the wall raises the pool to the
    // crest and the surface drops a whole step or more on the far side — one
    // rule producing pools, gorges and cascades.
    //
    // Catchment, in rain-weighted columns, a channel must carry before it holds
    // water. The world is ~74k columns, so this is a fraction of a percent of
    // it — raise for fewer, larger rivers. 0 disables the whole pass.
    [Export(PropertyHint.Range, "0,20000,10")] public float riverMinFlow = 900f;

    // Depth of the channel cut below the water surface, in voxels — rounded UP
    // to a whole quantizeStep so a carved bed stays on the world's lattice.
    // This is the only terrain this pass moves on ground that is already at the
    // water's own level; a pool over lower ground is filled, never dug.
    [Export(PropertyHint.Range, "1,16,1")] public int riverDepth = 2;

    // Channel half-width in voxels at riverMinFlow and at riverWidthFullFlow.
    // Interpolated logarithmically in flow, because flow is heavy-tailed and a
    // linear map would leave every tributary one column wide while the trunk
    // swallowed a terrace.
    [Export(PropertyHint.Range, "0.5,16,0.5")] public float riverHalfWidthMin = 1.5f;
    [Export(PropertyHint.Range, "0.5,32,0.5")] public float riverHalfWidthMax = 4f;
    [Export(PropertyHint.Range, "10,100000,10")] public float riverWidthFullFlow = 20000f;

    // How far the width may wander off that flow curve, as a fraction of it —
    // 0.5 means a reach can come out anywhere from half to one-and-a-half times
    // the width its catchment alone would give. Flow grows monotonically
    // downstream, so without this a river is a ribbon that only ever widens,
    // and the eye reads the smooth taper as a road rather than as water. The
    // noise is what puts pools and narrows along one reach.
    [Export(PropertyHint.Range, "0,1,0.01")] public float riverWidthNoise = 0.5f;

    // Wavelength of that wander, as a frequency in columns^-1. Keep it LONG
    // relative to riverHalfWidthMax: the channel is stamped as a disc per
    // column and the discs union, so a narrow between two wide columns is
    // simply filled in by its neighbours' discs. At the default 4 m half-width
    // a narrow only survives if it lasts ~8 columns, so anything above ~0.06
    // (a 16 m period) averages back out to a plain flow-width river.
    [Export(PropertyHint.Range, "0.001,0.2,0.0005")] public float riverWidthNoiseFrequency = 0.02f;

    // Surface current speed at riverMinFlow and at riverWidthFullFlow, in the
    // normalized [0,1] units ChunkState stores per env cell — the water shader
    // multiplies by its own `water_current_speed` global to reach m/s, so these
    // set the RATIO between a trickle and the trunk, not an absolute speed.
    // Interpolated on the same log-flow curve the width is, so one reach is
    // never wide and still.
    [Export(PropertyHint.Range, "0,1,0.01")] public float riverCurrentMin = 0.35f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float riverCurrentMax = 1f;

    // What share of that speed a LAKE column keeps. A lake on a trunk river
    // genuinely drifts toward its outlet, so zero reads as dead; full speed
    // reads as a river that happens to be 60 m wide. Applies to flooded basins
    // only — a channel crossing one is still a channel.
    [Export(PropertyHint.Range, "0,1,0.01")] public float lakeCurrentScale = 0.2f;

    // How far ABOVE the water surface the channel may cut its banks back, in
    // voxels. Along the river's own path the surface never sits below the
    // ground (the depression fill guarantees it), so this only bounds the
    // widening: a channel eats through a one-step lip beside it and stops at a
    // real wall instead of gouging a trench across it.
    [Export(PropertyHint.Range, "0,16,1")] public int riverBankCut = 2;

    // A flooded basin needs this many columns and this much depth at its
    // deepest before it counts as a lake. Below either it is a puddle in a
    // terrace's quantization noise, and stamping water there speckles the
    // world. A basin the river network runs through still has to clear them —
    // the channel simply passes through as a channel instead.
    [Export(PropertyHint.Range, "1,4096,1")] public int lakeMinColumns = 40;
    [Export(PropertyHint.Range, "1,32,1")] public int lakeMinDepth = 2;

    // Catchment a sink needs before it holds water. MUCH lower than
    // riverMinFlow, and separately authored for a reason: a channel has to carry
    // enough flow to run, but a hollow only has to catch more than it loses, so
    // reusing the river threshold vetoed 25 of this world's 30 sinks as "dry"
    // and left it with no lakes at all. Raise it if ponds appear in places with
    // no visible reason to be wet.
    [Export(PropertyHint.Range, "1,20000,10")] public float lakeMinFlow = 120f;

    // Largest share of the world one lake may cover. A wide, nearly level
    // basin can fill an implausible amount of the island off one spill point;
    // past this the basin is left dry and only its channel is cut. Logged when
    // it fires, because it means the terrain, not the tuning, produced
    // something odd.
    [Export(PropertyHint.Range, "0.001,0.5,0.001")] public float lakeMaxWorldFraction = 0.03f;

    [ExportGroup("Flattening")]
    // Share of a cell that must belong to an authored flattenSurface zone (the
    // village) before that cell is stamped to the zone's single level. The zone
    // kernel blends out over several chunks, so a low value flattens a wide
    // apron of countryside around the buildings — raise it to keep the clearing
    // tight to what the settlement actually needs.
    [Export(PropertyHint.Range, "0.1,1,0.01")] public float flattenCellWeight = 0.9f;
    // Vertical step every cell top snaps to, in voxels. Cell tops are the
    // world's readable ground, so they all sit on multiples of this — which is
    // also what makes the wall between two neighbours a whole number of steps
    // rather than an arbitrary height.
    [Export(PropertyHint.Range, "1,8,1")] public int quantizeStep = 2;

    // Tallest wall between two adjacent cells, in voxels — three storeys. Past
    // this a cliff stops reading as something the player can judge and becomes
    // a sheer face with no sense of scale. Enforced by lowering the high cell
    // (so the excess becomes another terrace further up, not one taller wall),
    // which is why it is a cell-graph rule rather than a per-column one.
    [Export(PropertyHint.Range, "2,64,1")] public int maxCellStep = 12;

    [ExportGroup("Ramps")]
    // A spanning network over the cell graph marks the routes roads will take.
    // Every edge of it that has a wall to climb becomes a RAMP: a narrow cutting
    // through the border, sloped along its length and flat across its width, and
    // the cells it passes through subdivide down to its own width so the ground
    // beside it steps down alongside instead of standing as one wall.
    //
    // The ramp is the ONLY sloped ground this approach makes. Everything else is
    // flat cell tops and the walls between them.
    //
    // Fraction of the non-tree cell edges added to the spanning network, on top
    // of the minimum spanning tree that guarantees every cell is reachable. 0
    // leaves a pure tree, where every route in and out of a dead-end cell is the
    // same route; a little redundancy is what gives the road network loops and
    // alternate passes.
    [Export(PropertyHint.Range, "0,2,0.01")] public float extraEdgeFraction = 0.6f;

    // Shortest wall that earns a ramp, in voxels. Below it the player steps or
    // mantles up unaided, so a cutting is clutter — and at one quantize step the
    // ramp comes out shorter than it is wide.
    [Export(PropertyHint.Range, "1,16,1")] public int rampMinDrop = 4;

    // Ramp width in voxels, rolled per ramp. This also sets how far the cells
    // along a route subdivide — a terrace the ramp's own width, so the ramp is
    // cut through fine ground rather than gouged across one big mesa. Keep it
    // near a road's width; much wider and the cutting reads as a valley.
    [Export(PropertyHint.Range, "2,32,1")] public float rampWidthMinMeters = 4f;
    [Export(PropertyHint.Range, "2,32,1")] public float rampWidthMaxMeters = 8f;

    // Steepest a ramp may climb, in voxels of rise per column. The ramp's LENGTH
    // follows from this and the drop it has to cover, so lowering it lengthens
    // ramps rather than steepening them. Keep at or below maxGradeStep (1) or
    // the ramp hardens into visible stairs instead of meshing as a grade.
    [Export(PropertyHint.Range, "0.1,1,0.05")] public float rampGrade = 0.5f;

    // Terrace size along a ramp corridor. Refining the route is what lets a
    // ramp thread short steps instead of gouging one long trench across a big
    // terrace — but it is applied to every cell the corridor touches, across
    // the whole network, so setting it to the ramp's own width shreds the
    // partition (measured: 137 cells -> 3425). Keep it a fraction of the
    // ordinary terrace size, not a fraction of the ramp.
    [Export(PropertyHint.Range, "4,128,1")] public float rampCellSizeMeters = 18f;

    // Longest one ramp may run either side of the border it crosses, in voxels.
    // A drop that would need more than this gets a steeper ramp rather than one
    // that trenches across a whole terrace.
    [Export(PropertyHint.Range, "4,256,1")] public float rampMaxHalfLength = 40f;

    [ExportGroup("Island")]
    // The world is an island, and the land/sea decision is made by its OWN noise
    // field rather than by a radial falloff. That separation is the point: a
    // radial mask makes elevation fall off with distance from the centre, so the
    // continent comes out a dome with its coast at the bottom of a long slope.
    // Here the coastline is a contour of this field and nothing more — height is
    // decided entirely by the zones, so the shore sits at whatever the land
    // beside it sits at, and an island that is mostly lowland has a shoreline
    // you can walk off.
    //
    // Octaves are what make the coast interesting: one gives a blobby continent,
    // three or four give bays, peninsulas and offshore islets.
    [Export(PropertyHint.Range, "0,0.05,0.00005")] public float continentFrequency = 0.0055f;
    [Export(PropertyHint.Range, "1,6,1")] public int continentOctaves = 4;

    // Field value that counts as the waterline. Raise it to shrink the island
    // and break more of it into islets, lower it to fill the map.
    [Export(PropertyHint.Range, "-1,1,0.01")] public float continentSeaLevel = -0.3f;

    // How strongly the landmass is pulled toward the middle of the map, as a
    // subtraction from the field at the map's corner. Pure noise is free to put
    // its land anywhere, and the zones are laid out by quadrant and cannot move
    // to meet it — without this a whole authored zone regularly ends up under
    // water. It biases where the WATER is, never how high the ground stands, so
    // it centres the island without the dome a radial height falloff makes. 0
    // leaves the coastline entirely to the noise.
    [Export(PropertyHint.Range, "0,2,0.01")] public float continentCenterBias = 1.2f;

    // How strongly high ground resists being drowned, per voxel of land height.
    // Two jobs. It keeps the mask from putting open sea over a whole authored
    // zone — the zones are fixed quadrants and cannot move to meet the coast, so
    // a blind mask regularly sank the mountains. And it is what makes the
    // shoreline interesting: the coast follows the relief, cutting bays into low
    // ground and leaving headlands where ridges run out to meet the water. 0
    // decouples them again and leaves the coastline to the noise alone.
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float continentHeightBias = 0.03f;

    // Half-width of the shore blend, in field units. This is the only place the
    // coast slopes: outside it the ground is at its zone's height, inside it
    // drops to the sea floor. Narrow reads as a cliff coast, wide as a beach —
    // but the cells quantize whatever it produces, so a wide value gives a run
    // of beach terraces rather than a smooth ramp.
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float shoreBand = 0.07f;

    // How far inland the RELIEF fades in, in field units. This is what brings
    // land to the water near sea level: within this band the ground is at its
    // zone's base height and nothing more, so the shore is a low step rather
    // than a hillside running into the sea. It deliberately does not touch the
    // uplift — an uplifted block that reaches the coast keeps its full throw and
    // meets the water as a CLIFF, which is where shore cliffs come from. Taper
    // both and every coast is a gentle beach; taper neither and the shore is the
    // steepest ground in the world.
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float coastalPlainBand = 0.3f;

    // Ocean margin the map border is guaranteed, in chunks. The continent field
    // is pushed under the waterline across this band so the world is ringed by
    // water however the noise fell — without it an island can run off the edge.
    [Export(PropertyHint.Range, "0,16,1")] public int edgeMarginChunks = 2;

    // How deep the sea floor sits below sea level, in voxels.
    [Export(PropertyHint.Range, "0,64,0.5")] public float oceanDepth = 3.5f;

    [ExportGroup("Offshore Islands")]
    // Islands are injected into the CONTINENT MASK, before the cells see it —
    // not stamped into the height field afterwards. Everything downstream
    // (partition, median flattening, the quantizeStep lattice, the coastal
    // relief taper, sliver merge) then treats an island as ordinary land, so it
    // terraces like the mainland for free. A post-hoc height stamp would sit
    // outside all of that and read as a foreign object dropped in the sea.
    //
    // Placed by COUNT, like every landform here. Logged when fewer than asked
    // for fit — the candidate rules below are strict and a crowded coastline
    // can genuinely leave nowhere to put one.
    [Export(PropertyHint.Range, "0,32,1")] public int islandCount = 3;

    // Island radius in METRES: where the falloff added to the mask reaches
    // zero. Two bounds, from opposite directions.
    //
    // The floor is minCellColumns (16 columns): an island whose whole area fits
    // inside one sliver is folded into the surrounding seabed cell by
    // MergeSlivers and vanishes with no other symptom. A radius of 12 covers
    // ~450 columns, well clear of it.
    //
    // The ceiling is how much open water the world actually has. Measured on
    // the default world, the ocean ring is narrow — no sea column is more than
    // ~40 voxels from the map border — and an island must clear
    // edgeMarginChunks (32 voxels) by its full radius, so anything past ~12
    // leaves nowhere legal to put one. The log says which test did the
    // rejecting; read it before raising this.
    [Export(PropertyHint.Range, "4,200,1")] public float islandRadiusMeters = 12f;

    // The continent-mask value the island's CENTRE is pulled to — a target, not
    // an amount added. The distinction matters and cost a round of debugging:
    // an additive strength has to be large enough to lift the deepest legal site
    // above the waterline, which then overshoots at every shallower one. At 0.55
    // it pushed island centres to ~0.45, past coastalPlainBand (0.3), so the
    // taper that is supposed to make an islet low and flat did not apply at all
    // — the islands came out at full relief, raised the world's ceiling, and
    // (because the landform pass caps mesas against that ceiling) pulled all
    // three mesas up with them. A target lands every island in the same place on
    // the mask however deep the water under it was.
    //
    // Read it against the two bands it sits between: above shoreBand it is land,
    // below coastalPlainBand the relief taper still applies. Near the bottom of
    // that gap gives a low flat islet; near the top, a bolder one.
    [Export(PropertyHint.Range, "0,2,0.005")] public float islandMaskPeak = 0.12f;

    // How far BELOW the waterline a candidate's mask value must already sit, in
    // field units. This is what stops an "island" spawning onto the mainland's
    // own shelf and reading as a lumpy peninsula instead of an island.
    [Export(PropertyHint.Range, "0,1,0.01")] public float islandSeaMargin = 0.08f;

    // Distance band from the mainland shore, in voxels. Close enough to read as
    // an archipelago belonging to the island, far enough to be its own thing.
    [Export(PropertyHint.Range, "0,512,1")] public float islandShoreDistanceMin = 12f;
    [Export(PropertyHint.Range, "0,512,1")] public float islandShoreDistanceMax = 110f;

    // Minimum spacing between two placed islands (or sea stacks), centre to
    // centre, in voxels.
    [Export(PropertyHint.Range, "0,512,1")] public float islandSpacingMeters = 32f;

    // SEA STACKS are the same mechanism with two changes: a small radius, and
    // the coastal relief taper SKIPPED inside them. An ordinary island runs
    // through coastalPlainBand and comes out low and flat, which is right for an
    // islet; a stack keeps its full relief and meets the water as a tall rock.
    [Export(PropertyHint.Range, "0,32,1")] public int seaStackCount = 2;
    [Export(PropertyHint.Range, "4,80,1")] public float seaStackRadiusMeters = 8f;

    // Relief a stack site must carry, in voxels, measured as the tallest relief
    // anywhere under the disc rather than at its centre — the field is ridged,
    // so a site whose centre is quiet can still have a crest a few columns away
    // and it is the crest that decides how the stack comes out. Skipping the
    // coastal taper only helps where there is relief to keep; below this a
    // stack is as flat as an islet and the site is better spent on one.
    [Export(PropertyHint.Range, "0,32,0.5")] public float seaStackMinRelief = 3f;

    // Tallest a stack may stand above sea level, in voxels. This is a WORLD SIZE
    // budget, not a look: with the taper off a stack's height is unbounded, and
    // FitVerticalExtent sizes the world to the finished height field, so one
    // tall stack lifts the world's ceiling and every column pays for the extra
    // chunk layer. Worse, it does it twice over — the landform pass caps mesas
    // against the world's existing ceiling, so a stack that raises that ceiling
    // silently licenses taller mesas as well. Measured: one stack took the
    // world from 4 chunks tall to 5 and pulled all three mesas up with it.
    // Keep this at or below the mainland's own maximum.
    [Export(PropertyHint.Range, "1,64,1")] public float seaStackMaxHeight = 14f;

    [ExportGroup("Landforms")]
    // Placed landforms: one shared pass picks N cells matching a topological
    // rule, applies an effect and PINS the result. All by COUNT, never by
    // per-cell probability — these are meant to be notable and infrequent, and
    // a count is the only way an author can say "three mesas in this world".
    //
    // Every one of them is applied AFTER the final RelaxCellWalls, which is the
    // only reason they survive: relax lowers any cell standing more than
    // maxCellStep above a neighbour and would flatten a mesa on its next pass.
    // They also take no ramps — a cutting through a mesa is exactly the thing
    // that stops it reading as a mesa.

    // MESA — a cell already standing at or above all its neighbours, lifted
    // clear of them. The raise is measured from the TALLEST neighbour, so the
    // shortest of its walls is this and the others are taller by however much
    // its neighbours differ (bounded by mesaNeighbourSpread below).
    [Export(PropertyHint.Range, "0,32,1")] public int mesaCount = 3;
    [Export(PropertyHint.Range, "2,32,1")] public int mesaRaise = 8;

    // How much a candidate's neighbours may differ among themselves, in voxels.
    // Shared by every rule below, and it is what keeps the walls inside the
    // world's 4–12 band: the tallest wall a landform ends up with is its own
    // raise or drop plus this, so the pair of them is the real ceiling. It also
    // picks better sites — a cell whose neighbours are all at one level reads as
    // a mesa, one perched on a hillside reads as a lump.
    //
    // Whatever this says, no landform is allowed to leave a wall taller than
    // maxCellStep; a candidate that would is passed over rather than clamped.
    [Export(PropertyHint.Range, "0,32,1")] public int landformNeighbourSpread = 4;

    // QUARRY — the mesa rule inverted: a cell at or below all its neighbours,
    // dropped several steps clear of them so it reads as a pit rather than a
    // dip. Deliberately NOT pinned against the water pass: a quarry is a sink,
    // and letting the river network either flood it or notch its rim is how a
    // real one ends up either a pool or a dry cut.
    [Export(PropertyHint.Range, "0,32,1")] public int quarryCount = 2;
    [Export(PropertyHint.Range, "2,32,1")] public int quarryDrop = 8;

    // CRATER — a ring of cells raised around a dropped floor, which is the one
    // landform here that needs the cell GRAPH rather than a single cell. The
    // rim is levelled to a common height first, so the ring reads as one lip
    // instead of the ground it was made from.
    [Export(PropertyHint.Range, "0,16,1")] public int craterCount = 1;
    [Export(PropertyHint.Range, "2,32,1")] public int craterRimRaise = 4;

    // Floor depth below the finished rim, in voxels. The floor is kept above
    // the waterline whatever this says — a crater flooded by the sea is just a
    // bay, and the interesting version is the one you can walk into.
    [Export(PropertyHint.Range, "4,48,1")] public int craterDepth = 10;

    // TERRACED STAIRS — a CHAIN of adjacent cells set exactly one quantizeStep
    // apart, walked through the cell graph. The only landform that is a route
    // rather than a place: every step is a 2-voxel lip the player steps over
    // unaided, so a flight of them climbs ground that would otherwise need a
    // ramp. Length is in CELLS.
    [Export(PropertyHint.Range, "0,16,1")] public int terraceStairCount = 2;
    [Export(PropertyHint.Range, "3,24,1")] public int terraceStairCells = 6;

    [ExportGroup("Land Bridges")]
    // A ribbon of solid ground spanning a gap, hollow beneath, arched over the
    // middle. NOT a cell feature: expressed as cells, MergeSlivers would fold
    // anything under minCellColumns away and RelaxCellWalls would drag the deck
    // down toward the gap floor under it. It is a direct per-column height write
    // after both passes — the same trick, and the same place in the pipeline,
    // that CutRamps already relies on.
    //
    // A candidate is a pair of cells at (or within one step of) the same level
    // that are NOT adjacent; the segment between their centroids is then trimmed
    // to the gap it actually crosses, and THAT is the bridge.
    [Export(PropertyHint.Range, "0,32,1")] public int landBridgeCount = 2;

    // Deck width in voxels, and the length of the GAP the deck crosses —
    // abutment to abutment, not centroid to centroid. Most of the distance
    // between two cells is their own ground, so a limit read off the centroids
    // measures mostly solid earth: it would reject a short crossing between two
    // large cells and pass a long one between two small ones. A gap shorter
    // than the minimum is one the player can walk around and the bridge is clutter;
    // longer than the maximum and it stops reading as one structure.
    [Export(PropertyHint.Range, "2,32,1")] public float bridgeWidthMeters = 6f;
    [Export(PropertyHint.Range, "6,128,1")] public float bridgeSpanMin = 10f;
    [Export(PropertyHint.Range, "6,128,1")] public float bridgeSpanMax = 30f;

    // How far the ground under the span must drop below the deck before it
    // counts as a gap worth bridging, in voxels. Also what the abutment walk
    // looks for: the gap is the run of ground this far under the deck, and the
    // columns either side of that run are where the bridge lands.
    [Export(PropertyHint.Range, "2,48,1")] public int bridgeGapDepth = 6;

    // How high the crown of the deck stands above its abutments, as a fraction
    // of the span. 0 is a flat plank. Proportional rather than absolute because
    // "mildly arched" is a statement about shape: a fixed rise that reads as a
    // gentle hump over 30m is a hillock over 12m. The arch is built from the
    // world's own vocabulary — flat treads on the quantizeStep lattice joined by
    // risers — so a rise under one step cannot express itself and comes out flat.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float bridgeArchGrade = 0.15f;

    // Chance that any ONE riser in an arch is spread into a 1-voxel-per-column
    // grade instead of standing as a crisp 2m step. Rolled per riser, not per
    // bridge, so a single arch mixes the two — step up off the ground, grade to
    // the crown, step down the far side. At 0 every deck is a flight of steps,
    // at 1 every riser that has room becomes a grade.
    [Export(PropertyHint.Range, "0,1,0.05")] public float bridgeArchSlopeChance = 0.5f;

    // Shortest tread, in columns, that may give up columns to spread a riser;
    // one with less room either side stays a step whatever the chance says.
    // Geometry has the veto here — the shortest treads in an arch are the two at
    // the abutments, so this is really the knob deciding whether a deck may
    // GRADE onto its abutment or must step onto it. Measured on the default
    // world: at 4 the abutment risers are always steps and short bridges never
    // grade at all; at 3 the split over ten spans is even.
    [Export(PropertyHint.Range, "2,16,1")] public int bridgeArchSlopeTread = 3;

    // Thickness of the deck, in voxels — the solid slab left under the walking
    // surface once the air below it is carved out. MINIMUM 2: a one-voxel deck
    // is the plateau approach's floating-slab failure, a roof with nothing
    // holding it up and nothing under it to read as structure.
    [Export(PropertyHint.Range, "2,16,1")] public int bridgeThickness = 3;

    [ExportGroup("Cliff Erosion")]
    // Tall cliff faces in this world are single flat planes: a cell border is
    // one vertical drop the whole way along it, and the tallest of them — the
    // coastal ones falling from the land to the sea floor — are the largest bare
    // surfaces in the world. This pass erodes the outermost metre or two of
    // them, which in a heightfield means two things at once:
    //
    //   BITE  — lower a shallow band back from the lip, in a stepped profile, so
    //           the face becomes a small terrace and a shorter drop. Where the
    //           bite is zero the lip is untouched and stands proud as a FINGER
    //           between the eroded runs. That contrast is the whole effect: the
    //           bitten runs read as eroded gullies and the runs left alone as
    //           the ridges between them.
    //   TALUS — raise scattered columns at the FOOT of the face by one step, a
    //           broken apron of rubble where the eroded material went.
    //
    // Both are direct per-column writes for the same reason land bridges are: as
    // cells, MergeSlivers would fold a three-column shelf away and
    // RelaxCellWalls would level it back into the terrace it came from. Both
    // keep the lattice — every change is a whole number of quantizeSteps — and
    // neither touches ramps, the village or a landform.

    // Shortest cliff worth eroding, in voxels. At 4 the world's most common wall
    // is included, and the height normalisation below handles it on its own: a
    // 4-voxel face scales to a cut-back of 0 or 1 columns, so most of them are
    // left alone and the rest get a single column nicked off. Below 4 there is
    // nothing left to cut that would not simply erase the terrace edge.
    [Export(PropertyHint.Range, "2,32,1")] public int cliffErosionMinDrop = 4;

    // Shortest cliff that may be SHAPED — stepped or sloped. Under it a cut goes
    // straight down to the cliff base instead: the edge retreats in plan and the
    // wall keeps its whole height.
    //
    // That distinction is a gameplay one, not a look. A 4-voxel wall is meant to
    // stop the player, and ANY horizontal surface introduced part-way down it is
    // a ledge to climb. So short walls get the noisy edge with no new horizontal
    // surface anywhere in it, and only walls with room to spare are ledged.
    [Export(PropertyHint.Range, "2,32,1")] public int cliffShapedMinDrop = 6;

    // Depth of a PURE CUT, in columns — the shape that drops straight to the
    // cliff base so the edge retreats in plan and the wall keeps its whole
    // height. Which end of the range a lip gets follows how far under the
    // erosion threshold its noise sample fell, so depth varies along a face
    // rather than alternating.
    [Export(PropertyHint.Range, "1,16,1")] public int cliffCutDepthMin = 1;
    [Export(PropertyHint.Range, "1,16,1")] public int cliffCutDepthMax = 2;

    // Width of a LEDGE, in columns, and how far below the cliff top it must sit.
    //
    // The clearance is what keeps a ledge from eating the top of the face: the
    // untouched terrace and the ledge are at least this far apart, so the cliff
    // still reads as a cliff and the ledge is not a step up onto it. It also
    // decides which walls may be ledged at all — a face with no room for a ledge
    // under the clearance can only be cut.
    [Export(PropertyHint.Range, "1,16,1")] public int cliffLedgeWidthMin = 2;
    [Export(PropertyHint.Range, "1,16,1")] public int cliffLedgeWidthMax = 3;
    [Export(PropertyHint.Range, "0,16,1")] public int cliffLedgeTopClearance = 4;

    // Share of cut lips that become a LEDGE rather than a pure cut, 0..1.
    //
    // There is deliberately no sloped shape. A slope across a cut one to three
    // columns wide resolves to a run of single-voxel steps, which read as
    // near-invisible ledges rather than as a grade — the exact thing a slope was
    // meant to avoid. Every shape here is either full-height or flat.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cliffLedgeShare = 0.4f;

    // Share of eligible lips cut at all, 0..1. The rest are left alone, and
    // those un-cut stretches standing proud between the cut ones are the
    // FINGERS — as much the effect as the cuts themselves.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cliffErosionAmount = 0.5f;

    // Extra on cliffs that fall below the waterline. Sea cliffs are the tallest
    // and most uniform faces here, so they are where it pays most.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cliffErosionCoastalBoost = 0.2f;

    // THREE independent fields, one per decision: how far back to cut, whether
    // to step or slope, and how low the step sits. Independent because sharing
    // one correlates them — every deep cut would also be a slope and every step
    // would bench at the same height, which reads as a pattern rather than as
    // erosion. Raise a frequency to make that decision vary over a shorter run
    // of cliff.
    [Export(PropertyHint.Range, "0.001,0.5,0.0005")] public float cliffErosionFrequency = 0.09f;
    [Export(PropertyHint.Range, "0.001,0.5,0.0005")] public float cliffModeFrequency = 0.045f;
    [Export(PropertyHint.Range, "0.001,0.5,0.0005")] public float cliffStepFrequency = 0.13f;

    // TALUS: share of the columns at the foot of an eroded face that are raised
    // one quantizeStep, as scattered rubble. Runs AFTER the water pass and skips
    // any column carrying a river or lake, so a rubble block can never dam a
    // channel that was routed before it existed.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cliffTalusCoverage = 0.35f;
    [Export(PropertyHint.Range, "0.001,1,0.0005")] public float cliffTalusFrequency = 0.22f;

    [ExportGroup("Caves")]
    // Caves are THREE separate things, because they answer to different rules:
    // ENTRANCES (the only place a cave may open to the outside), CAVERNS
    // (enclosed chambers), and TUNNELS joining them. Building them separately is
    // what stopped the overhangs — see CellularTerrainCaves.cs for the version
    // that did not, and why one flood outward from a mouth cannot be made safe
    // by a per-column rule.
    //
    // CEILINGS sit on HeightMap.LevelStep (interiorLevelStep, 4) — the world's
    // lattice for enclosed space, shared with building floors so the camera
    // cutaway slices caves and rooms at the same heights. FLOORS sit on
    // quantizeStep (2), with a smooth 1-voxel path bump allowed on top (below).
    // The arch over a mouth is the one documented exception to the ceiling rule.
    //
    // This is the number of ENTRANCES, i.e. of ways in.
    [Export(PropertyHint.Range, "0,5,1")] public int caveSystemCount = 3;

    // Floor-to-ceiling clearance, in voxels: the gap between the floor's top
    // solid voxel and the ceiling's bottom one is this minus one. Must leave 4
    // voxels of headroom everywhere after caveFloorPathRise is spent, so the
    // real floor is this - caveFloorPathRise - 1 >= 4.
    [Export(PropertyHint.Range, "6,32,2")] public int caveClearance = 6;

    // Rock kept between a cave ceiling and the ground above it, in voxels. This
    // is the rule the plateau approach broke: below about 4 the roof reads as a
    // crust and any surface detail on top of it pokes through into the cave.
    [Export(PropertyHint.Range, "2,32,1")] public int caveRoofRock = 4;


    // Minimum spacing between two mouths, in voxels.
    [Export(PropertyHint.Range, "4,512,1")] public float caveEntranceSpacing = 28f;

    // THE MOUTH. A porch cut through the cell wall, `caveMouthWidth` either side
    // of its centre line, with an ARCHED roof — a circular profile, full height
    // on the centre line, dropping by `caveMouthArchRise` at the jambs.
    //
    // Its DEPTH is not authored: it runs inward until the surrounding ground
    // closes in over the ceiling, and the tunnel starts where it stops. A fixed
    // depth leaves the first column behind the porch facing daylight wherever
    // the cliff face is not straight, and the enclosure check then deletes that
    // column — correctly — and severs the cave from its own entrance. The knob
    // below is only the cap: a mouth whose rock never closes in within it is a
    // slot through a spine, and is dropped rather than roofed.
    //
    // The arch is the ONE place a ceiling leaves the interior lattice, and it is
    // deliberate: that lattice exists so the camera cutaway slices enclosed
    // ROOMS at shared heights, and a mouth is an opening in a cliff face three
    // columns deep. A flat-topped rectangular hole reads as a slab with a gap
    // under it rather than as a cave.
    //
    // The rise is clamped so the shortest part of the opening still clears four
    // voxels of headroom. The sides round off by going SOLID, not by going low.
    [Export(PropertyHint.Range, "1,32,1")] public float caveMouthWidth = 3f;
    [Export(PropertyHint.Range, "2,32,1")] public int caveMouthMaxDepth = 10;
    [Export(PropertyHint.Range, "0,8,1")] public int caveMouthArchRise = 3;

    // THE SYSTEM behind a mouth: one short, LOCAL cave per entrance, and
    // nothing joins them to each other. Reach bounds how far it may spread from
    // its doorway; the column budget bounds how much of that it may fill.
    //
    // Short and local on purpose. The version this replaced placed chambers
    // independently and linked them with routed tunnels, which meant every
    // connection spanned whatever distance separated two unrelated points —
    // corridors over a hundred metres long, each needing to change level on the
    // way, and every level change another chance to put a floor off the lattice.
    // A system that never leaves its own neighbourhood needs no level changes at
    // all once it is inside.
    [Export(PropertyHint.Range, "8,200,1")] public float caveReach = 26f;
    [Export(PropertyHint.Range, "16,8192,16")] public int caveMaxColumns = 700;

    // How the flood spends its budget, 0..1. Toward 0 the cost is dominated by a
    // wander field and the system comes out as passages between wider lobes;
    // toward 1 it is dominated by distance and comes out as one round chamber.
    [Export(PropertyHint.Range, "0,1,0.01")] public float caveOpenness = 0.45f;

    // Levels the system may sit BELOW its own doorway, and how steeply the one
    // ramp that gets it there descends (voxels of drop per column). Tried
    // deepest first, falling back a level at a time, with 0 always legal.
    //
    // This is the only level change a system has, and it is at the entry — a
    // step in the middle of a cave is one the player meets with no warning,
    // while a ramp just inside the mouth reads as the way down. It is also what
    // puts a cave under real rock rather than inside the cliff face, and what
    // lets one reach below the waterline in a world whose land is barely twenty
    // voxels tall.
    [Export(PropertyHint.Range, "0,8,1")] public int caveDescentLevels = 2;
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float caveDescentGrade = 0.5f;

    // Width of the entry ramp, in columns. The system itself has no authored
    // width: a flood takes every column the rock allows, so a passage is as wide
    // as the ground it runs through. Widening a routed path by a fixed disc is
    // what kept producing narrow tunnels no matter how large the disc was.
    [Export(PropertyHint.Range, "1,16,1")] public int caveWidth = 7;

    // Frequency of the field tunnels wander along, and that warps a chamber's
    // outline. One lobe is roughly a chamber wide.
    [Export(PropertyHint.Range, "0.001,0.5,0.0005")] public float caveWanderFrequency = 0.055f;

    // STALAGMITES: single-column solid spikes left standing on the floor.
    // Derived from a position hash rather than stored state, because IsCarvedAt
    // has to answer the same for a voxel however often it is asked. Height is
    // capped so a spike never reaches its own ceiling — a floor-to-roof pillar
    // reads as structure, not as a cave.
    // Height is rolled over the inclusive min..max band, in voxels, then capped
    // two short of the ceiling wherever the headroom cannot afford the roll.
    [Export(PropertyHint.Range, "0,0.3,0.0005")] public float stalagmiteDensity = 0.03f;
    [Export(PropertyHint.Range, "1,8,1")] public int stalagmiteHeightMin = 2;
    [Export(PropertyHint.Range, "1,8,1")] public int stalagmiteHeightMax = 3;

    // Keep-out radius, in voxels, around a cave mouth where no stalagmite
    // stands. A spike in the doorway is the one place it reads as a bug.
    [Export(PropertyHint.Range, "0,32,1")] public float stalagmiteMouthClearance = 4f;
}
