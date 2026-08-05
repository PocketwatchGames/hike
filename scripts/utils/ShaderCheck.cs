using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

// Loads every .gdshader in the project so the engine parses it, without ever
// starting a game. Driven by the `shader_check` cvar off Main._Ready.
//
// The dummy renderer still runs the shader compiler, so `--headless` reports
// syntax errors, bad includes and unknown global uniforms exactly as a real
// run does. It must be a real boot though: `--script` never brings the
// rendering server up, so nothing gets parsed and every shader looks clean.
public static class ShaderCheck
{
	private const string SHADER_DIR = "res://shaders";
	// The rendering server parses on its own queue, so the loads have to
	// straddle real frames — loading and quitting in one go reports nothing.
	private const int FRAMES_BEFORE_QUIT = 5;

	public static async Task RunAndQuit(SceneTree tree)
	{
		List<string> paths = new List<string>();
		Collect(SHADER_DIR, paths);
		paths.Sort();
		GD.Print($"[shader_check] loading {paths.Count} shaders from {SHADER_DIR}");

		// Loading a Shader resource is NOT enough to compile it — the code is
		// only handed to the shader compiler when it's bound to a material. So
		// bind each one to a throwaway ShaderMaterial (the one place creating a
		// material in code is right: nothing renders, this IS the test) and hold
		// the references so none are freed before the server gets to them.
		List<ShaderMaterial> loaded = new List<ShaderMaterial>();
		foreach (string path in paths)
		{
			Shader shader = ResourceLoader.Load<Shader>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
			if (shader == null)
			{
				GD.PrintErr($"[shader_check] failed to load {path}");
				continue;
			}
			loaded.Add(new ShaderMaterial { Shader = shader });
		}

		for (int i = 0; i < FRAMES_BEFORE_QUIT; i++)
		{
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		}
		GD.Print($"[shader_check] done ({loaded.Count} shaders parsed)");
		tree.Quit();
	}

	private static void Collect(string dirPath, List<string> outPaths)
	{
		using DirAccess dir = DirAccess.Open(dirPath);
		if (dir == null)
		{
			GD.PrintErr($"[shader_check] cannot open {dirPath}");
			return;
		}
		foreach (string name in dir.GetFiles())
		{
			if (name.EndsWith(".gdshader"))
			{
				outPaths.Add(dirPath.PathJoin(name));
			}
		}
		foreach (string name in dir.GetDirectories())
		{
			Collect(dirPath.PathJoin(name), outPaths);
		}
	}
}
