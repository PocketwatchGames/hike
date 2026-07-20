using Godot;

// Synthetic input driver for unattended (typically headless) runs. Injects the
// same global Input actions a human would — a slowly-turning wander heading plus
// occasional jump / dash / melee pulses — so a run exercises movement, chunk
// streaming, and combat with no one at the controls. Enabled by the `autoplay`
// CVar; Main spawns one when `autostart` + `autoplay` are set.
//
// This deliberately drives Godot's Input singleton (which works headless) rather
// than reaching into Player, so it needs zero gameplay-side plumbing: Player
// reads these exact actions in ProcessInput. Purely a test harness — presentation
// timing (wall-clock delta) is fine; it makes no gameplay-authoritative decisions.
public partial class HeadlessBot : Node
{
    // Wander heading turns at this rate; a discrete action fires on each of the
    // two independent cooldowns. All tuning is here — this is a dev harness, not
    // authored content, so plain consts are appropriate.
    private const float TurnRateRadPerSec = 0.6f;
    private const float MoveStrength = 1.0f;
    private const float ActionMinInterval = 1.2f;
    private const float ActionMaxInterval = 3.5f;
    private const float LogInterval = 5.0f;

    private static readonly string[] MoveActions = { "MoveLeft", "MoveRight", "MoveUp", "MoveDown" };
    // Discrete pulses the bot fires at random. Kept to safe, always-available
    // verbs so the bot never wedges on a modal (no Interact/inventory).
    private static readonly string[] PulseActions = { "Jump", "Dash", "AttackMelee" };

    // Fixed seed so a headless run is reproducible.
    private readonly RandomNumberGenerator _rng = new() { Seed = 0xB0741234 };

    private float _heading;
    private float _actionTimer;
    private float _logTimer;

    // A fired pulse action, released after one full frame so IsActionJustPressed
    // latches exactly once.
    private string _pendingRelease;

    public override void _Ready()
    {
        _heading = _rng.RandfRange(0f, Mathf.Tau);
        _actionTimer = _rng.RandfRange(ActionMinInterval, ActionMaxInterval);
    }

    public override void _Process(double delta)
    {
        // Release last frame's pulse before evaluating this frame's input.
        if (_pendingRelease != null)
        {
            Input.ActionRelease(_pendingRelease);
            _pendingRelease = null;
        }

        Player player = Sim.Current?.player;
        if (player == null)
        {
            ClearMove();
            return;
        }

        float dt = (float)delta;
        _heading += TurnRateRadPerSec * dt * _rng.RandfRange(-1f, 1f);

        // Drive the four move axes from the wander heading.
        float x = Mathf.Cos(_heading);
        float z = Mathf.Sin(_heading);
        SetAxis("MoveLeft", "MoveRight", x);
        SetAxis("MoveUp", "MoveDown", z);

        _actionTimer -= dt;
        if (_actionTimer <= 0f)
        {
            string action = PulseActions[_rng.RandiRange(0, PulseActions.Length - 1)];
            Input.ActionPress(action);
            _pendingRelease = action;
            _actionTimer = _rng.RandfRange(ActionMinInterval, ActionMaxInterval);
        }

        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = LogInterval;
            Vector3 p = player.GlobalPosition;
            GD.Print($"[autoplay] player=({p.X:F1}, {p.Y:F1}, {p.Z:F1}) heading={Mathf.RadToDeg(_heading):F0}°");
        }
    }

    // Maps a signed value onto a negative/positive action pair.
    private void SetAxis(string negAction, string posAction, float value)
    {
        if (value < 0f)
        {
            Input.ActionPress(negAction, -value * MoveStrength);
            Input.ActionRelease(posAction);
        }
        else
        {
            Input.ActionPress(posAction, value * MoveStrength);
            Input.ActionRelease(negAction);
        }
    }

    private void ClearMove()
    {
        foreach (string a in MoveActions)
        {
            Input.ActionRelease(a);
        }
    }

    public override void _ExitTree()
    {
        ClearMove();
        if (_pendingRelease != null)
        {
            Input.ActionRelease(_pendingRelease);
            _pendingRelease = null;
        }
    }
}
