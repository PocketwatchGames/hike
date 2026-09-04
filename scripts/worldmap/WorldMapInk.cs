using Godot;

// How a painted document is DRAWN — every colour the map speaks, and the only
// thing in the painter that holds a WorldMapInkData.
//
// Split out of WorldMapState so the boundary is structural rather than a
// convention: the model and the bake do not compile against WorldMapInkData at
// all, so a bake pass CANNOT read a display value even by accident. That was a
// real bug — the map's ink was reachable from the document, and a headless
// check depended on it through StampColorAt.
//
// The rule for adding anything here: if the BAKE would ever need the answer,
// it does not belong in this class. Split the answer out of the inking and put
// it on the model instead — StampHitAt / StampColorAt is the worked example.
//
// Per-voxel plan colours are NOT here: they come from BlockData.minimapColor,
// a property of the subscene and the block catalog rather than of the painter's
// palette, so they stay on the model and only the compositing is ours.
public class WorldMapInk
{
    // The document being drawn. Read-only from here — a colour never mutates
    // the map.
    public readonly WorldMapState Map;

    // The authored palette.
    public readonly WorldMapInkData Data;

    // What the author is looking at — the cutaway plane and the water toggle.
    public readonly WorldMapView View;

    public WorldMapInk(WorldMapState map, WorldMapInkData data, WorldMapView view)
    {
        Map = map;
        Data = data;
        View = view;
        BuildWaterTypeInk();
    }

    // The colour a CUTAWAY view draws at a column: the elevation band of the
    // highest floor under the cut, or the rock colour where the column is solid
    // the whole way down with nothing hollow beneath.
    //
    // A floor found THROUGH rock keeps its exact band and reports `buried`; the
    // painter dithers those cells against the rock colour rather than tinting
    // them, because a tint would move the band into a shade some other height
    // already owns — the one thing the palette exists to prevent. Water is
    // composited only where the cut is open to it, since a floor seen through
    // rock is not under the pool standing on that rock.
    public Color CutawayColorAt(int px, int pz, int clipY, out bool buried)
    {
        int floor = Map.CutawayFloor(px, pz, clipY, out buried);
        if (floor < Map.Data.WorldMinY)
        {
            return Data.cutawayRockColor;
        }
        // Paving laid on THIS floor draws over the band, the same argument that
        // puts it on every map drawn from above: a road is a fact about the
        // ground you need while working beside it, and underground it is the one
        // thing telling a corridor you have finished from one you have not.
        BlockData paving = Map.PavingAtFloor(px, pz, floor);
        Color band = paving != null ? paving.minimapColor : ElevationColorAt(floor - Map.SeaLevel);
        return buried ? band : WithWaterOver(band, px, pz, floor, clipY);
    }

    private static readonly Color UNPAINTED_GROUND = new Color(0.30f, 0.29f, 0.27f);

    // What the ground-type views paint: the painted set's own colour and NOTHING
    // of the height, so the colour answers one question. Height is carried
    // entirely by the step outlines in those views, which is why they draw every
    // step down to 1m. Water still composites over the top — a flooded column
    // reads as water first, whatever the ground under it is.
    // Paving is resolved HERE rather than in the paving view, so a road shows on
    // every view that draws ground — you cannot lay props or mobs sensibly along
    // a road you cannot see. It wins over the ground set because it is what the
    // surface is actually made of once paved.
    public Color GroundColorAt(int px, int pz)
    {
        BlockData paving = Map.SurfacePavingAt(px, pz);
        if (paving != null)
        {
            return WithWater(paving.minimapColor, px, pz);
        }
        int idx = Map.GroundIndexAt(px, pz);
        GroundSetData[] sets = Map.GroundSets;
        Color c = idx >= 0 && idx < sets.Length && sets[idx] != null ? sets[idx].mapColor : UNPAINTED_GROUND;
        return WithWater(c, px, pz);
    }

    public Color StampColorAt(WorldMapState.StampPlan plan, int px, int pz, Color under,
        SubscenePlacement selected)
    {
        if (!Map.StampHitAt(plan, px, pz, out int i, out int top))
        {
            return under;
        }
        bool isSelected = plan.Stamps[i] == selected;
        if (top >= 0)
        {
            // Selected is lifted toward white rather than recoloured, so the
            // plan stays readable while the selection is obvious.
            Rect2I fp = plan.Footprints[i];
            int at = (pz - fp.Position.Y) * fp.Size.X + (px - fp.Position.X);
            Color content = plan.Colors[i][at];
            return isSelected ? content.Lerp(Colors.White, 0.35f) : content;
        }
        Color ink = Data.placementInk;
        return under.Lerp(new Color(ink.R, ink.G, ink.B),
            isSelected ? ink.A * 0.5f : ink.A * 0.2f);
    }

    // ---- Shared palette / colours (used by the views) -------------------

    public static Color RegionColor(int idx)
    {
        if (idx <= 0)
        {
            return new Color(0.22f, 0.22f, 0.24f);
        }
        return Color.FromHsv((idx * 0.61803398875f) % 1f, 0.55f, 0.85f);
    }

    public static Color ZoneColor(int idx)
    {
        return Color.FromHsv((idx * 0.61803398875f + 0.13f) % 1f, 0.45f, 0.9f);
    }

    // Hillshade of the RAW (unsnapped) height field: the smooth surface the
    // author is sculpting, so the map reads as landform. Deliberately not the
    // snapped field — a terraced height field has zero gradient across each
    // plateau and would shade as flat slabs; the steps are drawn as edge
    // outlines instead, which is the job they do well.
    // 1 texel == 1 metre, so the gradient is a plain central difference.
    public float ReliefShade(int px, int pz, Vector3 light)
    {
        float hl = Map.ElevationVoxels(px - 1, pz);
        float hr = Map.ElevationVoxels(px + 1, pz);
        float hd = Map.ElevationVoxels(px, pz - 1);
        float hu = Map.ElevationVoxels(px, pz + 1);
        var n = new Vector3(-(hr - hl) * 0.5f, 1f, -(hu - hd) * 0.5f).Normalized();
        return Mathf.Max(n.Dot(light), 0f);
    }

    // Standing water, honouring View.ShowWater. OPAQUE by design: the elevation band
    // underneath must not read through, or the map says "low ground" and
    // "underwater" in the same colour language. Just two shades — the shallows
    // you can wade, and everything below them.
    public Color WithWater(Color terrain, int px, int pz, int clipY = int.MaxValue)
    {
        // Against the DISPLAYED surface, so a bridge built over a lake reads as
        // the land it is rather than as the water it spans.
        return WithWaterOver(terrain, px, pz, Map.SurfaceBelow(px, pz, clipY), clipY);
    }

    // Same, for a caller that already knows the surface. Worth the overload: it
    // is called per texel per rebuild, and resolving the surface a second time
    // means a second walk down the column.
    public Color WithWaterOver(Color terrain, int px, int pz, int surface, int clipY)
    {
        if (!View.ShowWater)
        {
            return terrain;
        }
        int depth = Mathf.Min(Map.WaterSurface(px, pz), clipY) - surface;
        if (depth <= 0)
        {
            return terrain;
        }
        // Shallow to deep across waterDeepAtVoxels, so a shoreline reads pale and
        // a lake bed dark: the map says how deep water is, not merely that it is
        // there. Two authored stops rather than a long ramp — the shore is the
        // edge an author aims at, and more stops make that edge harder to find.
        float t = Mathf.Clamp(depth / (float)Mathf.Max(1, Data.waterDeepAtVoxels), 0f, 1f);
        Color water = Data.shallowWaterColor.Lerp(Data.deepWaterColor, t);
        // A PAINTED type tints the water it was painted on, on every view that
        // draws water — the same argument paving makes by resolving inside
        // GroundColorAt: you cannot paint scum along a shoreline you cannot see,
        // and a type invisible on the map is one you cannot tell you have
        // already laid down.
        //
        // Tint rather than replace, so the depth shading underneath survives:
        // the map still has to say how deep the water is.
        //
        // Only what the document HOLDS, never what the bake will infer. A zone's
        // own water is applied per column by a noise field at bake time, so
        // drawing it here would show a pattern the bake does not reproduce —
        // the same reason latent water is deliberately invisible.
        Color ink = WaterTypeInkAt(px, pz);
        return ink.A > 0f ? water.Lerp(ink, Data.waterTypeTintStrength) : water;
    }

    // The painted type's map colour for a column, or alpha 0 for none. Resolved
    // against a table built once at load: this runs for every texel of every
    // rebuild (~295k), so walking to the BlockData per texel would put a
    // resource dereference in the map's hottest loop.
    private Color WaterTypeInkAt(int px, int pz)
    {
        // Cheapest gates first: this is reached for every WET texel of every
        // rebuild, and the GetPixel below is a native call. Dry land never gets
        // here at all — WithWaterOver has already returned.
        if (_waterTypeInk == null || _waterTypeInk.Length == 0 || Data.waterTypeTintStrength <= 0f)
        {
            return default;
        }
        int idx = Map.WaterTypeIndexAt(px, pz);
        return idx >= 0 && idx < _waterTypeInk.Length ? _waterTypeInk[idx] : default;
    }

    // Alpha is the "is there one" flag, so an empty palette slot and an
    // unpaintable entry both read as absent without a second array.
    private Color[] _waterTypeInk;

    private void BuildWaterTypeInk()
    {
        BlockData[] types = Map.WaterTypes;
        _waterTypeInk = new Color[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            BlockData b = types[i];
            _waterTypeInk[i] = b != null && b.render == EBlockRender.Water
                ? new Color(b.minimapColor.R, b.minimapColor.G, b.minimapColor.B, 1f)
                : default;
        }
    }

    // Water is drawn flat, so the painter skips relief shading on it.
    public bool IsSubmerged(int px, int pz)
    {
        return View.ShowWater && Map.Underwater(px, pz);
    }

    // Colour of one column: the authored hue for its 4-metre band, shaded by
    // which metre of the band it sits on. Both halves of the pair carry meaning,
    // so a 1m step reads as a shade change and a 4m step as a hue change, and
    // neither depends on the ramp being wide enough to see — which is what the
    // old green-to-white hypsometric ramp failed at, since neighbouring steps
    // differed by a few percent across dozens of levels.
    // Reads the WEATHERED height, like the step outlines do. Colouring the raw
    // painted height instead left the bands saying one thing and the outlines
    // another, and the bands are the half an author reads a cliff's height from.
    public Color ElevationColor(int px, int pz, int clipY = int.MaxValue)
    {
        return ElevationColorAt(Map.SurfaceBelow(px, pz, clipY) - Map.SeaLevel);
    }

    // Same palette, addressed by height rather than by column — the brush cursor
    // shows the height it is about to write, which is not on the map yet.
    // Memo over the document's whole signed range. The palette is authored and
    // immutable at runtime, and this is asked per texel per rebuild, so the
    // banding math runs once per distinct height instead of ~295k times.
    private Color[] _bandMemo;

    public Color ElevationColorAt(int voxelsRelSea)
    {
        int lo = Mathf.FloorToInt(Map.Data.minElevationVoxels) - 1;
        int hi = Mathf.CeilToInt(Map.Data.maxElevationVoxels) + 1;
        if (voxelsRelSea >= lo && voxelsRelSea <= hi)
        {
            _bandMemo ??= new Color[hi - lo + 1];
            Color memo = _bandMemo[voxelsRelSea - lo];
            if (memo.A > 0f)
            {
                return memo;
            }
            Color made = BandColor(voxelsRelSea);
            _bandMemo[voxelsRelSea - lo] = made;
            return made;
        }
        return BandColor(voxelsRelSea);
    }

    private Color BandColor(int voxelsRelSea)
    {
        Color[] hues = Data.elevationBandHues;
        if (hues == null || hues.Length == 0)
        {
            return new Color(0.5f, 0.5f, 0.5f);
        }
        int v = voxelsRelSea;
        int per = Mathf.Max(1, Data.metersPerBand);

        // Floor division, not C# truncation: heights go negative below the
        // waterline and -1 must land in the band BELOW zero, not in band 0.
        int band = v >= 0 ? v / per : ((v + 1) / per) - 1;
        int within = v - band * per;   // always 0..per-1

        // The authored colour is the band's BASE — its lowest metre — and each
        // metre above lifts every channel by a fraction of that channel's own
        // headroom to white, so the hue stays recognisably itself while getting
        // steadily paler and the step shows even in a channel that started near
        // full.
        //
        // The band's TOP metre lands at elevationBandMaxBrightness of the way to
        // white and the metres between divide that evenly, which makes the one
        // knob the band's whole contrast range. Note the top metre reaches it
        // exactly — dividing by `per` instead would spend part of the range on a
        // metre that belongs to the next band.
        Color baseColor = hues[((band % hues.Length) + hues.Length) % hues.Length];
        float lift = per > 1
            ? Data.elevationBandMaxBrightness * within / (per - 1)
            : 0f;
        return new Color(
            baseColor.R + (1f - baseColor.R) * lift,
            baseColor.G + (1f - baseColor.G) * lift,
            baseColor.B + (1f - baseColor.B) * lift);
    }
}
