using Godot;

// Something whose spawn entry an authoring panel edits: a painter placement, or
// an entity a stamped scene brings with it. The panel reads `Entry` and writes
// through `EditableEntry`, which forks on the first edit so a shared palette
// file is never written to.
public interface IEntryEditTarget
{
    SpawnEntryData Entry { get; }

    SpawnEntryData EditableEntry();

    string DisplayName();
}
