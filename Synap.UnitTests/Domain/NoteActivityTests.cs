using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.UnitTests.Domain;

/// <summary>
/// smart-resurfacing 1.1 - apertura de una nota: solo mueve LastViewedAt.
/// </summary>
public class NoteActivityTests
{
    private static Note NewNote(NoteStatus? status = null)
        => Note.Create(UserId.CreateFromDatabase(Guid.NewGuid()), NoteType.Text, "Helm", "contenido", status);

    /// <summary>specs/redescubrimiento «Abrir no es editar».</summary>
    [Fact]
    public void MarkViewed_moves_only_LastViewedAt()
    {
        var note = NewNote();
        var updatedAtBefore = note.UpdatedAt;
        var statusBefore = note.Status;
        var statusChangedAtBefore = note.StatusChangedAt;

        note.MarkViewed();

        Assert.NotNull(note.LastViewedAt);
        Assert.Equal(updatedAtBefore, note.UpdatedAt);
        Assert.Equal(statusBefore, note.Status);
        Assert.Equal(statusChangedAtBefore, note.StatusChangedAt);
    }

    [Fact]
    public void New_note_has_no_LastViewedAt_nor_LastResurfacedAt()
    {
        var note = NewNote();

        Assert.Null(note.LastViewedAt);
        Assert.Null(note.LastResurfacedAt);
    }

    [Fact]
    public void MarkResurfaced_moves_only_LastResurfacedAt()
    {
        var note = NewNote();
        var updatedAtBefore = note.UpdatedAt;

        note.MarkResurfaced();

        Assert.NotNull(note.LastResurfacedAt);
        Assert.Equal(updatedAtBefore, note.UpdatedAt);
        Assert.Null(note.LastViewedAt);
    }
}
