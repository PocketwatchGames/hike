using Godot;

// What a placed entity can raise to its placement's hooks. Which of these an
// entity actually raises is its entry's to say (SpawnEntryData.HookEvents).
// APPEND new members only — written into .tres and the entity wire format.
public enum EEntityEvent
{
    // Swung open, by the player or a world script.
    Open,
    Close,
    // A key removed its lock. Raised once, just before the Open it causes.
    Unlock,
}

// One per-placement reaction: when `hookEvent` happens, run `commands` — the
// same typed commands a conversation sheet's action cell takes
// (`var:antica_bridge_opened=true; start_quest:find_her`), minus the ones that
// act on a speaker. Authored in the painter on the placement's fork, or on a
// stamped scene's override of one of its entities.
[GlobalClass]
public partial class EntityHook : Resource, IValidatedRecord
{
    [Export] public EEntityEvent hookEvent = EEntityEvent.Open;
    // `;`-separated ActionCommand tokens.
    [Export] public string commands = "";
    // Run only the first time the event is raised. The fired flag is saved
    // with the entity.
    [Export] public bool once;

    public string ValidateField(StringName field, string value, string world)
    {
        return field == PropertyName.commands ? EntityHookCommands.FirstError(value, world) : null;
    }
}

// A record whose text fields can be checked as they are typed; the painter's
// inspector shows the message under the field.
public interface IValidatedRecord
{
    // Null when `value` is fine for `field`.
    string ValidateField(StringName field, string value, string world);
}
