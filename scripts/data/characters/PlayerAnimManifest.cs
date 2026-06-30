using Godot;
using System.Collections.Generic;

// Authoring-only editor tool: merges single-clip animation FBXs from a folder
// into the player's combined AnimationLibrary (human_anims.res), so adding
// a downloaded animation is "drop the FBX in the folder + press a button"
// instead of the manual extract / Save-to-File / Load / rename / set-loop loop.
//
// Workflow:
//   1. Drop each animation FBX into SourceFolder, named after the clip slot it
//      fills: Idle.fbx -> "idle", Run.fbx -> "run", Swim_Idle.fbx -> "swim_idle"
//      (the filename, lower-cased, IS the clip name the game looks up — see the
//      EAnimation -> name map in default_player.tres).
//   2. Open this resource in the inspector and press "Rebuild Animation Library"
//      (or Project > Tools > Rebuild Player Animations).
//
// Per-clip loop + speed + events are authored in the Clips list (one row each).
// Loop, Speed, and Events are BAKED at rebuild: Loop sets the clip's loop mode,
// Speed time-scales its keyframes, and each PlayerAnimEvent becomes a Call
// Method Track key — all written into human_anims.res. There is no runtime cost
// and no import-time involvement — re-tune a value and rebuild to apply. Any FBX
// in the folder without a Clips row gets a default row appended (Loop guessed
// from the clip name, Speed 1) so every animation shows up as an editable row
// after a rebuild.
//
// EVENTS SURVIVE RE-IMPORT: the rebuild replaces each clip wholesale with a
// fresh duplicate of the source FBX animation, so any method tracks / events
// added directly in the AnimationPlayer dock are LOST on the next rebuild (this
// is how the footstep cues were silently wiped once). Authoring events on the
// Clips rows here — the text .tres, not the binary .res — makes them durable:
// rebuild re-bakes them every time. To pull events you tuned visually in the
// dock back into the manifest, press "Capture Events From Library".
//
// MERGE, not replace: each rebuild adds/overwrites only the clips that have an
// FBX in the folder and leaves every other clip in the library untouched, so
// you can swap in a new clip without FBXs for all the others. To delete a clip,
// remove it in the AnimationPlayer's library dock.
//
// All source clips must be rigged to the same Synty skeleton the player mesh
// uses (rootSkeleton/Skeleton3D with *_joint bones); the imported tracks then
// bind without any retargeting. If a downloaded clip's tracks read
// "mixamorig:Hips" instead, it's on the wrong skeleton and won't apply — re-rig
// it to the Synty skeleton before adding. Mirrors the VoxelAtlasManifest
// pattern (editor-visible source of truth + ExportToolButton + thin addon).
[Tool]
[GlobalClass]
public partial class PlayerAnimManifest : Resource
{
    // Folder scanned for *.fbx animation sources. MUST be kept separate from the
    // character rig FBXs (BasicHero_F/M, HeroPoses) so those aren't swept in as
    // bogus clips.
    [Export(PropertyHint.Dir)]
    public string sourceFolder = "res://assets/models/characters/polysplit/anims";

    // The combined library written/merged into. The player.tscn AnimationPlayer
    // loads this as its default ("") library, so clip names must be bare
    // ("idle", not "lib/idle").
    [Export(PropertyHint.GlobalFile, "*.res")]
    public string outputLibraryPath = "res://assets/models/characters/polysplit/human_anims.res";

    // One row per clip: its name (= source filename, lower-cased), loop flag,
    // playback speed, and authored events. The single source of truth for
    // per-clip authoring — auto-grows as new FBXs are added to the folder and
    // rebuilt.
    [Export]
    public PlayerAnimClipSetting[] clips = System.Array.Empty<PlayerAnimClipSetting>();

    // NodePath (relative to the AnimationPlayer's root_node) of the node whose
    // method a baked event calls — the rig's ModelAnimator. The player model
    // packages put it one level above the FBX root, so the default resolves in
    // both BasicHero_F and BasicHero_M sharing this one library. Authored here
    // (not hardcoded) so a different rig layout can repoint it.
    [Export]
    public string methodTrackTarget = "../../ModelAnimator";

    // Initial loop guess for a freshly-discovered clip's auto-appended row.
    // These are the typical one-shots; everything else defaults to looping.
    // Only seeds the DEFAULT — the Clips row is authoritative once it exists.
    private static readonly HashSet<string> DefaultOneShots = new()
    {
        "attack", "attack1", "attack2", "jump", "dash", "die", "land",
    };

    // Inspector button (Godot 4.4+). Same entry point as the Tools menu item.
    [ExportToolButton("Rebuild Animation Library")]
    public Callable RebuildButton => Callable.From(RebuildLibrary);

    // Pull the method-track keys currently in the library back into the manifest
    // as PlayerAnimEvents, so events tuned visually in the AnimationPlayer dock
    // become durable (re-baked on every future rebuild) instead of being lost on
    // the next FBX re-import. The author's loop: scrub + place a Call Method
    // Track key in the dock, press this, then rebuilds preserve it.
    [ExportToolButton("Capture Events From Library")]
    public Callable CaptureButton => Callable.From(CaptureEventsFromLibrary);

    // Merge every *.fbx in SourceFolder into the output AnimationLibrary, baking
    // each clip's loop + speed from its Clips row. Existing clips not represented
    // by an FBX are preserved; newly-found clips get a default row appended.
    public void RebuildLibrary()
    {
        using DirAccess dir = DirAccess.Open(sourceFolder);
        if (dir == null)
        {
            GD.PushError($"PlayerAnimManifest: cannot open source folder '{sourceFolder}' (error {DirAccess.GetOpenError()}).");
            return;
        }

        AnimationLibrary lib = null;
        if (Godot.FileAccess.FileExists(outputLibraryPath))
        {
            // Load the cached shared instance so an open scene's AnimationPlayer
            // sees the merged clips immediately, not just after reload.
            lib = ResourceLoader.Load<AnimationLibrary>(outputLibraryPath, "", ResourceLoader.CacheMode.Reuse);
        }
        lib ??= new AnimationLibrary();

        Dictionary<string, PlayerAnimClipSetting> byName = new();
        if (clips != null)
        {
            foreach (PlayerAnimClipSetting c in clips)
            {
                if (c != null && !string.IsNullOrEmpty(c.name))
                {
                    byName[c.name] = c;
                }
            }
        }

        List<string> merged = new();
        List<PlayerAnimClipSetting> appended = new();

        dir.ListDirBegin();
        for (string file = dir.GetNext(); file != ""; file = dir.GetNext())
        {
            if (dir.CurrentIsDir() || !file.ToLower().EndsWith(".fbx"))
            {
                continue;
            }

            string clip = file.GetBaseName().ToLower();
            string path = sourceFolder.PathJoin(file);
            PackedScene scene = ResourceLoader.Load<PackedScene>(path);
            if (scene == null)
            {
                GD.PushWarning($"PlayerAnimManifest: '{file}' is not an imported scene yet — skipped.");
                continue;
            }

            Node inst = scene.Instantiate();
            AnimationPlayer ap = FindAnimationPlayer(inst);
            string[] names = ap?.GetAnimationList() ?? System.Array.Empty<string>();
            if (names.Length == 0)
            {
                GD.PushWarning($"PlayerAnimManifest: '{file}' has no animation — skipped.");
                inst.Free();
                continue;
            }
            if (names.Length > 1)
            {
                GD.PushWarning($"PlayerAnimManifest: '{file}' has {names.Length} clips; using the first ('{names[0]}') for '{clip}'.");
            }

            if (!byName.TryGetValue(clip, out PlayerAnimClipSetting setting))
            {
                setting = new PlayerAnimClipSetting
                {
                    name = clip,
                    loop = !DefaultOneShots.Contains(clip),
                    speed = 1f,
                    ResourceName = clip,
                };
                byName[clip] = setting;
                appended.Add(setting);
            }

            // Duplicate so the embedded copy is independent of the imported FBX.
            Animation anim = (Animation)ap.GetAnimation(names[0]).Duplicate(true);
            anim.LoopMode = setting.loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            ApplyClipSpeed(anim, setting.speed, clip);
            // Re-bake authored events (footsteps, hit frames, ...) onto the fresh
            // duplicate — done AFTER speed scaling so normalized times map onto
            // the final clip length. This is what makes events outlast a re-import.
            ApplyClipEvents(anim, setting, clip);

            if (lib.HasAnimation(clip))
            {
                lib.RemoveAnimation(clip);
            }
            lib.AddAnimation(clip, anim);
            merged.Add(clip);
            inst.Free();
        }
        dir.ListDirEnd();

        if (merged.Count == 0)
        {
            GD.PushWarning($"PlayerAnimManifest: no .fbx clips found in '{sourceFolder}'. Nothing written.");
            return;
        }

        Error err = ResourceSaver.Save(lib, outputLibraryPath);
        if (err != Error.Ok)
        {
            GD.PushError($"PlayerAnimManifest: failed to save '{outputLibraryPath}' (error {err}).");
            return;
        }

        // Grow the visible Clips list with any newly-discovered clips so every
        // animation in the folder has an editable row, then persist the manifest.
        if (appended.Count > 0)
        {
            List<PlayerAnimClipSetting> grown = new(clips ?? System.Array.Empty<PlayerAnimClipSetting>());
            grown.AddRange(appended);
            clips = grown.ToArray();
            if (!string.IsNullOrEmpty(ResourcePath))
            {
                ResourceSaver.Save(this, ResourcePath);
            }
            EmitChanged();
        }

        merged.Sort();
        GD.Print($"PlayerAnimManifest: merged {merged.Count} clip(s) into {outputLibraryPath}: {string.Join(", ", merged)}");
        if (Engine.IsEditorHint())
        {
            EditorInterface.Singleton.GetResourceFilesystem().Scan();
        }
    }

    // Time-scale every keyframe (and the clip length) by 1/speed so the clip
    // plays `speed`x faster. A uniform positive scale preserves key ordering, so
    // re-setting times in place needs no re-sort. speed <= 0 or 1 is a no-op.
    private static void ApplyClipSpeed(Animation anim, float speed, string clip)
    {
        if (speed <= 0f)
        {
            GD.PushWarning($"PlayerAnimManifest: clip '{clip}' has non-positive speed {speed}; ignored.");
            return;
        }
        if (Mathf.IsEqualApprox(speed, 1f))
        {
            return;
        }
        double timeFactor = 1.0 / speed;
        for (int t = 0; t < anim.GetTrackCount(); t++)
        {
            int keys = anim.TrackGetKeyCount(t);
            for (int k = 0; k < keys; k++)
            {
                anim.TrackSetKeyTime(t, k, anim.TrackGetKeyTime(t, k) * timeFactor);
            }
        }
        anim.Length = (float)(anim.Length * timeFactor);
    }

    // Insert one Call Method Track per clip carrying a key for each authored
    // PlayerAnimEvent, at NormalizedTime * (post-speed) clip length. Pointed at
    // MethodTrackTarget (the rig's ModelAnimator). No events = no track added, so
    // clips without events are byte-identical to the plain FBX duplicate.
    private void ApplyClipEvents(Animation anim, PlayerAnimClipSetting setting, string clip)
    {
        if (setting?.events == null || setting.events.Length == 0)
        {
            return;
        }
        int track = -1;
        foreach (PlayerAnimEvent ev in setting.events)
        {
            if (ev == null || string.IsNullOrEmpty(ev.method))
            {
                continue;
            }
            if (track < 0)
            {
                track = anim.AddTrack(Animation.TrackType.Method);
                anim.TrackSetPath(track, methodTrackTarget);
            }
            double time = Mathf.Clamp(ev.normalizedTime, 0f, 1f) * anim.Length;
            Godot.Collections.Dictionary key = new()
            {
                { "method", ev.method },
                { "args", ev.args ?? new Godot.Collections.Array() },
            };
            anim.TrackInsertKey(track, time, key);
        }
        if (track >= 0)
        {
            GD.Print($"PlayerAnimManifest: baked {setting.events.Length} event(s) onto '{clip}'.");
        }
    }

    // Reverse of the bake: read the method-track keys currently in the library
    // and write them into the matching Clips rows as PlayerAnimEvents (normalized
    // times). Lets events tuned visually in the AnimationPlayer dock be made
    // durable, after which every rebuild re-applies them. Only clips that exist
    // as Clips rows are updated; a clip with no method tracks gets an empty list
    // (so deleting a key in the dock + capturing also clears it from the manifest).
    public void CaptureEventsFromLibrary()
    {
        if (!Godot.FileAccess.FileExists(outputLibraryPath))
        {
            GD.PushError($"PlayerAnimManifest: library '{outputLibraryPath}' does not exist; nothing to capture.");
            return;
        }
        AnimationLibrary lib = ResourceLoader.Load<AnimationLibrary>(outputLibraryPath, "", ResourceLoader.CacheMode.Reuse);
        if (lib == null)
        {
            GD.PushError($"PlayerAnimManifest: failed to load '{outputLibraryPath}'.");
            return;
        }

        Dictionary<string, PlayerAnimClipSetting> byName = new();
        if (clips != null)
        {
            foreach (PlayerAnimClipSetting c in clips)
            {
                if (c != null && !string.IsNullOrEmpty(c.name))
                {
                    byName[c.name] = c;
                }
            }
        }

        int totalEvents = 0;
        int clipsTouched = 0;
        foreach (string clip in lib.GetAnimationList())
        {
            if (!byName.TryGetValue(clip, out PlayerAnimClipSetting setting))
            {
                continue;
            }
            Animation anim = lib.GetAnimation(clip);
            float length = anim.Length > 0f ? anim.Length : 1f;
            List<PlayerAnimEvent> events = new();
            for (int t = 0; t < anim.GetTrackCount(); t++)
            {
                if (anim.TrackGetType(t) != Animation.TrackType.Method)
                {
                    continue;
                }
                int keys = anim.TrackGetKeyCount(t);
                for (int k = 0; k < keys; k++)
                {
                    Godot.Collections.Dictionary key = anim.TrackGetKeyValue(t, k).AsGodotDictionary();
                    events.Add(new PlayerAnimEvent
                    {
                        normalizedTime = (float)(anim.TrackGetKeyTime(t, k) / length),
                        method = key.TryGetValue("method", out Variant m) ? m.AsStringName() : "",
                        args = key.TryGetValue("args", out Variant a) ? a.AsGodotArray() : new Godot.Collections.Array(),
                    });
                }
            }
            if (events.Count != (setting.events?.Length ?? 0) || events.Count > 0)
            {
                setting.events = events.ToArray();
                clipsTouched++;
                totalEvents += events.Count;
            }
        }

        if (!string.IsNullOrEmpty(ResourcePath))
        {
            ResourceSaver.Save(this, ResourcePath);
        }
        EmitChanged();
        GD.Print($"PlayerAnimManifest: captured {totalEvents} event(s) across {clipsTouched} clip(s) into the manifest.");
    }

    private static AnimationPlayer FindAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer ap)
        {
            return ap;
        }
        foreach (Node child in node.GetChildren())
        {
            AnimationPlayer found = FindAnimationPlayer(child);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }
}
