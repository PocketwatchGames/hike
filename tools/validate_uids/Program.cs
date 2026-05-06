using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

class Program
{
	static readonly string[] ScriptScanRoots = new[] { "scripts", "addons", "tools" };
	static readonly string[] SceneScanRoots = new[] { "scenes", "resources", "addons" };
	static readonly string[] SceneExtensions = new[] { ".tscn", ".tres" };

	static readonly Regex UidValueRegex = new Regex(@"^uid://[a-z0-9]+$", RegexOptions.Compiled);
	static readonly Regex ExtResourceRegex = new Regex(
		@"\[ext_resource\b(?<attrs>[^\]]*)\]",
		RegexOptions.Compiled);
	static readonly Regex AttrRegex = new Regex(
		@"(?<key>\w+)\s*=\s*""(?<val>[^""]*)""",
		RegexOptions.Compiled);

	static readonly char[] UidCharset = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();
	static readonly Random UidRng = new Random();

	static int Main(string[] args)
	{
		string repoRoot = ResolveRepoRoot(args);
		bool fix = false;

		foreach (string a in args)
		{
			if (a == "--fix")
			{
				fix = true;
			}
		}

		Console.WriteLine($"Repo root: {repoRoot}");
		Console.WriteLine($"Mode: {(fix ? "validate + auto-fix missing .cs.uid sidecars" : "validate only")}");
		Console.WriteLine();

		var issues = new List<string>();

		var uidByPath = ScanUidSidecars(repoRoot, issues);
		ValidateScriptSidecars(repoRoot, uidByPath, issues, fix);
		ValidateUidUniqueness(uidByPath, issues);
		ValidateSceneReferences(repoRoot, uidByPath, issues);

		Console.WriteLine();
		if (issues.Count == 0)
		{
			Console.WriteLine("OK: no UID issues found.");
			return 0;
		}

		Console.WriteLine($"FAIL: {issues.Count} issue(s):");
		foreach (string issue in issues)
		{
			Console.WriteLine("  " + issue);
		}

		return 1;
	}

	static string ResolveRepoRoot(string[] args)
	{
		foreach (string a in args)
		{
			if (a.StartsWith("--root="))
			{
				return Path.GetFullPath(a.Substring("--root=".Length));
			}
		}

		string fromBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
		if (Directory.Exists(Path.Combine(fromBin, "scripts")))
		{
			return fromBin;
		}

		return Directory.GetCurrentDirectory();
	}

	static Dictionary<string, string> ScanUidSidecars(string repoRoot, List<string> issues)
	{
		var uidByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (string root in EnumerateRoots(repoRoot, SceneScanRoots))
		{
			foreach (string uidFile in Directory.EnumerateFiles(root, "*.uid", SearchOption.AllDirectories))
			{
				if (IsExcluded(uidFile))
				{
					continue;
				}

				ReadAndRecordUid(repoRoot, uidFile, uidByPath, issues);
			}
		}

		foreach (string root in EnumerateRoots(repoRoot, ScriptScanRoots))
		{
			foreach (string uidFile in Directory.EnumerateFiles(root, "*.uid", SearchOption.AllDirectories))
			{
				if (IsExcluded(uidFile))
				{
					continue;
				}

				ReadAndRecordUid(repoRoot, uidFile, uidByPath, issues);
			}
		}

		return uidByPath;
	}

	static void ReadAndRecordUid(string repoRoot, string uidFile, Dictionary<string, string> uidByPath, List<string> issues)
	{
		string contents = File.ReadAllText(uidFile).Trim();
		string rel = Relative(repoRoot, uidFile);

		if (string.IsNullOrEmpty(contents))
		{
			issues.Add($"{rel}: empty .uid file");
			return;
		}

		if (!UidValueRegex.IsMatch(contents))
		{
			issues.Add($"{rel}: malformed UID value '{contents}' (expected uid://[a-z0-9]+)");
			return;
		}

		string targetPath = uidFile.Substring(0, uidFile.Length - ".uid".Length);
		uidByPath[targetPath] = contents;
	}

	static void ValidateScriptSidecars(string repoRoot, Dictionary<string, string> uidByPath, List<string> issues, bool fix)
	{
		foreach (string root in EnumerateRoots(repoRoot, ScriptScanRoots))
		{
			foreach (string csFile in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
			{
				if (IsExcluded(csFile))
				{
					continue;
				}

				string sidecar = csFile + ".uid";
				if (File.Exists(sidecar))
				{
					continue;
				}

				string rel = Relative(repoRoot, csFile);
				if (fix)
				{
					string newUid = GenerateUid();
					File.WriteAllText(sidecar, newUid + Environment.NewLine);
					uidByPath[csFile] = newUid;
					Console.WriteLine($"  fixed: created {Relative(repoRoot, sidecar)} -> {newUid}");
				}
				else
				{
					issues.Add($"{rel}: missing .cs.uid sidecar (run with --fix to auto-create)");
				}
			}
		}
	}

	static void ValidateUidUniqueness(Dictionary<string, string> uidByPath, List<string> issues)
	{
		var byUid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var kv in uidByPath)
		{
			if (!byUid.TryGetValue(kv.Value, out var list))
			{
				list = new List<string>();
				byUid[kv.Value] = list;
			}

			list.Add(kv.Key);
		}

		foreach (var kv in byUid)
		{
			if (kv.Value.Count > 1)
			{
				issues.Add($"duplicate UID {kv.Key} on: {string.Join(", ", kv.Value)}");
			}
		}
	}

	static void ValidateSceneReferences(string repoRoot, Dictionary<string, string> uidByPath, List<string> issues)
	{
		var uidLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var kv in uidByPath)
		{
			uidLookup[kv.Value] = kv.Key;
		}

		foreach (string root in EnumerateRoots(repoRoot, SceneScanRoots))
		{
			foreach (string sceneFile in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
			{
				if (IsExcluded(sceneFile))
				{
					continue;
				}

				bool match = false;
				foreach (string ext in SceneExtensions)
				{
					if (sceneFile.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
					{
						match = true;
						break;
					}
				}

				if (!match)
				{
					continue;
				}

				ValidateSceneFile(repoRoot, sceneFile, uidByPath, uidLookup, issues);
			}
		}
	}

	static void ValidateSceneFile(
		string repoRoot,
		string sceneFile,
		Dictionary<string, string> uidByPath,
		Dictionary<string, string> uidLookup,
		List<string> issues)
	{
		string[] lines = File.ReadAllLines(sceneFile);
		string sceneRel = Relative(repoRoot, sceneFile);

		for (int i = 0; i < lines.Length; i++)
		{
			Match m = ExtResourceRegex.Match(lines[i]);
			if (!m.Success)
			{
				continue;
			}

			string attrs = m.Groups["attrs"].Value;
			string? path = null;
			string? uid = null;

			foreach (Match a in AttrRegex.Matches(attrs))
			{
				if (a.Groups["key"].Value == "path")
				{
					path = a.Groups["val"].Value;
				}
				else if (a.Groups["key"].Value == "uid")
				{
					uid = a.Groups["val"].Value;
				}
			}

			if (path == null)
			{
				continue;
			}

			if (!path.StartsWith("res://"))
			{
				continue;
			}

			string targetRel = path.Substring("res://".Length).Replace('/', Path.DirectorySeparatorChar);
			string targetAbs = Path.Combine(repoRoot, targetRel);

			if (!File.Exists(targetAbs))
			{
				issues.Add($"{sceneRel}:{i + 1}: ext_resource path does not exist: {path}");
				continue;
			}

			if (uid == null)
			{
				continue;
			}

			if (uidByPath.TryGetValue(targetAbs, out string? expected))
			{
				if (!string.Equals(expected, uid, StringComparison.OrdinalIgnoreCase))
				{
					issues.Add(
						$"{sceneRel}:{i + 1}: uid {uid} does not match {Relative(repoRoot, targetAbs)}.uid ({expected})");
				}
			}
			else
			{
				if (uidLookup.TryGetValue(uid, out string? owner))
				{
					issues.Add(
						$"{sceneRel}:{i + 1}: uid {uid} belongs to {Relative(repoRoot, owner)} but reference points to {path}");
				}
			}
		}
	}

	static IEnumerable<string> EnumerateRoots(string repoRoot, string[] subdirs)
	{
		foreach (string sub in subdirs)
		{
			string full = Path.Combine(repoRoot, sub);
			if (Directory.Exists(full))
			{
				yield return full;
			}
		}
	}

	static bool IsExcluded(string fullPath)
	{
		string normalized = fullPath.Replace('\\', '/');
		return normalized.Contains("/.godot/")
			|| normalized.Contains("/bin/")
			|| normalized.Contains("/obj/")
			|| normalized.Contains("/.vs/");
	}

	static string Relative(string repoRoot, string fullPath)
	{
		string rel = Path.GetRelativePath(repoRoot, fullPath);
		return rel.Replace('\\', '/');
	}

	static string GenerateUid()
	{
		var sb = new System.Text.StringBuilder("uid://", 6 + 13);
		for (int i = 0; i < 13; i++)
		{
			sb.Append(UidCharset[UidRng.Next(UidCharset.Length)]);
		}

		return sb.ToString();
	}
}
