// Which WORLD a resource belongs to, read off where it sits: anything under
// resources/data/worlds/<name>/ is that world's own — its items, its
// conversations — and is offered by name (a picker, `give`, a sheet cell) only
// while working in that world. worlds/shared/ and every tree outside worlds/
// belong to no world and are offered everywhere.
//
// A context with NO world (a document outside worlds/, a subscene) is offered
// only the unscoped resources: what it references has to work in every world it
// ends up in.
//
// Visibility only. A world-scoped resource still loads from anywhere by path —
// this decides what an author is OFFERED, so another world's items stop crowding
// the list and cannot be picked by accident.
//
// tools/conversation_import applies the same rule on its own (it cannot
// reference this assembly): a sheet's `give:` resolves items/, then
// worlds/shared/items/, then its own world's items/.
public static class WorldScope
{
    private const string WorldsRoot = "res://resources/data/worlds/";
    private const string Shared = "shared";

    // The world a res:// path belongs to, or null for an unscoped one.
    public static string Of(string resPath)
    {
        if (string.IsNullOrEmpty(resPath) || !resPath.StartsWith(WorldsRoot))
        {
            return null;
        }
        int end = resPath.IndexOf('/', WorldsRoot.Length);
        if (end < 0)
        {
            return null;
        }
        string world = resPath.Substring(WorldsRoot.Length, end - WorldsRoot.Length);
        return world == Shared ? null : world;
    }

    // May a context working in `world` (null = none) be offered this resource?
    public static bool Offers(string world, string resPath)
    {
        string owner = Of(resPath);
        return owner == null || owner == world;
    }
}
