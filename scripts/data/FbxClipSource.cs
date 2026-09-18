using Godot;

// Parses an animation FBX from asset_src/ into a scene that plays like the
// importer-built one it replaces: same fps, trimmed, and a constant track dropped
// only when it sits at rest. Not track-for-track identical — the importer's
// tolerances differ at float-noise level and it also thinned keys — so compare
// sampled poses, not track counts. One implementation for every builder:
// PlayerAnimManifest calls Parse; tools/build_*_anims.gd call ParseScene on the
// script loaded by path (GDScript cannot see C# statics, and a headless -s run
// never registers C# global class names).
public partial class FbxClipSource : RefCounted
{
    // The scene importer's animation/fps default, which every committed library
    // was baked at.
    public const float BAKE_FPS = 30f;

    public Node ParseScene(string relativePath, bool keepConstantTracks)
    {
        return Parse(relativePath, keepConstantTracks);
    }

    // relativePath is under asset_src/. Null when the file does not parse; the
    // caller frees the returned scene.
    //
    // keepConstantTracks keeps a constant track that sits at rest, which the
    // importer drops — a HELD pose (climb_idle) is nothing but such tracks.
    public static Node Parse(string relativePath, bool keepConstantTracks)
    {
        string path = AssetSource.GlobalPath(relativePath);
        var document = new FbxDocument();
        var state = new FbxState();
        Error err = document.AppendFromFile(path, state);
        if (err != Error.Ok)
        {
            GD.PushWarning($"FbxClipSource: could not parse '{path}' (error {err}).");
            return null;
        }
        // Not GenerateScene's own removeImmutableTracks: it drops EVERY constant
        // track, where the importer drops only those at rest. A constant pose away
        // from rest (the sparrow's folded wings) is what holds those bones, and
        // losing it cost up to 49 degrees on birdy.
        Node scene = document.GenerateScene(state, BAKE_FPS, trimming: true, removeImmutableTracks: false);
        PruneConstantTracks(scene, keepConstantTracks);
        return scene;
    }

    private static void PruneConstantTracks(Node node, bool keepConstantTracks)
    {
        if (node is AnimationPlayer player)
        {
            Node root = player.GetNodeOrNull(player.RootNode);
            foreach (string name in player.GetAnimationList())
            {
                Animation anim = player.GetAnimation(name);
                for (int track = anim.GetTrackCount() - 1; track >= 0; track--)
                {
                    PruneTrack(anim, track, root, keepConstantTracks);
                }
            }
        }
        foreach (Node child in node.GetChildren())
        {
            PruneConstantTracks(child, keepConstantTracks);
        }
    }

    // A constant track collapses to its one key; at rest it goes entirely,
    // unless the clip keeps them.
    private static void PruneTrack(Animation anim, int track, Node root, bool keepConstantTracks)
    {
        Animation.TrackType type = anim.TrackGetType(track);
        if (type != Animation.TrackType.Position3D && type != Animation.TrackType.Rotation3D
            && type != Animation.TrackType.Scale3D)
        {
            return;
        }
        int keys = anim.TrackGetKeyCount(track);
        if (keys == 0)
        {
            return;
        }
        Variant first = anim.TrackGetKeyValue(track, 0);
        for (int k = 1; k < keys; k++)
        {
            if (!Same(type, first, anim.TrackGetKeyValue(track, k)))
            {
                return;
            }
        }
        if (!keepConstantTracks && TryGetRest(root, anim.TrackGetPath(track), type, out Variant rest) && Same(type, first, rest))
        {
            anim.RemoveTrack(track);
            return;
        }
        for (int k = keys - 1; k >= 1; k--)
        {
            anim.TrackRemoveKey(track, k);
        }
    }

    private static bool TryGetRest(Node root, NodePath path, Animation.TrackType type, out Variant rest)
    {
        rest = default;
        Node node = root?.GetNodeOrNull(new NodePath(path.GetConcatenatedNames()));
        if (node is Skeleton3D skeleton && path.GetSubNameCount() > 0)
        {
            int bone = skeleton.FindBone(path.GetSubName(0));
            if (bone < 0)
            {
                return false;
            }
            Transform3D boneRest = skeleton.GetBoneRest(bone);
            rest = type switch
            {
                Animation.TrackType.Position3D => boneRest.Origin,
                Animation.TrackType.Rotation3D => boneRest.Basis.GetRotationQuaternion(),
                _ => boneRest.Basis.Scale,
            };
            return true;
        }
        if (node is Node3D node3D && path.GetSubNameCount() == 0)
        {
            rest = type switch
            {
                Animation.TrackType.Position3D => node3D.Position,
                Animation.TrackType.Rotation3D => node3D.Quaternion,
                _ => node3D.Scale,
            };
            return true;
        }
        return false;
    }

    private static bool Same(Animation.TrackType type, Variant a, Variant b)
    {
        if (type == Animation.TrackType.Rotation3D)
        {
            Quaternion qa = a.AsQuaternion();
            Quaternion qb = b.AsQuaternion();
            return qa.IsEqualApprox(qb) || qa.IsEqualApprox(-qb);
        }
        return a.AsVector3().IsEqualApprox(b.AsVector3());
    }
}
