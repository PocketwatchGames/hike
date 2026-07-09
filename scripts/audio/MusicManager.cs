using Godot;

// Project-global dynamic music director (Godot autoload, autoloads/music_manager.tscn)
// so the single instance outlives the menu <-> game scene swaps. Mirrors
// MaterialRegistry's singleton shape (static Instance set in _EnterTree; NOT
// [Tool], so Instance is null in the editor).
//
// SINGLE-LAYER: one piece plays at a time, crossfading; nothing ducks under
// another and swells back. Pieces fall in two categories:
//
//   RESPONSIVE / EVENT BEDS (continuous — loop while their state holds):
//     * Death   — interrupts everything, cannot be interrupted; until respawn.
//     * Combat  — interrupts everything except Death; until combat ends.
//     * Camp    — when the player activates camp (from outside). Stops when they
//                 sleep (→ silence, then the wake ambient) and when they leave
//                 (→ an ambient cue). Menu / Loading are beds too.
//
//   AMBIENT CUES (one-shot — play once, then silence):
//     * Sunrise / Daytime / Sunset / Night. Triggered once on: waking from a
//       camp sleep, leaving camp, a time-of-day phase transition during play, or
//       respawn — always the cue for the CURRENT phase (wake mid-sunset → Sunset).
//       They interrupt each other on a phase change but never re-trigger the same
//       phase. Daytime is the old "explore" track, now played when Sunrise ends.
//
// Combat / Death / Camp interrupt the ambient cues; the ambient cues never
// interrupt them. Region entry deliberately does NOT touch the music.
//
// Event wiring: a GameClient is created/destroyed per session, so the manager
// holds no permanent reference. Main calls BindGame on the fresh client (and
// pushes SetLoading); CampScreen pushes camp enter/leave/sleep; BindGame hooks
// onQuitToMenu to auto-detach.
[GlobalClass]
public partial class MusicManager : Node
{
    public static MusicManager Instance { get; private set; }

    public enum EMusicPiece
    {
        Silent,
        Menu,
        Loading,
        Camp,
        Combat,
        Death,
        Sunrise,
        Daytime,
        Sunset,
        Night,
    }

    // One track per piece, wired in the autoload scene's inspector.
    [ExportGroup("Tracks")]
    [Export] public MusicTrackData menuTrack;
    [Export] public MusicTrackData loadingTrack;
    [Export] public MusicTrackData campTrack;
    [Export] public MusicTrackData combatTrack;
    [Export] public MusicTrackData deathTrack;
    [Export] public MusicTrackData sunriseTrack;
    [Export] public MusicTrackData daytimeTrack;
    [Export] public MusicTrackData sunsetTrack;
    [Export] public MusicTrackData nightTrack;

    [Export(PropertyHint.Range, "0.1,8,0.1")] public float crossfadeSeconds = 2.0f;

    // Master music level, stacked on top of each track's own volumeDb.
    [Export(PropertyHint.Range, "-40,6,0.5")] public float masterVolumeDb = 0f;

    // Awake-day time-of-day (0 = sunrise, 1/3 = noon, 2/3 = sunset, 1 = midnight)
    // phase boundaries. [sunrise, daytime) → Sunrise cue; [daytime, sunset) →
    // Daytime; [sunset, night) → Sunset; the rest → Night.
    [ExportGroup("Time Of Day Phases")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunriseTimeOfDay = 0.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float daytimeTimeOfDay = 0.12f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunsetTimeOfDay = 0.67f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float nightTimeOfDay = 0.75f;

    private const string MUSIC_BUS = "Music";
    private const float DB_FLOOR = -80f;
    private const float SILENCE_AMP = 0.001f;

    // Two players crossfade between pieces: _active fades in, _idle fades out.
    private AudioStreamPlayer _active;
    private AudioStreamPlayer _idle;
    private MusicTrackData _activeTrack;
    private MusicTrackData _idleTrack;
    private float _fade = 1f;

    private GameClient _game;
    private bool _loading;
    private bool _playerDead;
    private bool _inCombat;
    private bool _camping;

    // Piece currently loaded on _active. Drives the same-piece guard (an ambient
    // cue never re-triggers itself) and the loop / one-shot finish handling.
    private EMusicPiece _currentPiece = EMusicPiece.Silent;
    // Previous-frame time-of-day for phase-transition detection; NaN until first
    // sampled so a fresh bind doesn't fire on the initial reading.
    private double _prevTimeOfDay = double.NaN;

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this) { Instance = null; }
    }

    public override void _Ready()
    {
        // Keep music and crossfades running while the game is paused.
        ProcessMode = ProcessModeEnum.Always;
        _active = NewPlayer("MusicA");
        _idle = NewPlayer("MusicB");
        Play(EMusicPiece.Menu);
    }

    private AudioStreamPlayer NewPlayer(string name)
    {
        var p = new AudioStreamPlayer
        {
            Name = name,
            Bus = MUSIC_BUS,
            VolumeDb = DB_FLOOR,
        };
        AddChild(p);
        return p;
    }

    // ----- Session binding -------------------------------------------------

    public void BindGame(GameClient game)
    {
        if (game == null) { return; }
        Unbind();
        _game = game;
        _loading = false;
        _playerDead = false;
        _inCombat = false;
        _camping = false;
        _prevTimeOfDay = double.NaN;

        _game.onCombatBegin += OnCombatBegin;
        _game.onCombatEnd += OnCombatEnd;
        _game.onPlayerDied += OnPlayerDied;
        _game.onPlayerRespawned += OnPlayerRespawned;
        _game.onQuitToMenu += Unbind;
        Log("bound game");
    }

    public void Unbind()
    {
        if (_game != null)
        {
            _game.onCombatBegin -= OnCombatBegin;
            _game.onCombatEnd -= OnCombatEnd;
            _game.onPlayerDied -= OnPlayerDied;
            _game.onPlayerRespawned -= OnPlayerRespawned;
            _game.onQuitToMenu -= Unbind;
            _game = null;
        }
        _inCombat = false;
        _playerDead = false;
        _camping = false;
        Play(EMusicPiece.Menu);
        Log("unbound game");
    }

    // Pushed by Main: Loading bed while the loading screen is up, then the
    // current time-of-day ambient cue once the game is in (or Menu if none).
    public void SetLoading(bool loading)
    {
        _loading = loading;
        if (loading)
        {
            Play(EMusicPiece.Loading);
        }
        else if (_game != null)
        {
            Play(CurrentPhase());
        }
        else
        {
            Play(EMusicPiece.Menu);
        }
    }

    // ----- Event handlers --------------------------------------------------

    private void OnCombatBegin()
    {
        _inCombat = true;
        Play(EMusicPiece.Combat);
    }

    private void OnCombatEnd()
    {
        _inCombat = false;
        // Combat end → silence (it is not an ambient-cue trigger). Death still
        // wins if it landed this frame.
        if (!_playerDead)
        {
            Play(EMusicPiece.Silent);
        }
    }

    private void OnPlayerDied(Player player)
    {
        _playerDead = true;
        Play(EMusicPiece.Death);
    }

    private void OnPlayerRespawned(Player player)
    {
        _playerDead = false;
        Play(CurrentPhase());
    }

    // Camp enter / leave, pushed by CampScreen.Open / Close. Entering from outside
    // plays camp music; leaving stops it and plays the current ambient cue.
    public void SetCamping(bool camping)
    {
        if (camping == _camping) { return; }
        _camping = camping;
        Play(camping ? EMusicPiece.Camp : CurrentPhase());
    }

    // Sleeping at a camp stops the camp music (→ silence through the fade/skip).
    public void OnCampSleepStart()
    {
        Play(EMusicPiece.Silent);
    }

    // Waking from a camp sleep plays the cue for whatever phase the player woke
    // in — even mid-phase (wake during sunset → Sunset).
    public void OnCampSleepWake()
    {
        Play(CurrentPhase());
    }

    // ----- Per-frame -------------------------------------------------------

    public override void _Process(double delta)
    {
        PollTimeOfDay();
        TickCrossfade((float)delta);

        // Continuous beds loop; ambient one-shots fall to silence when they end.
        if (_fade >= 1f && _currentPiece != EMusicPiece.Silent && !_active.Playing)
        {
            if (IsContinuous(_currentPiece))
            {
                if (_activeTrack?.stream != null) { _active.Play(); }
            }
            else
            {
                _currentPiece = EMusicPiece.Silent;
            }
        }
    }

    // Fire the matching ambient cue on a time-of-day phase change during normal
    // play. Only acts in the ambient layer (no bed active) — phase changes under
    // Combat / Camp / Death are silent — but _prevTimeOfDay is tracked every
    // frame so returning to the ambient layer doesn't fire a stale change.
    private void PollTimeOfDay()
    {
        if (_game == null) { return; }
        WorldState ws = World.Current?.WorldState;
        if (ws == null) { return; }

        double t = ws.TimeOfDay01;
        double prev = _prevTimeOfDay;
        _prevTimeOfDay = t;
        if (double.IsNaN(prev)) { return; }
        if (!InAmbientLayer()) { return; }

        EMusicPiece phaseNow = PhaseCue(t);
        if (phaseNow != PhaseCue(prev))
        {
            Play(phaseNow);
        }
    }

    private bool InAmbientLayer()
    {
        return _game != null && !_loading && !_playerDead && !_inCombat && !_camping;
    }

    private static bool IsContinuous(EMusicPiece p)
    {
        return p == EMusicPiece.Menu || p == EMusicPiece.Loading
            || p == EMusicPiece.Camp || p == EMusicPiece.Combat || p == EMusicPiece.Death;
    }

    private EMusicPiece CurrentPhase()
    {
        return PhaseCue(World.Current?.WorldState?.TimeOfDay01 ?? 0.0);
    }

    private EMusicPiece PhaseCue(double tod)
    {
        if (tod >= sunriseTimeOfDay && tod < daytimeTimeOfDay) { return EMusicPiece.Sunrise; }
        if (tod >= daytimeTimeOfDay && tod < sunsetTimeOfDay) { return EMusicPiece.Daytime; }
        if (tod >= sunsetTimeOfDay && tod < nightTimeOfDay) { return EMusicPiece.Sunset; }
        return EMusicPiece.Night;
    }

    private MusicTrackData TrackFor(EMusicPiece piece)
    {
        return piece switch
        {
            EMusicPiece.Menu => menuTrack,
            EMusicPiece.Loading => loadingTrack,
            EMusicPiece.Camp => campTrack,
            EMusicPiece.Combat => combatTrack,
            EMusicPiece.Death => deathTrack,
            EMusicPiece.Sunrise => sunriseTrack,
            EMusicPiece.Daytime => daytimeTrack,
            EMusicPiece.Sunset => sunsetTrack,
            EMusicPiece.Night => nightTrack,
            _ => null,
        };
    }

    // ----- Crossfade -------------------------------------------------------

    // Crossfade to a piece. No-op if it's already current — so an ambient cue
    // never interrupts itself (a phase that's already playing, or a re-fired
    // leave/wake of the same phase, is left to keep playing or stay silent).
    private void Play(EMusicPiece piece)
    {
        if (piece == _currentPiece)
        {
            return;
        }
        MusicTrackData track = TrackFor(piece);
        _currentPiece = piece;
        // Swap roles: the old _active fades out as _idle; the new track fades in.
        (_active, _idle) = (_idle, _active);
        _idleTrack = _activeTrack;
        _activeTrack = track;

        if (track?.stream != null)
        {
            _active.Stream = track.stream;
            _active.Play();
        }
        else
        {
            _active.Stop();
        }
        _fade = 0f;
        Log($"piece -> {piece} ({(track != null ? track.displayName : "(silence)")})");
    }

    private void TickCrossfade(float delta)
    {
        if (_fade < 1f)
        {
            float step = crossfadeSeconds > 0f ? delta / crossfadeSeconds : 1f;
            _fade = Mathf.Min(1f, _fade + step);
        }

        ApplyVolume(_active, _activeTrack, _fade);
        ApplyVolume(_idle, _idleTrack, 1f - _fade);

        if (_fade >= 1f && _idle.Playing)
        {
            _idle.Stop();
            _idleTrack = null;
        }
    }

    private void ApplyVolume(AudioStreamPlayer player, MusicTrackData track, float amp)
    {
        if (track?.stream == null || amp <= SILENCE_AMP)
        {
            player.VolumeDb = DB_FLOOR;
            return;
        }
        player.VolumeDb = Mathf.LinearToDb(amp) + track.volumeDb + masterVolumeDb;
    }

    private static void Log(string msg)
    {
        if (CVars.musicDebug.Value) { GD.Print($"[Music] {msg}"); }
    }
}
