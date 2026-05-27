using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

class Program
{
	static readonly string[] ScriptScanRoots = new[] { "scripts", "addons", "tools" };
	static readonly string[] SceneScanRoots = new[] { "scenes", "resources", "addons" };
	static readonly string[] ShaderScanRoots = new[] { "shaders" };
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
		Console.WriteLine($"Mode: {(fix ? "validate + auto-fix missing .cs.uid sidecars + reconcile uid mismatches by reference-majority" : "validate only")}");
		Console.WriteLine();

		var issues = new List<string>();

		var uidByPath = ScanUidSidecars(repoRoot, issues);
		ValidateScriptSidecars(repoRoot, uidByPath, issues, fix);
		ValidateUidUniqueness(uidByPath, issues);
		ValidateSceneReferences(repoRoot, uidByPath, issues, fix);

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

		foreach (string root in EnumerateRoots(repoRoot, ShaderScanRoots))
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

	readonly record struct SceneRef(string SceneFile, int LineIndex, string TargetAbs, string Uid);

	static void ValidateSceneReferences(string repoRoot, Dictionary<string, string> uidByPath, List<string> issues, bool fix)
	{
		var uidLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var kv in uidByPath)
		{
			uidLookup[kv.Value] = kv.Key;
		}

		// Phase A: collect every ext_resource reference to a sidecar-owned target,
		// plus immediate structural issues (missing paths, uid-points-elsewhere).
		var refs = new List<SceneRef>();
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

				CollectSceneRefs(repoRoot, sceneFile, uidByPath, uidLookup, refs, issues);
			}
		}

		// Phase B: group references by target and decide the genuine uid by
		// reference-MAJORITY — the value Godot actually wrote across the most
		// scenes/resources. The sidecar gets NO vote: a .cs.uid / .gdshader.uid
		// sidecar is the artifact most often corrupted by headless edits (a bad
		// agent fabricating a uid), so it can't be trusted as the source of
		// truth. We only auto-resolve a STRICT majority; any tie is reported and
		// left for a human, so --fix can never spread a wrong uid the way a
		// "sidecar always wins" rule does.
		var byTarget = new Dictionary<string, List<SceneRef>>(StringComparer.OrdinalIgnoreCase);
		foreach (SceneRef r in refs)
		{
			if (!byTarget.TryGetValue(r.TargetAbs, out var list))
			{
				list = new List<SceneRef>();
				byTarget[r.TargetAbs] = list;
			}

			list.Add(r);
		}

		// sceneFile -> (lineIndex -> (oldUid, newUid)) edits batched per file.
		var edits = new Dictionary<string, Dictionary<int, (string Old, string New)>>(StringComparer.OrdinalIgnoreCase);

		foreach (var kv in byTarget)
		{
			string targetAbs = kv.Key;
			List<SceneRef> targetRefs = kv.Value;
			uidByPath.TryGetValue(targetAbs, out string? sidecarUid);

			var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (SceneRef r in targetRefs)
			{
				votes.TryGetValue(r.Uid, out int c);
				votes[r.Uid] = c + 1;
			}

			// Fully consistent: all references agree and the sidecar matches.
			if (votes.Count == 1)
			{
				string only = FirstKey(votes);
				if (sidecarUid == null || string.Equals(sidecarUid, only, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
			}

			// Strict-majority winner among references.
			string genuine = "";
			int top = -1;
			bool tie = false;
			foreach (var v in votes)
			{
				if (v.Value > top)
				{
					top = v.Value;
					genuine = v.Key;
					tie = false;
				}
				else if (v.Value == top)
				{
					tie = true;
				}
			}

			string targetRel = Relative(repoRoot, targetAbs);
			if (tie)
			{
				string breakdown = VoteBreakdown(votes);
				string sidePart = sidecarUid != null ? $", sidecar {sidecarUid}" : string.Empty;
				issues.Add($"{targetRel}: ambiguous uid — reference vote tied [{breakdown}]{sidePart} (resolve manually, even with --fix)");
				continue;
			}

			// Reconcile the sidecar to the genuine uid.
			if (sidecarUid != null && !string.Equals(sidecarUid, genuine, StringComparison.OrdinalIgnoreCase))
			{
				if (fix)
				{
					File.WriteAllText(targetAbs + ".uid", genuine + Environment.NewLine);
					uidByPath[targetAbs] = genuine;
					Console.WriteLine($"  fixed: {targetRel}.uid {sidecarUid} -> {genuine} (matches {top} reference(s))");
				}
				else
				{
					issues.Add($"{targetRel}.uid: sidecar {sidecarUid} is an outlier; {top} reference(s) use {genuine}");
				}
			}

			// Reconcile minority references to the genuine uid.
			foreach (SceneRef r in targetRefs)
			{
				if (string.Equals(r.Uid, genuine, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (fix)
				{
					if (!edits.TryGetValue(r.SceneFile, out var lineMap))
					{
						lineMap = new Dictionary<int, (string, string)>();
						edits[r.SceneFile] = lineMap;
					}

					lineMap[r.LineIndex] = (r.Uid, genuine);
				}
				else
				{
					issues.Add($"{Relative(repoRoot, r.SceneFile)}:{r.LineIndex + 1}: uid {r.Uid} does not match genuine {genuine} for {targetRel}");
				}
			}
		}

		// Phase C: apply batched edits per scene file.
		foreach (var kv in edits)
		{
			ApplyUidEdits(repoRoot, kv.Key, kv.Value);
		}
	}

	static void CollectSceneRefs(
		string repoRoot,
		string sceneFile,
		Dictionary<string, string> uidByPath,
		Dictionary<string, string> uidLookup,
		List<SceneRef> refs,
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

			if (path == null || !path.StartsWith("res://"))
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

			if (uidByPath.ContainsKey(targetAbs))
			{
				refs.Add(new SceneRef(sceneFile, i, targetAbs, uid));
			}
			else if (uidLookup.TryGetValue(uid, out string? owner))
			{
				issues.Add($"{sceneRel}:{i + 1}: uid {uid} belongs to {Relative(repoRoot, owner)} but reference points to {path}");
			}
		}
	}

	static void ApplyUidEdits(string repoRoot, string sceneFile, Dictionary<int, (string Old, string New)> lineEdits)
	{
		string originalText = File.ReadAllText(sceneFile);
		string newline = originalText.Contains("\r\n") ? "\r\n" : "\n";
		bool trailingNewline = originalText.EndsWith(newline);
		string[] lines = originalText.Split(new[] { newline }, StringSplitOptions.None);
		if (trailingNewline && lines.Length > 0 && lines[lines.Length - 1].Length == 0)
		{
			Array.Resize(ref lines, lines.Length - 1);
		}

		string sceneRel = Relative(repoRoot, sceneFile);
		foreach (var kv in lineEdits)
		{
			int i = kv.Key;
			(string oldUid, string newUid) = kv.Value;
			lines[i] = lines[i].Replace($"uid=\"{oldUid}\"", $"uid=\"{newUid}\"");
			Console.WriteLine($"  fixed: {sceneRel}:{i + 1} uid {oldUid} -> {newUid}");
		}

		string output = string.Join(newline, lines) + (trailingNewline ? newline : string.Empty);
		File.WriteAllText(sceneFile, output);
	}

	static string FirstKey(Dictionary<string, int> votes)
	{
		foreach (var kv in votes)
		{
			return kv.Key;
		}

		return string.Empty;
	}

	static string VoteBreakdown(Dictionary<string, int> votes)
	{
		var parts = new List<string>();
		foreach (var kv in votes)
		{
			parts.Add($"{kv.Key}×{kv.Value}");
		}

		parts.Sort(StringComparer.Ordinal);
		return string.Join(", ", parts);
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
