using System.Collections.Generic;
using Godot;

// Manages `global uniform` shader parameters from C#.
//
// Two cases, two methods:
//
// `Register(name, type, default)` — for globals that ARE declared in
// `project.godot`'s [shader_globals] section. The engine creates the
// variable at startup; this call sets the C# default value before the
// first material that uses it compiles. Use for scalar/vector globals
// with a sensible static default that you also want visible in the
// editor's Project Settings UI.
//
// `RegisterRuntime(name, type, value)` — for globals that are NOT in
// `project.godot`. Creates the global in `RenderingServer` directly via
// `GlobalShaderParameterAdd`. Use this for sampler globals whose value
// is a runtime-constructed texture (which can't be expressed as a
// `res://` path in `project.godot`), or any global whose only meaningful
// value is computed at runtime.
//
// **Why both:** the runtime `RenderingServer.GlobalShaderParameterGet`
// and `GetList` are editor-only, so we cannot detect at runtime whether
// a name is already declared. The caller knows; pick the right method.
//
// **Standalone launch reliability:** a standalone launch (e.g. via
// VS Code → Godot.exe) compiles shaders very early. Both methods must
// run before the first material that uses the global compiles, so make
// the call from `_Ready` of whatever owns the per-frame `Set` calls.
public static class ShaderGlobals
{
    private static readonly HashSet<StringName> _registered = new();

    // Seeds (or re-seeds) the value. Deliberately NOT once-only: menu → game →
    // menu → game re-creates the per-session textures behind the sampler
    // globals, and skipping the second seed would leave every global pointing
    // at the freed first-session texture for the rest of the process.
    public static void Register(string name, RenderingServer.GlobalShaderParameterType type, Variant defaultValue)
    {
        RenderingServer.GlobalShaderParameterSet(name, defaultValue);
    }

    // Restores a global to the default declared in project.godot's
    // [shader_globals] (for samplers, the placeholder texture).
    //
    // A sampler global bound to a per-session texture — the windowed volume
    // maps, the projector SubViewports — MUST be reset before that texture is
    // freed. The renderer rebuilds the global uniform set of every material
    // referencing it the instant the RID dies, and a dangling RID fails
    // `uniform_set_create` once per material ("Texture (binding: N) is not a
    // valid texture"), leaving each of those materials without a uniform set.
    public static void ResetToProjectDefault(string name)
    {
        Variant declared = ProjectSettings.GetSetting($"shader_globals/{name}");
        if (declared.VariantType != Variant.Type.Dictionary)
        {
            GD.PushWarning($"ShaderGlobals: '{name}' has no [shader_globals] declaration to reset to.");
            return;
        }
        if (!declared.AsGodotDictionary().TryGetValue("value", out Variant value))
        {
            return;
        }
        // Sampler defaults are stored as a res:// path; every other type
        // stores the value itself.
        if (value.VariantType == Variant.Type.String)
        {
            value = ResourceLoader.Load(value.AsString());
        }
        RenderingServer.GlobalShaderParameterSet(name, value);
    }

    public static void RegisterRuntime(string name, RenderingServer.GlobalShaderParameterType type, Variant value)
    {
        StringName key = name;
        if (_registered.Contains(key))
        {
            return;
        }
        RenderingServer.GlobalShaderParameterAdd(key, type, value);
        _registered.Add(key);
    }
}
