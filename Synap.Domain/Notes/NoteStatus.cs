namespace Synap.Domain;

/// <summary>
/// What a note is, as work. Serialized as "pending" / "inProgress" / "paused" / "completed"
/// through <see cref="NoteStatusJsonConverter"/>, for the same reason <see cref="NoteType"/> is:
/// the kernel's result handler serializes enums PascalCase, and a property-level converter wins.
///
/// The absence of a status is not a member here - it is a null <see cref="Note.Status"/>, and it
/// means the note is material rather than work (note-status design.md Decision 1). A member named
/// None would make "not work" a kind of work and would need excluding from every query by hand.
/// </summary>
public enum NoteStatus
{
    Pending,
    InProgress,
    Paused,
    Completed,
}
