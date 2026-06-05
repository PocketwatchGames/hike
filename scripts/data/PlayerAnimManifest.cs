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
// Per-clip loop + speed are authored in the Clips list (one row each). Loop and
// Speed are BAKED at rebuild: Loop sets the clip's loop mode, Speed time-scales
// its keyframes, both written into human_anims.res. There is no runtime
// cost and no import-time involvement — re-tune a value and rebuild to apply.
// Any FBX in the folder without a Clips row gets a default row appended (Loop
// guessed from the clip name, Speed 1) so every animation shows up as an
// editable row after a rebuild.
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
    public string SourceFolder = "res://assets/models/characters/polysplit/anims";

    // The combined library written/merged into. The player.tscn AnimationPlayer
    // loads this as its default ("") library, so clip names must be bare
    // ("idle", not "lib/idle").
    [Export(PropertyHint.GlobalFile, "*.res")]
    public string OutputLibraryPath = "res://assets/models/characters/polysplit/human_anims.res";

    // One row per clip: its name (= source filename, lower-cased), loop flag,
    // and playback speed. The single source of truth for per-clip authoring —
    // auto-grows as new FBXs are added to the folder and rebuilt.
    [Export]
    public PlayerAnimClipSetting[] Clips = System.Array.Empty<PlayerAnimClipSetting>();

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

    // Merge every *.fbx in SourceFolder into the output AnimationLibrary, baking
    // each clip's loop + speed from its Clips row. Existing clips not represented
    // by an FBX are preserved; newly-found clips get a default row appended.
    public void RebuildLibrary()
    {
        using DirAccess dir = DirAccess.Open(SourceFolder);
        if (dir == null)
        {
            GD.PushError($"PlayerAnimManifest: cannot open source folder '{SourceFolder}' (error {DirAccess.GetOpenError()}).");
            return;
        }

        AnimationLibrary lib = null;
        if (Godot.FileAccess.FileExists(OutputLibraryPath))
        {
            // Load the cached shared instance so an open scene's AnimationPlayer
            // sees the merged clips immediately, not just after reload.
            lib = ResourceLoader.Load<AnimationLibrary>(OutputLibraryPath, "", ResourceLoader.CacheMode.Reuse);
        }
        lib ??= new AnimationLibrary();

        Dictionary<string, PlayerAnimClipSetting> byName = new();
        if (Clips != null)
        {
            foreach (PlayerAnimClipSetting c in Clips)
            {
                if (c != null && !string.IsNullOrEmpty(c.Name))
                {
                    byName[c.Name] = c;
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
            string path = SourceFolder.PathJoin(file);
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
                    Name = clip,
                    Loop = !DefaultOneShots.Contains(clip),
                    Speed = 1f,
                    ResourceName = clip,
                };
                byName[clip] = setting;
                appended.Add(setting);
            }

            // Duplicate so the embedded copy is independent of the imported FBX.
            Animation anim = (Animation)ap.GetAnimation(names[0]).Duplicate(true);
            anim.LoopMode = setting.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            ApplyClipSpeed(anim, setting.Speed, clip);

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
            GD.PushWarning($"PlayerAnimManifest: no .fbx clips found in '{SourceFolder}'. Nothing written.");
            return;
        }

        Error err = ResourceSaver.Save(lib, OutputLibraryPath);
        if (err != Error.Ok)
        {
            GD.PushError($"PlayerAnimManifest: failed to save '{OutputLibraryPath}' (error {err}).");
            return;
        }

        // Grow the visible Clips list with any newly-discovered clips so every
        // animation in the folder has an editable row, then persist the manifest.
        if (appended.Count > 0)
        {
            List<PlayerAnimClipSetting> grown = new(Clips ?? System.Array.Empty<PlayerAnimClipSetting>());
            grown.AddRange(appended);
            Clips = grown.ToArray();
            if (!string.IsNullOrEmpty(ResourcePath))
            {
                ResourceSaver.Save(this, ResourcePath);
            }
            EmitChanged();
        }

        merged.Sort();
        GD.Print($"PlayerAnimManifest: merged {merged.Count} clip(s) into {OutputLibraryPath}: {string.Join(", ", merged)}");
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
