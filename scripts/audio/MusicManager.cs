using System;
using Godot;

// Project-global dynamic music director. Registered as a Godot autoload
// (autoloads/music_manager.tscn, [autoload] in project.godot) so the single
// instance outlives the menu <-> game scene swaps Main does — the only place
// in the tree that survives them. Mirrors MaterialRegistry's singleton shape
// (static Instance set in _EnterTree; NOT [Tool], so Instance is null in the
// editor).
//
// Two layers:
//   BEDS  — looping background music. Each frame the manager computes which
//           EMusicStates are ACTIVE (Menu when no game is bound; Loading,
//           Explore, Combat, Death while a game runs), asks the cue table for
//           the highest-priority active cue, and plays it. When the winning
//           cue keeps the same track but a different clip (Explore -> Combat
//           inside one AudioStreamInteractive) it calls SwitchToClipByName for
//           an engine-driven seamless transition; when the track itself
//           changes it crossfades two AudioStreamPlayers.
//   STINGS — one-shot punctuations (sunrise, sunset, night, combat victory,
//           player death) played over the bed on a third player, fire-and-forget.
//
// An empty/unmatched cue table simply fades to silence — no crash.
//
// Event wiring: a GameClient is created and destroyed per session, so the
// manager must not hold a permanent reference. Main calls BindGame on the
// fresh client; BindGame subscribes to its events (combat, region, player
// spawn/death) and hooks onQuitToMenu to auto-detach, so handlers never linger
// on a freed client. Loading state is pushed in by Main directly.
[GlobalClass]
public partial class MusicManager : Node
{
    public static MusicManager Instance { get; private set; }

    // Authored tables wired in the autoload scene's inspector.
    [Export] public MusicCueData[] cues = Array.Empty<MusicCueData>();
    [Export] public MusicStingData[] stings = Array.Empty<MusicStingData>();

    [Export(PropertyHint.Range, "0.1,8,0.1")] public float crossfadeSeconds = 2.0f;

    // Master music level, stacked on top of each track's own volumeDb.
    [Export(PropertyHint.Range, "-40,6,0.5")] public float masterVolumeDb = 0f;

    // Beds duck by this much for stingDuckHoldSeconds after a sting fires, so the
    // punctuation is heard over the music instead of masked by it (e.g. the
    // combat bed is still fading out when the victory stinger lands). Held for a
    // fixed short window — NOT the full sting length — so a long (8s) stinger
    // doesn't keep the bed ducked while you walk back into combat.
    [Export(PropertyHint.Range, "-40,0,1")] public float stingDuckDb = -14f;
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float stingDuckSeconds = 0.25f;
    [Export(PropertyHint.Range, "0,8,0.25")] public float stingDuckHoldSeconds = 1.5f;

    // Normalized time-of-day (0=midnight, 0.25=sunrise, 0.5=noon, 0.75=sunset)
    // at which the sunrise / sunset / night stings fire as the clock crosses
    // them. Night is the "after sunset" hook — a bit past sunsetTimeOfDay.
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunriseTimeOfDay = 0.25f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunsetTimeOfDay = 0.75f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float nightTimeOfDay = 0.8f;

    private const string MUSIC_BUS = "Music";
    private const float DB_FLOOR = -80f;
    private const float SILENCE_AMP = 0.001f;

    // Two players crossfade between bed TRACKS: _active fades in, _idle fades
    // out. Clip changes within a track don't use these — they SwitchToClipByName.
    private AudioStreamPlayer _active;
    private AudioStreamPlayer _idle;
    private AudioStreamPlayer _stingPlayer;

    private MusicTrackData _activeTrack;
    private MusicTrackData _idleTrack;
    private string _activeClip = "";
    // Cached interactive playback of _active, or null when its stream isn't an
    // AudioStreamInteractive (clip switches then no-op).
    private AudioStreamPlaybackInteractive _activePlayback;
    // Crossfade progress in favor of _active, 0..1.
    private float _fade = 1f;
    // Current bed duck (dB), eased toward stingDuckDb; _duckRemaining counts
    // down the fixed hold window after a sting fires.
    private float _duckDb;
    private float _duckRemaining;
    // Tracks the sting player across frames to log where a sting actually stops
    // (cut early vs. played out) under music_debug.
    private bool _stingWasPlaying;

    // Bound game session, or null on the menu / editor / painter screens.
    private GameClient _game;

    private bool _loading;
    private bool _playerDead;
    private bool _inCombat;
    // Set while the player is resting at a campfire (camp screen open). Pushed in
    // directly by CampScreen.Open/Close — like _loading, it's a screen state with
    // no GameClient event of its own.
    private bool _camping;
    // Explore is a one-shot triggered by region entry (its flight track is
    // imported non-looping). _exploreActive is set on entering a region while
    // nothing higher is playing, and cleared when the track finishes or a higher
    // bed takes over — so each new region replays it, it never loops, and
    // re-entering while it's still playing doesn't restart it.
    private bool _exploreActive;
    // State of the bed currently loaded on _active (null = silence). Drives the
    // explore one-shot bookkeeping.
    private EMusicState? _activeBedState;

    // Previous-frame time-of-day for sunrise/sunset edge detection; NaN until
    // first sampled so a fresh bind doesn't fire on the initial reading.
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
        _stingPlayer = NewPlayer("MusicSting");
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

    // Called by Main with the freshly-created GameClient.
    public void BindGame(GameClient game)
    {
        if (game == null) { return; }
        Unbind();
        _game = game;
        _loading = false;
        _playerDead = false;
        _inCombat = false;
        _camping = false;
        _exploreActive = false;
        _activeBedState = null;
        _prevTimeOfDay = double.NaN;

        _game.onCombatBegin += OnCombatBegin;
        _game.onCombatEnd += OnCombatEnd;
        _game.onCombatVictory += OnCombatVictory;
        _game.onAnnouncement += OnAnnouncement;
        _game.onPlayerSpawned += OnPlayerSpawned;
        _game.onPlayerDied += OnPlayerDied;
        _game.onQuitToMenu += Unbind;
        Log("bound game");
    }

    public void Unbind()
    {
        if (_game != null)
        {
            _game.onCombatBegin -= OnCombatBegin;
            _game.onCombatEnd -= OnCombatEnd;
            _game.onCombatVictory -= OnCombatVictory;
            _game.onAnnouncement -= OnAnnouncement;
            _game.onPlayerSpawned -= OnPlayerSpawned;
            _game.onPlayerDied -= OnPlayerDied;
            _game.onQuitToMenu -= Unbind;
            _game = null;
        }
        _inCombat = false;
        _playerDead = false;
        _camping = false;
        _exploreActive = false;
        _activeBedState = null;
        Log("unbound game");
    }

    // Pushed by Main while the loading screen is up (before a GameClient
    // exists). StartMainMenu clears it on every path back to the menu.
    public void SetLoading(bool loading)
    {
        _loading = loading;
    }

    // Pushed by CampScreen.Open/Close while the player rests at a campfire.
    public void SetCamping(bool camping)
    {
        _camping = camping;
    }

    // Called when the player wakes from a camp sleep (GameClient.EndSleep). If the
    // skipped span crossed a sunrise / sunset / nightfall threshold, punctuate the
    // wake with that time-of-day sting — the same cue a natural crossing fires (a
    // big sleep jump is otherwise ignored by PollTimeOfDayStings). Either way, arm
    // the explore bed so ambient music resumes once camp ends. todBefore is the
    // normalized time-of-day at sleep start; hoursAdvanced is the in-world span
    // actually slept.
    public void OnCampSleepWake(double todBefore, double hoursAdvanced)
    {
        // Explore is the fallback ambient bed after camp (lowest priority, so the
        // camp bed keeps playing until the player leaves).
        _exploreActive = true;

        double span = hoursAdvanced / 24.0;
        if (span <= 0.0)
        {
            return;
        }
        bool crossed = span >= 1.0
            || ThresholdCrossed(todBefore, span, sunriseTimeOfDay)
            || ThresholdCrossed(todBefore, span, sunsetTimeOfDay)
            || ThresholdCrossed(todBefore, span, nightTimeOfDay);
        if (!crossed)
        {
            return;
        }
        double tod = World.Current?.WorldState?.TimeOfDay01 ?? todBefore;
        PlaySting(PhaseSting(tod));
    }

    // Does the forward arc of length `span` (in days) starting at todBefore pass
    // the normalized threshold `t`? span >= 1 (handled by the caller) crosses
    // everything; here span < 1.
    private static bool ThresholdCrossed(double todBefore, double span, double t)
    {
        double d = t - todBefore;
        d -= System.Math.Floor(d);
        return d < span;
    }

    // The time-of-day sting matching the phase the wake time falls in: daytime →
    // the morning cue, the brief dusk band → sunset, otherwise (deep night /
    // pre-dawn) → night.
    private EMusicSting PhaseSting(double tod)
    {
        if (tod >= sunriseTimeOfDay && tod < sunsetTimeOfDay)
        {
            return EMusicSting.Sunrise;
        }
        if (tod >= sunsetTimeOfDay && tod < nightTimeOfDay)
        {
            return EMusicSting.Sunset;
        }
        return EMusicSting.Night;
    }

    // (Re)spawn clears the death state so explore / combat beds resume.
    private void OnPlayerSpawned(Player player)
    {
        _playerDead = false;
    }

    private void OnPlayerDied(Player player)
    {
        _playerDead = true;
        Log("player died");
        PlaySting(EMusicSting.PlayerDeath);
    }

    private void OnCombatBegin()
    {
        _inCombat = true;
        Log("combat begin");
    }

    private void OnCombatEnd()
    {
        _inCombat = false;
        Log("combat end");
    }

    // Fires alongside OnCombatEnd when the player killed the last threat. The
    // bed has already left Combat (OnCombatEnd); this lays the victory sting
    // over the return to Explore — the unique kill-the-final-mob cue, vs the
    // plain crossfade you get from running away.
    private void OnCombatVictory()
    {
        PlaySting(EMusicSting.CombatVictory);
    }

    // Entering a region latches Explore on. It's the lowest-priority bed, so it
    // only actually plays when nothing higher (Loading/Combat/Death) is active,
    // and re-entering doesn't restart it (ResolveBed returns the same track).
    private void OnAnnouncement(Announcement a)
    {
        if (a == null || a.type != EAnnouncementType.Region) { return; }
        // Trigger explore only when nothing higher is already playing ("something
        // else isn't playing"). When explore is itself the current bed this just
        // keeps it set — ResolveBed returns the same track, so it won't restart
        // (no self-interrupt).
        if (_activeBedState == null || _activeBedState == EMusicState.Explore)
        {
            _exploreActive = true;
            Log("region entered -> explore triggered");
        }
    }

    // ----- Per-frame -------------------------------------------------------

    public override void _Process(double delta)
    {
        PollTimeOfDayStings();

        ResolveBed(out MusicTrackData track, out string clip, out EMusicState? state);
        if (track != _activeTrack)
        {
            StartCrossfade(track, clip, state);
        }
        else if (clip != _activeClip)
        {
            SwitchClip(clip);
        }

        TickCrossfade((float)delta);

        // Explore one-shot: when its non-looping track finishes, drop the trigger
        // so it stays silent until the next region entry instead of resolving to
        // explore again.
        if (_activeBedState == EMusicState.Explore && _fade >= 1f && !_active.Playing)
        {
            _exploreActive = false;
            Log("explore finished -> silent until next region");
        }

        // Did the last sting play out, or get cut early? Position ~= length means
        // it finished; a small position means something stopped it.
        bool stingPlaying = _stingPlayer.Playing;
        if (_stingWasPlaying && !stingPlaying)
        {
            Log($"sting stopped at {_stingPlayer.GetPlaybackPosition():F2}s");
        }
        _stingWasPlaying = stingPlaying;
    }

    // Fire sunrise / sunset as the world clock crosses their thresholds.
    private void PollTimeOfDayStings()
    {
        if (_game == null) { return; }
        World w = World.Current;
        WorldState ws = w?.WorldState;
        if (ws == null) { return; }

        double t = ws.TimeOfDay01;
        double prev = _prevTimeOfDay;
        _prevTimeOfDay = t;
        if (double.IsNaN(prev)) { return; }

        // Only treat a small forward step as a real crossing; a big jump is a
        // load / debug scrub, not the sun moving.
        double step = t - prev;
        if (step < 0 || step > 0.1) { return; }

        if (prev < sunriseTimeOfDay && t >= sunriseTimeOfDay) { PlaySting(EMusicSting.Sunrise); }
        if (prev < sunsetTimeOfDay && t >= sunsetTimeOfDay) { PlaySting(EMusicSting.Sunset); }
        if (prev < nightTimeOfDay && t >= nightTimeOfDay) { PlaySting(EMusicSting.Night); }
    }

    // ----- Bed resolution --------------------------------------------------

    // Highest-priority active cue whose biome filter matches. Returns a null
    // track for silence.
    private void ResolveBed(out MusicTrackData track, out string clip, out EMusicState? state)
    {
        int biome = -1;
        if (_game != null && AmbienceController.Current != null)
        {
            biome = AmbienceController.Current.State.BiomeId;
        }

        MusicCueData best = null;
        for (int i = 0; i < cues.Length; i++)
        {
            MusicCueData cue = cues[i];
            if (cue == null || cue.track == null) { continue; }
            if (!IsStateActive(cue.state)) { continue; }
            if (cue.biomeId >= 0 && cue.biomeId != biome) { continue; }
            if (best == null || cue.priority > best.priority) { best = cue; }
        }

        track = best?.track;
        clip = best?.clipName ?? "";
        state = best?.state;
    }

    private bool IsStateActive(EMusicState state)
    {
        switch (state)
        {
            case EMusicState.Silent: return true;            // valid fallback; author at lowest priority
            case EMusicState.Menu: return _game == null;
            case EMusicState.Loading: return _loading;
            case EMusicState.Explore: return _game != null && !_playerDead && _exploreActive;
            case EMusicState.Combat: return _game != null && !_playerDead && _inCombat;
            case EMusicState.Camp: return _game != null && !_playerDead && _camping;
            case EMusicState.Death: return _playerDead;
            default: return false;
        }
    }

    // ----- Playback --------------------------------------------------------

    private void StartCrossfade(MusicTrackData track, string clip, EMusicState? state)
    {
        // Swap roles: the old _active starts fading out as _idle; the new track
        // fades in on what is now _active.
        (_active, _idle) = (_idle, _active);
        _idleTrack = _activeTrack;
        _activeTrack = track;
        _activeClip = clip;
        _activePlayback = null;
        _activeBedState = track != null ? state : null;
        // Switching to any non-explore bed consumes the explore trigger so
        // explore doesn't resume after that bed ends (e.g. after combat) and
        // mask the victory sting — only a fresh region entry replays it.
        if (_activeBedState != EMusicState.Explore) { _exploreActive = false; }

        if (track?.stream != null)
        {
            _active.Stream = track.stream;
            _active.Play();
            _activePlayback = _active.GetStreamPlayback() as AudioStreamPlaybackInteractive;
            ApplyClip(clip);
        }
        else
        {
            _active.Stop();
        }
        _fade = 0f;
        Log($"bed -> {(track != null ? track.displayName : "(silence)")}");
    }

    private void SwitchClip(string clip)
    {
        _activeClip = clip;
        ApplyClip(clip);
    }

    private void ApplyClip(string clip)
    {
        if (_activePlayback != null && !string.IsNullOrEmpty(clip))
        {
            _activePlayback.SwitchToClipByName(clip);
        }
    }

    private void TickCrossfade(float delta)
    {
        if (_fade < 1f)
        {
            float step = crossfadeSeconds > 0f ? delta / crossfadeSeconds : 1f;
            _fade = Mathf.Min(1f, _fade + step);
        }

        // Duck the beds for the fixed hold window after a sting fires, ease back.
        if (_duckRemaining > 0f) { _duckRemaining -= delta; }
        float targetDuck = _duckRemaining > 0f ? stingDuckDb : 0f;
        float duckStep = stingDuckSeconds > 0f ? Mathf.Abs(stingDuckDb) * delta / stingDuckSeconds : Mathf.Abs(stingDuckDb);
        _duckDb = Mathf.MoveToward(_duckDb, targetDuck, duckStep);

        ApplyVolume(_active, _activeTrack, _fade);
        ApplyVolume(_idle, _idleTrack, 1f - _fade);

        // Once the outgoing track is inaudible, stop it so it isn't decoding.
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
        player.VolumeDb = Mathf.LinearToDb(amp) + track.volumeDb + masterVolumeDb + _duckDb;
    }

    // Console-driven (music_test_sting): fire the death stinger standalone to
    // separate a silent-file/player problem from a play-context one.
    public void PlayTestSting()
    {
        PlaySting(EMusicSting.PlayerDeath);
    }

    private void PlaySting(EMusicSting sting)
    {
        MusicTrackData track = FindSting(sting);
        if (track?.stream == null)
        {
            Log($"sting {sting} -> no track wired");
            return;
        }
        _stingPlayer.Stream = track.stream;
        _stingPlayer.VolumeDb = track.volumeDb + masterVolumeDb;
        _stingPlayer.Play();
        _duckRemaining = stingDuckHoldSeconds;
        Log($"sting {sting} -> {track.displayName} [playing={_stingPlayer.Playing} db={_stingPlayer.VolumeDb:F1} bus={_stingPlayer.Bus} len={track.stream.GetLength():F2}s ts={Engine.TimeScale:F2} muted={AudioServer.IsBusMute(AudioServer.GetBusIndex(MUSIC_BUS))}]");
    }

    private static void Log(string msg)
    {
        if (CVars.musicDebug.Value) { GD.Print($"[Music] {msg}"); }
    }

    private MusicTrackData FindSting(EMusicSting sting)
    {
        for (int i = 0; i < stings.Length; i++)
        {
            MusicStingData s = stings[i];
            if (s != null && s.sting == sting) { return s.track; }
        }
        return null;
    }
}
