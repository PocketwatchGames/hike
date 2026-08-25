using Godot;
using System.Threading.Tasks;

// Answers ONE engine question in text, with no world and no game: does a
// TRANSPARENT material with depth_draw_always actually make a later transparent
// draw behind it fail the depth test?
//
// The whole waterfall-against-water sort rests on that (see waterfall.gdshader),
// and a screenshot of the game cannot settle it — a fall that has vanished
// behind a pool looks exactly the same whether the pool CULLED it or simply
// painted over it. So this draws both cases as flat quads and reads the pixels.
//
// Must run WINDOWED. The dummy renderer rasterizes nothing, so under --headless
// every sample comes back as the clear colour.
public static class DepthSortCheck
{
	// Its own SubViewport with its own World3D, so nothing the root scene draws
	// — menu, console, HUD — can land on a sample point.
	private const int VIEW_SIZE = 64;
	// PERSPECTIVE, at the game's near plane and a plausible view distance. It
	// has to be: a clip-space depth nudge is linear under an orthogonal camera
	// and wildly non-linear under a perspective one, so an orthogonal harness
	// would answer the third pair's question wrong.
	private const float CAMERA_FOV = 70f;
	private const float CAMERA_NEAR = 0.1f;
	private const float VIEW_DISTANCE = 20f;
	// Pair centres, spaced so each lands on its own third of the viewport.
	private const float PAIR_OFFSET = 7f;
	private const float NEAR_Z = -VIEW_DISTANCE;
	private const float FAR_Z = -(VIEW_DISTANCE + 2f);
	private const float NEAR_QUAD = 4f;
	private const float FAR_QUAD = 6f;
	// The bias waterfall.gdshader carried into its depth write.
	private const float CLIP_BIAS = 0.001f;
	// The band the real materials use.
	private const int NEAR_PRIORITY = -6;
	private const int FAR_PRIORITY = -3;
	// The rendering server draws on its own queue; the readback has to straddle
	// real frames or it comes back empty.
	private const int FRAMES_BEFORE_READ = 4;

	public static async Task RunAndQuit(SceneTree tree)
	{
		var view = new SubViewport();
		view.Size = new Vector2I(VIEW_SIZE, VIEW_SIZE);
		view.OwnWorld3D = true;
		view.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

		var camera = new Camera3D();
		camera.Fov = CAMERA_FOV;
		camera.Near = CAMERA_NEAR;
		camera.Position = Vector3.Zero;
		view.AddChild(camera);
		camera.Current = true;

		// LEFT pair is the control: the near quad writes no depth, exactly like
		// every transparent material in the project did before. The far quad
		// MUST paint over it — if it doesn't, the harness is wrong and the test
		// pair's answer means nothing.
		BuildPair(view, -PAIR_OFFSET, BaseMaterial3D.DepthDrawModeEnum.Disabled);
		// MIDDLE pair is the test: the near quad writes depth.
		BuildPair(view, 0f, BaseMaterial3D.DepthDrawModeEnum.Always);
		// RIGHT pair writes depth too, but through a vertex shader carrying the
		// `POSITION.z -= 0.001 * POSITION.w` nudge waterfall.gdshader had. A
		// clip-space offset is a FIXED step in a depth buffer whose world-space
		// meaning goes as distance squared over the near plane, so at 20 m with
		// near 0.1 it is worth metres, not the "0.02 world units" its comment
		// claimed. This is what that costs once the material writes depth.
		Quad(view, new Vector3(PAIR_OFFSET, 0f, FAR_Z), FAR_QUAD,
			Flat(Colors.Green, BaseMaterial3D.DepthDrawModeEnum.Disabled, FAR_PRIORITY));
		Quad(view, new Vector3(PAIR_OFFSET, 0f, NEAR_Z), NEAR_QUAD, BiasedNear());

		// Deferred: this runs from Main._Ready, and the root is still setting up
		// its own children there, so a direct AddChild is refused.
		tree.Root.CallDeferred(Node.MethodName.AddChild, view);

		for (int i = 0; i < FRAMES_BEFORE_READ; i++)
		{
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		}

		Image image = view.GetTexture().GetImage();
		int y = VIEW_SIZE / 2;
		Color control = image.GetPixel(VIEW_SIZE / 4, y);
		Color test = image.GetPixel(VIEW_SIZE / 2, y);
		Color biased = image.GetPixel(VIEW_SIZE * 3 / 4, y);

		GD.Print($"[depth_sort_check] control (near depth_draw_never):  {Describe(control)}");
		GD.Print($"[depth_sort_check] test    (near depth_draw_always): {Describe(test)}");
		GD.Print($"[depth_sort_check] biased  (+ POSITION.z nudge):     {Describe(biased)}");
		if (IsFar(biased))
		{
			GD.Print("[depth_sort_check] the clip-space nudge pushed the near quad BEHIND a quad "
				+ "2 m further away — a depth write cannot carry that bias.");
		}
		if (!IsFar(control))
		{
			GD.Print("[depth_sort_check] INCONCLUSIVE: the control did not draw the far quad over "
				+ "the near one, so the harness is not measuring what it thinks it is.");
		}
		else if (IsNear(test))
		{
			GD.Print("[depth_sort_check] PASS: depth_draw_always on a transparent material DOES "
				+ "cull a later transparent draw behind it.");
		}
		else
		{
			GD.Print("[depth_sort_check] FAIL: depth_draw_always did NOT cull the far quad — a "
				+ "transparent material cannot occlude another transparent one this way.");
		}
		tree.Quit();
	}

	// Near quad red, far quad green, both fully opaque but both in the
	// transparent pass — which is the configuration under test. Alpha 1.0 does
	// not make them opaque-pass: TransparencyEnum.Alpha is explicit.
	private static void BuildPair(Node parent, float x, BaseMaterial3D.DepthDrawModeEnum nearDepth)
	{
		Quad(parent, new Vector3(x, 0f, FAR_Z), FAR_QUAD,
			Flat(Colors.Green, BaseMaterial3D.DepthDrawModeEnum.Disabled, FAR_PRIORITY));
		Quad(parent, new Vector3(x, 0f, NEAR_Z), NEAR_QUAD,
			Flat(Colors.Red, nearDepth, NEAR_PRIORITY));
	}

	// Built in code deliberately: the flags under test ARE the test, and an
	// authored .tres would put them out of the harness's reach.
	private static StandardMaterial3D Flat(Color color, BaseMaterial3D.DepthDrawModeEnum depthDraw, int priority)
	{
		var material = new StandardMaterial3D();
		material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		material.DepthDrawMode = depthDraw;
		material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
		material.AlbedoColor = color;
		material.RenderPriority = priority;
		return material;
	}

	private static void Quad(Node parent, Vector3 position, float size, Material material)
	{
		var instance = new MeshInstance3D();
		instance.Mesh = new QuadMesh { Size = new Vector2(size, size) };
		instance.MaterialOverride = material;
		instance.Position = position;
		parent.AddChild(instance);
	}

	// Same flags as the test pair, but through a shader so the vertex stage can
	// carry the nudge. Built in code for the same reason the materials are.
	private static ShaderMaterial BiasedNear()
	{
		var shader = new Shader();
		shader.Code = @"shader_type spatial;
render_mode blend_premul_alpha, depth_draw_always, cull_disabled, unshaded;
void vertex() {
	POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	POSITION.z -= " + CLIP_BIAS.ToString(System.Globalization.CultureInfo.InvariantCulture) + @" * POSITION.w;
}
void fragment() {
	ALBEDO = vec3(1.0, 0.0, 0.0);
	ALPHA = 1.0;
}";
		var material = new ShaderMaterial();
		material.Shader = shader;
		material.RenderPriority = NEAR_PRIORITY;
		return material;
	}

	private static bool IsNear(Color c) => c.R > 0.5f && c.G < 0.5f;

	private static bool IsFar(Color c) => c.G > 0.5f && c.R < 0.5f;

	private static string Describe(Color c)
	{
		string name = IsNear(c) ? "NEAR quad (red)" : IsFar(c) ? "FAR quad (green)" : "neither";
		return $"{name}  rgb({c.R:0.00}, {c.G:0.00}, {c.B:0.00})";
	}
}
