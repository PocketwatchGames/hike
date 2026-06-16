using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

// Headless validator for the scripting-variable bank. Checks that every
// authored reference (ScriptVarCondition / ScriptVarTransition /
// SetScriptVarAction) names a variable that actually exists in
// resources/data/script_variables/, that ordering comparisons are only used
// on Int variables, and that every declared variable is registered in a
// ScriptVariableRegistry (so it gets seeded at runtime). Emits findings in
// the MSBuild "file(line): warning CODE: text" format so the build surfaces
// them; returns non-zero when any are found (the build target runs it
// non-blocking via ContinueOnError).
//
// Mirrors tools/validate_uids: run `dotnet run --project tools/validate_script_vars`.
class Program
{
    const string VarDeclRoot = "resources/data/script_variables";
    static readonly string[] RefScanRoots = { "resources", "scenes" };
    static readonly string[] SceneExtensions = { ".tres", ".tscn" };

    // Reference script file names whose blocks carry a `variable` field.
    static readonly string[] RefScriptFiles =
    {
        "ScriptVarCondition.cs",
        "ScriptVarTransition.cs",
        "SetScriptVarAction.cs",
    };

    const int TypeBool = 0;

    // EScriptVarCompareOp ordinals that only make sense on an Int variable:
    // GreaterThan(4), GreaterOrEqual(5), LessThan(6), LessOrEqual(7).
    static readonly HashSet<int> OrderingOps = new() { 4, 5, 6, 7 };

    static readonly Regex ExtResourceRegex = new(@"\[ext_resource\b(?<attrs>[^\]]*)\]", RegexOptions.Compiled);
    static readonly Regex AttrRegex = new(@"(?<key>\w+)\s*=\s*""(?<val>[^""]*)""", RegexOptions.Compiled);
    static readonly Regex IdRegex = new(@"^\s*Id\s*=\s*&?""(?<id>[^""]*)""", RegexOptions.Compiled);
    static readonly Regex TypeRegex = new(@"^\s*Type\s*=\s*(?<n>\d+)", RegexOptions.Compiled);
    static readonly Regex VariableRegex = new(@"^\s*variable\s*=\s*&?""(?<id>[^""]*)""", RegexOptions.Compiled);
    static readonly Regex OpRegex = new(@"^\s*op\s*=\s*(?<n>\d+)", RegexOptions.Compiled);
    static readonly Regex ScriptRefRegex = new(@"^\s*script\s*=\s*ExtResource\(\s*""(?<id>[^""]*)""\s*\)", RegexOptions.Compiled);

    sealed class VarDecl
    {
        public int Type;
        public string File = "";
        public bool Registered;
    }

    static int Main(string[] args)
    {
        string repoRoot = ResolveRepoRoot(args);
        var issues = new List<string>();

        Dictionary<string, VarDecl> declared = ScanDeclarations(repoRoot, issues);
        MarkRegistered(repoRoot, declared);
        ScanReferences(repoRoot, declared, issues);

        // Declared but never registered → won't be seeded into the bank.
        foreach (KeyValuePair<string, VarDecl> kv in declared)
        {
            if (!kv.Value.Registered)
            {
                issues.Add($"{kv.Value.File}(1): warning SCRIPTVAR3: variable '{kv.Key}' is declared but not listed in any ScriptVariableRegistry — it won't be seeded at runtime.");
            }
        }

        if (issues.Count == 0)
        {
            Console.WriteLine($"validate_script_vars: OK ({declared.Count} variable(s) declared, references consistent).");
            return 0;
        }
        foreach (string issue in issues)
        {
            Console.WriteLine(issue);
        }
        return 1;
    }

    static Dictionary<string, VarDecl> ScanDeclarations(string repoRoot, List<string> issues)
    {
        var declared = new Dictionary<string, VarDecl>(StringComparer.Ordinal);
        string root = Path.Combine(repoRoot, VarDeclRoot);
        if (!Directory.Exists(root))
        {
            return declared;
        }
        foreach (string file in Directory.EnumerateFiles(root, "*.tres", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            if (!text.Contains("script_class=\"ScriptVariableData\""))
            {
                continue;
            }
            string rel = Relative(repoRoot, file);
            string? id = null;
            int type = TypeBool;
            foreach (string line in text.Split('\n'))
            {
                Match im = IdRegex.Match(line);
                if (im.Success)
                {
                    id = im.Groups["id"].Value;
                }
                Match tm = TypeRegex.Match(line);
                if (tm.Success)
                {
                    type = int.Parse(tm.Groups["n"].Value);
                }
            }
            if (string.IsNullOrEmpty(id))
            {
                issues.Add($"{rel}(1): warning SCRIPTVAR4: ScriptVariableData has an empty Id.");
                continue;
            }
            if (declared.TryGetValue(id, out VarDecl? existing))
            {
                issues.Add($"{rel}(1): warning SCRIPTVAR5: duplicate variable Id '{id}' (also in {existing.File}).");
                continue;
            }
            declared[id] = new VarDecl { Type = type, File = rel };
        }
        return declared;
    }

    // Flags each declared variable referenced by some ScriptVariableRegistry
    // .tres (matched by the variable's source path appearing as an ext_resource).
    static void MarkRegistered(string repoRoot, Dictionary<string, VarDecl> declared)
    {
        var fileToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, VarDecl> kv in declared)
        {
            fileToId[kv.Value.File.Replace('\\', '/')] = kv.Key;
        }
        foreach (string root in EnumerateRoots(repoRoot, RefScanRoots))
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.tres", SearchOption.AllDirectories))
            {
                if (IsExcluded(file))
                {
                    continue;
                }
                string text = File.ReadAllText(file);
                if (!text.Contains("script_class=\"ScriptVariableRegistry\""))
                {
                    continue;
                }
                foreach (Match m in ExtResourceRegex.Matches(text))
                {
                    string? path = null;
                    foreach (Match a in AttrRegex.Matches(m.Groups["attrs"].Value))
                    {
                        if (a.Groups["key"].Value == "path")
                        {
                            path = a.Groups["val"].Value;
                        }
                    }
                    if (path == null || !path.StartsWith("res://"))
                    {
                        continue;
                    }
                    string targetRel = path.Substring("res://".Length);
                    if (fileToId.TryGetValue(targetRel, out string? id))
                    {
                        declared[id].Registered = true;
                    }
                }
            }
        }
    }

    static void ScanReferences(string repoRoot, Dictionary<string, VarDecl> declared, List<string> issues)
    {
        foreach (string root in EnumerateRoots(repoRoot, RefScanRoots))
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (IsExcluded(file) || !HasSceneExtension(file))
                {
                    continue;
                }
                ScanFileReferences(repoRoot, file, declared, issues);
            }
        }
    }

    static void ScanFileReferences(string repoRoot, string file, Dictionary<string, VarDecl> declared, List<string> issues)
    {
        string[] lines = File.ReadAllLines(file);
        string rel = Relative(repoRoot, file);

        // ext_resource id -> true when it points at one of our reference scripts.
        var refScriptIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            Match m = ExtResourceRegex.Match(line);
            if (!m.Success)
            {
                continue;
            }
            string? id = null;
            string? path = null;
            foreach (Match a in AttrRegex.Matches(m.Groups["attrs"].Value))
            {
                if (a.Groups["key"].Value == "id") { id = a.Groups["val"].Value; }
                else if (a.Groups["key"].Value == "path") { path = a.Groups["val"].Value; }
            }
            if (id == null || path == null)
            {
                continue;
            }
            foreach (string refFile in RefScriptFiles)
            {
                if (path.EndsWith(refFile, StringComparison.Ordinal))
                {
                    refScriptIds.Add(id);
                    break;
                }
            }
        }
        if (refScriptIds.Count == 0)
        {
            return;
        }

        // Walk blocks; a block is delimited by lines starting with '['. Capture
        // its script id + variable/op, validate when the block closes.
        string? blockScriptId = null;
        string? blockVar = null;
        int blockVarLine = 0;
        int blockOp = 0;

        void Flush()
        {
            if (blockScriptId != null && refScriptIds.Contains(blockScriptId) && blockVar != null)
            {
                ValidateRef(rel, blockVarLine, blockVar, blockOp, declared, issues);
            }
            blockScriptId = null;
            blockVar = null;
            blockVarLine = 0;
            blockOp = 0;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("["))
            {
                Flush();
                continue;
            }
            Match sm = ScriptRefRegex.Match(line);
            if (sm.Success) { blockScriptId = sm.Groups["id"].Value; }
            Match vm = VariableRegex.Match(line);
            if (vm.Success) { blockVar = vm.Groups["id"].Value; blockVarLine = i + 1; }
            Match om = OpRegex.Match(line);
            if (om.Success) { blockOp = int.Parse(om.Groups["n"].Value); }
        }
        Flush();
    }

    static void ValidateRef(string rel, int line, string variable, int op, Dictionary<string, VarDecl> declared, List<string> issues)
    {
        if (string.IsNullOrEmpty(variable))
        {
            issues.Add($"{rel}({line}): warning SCRIPTVAR0: ScriptVar reference has an empty variable name.");
            return;
        }
        if (!declared.TryGetValue(variable, out VarDecl? decl))
        {
            issues.Add($"{rel}({line}): warning SCRIPTVAR1: references undeclared scripting variable '{variable}' (add a ScriptVariableData under {VarDeclRoot}/).");
            return;
        }
        if (decl.Type == TypeBool && OrderingOps.Contains(op))
        {
            issues.Add($"{rel}({line}): warning SCRIPTVAR2: ordering comparison used on Bool variable '{variable}' — only Equal/NotEqual/IsTrue/IsFalse apply to a flag.");
        }
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

    static bool HasSceneExtension(string file)
    {
        foreach (string ext in SceneExtensions)
        {
            if (file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
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
        return Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
    }
}
