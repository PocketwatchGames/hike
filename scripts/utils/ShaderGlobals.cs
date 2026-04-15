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

    public static void Register(string name, RenderingServer.GlobalShaderParameterType type, Variant defaultValue)
    {
        StringName key = name;
        if (_registered.Contains(key))
        {
            return;
        }
        RenderingServer.GlobalShaderParameterSet(key, defaultValue);
        _registered.Add(key);
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
