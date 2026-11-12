using System.Collections.Generic;
using Godot;

// debug_discoverable_perception: one Control that draws a readout over every
// Discoverable within the player's vision range. Drawn rather than built from
// Labels so the overlay costs one node however many traps and chests are loaded.
[GlobalClass]
public partial class DiscoverableDebugOverlay : Control
{
    private const int FontSize = 10;
    private const int OutlineSize = 3;
    private const float LineWidth = 400f;

    private Camera3D _camera;
    private readonly List<Discoverable> _discoverables = new();
    private bool _drewLastFrame;

    public void Init(Camera3D camera)
    {
        _camera = camera;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public void Register(Discoverable discoverable)
    {
        _discoverables.Add(discoverable);
    }

    public void Unregister(Discoverable discoverable)
    {
        int index = _discoverables.IndexOf(discoverable);
        if (index >= 0)
        {
            _discoverables.RemoveAtSwap(index);
        }
    }

    public override void _Process(double delta)
    {
        bool enabled = CVars.debugDiscoverablePerception.Value;
        if (enabled || _drewLastFrame)
        {
            QueueRedraw();
        }
        _drewLastFrame = enabled;
    }

    public override void _Draw()
    {
        if (!CVars.debugDiscoverablePerception.Value || _camera == null)
        {
            return;
        }
        Sim sim = Sim.Current;
        Player player = sim?.player;
        if (player?.data == null || GameClient.Current == null)
        {
            return;
        }
        Font font = ThemeDB.FallbackFont;
        Vector3 playerPos = player.GlobalPosition;
        float range = player.data.visionRange;
        float targetLightMax = sim.SimData?.targetLightMax ?? 0f;
        float nightVisionRelief = Mathf.Clamp(player.ComposeStat(EStat.NightVision) - 1f, 0f, 1f);
        float dilationRelief = player.EyeDilation * player.data.eyeDilationVisionRelief;

        for (int i = _discoverables.Count - 1; i >= 0; i--)
        {
            Discoverable d = _discoverables[i];
            if (d == null || !IsInstanceValid(d))
            {
                _discoverables.RemoveAtSwap(i);
                continue;
            }
            Vector3 pos = d.GlobalPosition;
            if (pos.DistanceSquaredTo(playerPos) > range * range || _camera.IsPositionBehind(pos))
            {
                continue;
            }
            float rawLight = sim.GetPerceivedLight(pos + new Vector3(0f, d.lightSampleHeight, 0f));
            string text = Format(d, rawLight, targetLightMax, dilationRelief, nightVisionRelief);
            Vector2 screen = GameClient.Current.ProjectToScreen(pos);
            DrawMultilineStringOutline(font, screen, text, HorizontalAlignment.Left, LineWidth, FontSize, -1, OutlineSize, Colors.Black);
            DrawMultilineString(font, screen, text, HorizontalAlignment.Left, LineWidth, FontSize, -1, StateColor(d.State));
        }
    }

    private static string Format(Discoverable d, float rawLight, float targetLightMax, float dilationRelief, float nightVisionRelief)
    {
        PerceptionDebug p = d.LastDebug;
        string host = d.GetParent()?.Name ?? d.Name;
        string los = p.los switch { EPerceptionLos.Clear => "+", EPerceptionLos.Blocked => "-", _ => "?" };
        string frozen = d.IsDiscovered ? " (at discovery)" : "";
        return $"{host} {d.State} P{d.Perception:F2} by {d.DiscoveryCause}\n"
            + $"V{p.vision:F2} L{p.lighting:F2} D{p.distance:F2} Fg{p.fog:F2} prom{d.prominence:F2} LOS{los}{frozen}\n"
            + $"now: light {rawLight:F2}/{targetLightMax:F2} dil x{PlayerPerception.LightGain(dilationRelief):F2} nv x{PlayerPerception.LightGain(nightVisionRelief):F2}"
            + $" ^{Sim.Current?.SimData?.targetLightPower ?? 1f:F2} = {Sim.Current?.SimData?.NormalizeLight(rawLight, PlayerPerception.LightGain(dilationRelief) * PlayerPerception.LightGain(nightVisionRelief)) ?? 0f:F2}";
    }

    private static Color StateColor(EPlayerPerceptionState state)
    {
        return state switch
        {
            EPlayerPerceptionState.Discovered => Colors.OrangeRed,
            EPlayerPerceptionState.Detected => Colors.Yellow,
            _ => Colors.LightGreen,
        };
    }
}
