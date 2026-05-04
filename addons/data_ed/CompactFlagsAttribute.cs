using System;

// Opt-in marker for the data_ed FlagsPropertyEditor. Place on an exported
// [Flags] enum property to swap Godot's default 32-checkbox grid for the
// compact MenuButton dropdown. The attribute is a plain C# marker so it
// compiles into game builds harmlessly — only the editor-side inspector
// plugin reads it.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class CompactFlagsAttribute : Attribute
{
}
