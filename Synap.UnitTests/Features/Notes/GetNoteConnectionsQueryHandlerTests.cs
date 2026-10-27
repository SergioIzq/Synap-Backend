using Synap.Application.Features.Notes.Queries;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;
using Synap.UnitTests.Features.Settings;

namespace Synap.UnitTests.Features.Notes;

/// <summary>
/// smart-resurfacing 1.6 — handler de GetNoteConnectionsQuery:
/// propiedad, status ready/pending, tema común y 503 ante fallo del ai-service.
/// </summary>
public class GetNoteConnectionsQueryHandlerTests
{
    private static readonly Guid Me = Guid.NewGuid();

    private readonly FakeNoteRepository _notes = new();
    private readonly FakeReadRepository _read = new();
    private readonly FakeAiServiceClient _ai = new();

    private GetNoteConnectionsQueryHandler Handler()
        => new(_notes, _read, _ai, new FakeUserContext(Me));

    private Note Seed(Guid? owner = null)
        => _notes.Add(Note.Create(
            UserId.CreateFromDatabase(owner ?? Me), NoteType.Text, "Helm", "contenido"));

    private sealed class FakeReadRepository : INoteReadRepository
    {
        public List<(Guid NoteId, string TagName)> TagRows { get; } = [];

        public Task<PagedResult<NoteSearchResult>> SearchAsync(Guid userId, NoteSearchCriteria criteria, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<NoteSearchResult?> GetByIdAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListTagsAsync(Guid userId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<(Guid NoteId, string TagName)>> GetTagsByNoteIdsAsync(
            Guid userId, IReadOnlyList<Guid> noteIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(Guid, string)>>(TagRows);
    }

    [Fact]
    public async Task Returns_NotFound_for_another_users_note()
    {
        var other = Guid.NewGuid();
        var note = Seed(owner: other);

        var result = await Handler().Handle(new GetNoteConnectionsQuery(note.Id.Value), default);

        Assert.True(result.IsFailure);
        Assert.Equal("Nota no encontrada.", result.Error.Message);
    }

    [Fact]
    public async Task Returns_Pending_when_indexed_false()
    {
        var note = Seed();
        _ai.SetConnections(note.Id.Value, new NoteConnectionsResult(NoteConnectionStatus.Pending, []));

        var result = await Handler().Handle(new GetNoteConnectionsQuery(note.Id.Value), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(NoteConnectionStatus.Pending, result.Value.Status);
    }

    [Fact]
    public async Task Returns_failure_on_ai_service_unavailable()
    {
        var note = Seed();
        // No se preconfiguran conexiones → el fake lanza HttpRequestException

        var result = await Handler().Handle(new GetNoteConnectionsQuery(note.Id.Value), default);

        Assert.True(result.IsFailure);
        Assert.Equal("ai-service-unavailable", result.Error.Code);
    }

    [Fact]
    public async Task Calculates_common_topic_when_two_notes_share_tag()
    {
        var note = Seed();
        var conn1 = Guid.NewGuid();
        var conn2 = Guid.NewGuid();
        _ai.SetConnections(note.Id.Value, new NoteConnectionsResult(
            NoteConnectionStatus.Ready,
            [
                new RelatedNote(conn1, "Kubernetes 1", "contenido", NoteType.Text, 0.80),
                new RelatedNote(conn2, "Kubernetes 2", "contenido", NoteType.Text, 0.75),
            ]));
        _read.TagRows.Add((conn1, "kubernetes"));
        _read.TagRows.Add((conn2, "kubernetes"));

        var result = await Handler().Handle(new GetNoteConnectionsQuery(note.Id.Value), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("kubernetes", result.Value.Topic);
    }

    [Fact]
    public async Task No_topic_when_no_tag_shared_by_two_connections()
    {
        var note = Seed();
        var conn1 = Guid.NewGuid();
        var conn2 = Guid.NewGuid();
        _ai.SetConnections(note.Id.Value, new NoteConnectionsResult(
            NoteConnectionStatus.Ready,
            [
                new RelatedNote(conn1, "Nota A", "contenido", NoteType.Text, 0.80),
                new RelatedNote(conn2, "Nota B", "contenido", NoteType.Text, 0.75),
            ]));
        _read.TagRows.Add((conn1, "helm"));
        _read.TagRows.Add((conn2, "docker"));

        var result = await Handler().Handle(new GetNoteConnectionsQuery(note.Id.Value), default);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Topic);
    }
}
