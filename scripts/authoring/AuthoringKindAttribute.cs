using System;

// What a Resource subclass is called where an authoring tool offers it as one
// KIND of its base — the painter's "+ add" menu on a polymorphic list
// (EntityInspector.Kinds). A subclass without one is named after its
// class.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AuthoringKindAttribute : Attribute
{
    public readonly string Label;

    public AuthoringKindAttribute(string label)
    {
        Label = label;
    }
}
