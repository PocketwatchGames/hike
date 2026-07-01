using Godot;
using System.Threading.Tasks;

// The water ripple normal maps (and the cloud noise) are NoiseTexture2D, which
// Godot generates asynchronously on a worker thread. Until generation finishes
// the texture's GPU image is an invalid placeholder, so any material that binds
// it fails uniform_set_create EVERY frame ("Texture (binding N) is not a valid
// texture") until the image lands. A CPU-pegged worldgen can starve that worker
// thread for most of a session, so the spam persists rather than clearing in a
// frame or two.
//
// StartGame awaits this once, up front — before worldgen pegs the CPU and
// before any water/sky material is built — so every later GD.Load of these
// paths (the shared water material, its per-chunk duplicates, the water cap,
// reflection sprites, the sky) returns a fully-generated, immediately-bindable
// texture. Keeps the noise authorable in the inspector (no baking) while never
// binding an unready texture.
public static class WaterRipples
{
    // Loaded and awaited up front; later GD.Load calls hit the resource cache.
    private static readonly string[] NoisePaths =
    {
        "res://assets/textures/water_ripple_a.tres",
        "res://assets/textures/water_ripple_b.tres",
        "res://assets/textures/skybox/cloud_noise.tres",
        // Sky/reflection star_texture global uniform. Only rasterized once the
        // sky dome is in frame (e.g. a perspective/wider-FOV preset), so a
        // starved cold worldgen spams uniform_set_create the moment sky renders.
        "res://assets/textures/skybox/starfield_placeholder.tres",
    };

    public static async Task EnsureReady(Node ctx)
    {
        var textures = new NoiseTexture2D[NoisePaths.Length];
        for (int i = 0; i < NoisePaths.Length; i++)
        {
            textures[i] = GD.Load<NoiseTexture2D>(NoisePaths[i]);
        }

        // Generation runs on a worker thread and reports back deferred on the
        // main thread, so poll per-frame until every image exists. Cap the wait
        // so a generation failure can never hang the loading screen.
        const int maxFrames = 600;
        for (int frame = 0; frame < maxFrames; frame++)
        {
            bool allReady = true;
            foreach (NoiseTexture2D tex in textures)
            {
                if (tex == null || tex.GetImage() == null)
                {
                    allReady = false;
                    break;
                }
            }
            if (allReady)
            {
                return;
            }
            await ctx.ToSignal(ctx.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        GD.PushWarning("WaterRipples: noise textures not ready after wait; water/sky may flicker briefly.");
    }
}
