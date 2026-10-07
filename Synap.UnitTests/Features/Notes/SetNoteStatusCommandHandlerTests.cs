using Synap.Application.Features.Notes.Commands.SetStatus;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;
using Synap.UnitTests.Features.Settings;

namespace Synap.UnitTests.Features.Notes;

/// <summary>
/// note-status task 4.1 - specs/knowledge-vault "Note status" and "Changing a status is not
/// editing the note", with in-memory fakes.
/// </summary>
public class SetNoteStatusCommandHandlerTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private readonly FakeNoteRepository _notes = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private SetNoteStatusCommandHandler Handler() => new(_notes, _unitOfWork, new FakeUserContext(Me));

    private Note Seed(Guid owner = default, NoteStatus? status = null)
        => _notes.Add(Note.Create(
            UserId.CreateFromDatabase(owner == default ? Me : owner), NoteType.Text, "Migrar auth", "contenido", status));

    [Fact]
    public async Task Sets_the_status_and_saves()
    {
        var note = Seed();

        var result = await Handler().Handle(new SetNoteStatusCommand(note.Id.Value, "inProgress"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(NoteStatus.InProgress, note.Status);
        Assert.NotNull(note.StatusChangedAt);
        Assert.Equal(1, _unitOfWork.SaveCalls);
    }

    [Theory]
    [InlineData("pending", NoteStatus.Pending)]
    [InlineData("inProgress", NoteStatus.InProgress)]
    [InlineData("PAUSED", NoteStatus.Paused)]
    [InlineData("completed", NoteStatus.Completed)]
    public async Task Accepts_every_status_whatever_its_casing(string wire, NoteStatus expected)
    {
        var note = Seed();

        await Handler().Handle(new SetNoteStatusCommand(note.Id.Value, wire), default);

        Assert.Equal(expected, note.Status);
    }

    /// <summary>specs/knowledge-vault "Clearing a status".</summary>
    [Fact]
    public async Task A_null_status_clears_it()
    {
        var note = Seed(status: NoteStatus.Paused);

        var result = await Handler().Handle(new SetNoteStatusCommand(note.Id.Value, null), default);

        Assert.True(result.IsSuccess);
        Assert.Null(note.Status);
        Assert.Null(note.StatusChangedAt);
    }

    /// <summary>specs/knowledge-vault "An unknown status is rejected".</summary>
    [Theory]
    [InlineData("archivada")]
    [InlineData("1")]
    public async Task Rejects_an_unknown_status_without_touching_the_note(string wire)
    {
        var note = Seed(status: NoteStatus.Pending);

        var result = await Handler().Handle(new SetNoteStatusCommand(note.Id.Value, wire), default);

        Assert.Equal(SetNoteStatusCommandHandler.InvalidStatus.Message, result.Error.Message);
        Assert.Equal(NoteStatus.Pending, note.Status);
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    /// <summary>specs/knowledge-vault "Another user's note cannot be marked".</summary>
    [Fact]
    public async Task Another_users_note_is_reported_as_not_found_and_left_alone()
    {
        var theirNote = Seed(Other, NoteStatus.Pending);

        var result = await Handler().Handle(new SetNoteStatusCommand(theirNote.Id.Value, "completed"), default);

        Assert.True(result.IsFailure);
        Assert.Equal("Nota no encontrada.", result.Error.Message);
        Assert.Equal(NoteStatus.Pending, theirNote.Status);
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task A_note_that_does_not_exist_is_reported_as_not_found()
    {
        var result = await Handler().Handle(new SetNoteStatusCommand(Guid.NewGuid(), "pending"), default);

        Assert.True(result.IsFailure);
        Assert.Equal("Nota no encontrada.", result.Error.Message);
    }

    /// <summary>
    /// specs/knowledge-vault "Status change leaves the note's text alone" - the handler half of
    /// design.md Decision 5.
    /// </summary>
    [Fact]
    public async Task Marking_a_note_leaves_its_text_and_last_modified_time_alone()
    {
        var note = Seed();
        var updatedAt = note.UpdatedAt;

        await Handler().Handle(new SetNoteStatusCommand(note.Id.Value, "completed"), default);

        Assert.Equal("Migrar auth", note.Title);
        Assert.Equal("contenido", note.Content);
        Assert.Equal(updatedAt, note.UpdatedAt);
    }
}
