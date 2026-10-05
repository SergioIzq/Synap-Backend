using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.UnitTests.Domain;

/// <summary>
/// note-status tasks 1.2-1.4 - the status on the aggregate: it is optional, any status may follow
/// any other, and it is kept apart from the note's text.
/// </summary>
public class NoteStatusTests
{
    private static Note NewNote(NoteStatus? status = null)
        => Note.Create(UserId.CreateFromDatabase(Guid.NewGuid()), NoteType.Text, "Migrar auth", "contenido", status);

    /// <summary>specs/knowledge-vault "A note has no status by default".</summary>
    [Fact]
    public void A_new_note_has_no_status()
    {
        var note = NewNote();

        Assert.Null(note.Status);
        Assert.Null(note.StatusChangedAt);
    }

    /// <summary>specs/knowledge-vault "Mark a note with a status".</summary>
    [Fact]
    public void Setting_a_status_records_the_moment()
    {
        var note = NewNote();

        note.SetStatus(NoteStatus.Pending);

        Assert.Equal(NoteStatus.Pending, note.Status);
        Assert.NotNull(note.StatusChangedAt);
    }

    /// <summary>specs/knowledge-vault "Only one status at a time".</summary>
    [Fact]
    public void A_new_status_replaces_the_old_one()
    {
        var note = NewNote(NoteStatus.Pending);

        note.SetStatus(NoteStatus.InProgress);

        Assert.Equal(NoteStatus.InProgress, note.Status);
    }

    /// <summary>
    /// specs/knowledge-vault "Any status may follow any other" - design.md Decision 3: there is no
    /// state machine, so reopening something completed is ordinary and is not rejected.
    /// </summary>
    [Fact]
    public void Completed_can_go_back_to_in_progress()
    {
        var note = NewNote(NoteStatus.Completed);

        note.SetStatus(NoteStatus.InProgress);

        Assert.Equal(NoteStatus.InProgress, note.Status);
        Assert.NotNull(note.StatusChangedAt);
    }

    [Theory]
    [InlineData(NoteStatus.Pending)]
    [InlineData(NoteStatus.InProgress)]
    [InlineData(NoteStatus.Paused)]
    [InlineData(NoteStatus.Completed)]
    public void Any_status_can_follow_any_other(NoteStatus next)
    {
        var note = NewNote(NoteStatus.Paused);

        note.SetStatus(next);

        Assert.Equal(next, note.Status);
    }

    /// <summary>specs/knowledge-vault "Clearing a status" - it becomes a note that never had one.</summary>
    [Fact]
    public void Clearing_a_status_leaves_the_note_as_if_it_never_had_one()
    {
        var note = NewNote(NoteStatus.InProgress);

        note.SetStatus(null);

        Assert.Null(note.Status);
        Assert.Null(note.StatusChangedAt);
    }

    /// <summary>specs/knowledge-vault "Create a note with a status" (task 1.3).</summary>
    [Fact]
    public void A_note_created_with_a_status_carries_it_from_its_creation()
    {
        var note = NewNote(NoteStatus.Pending);

        Assert.Equal(NoteStatus.Pending, note.Status);
        Assert.Equal(note.FechaCreacion, note.StatusChangedAt);
    }

    /// <summary>specs/knowledge-vault "Create a note without a status" (task 1.3).</summary>
    [Fact]
    public void A_note_created_without_a_status_has_no_status_moment()
    {
        var note = NewNote();

        Assert.Null(note.Status);
        Assert.Null(note.StatusChangedAt);
    }

    /// <summary>
    /// specs/knowledge-vault "Editing content leaves the status alone" (task 1.4) - the half of
    /// design.md Decision 5 that lives on the aggregate.
    /// </summary>
    [Fact]
    public void Editing_the_content_leaves_the_status_untouched()
    {
        var note = NewNote(NoteStatus.InProgress);
        var markedAt = note.StatusChangedAt;

        note.UpdateContent("Otro título", "otro contenido");

        Assert.Equal(NoteStatus.InProgress, note.Status);
        Assert.Equal(markedAt, note.StatusChangedAt);
    }

    /// <summary>
    /// The other half: marking a note is not editing it, so UpdatedAt must not move - it is the
    /// signal that says how long a note has sat untouched (design.md Decision 5).
    /// </summary>
    [Fact]
    public void Setting_a_status_leaves_the_notes_text_and_updated_at_untouched()
    {
        var note = NewNote();
        var updatedAt = note.UpdatedAt;

        note.SetStatus(NoteStatus.Completed);

        Assert.Equal("Migrar auth", note.Title);
        Assert.Equal("contenido", note.Content);
        Assert.Equal(updatedAt, note.UpdatedAt);
    }
}
