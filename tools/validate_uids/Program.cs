using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

class Program
{
	static readonly string[] ScriptScanRoots = new[] { "scripts", "addons", "tools" };
	static readonly string[] SceneScanRoots = new[] { "scenes", "resources", "addons" };
	static readonly string[] ShaderScanRoots = new[] { "shaders" };
	static readonly string[] ImportScanRoots = new[] { "assets", "resources", "scenes", "addons", "shaders" };
	static readonly string[] SceneExtensions = new[] { ".tscn", ".tres" };

	static readonly Regex UidValueRegex = new Regex(@"^uid://[a-z0-9]+$", RegexOptions.Compiled);
	static readonly Regex ExtResourceRegex = new Regex(
		@"\[ext_resource\b(?<attrs>[^\]]*)\]",
		RegexOptions.Compiled);
	static readonly Regex ResourceHeaderRegex = new Regex(
		@"^\[(?:gd_resource|gd_scene)\b(?<attrs>[^\]]*)\]",
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
		bool mintHeaders = false;

		foreach (string a in args)
		{
			if (a == "--fix")
			{
				fix = true;
			}
			else if (a == "--mint-headers")
			{
				mintHeaders = true;
			}
		}

		Console.WriteLine($"Repo root: {repoRoot}");
		Console.WriteLine($"Mode: {(fix ? "validate + auto-fix missing .cs.uid sidecars + reconcile uid mismatches by reference-majority" : "validate only")}");
		Console.WriteLine();

		var issues = new List<string>();

		var uidByPath = ScanUidSidecars(repoRoot, issues);
		var importUidByPath = ScanImportUids(repoRoot, issues);
		var headerlessTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var headerUidByPath = ScanResourceHeaders(repoRoot, headerlessTargets, issues);
		MintHeaderUids(repoRoot, headerlessTargets, headerUidByPath, new[] { uidByPath, importUidByPath }, fix && mintHeaders);
		ValidateScriptSidecars(repoRoot, uidByPath, issues, fix);
		ValidateUidUniqueness(new[] { uidByPath, importUidByPath, headerUidByPath }, issues);
		ValidateSceneReferences(repoRoot, uidByPath, importUidByPath, headerUidByPath, headerlessTargets, issues, fix);

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

	// Imported assets (.png, .fbx, .wav) keep their uid in the sibling .import file,
	// NOT in a .uid sidecar. Godot regenerates .import from the source asset, so it is
	// authoritative for references — and must never be reconciled by writing a .uid
	// sidecar next to the asset (that file does not belong there and leaves the
	// mismatch unfixed, so the "fix" repeats on every run).
	static Dictionary<string, string> ScanImportUids(string repoRoot, List<string> issues)
	{
		var importUidByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (string root in EnumerateRoots(repoRoot, ImportScanRoots))
		{
			foreach (string importFile in Directory.EnumerateFiles(root, "*.import", SearchOption.AllDirectories))
			{
				if (IsExcluded(importFile))
				{
					continue;
				}

				string targetPath = importFile.Substring(0, importFile.Length - ".import".Length);
				if (!File.Exists(targetPath))
				{
					continue;
				}

				foreach (string line in File.ReadLines(importFile))
				{
					Match a = AttrRegex.Match(line);
					if (!line.StartsWith("uid=") || !a.Success)
					{
						continue;
					}

					string val = a.Groups["val"].Value;
					if (UidValueRegex.IsMatch(val))
					{
						importUidByPath[targetPath] = val;
					}
					else
					{
						issues.Add($"{Relative(repoRoot, importFile)}: malformed UID value '{val}'");
					}

					break;
				}
			}
		}

		return importUidByPath;
	}

	// A .tres/.tscn carries its own uid inline in the [gd_resource]/[gd_scene] header.
	// Unlike a sidecar this lives in the file Godot itself wrote, so it is the
	// AUTHORITY for references to that file rather than one vote among many.
	static Dictionary<string, string> ScanResourceHeaders(
		string repoRoot,
		HashSet<string> headerlessTargets,
		List<string> issues)
	{
		var headerUidByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var missingHeader = new List<string>();

		foreach (string root in EnumerateRoots(repoRoot, SceneScanRoots))
		{
			foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
			{
				if (IsExcluded(file) || !HasSceneExtension(file))
				{
					continue;
				}

				string? first = null;
				foreach (string line in File.ReadLines(file))
				{
					first = line;
					break;
				}

				if (first == null)
				{
					continue;
				}

				Match m = ResourceHeaderRegex.Match(first);
				if (!m.Success)
				{
					continue;
				}

				string? uid = null;
				foreach (Match a in AttrRegex.Matches(m.Groups["attrs"].Value))
				{
					if (a.Groups["key"].Value == "uid")
					{
						uid = a.Groups["val"].Value;
					}
				}

				string rel = Relative(repoRoot, file);
				if (uid == null)
				{
					missingHeader.Add(rel);
					headerlessTargets.Add(file);
					continue;
				}

				if (!UidValueRegex.IsMatch(uid))
				{
					issues.Add($"{rel}:1: malformed UID value '{uid}' in resource header");
					continue;
				}

				headerUidByPath[file] = uid;
			}
		}

		// Reported later by ValidateSceneReferences, which first adopts a unanimous
		// reference value into the header where one exists.
		return headerUidByPath;
	}

	// A .tres/.tscn with no header uid and no uid-bearing reference has no identity to
	// infer, so this mints one. OPT-IN (--mint-headers) and normally the wrong choice:
	// a uid minted here is absent from Godot's registry, so every load logs
	// "ext_resource, invalid UID ... using text path instead" until the GUI editor
	// scans and registers it. Letting the editor mint them instead is atomic and
	// warning-free. Measured on this repo: minting 145 headers added 257 warnings.
	static void MintHeaderUids(
		string repoRoot,
		HashSet<string> headerlessTargets,
		Dictionary<string, string> headerUidByPath,
		IEnumerable<Dictionary<string, string>> otherSources,
		bool mint)
	{
		if (!mint || headerlessTargets.Count == 0)
		{
			return;
		}

		var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var source in otherSources)
		{
			foreach (var kv in source)
			{
				taken.Add(kv.Value);
			}
		}

		foreach (var kv in headerUidByPath)
		{
			taken.Add(kv.Value);
		}

		var targets = new List<string>(headerlessTargets);
		targets.Sort(StringComparer.Ordinal);

		foreach (string target in targets)
		{
			string minted;
			do
			{
				minted = GenerateUid();
			}
			while (taken.Contains(minted));

			taken.Add(minted);
			InsertHeaderUid(target, minted);
			headerUidByPath[target] = minted;
			Console.WriteLine($"  fixed: {Relative(repoRoot, target)}:1 minted header uid {minted}");
		}

		headerlessTargets.Clear();
	}

	static bool HasSceneExtension(string path)
	{
		foreach (string ext in SceneExtensions)
		{
			if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
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

	static void ValidateUidUniqueness(
		IEnumerable<Dictionary<string, string>> sources,
		List<string> issues)
	{
		var byUid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var source in sources)
		{
			foreach (var kv in source)
			{
				if (!byUid.TryGetValue(kv.Value, out var list))
				{
					list = new List<string>();
					byUid[kv.Value] = list;
				}

				list.Add(kv.Key);
			}
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

	static void ValidateSceneReferences(
		string repoRoot,
		Dictionary<string, string> uidByPath,
		Dictionary<string, string> importUidByPath,
		Dictionary<string, string> headerUidByPath,
		HashSet<string> headerlessTargets,
		List<string> issues,
		bool fix)
	{
		var headerlessRefs = new List<SceneRef>();
		var uidLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var source in new[] { uidByPath, importUidByPath, headerUidByPath })
		{
			foreach (var kv in source)
			{
				uidLookup[kv.Value] = kv.Key;
			}
		}

		// sceneFile -> (lineIndex -> edit) batched per file. Populated by the
		// header-authority and missing-uid passes as well as majority reconciliation.
		var edits = new Dictionary<string, Dictionary<int, UidEdit>>(StringComparer.OrdinalIgnoreCase);

		// Phase A: collect every ext_resource reference to a sidecar-owned target,
		// plus immediate structural issues (missing paths, uid-points-elsewhere,
		// header-authority mismatches, absent uid= attributes).
		var refs = new List<SceneRef>();
		foreach (string root in EnumerateRoots(repoRoot, SceneScanRoots))
		{
			foreach (string sceneFile in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
			{
				if (IsExcluded(sceneFile) || !HasSceneExtension(sceneFile))
				{
					continue;
				}

				CollectSceneRefs(repoRoot, sceneFile, uidByPath, importUidByPath, headerUidByPath, uidLookup, headerlessTargets, headerlessRefs, refs, edits, issues, fix);
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
				// A tie means the references carry no majority signal, so the sidecar —
				// which is what Godot reads to register this file — breaks it. It only
				// loses to a STRICT majority, so this can't spread a bad sidecar.
				if (sidecarUid != null && votes.ContainsKey(sidecarUid))
				{
					genuine = sidecarUid;
					top = votes[sidecarUid];
				}
				else
				{
					string breakdown = VoteBreakdown(votes);
					string sidePart = sidecarUid != null ? $", sidecar {sidecarUid}" : string.Empty;
					issues.Add($"{targetRel}: ambiguous uid — reference vote tied [{breakdown}]{sidePart} (resolve manually, even with --fix)");
					continue;
				}
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
					QueueEdit(edits, r.SceneFile, r.LineIndex, new UidEdit(r.Uid, genuine));
				}
				else
				{
					issues.Add($"{Relative(repoRoot, r.SceneFile)}:{r.LineIndex + 1}: uid {r.Uid} does not match genuine {genuine} for {targetRel}");
				}
			}
		}

		// Phase C: adopt a unanimous reference uid into a headerless resource's own
		// header. Anything left over has no references to learn from (loaded by path
		// only), so only Godot can mint it.
		var byHeaderless = new Dictionary<string, List<SceneRef>>(StringComparer.OrdinalIgnoreCase);
		foreach (SceneRef r in headerlessRefs)
		{
			if (!byHeaderless.TryGetValue(r.TargetAbs, out var list))
			{
				list = new List<SceneRef>();
				byHeaderless[r.TargetAbs] = list;
			}

			list.Add(r);
		}

		foreach (var kv in byHeaderless)
		{
			var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (SceneRef r in kv.Value)
			{
				distinct.Add(r.Uid);
			}

			string targetRel = Relative(repoRoot, kv.Key);
			if (distinct.Count != 1)
			{
				issues.Add($"{targetRel}:1: no uid= in header and references disagree [{string.Join(", ", distinct)}] (resolve manually)");
				continue;
			}

			string adopt = FirstOf(distinct);
			if (fix)
			{
				InsertHeaderUid(kv.Key, adopt);
				headerlessTargets.Remove(kv.Key);
				Console.WriteLine($"  fixed: {targetRel}:1 added header uid {adopt} ({kv.Value.Count} reference(s) agree)");
			}
			else
			{
				issues.Add($"{targetRel}:1: no uid= in header; {kv.Value.Count} reference(s) agree on {adopt}");
			}
		}

		if (headerlessTargets.Count > 0)
		{
			var rest = new List<string>();
			foreach (string p in headerlessTargets)
			{
				rest.Add(Relative(repoRoot, p));
			}

			rest.Sort(StringComparer.Ordinal);
			int shown = Math.Min(rest.Count, 10);
			string more = rest.Count > shown ? $", +{rest.Count - shown} more" : string.Empty;
			issues.Add($"{rest.Count} resource(s) have no uid= in their header and no references to infer it from — only Godot can mint these: {string.Join(", ", rest.GetRange(0, shown))}{more}");
		}

		// Phase D: apply batched edits per scene file.
		foreach (var kv in edits)
		{
			ApplyUidEdits(repoRoot, kv.Key, kv.Value);
		}
	}

	static string FirstOf(HashSet<string> set)
	{
		foreach (string s in set)
		{
			return s;
		}

		return string.Empty;
	}

	// Godot writes uid= last in the header, after format=.
	static void InsertHeaderUid(string file, string uid)
	{
		string text = File.ReadAllText(file);
		string newline = text.Contains("\r\n") ? "\r\n" : "\n";
		int eol = text.IndexOf(newline, StringComparison.Ordinal);
		string first = eol < 0 ? text : text.Substring(0, eol);

		int close = first.LastIndexOf(']');
		if (close < 0)
		{
			return;
		}

		string patched = first.Substring(0, close) + $" uid=\"{uid}\"" + first.Substring(close);
		File.WriteAllText(file, patched + (eol < 0 ? string.Empty : text.Substring(eol)));
	}

	static void CollectSceneRefs(
		string repoRoot,
		string sceneFile,
		Dictionary<string, string> uidByPath,
		Dictionary<string, string> importUidByPath,
		Dictionary<string, string> headerUidByPath,
		Dictionary<string, string> uidLookup,
		HashSet<string> headerlessTargets,
		List<SceneRef> headerlessRefs,
		List<SceneRef> refs,
		Dictionary<string, Dictionary<int, UidEdit>> edits,
		List<string> issues,
		bool fix)
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

			// A .tres/.tscn target's own header, and an imported asset's .import file,
			// are authoritative for the target's uid — compare directly rather than
			// putting them to a vote.
			headerUidByPath.TryGetValue(targetAbs, out string? headerUid);
			importUidByPath.TryGetValue(targetAbs, out string? importUid);

			if (uid == null)
			{
				// Godot backfills the attribute on its next save of this file. Writing
				// it now keeps that save from showing up as an unrelated diff.
				string? authority = headerUid ?? importUid;
				if (authority == null)
				{
					uidByPath.TryGetValue(targetAbs, out authority);
				}

				if (authority == null)
				{
					continue;
				}

				if (fix)
				{
					QueueEdit(edits, sceneFile, i, new UidEdit(null, authority));
				}
				else
				{
					issues.Add($"{sceneRel}:{i + 1}: ext_resource has no uid= for {path} (expected {authority}; Godot will add it on next save)");
				}

				continue;
			}

			// .import is machine-generated from the source asset on every reimport, so
			// unlike a hand-authorable header it can be trusted to correct references.
			if (importUid != null)
			{
				if (!string.Equals(uid, importUid, StringComparison.OrdinalIgnoreCase))
				{
					if (fix)
					{
						QueueEdit(edits, sceneFile, i, new UidEdit(uid, importUid));
					}
					else
					{
						issues.Add($"{sceneRel}:{i + 1}: uid {uid} does not match {Relative(repoRoot, targetAbs)}.import uid {importUid}");
					}
				}

				continue;
			}

			// Never auto-fixed: a header uid can itself be fabricated (several in this
			// repo spell out their filename), so neither side of the disagreement is
			// reliably genuine. Only the editor knows which uid it has registered —
			// open the project, let it re-save, and commit that.
			if (headerUid != null)
			{
				if (!string.Equals(uid, headerUid, StringComparison.OrdinalIgnoreCase))
				{
					// If the stale uid is registered to some OTHER file, Godot resolves
					// the reference to that file instead of falling back to path= — the
					// silent wrong-asset load. Never rewrite blind in that case.
					if (uidLookup.TryGetValue(uid, out string? other)
						&& !string.Equals(other, targetAbs, StringComparison.OrdinalIgnoreCase))
					{
						issues.Add($"{sceneRel}:{i + 1}: WRONG ASSET — uid {uid} is registered to {Relative(repoRoot, other)}, but path= says {path} (header uid {headerUid})");
					}
					else if (fix)
					{
						// The header is what Godot reads to build its uid registry, so it
						// DEFINES this file's identity — even a hand-typed one. A
						// disagreeing reference is therefore always the stale side.
						QueueEdit(edits, sceneFile, i, new UidEdit(uid, headerUid));
					}
					else
					{
						issues.Add($"{sceneRel}:{i + 1}: uid {uid} does not match {Relative(repoRoot, targetAbs)} header uid {headerUid}");
					}
				}

				continue;
			}

			// Target is a .tres/.tscn with no uid= in its header. References already
			// carry the id Godot assigned it at scan time, so a unanimous reference
			// value can be adopted into the header.
			if (headerlessTargets.Contains(targetAbs))
			{
				headerlessRefs.Add(new SceneRef(sceneFile, i, targetAbs, uid));
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

	// Old == null means the line carries no uid= attribute and one must be inserted.
	readonly record struct UidEdit(string? Old, string New);

	static void QueueEdit(
		Dictionary<string, Dictionary<int, UidEdit>> edits,
		string sceneFile,
		int lineIndex,
		UidEdit edit)
	{
		if (!edits.TryGetValue(sceneFile, out var lineMap))
		{
			lineMap = new Dictionary<int, UidEdit>();
			edits[sceneFile] = lineMap;
		}

		lineMap[lineIndex] = edit;
	}

	static void ApplyUidEdits(string repoRoot, string sceneFile, Dictionary<int, UidEdit> lineEdits)
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
			UidEdit edit = kv.Value;
			if (edit.Old == null)
			{
				lines[i] = InsertUidAttribute(lines[i], edit.New);
				Console.WriteLine($"  fixed: {sceneRel}:{i + 1} added uid {edit.New}");
			}
			else
			{
				lines[i] = lines[i].Replace($"uid=\"{edit.Old}\"", $"uid=\"{edit.New}\"");
				Console.WriteLine($"  fixed: {sceneRel}:{i + 1} uid {edit.Old} -> {edit.New}");
			}
		}

		string output = string.Join(newline, lines) + (trailingNewline ? newline : string.Empty);
		File.WriteAllText(sceneFile, output);
	}

	// Godot writes uid= immediately before path=; matching that keeps the line
	// byte-identical to what the editor would produce on its next save.
	static string InsertUidAttribute(string line, string uid)
	{
		int at = line.IndexOf(" path=\"", StringComparison.Ordinal);
		if (at < 0)
		{
			return line;
		}

		return line.Substring(0, at) + $" uid=\"{uid}\"" + line.Substring(at);
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
