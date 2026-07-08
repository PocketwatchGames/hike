// Per-instance discovery tier for a map marker. Higher value = more known, so
// union-on-read and bank-merge both take the MAX across the two Knowledge stores.
public enum EMapMarkerLevel
{
    // Not discovered. The default; a record is never stored at this level (a
    // marker with no record is Unknown by absence).
    Unknown = 0,
    // Existence known from map reveal — the maps draw a shared "?" icon. We know
    // something is here but not what.
    Sensed,
    // Type known — the maps draw the marker's own icon and show its name on hover.
    Identified,
    // Full detail known (e.g. a forge's offerings). Reserved: the state model
    // carries it but nothing promotes here yet (later pass).
    Detailed,
}
