using Synap.Domain;
using Synap.Infrastructure.Persistence.Data.Notes;
using Synap.Infrastructure.Persistence.Data.Tags;
using Synap.Shared.Domain.ValueObjects.Ids;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// Task 5.1 - proves specs/knowledge-vault's "per-user isolation" invariant against a real
/// Postgres, not mocks: a bug in a repository's WHERE clause is exactly the kind of thing a
/// mocked repository would never catch.
/// </summary>
[Collection(PostgresCollection.Name)]
public class NoteIsolationTests
{
    private readonly PostgresFixture _fixture;

    public NoteIsolationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetOwnedByUserAsync_never_returns_another_users_note()
    {
        await using var context = _fixture.CreateContext();
        var noteWriteRepository = new NoteWriteRepository(context);

        var userA = UserId.CreateFromDatabase(Guid.NewGuid());
        var userB = UserId.CreateFromDatabase(Guid.NewGuid());

        var userBsNote = Note.Create(userB, NoteType.Text, "User B's private note", "secret content");
        await noteWriteRepository.CreateAsync(userBsNote, default);
        await context.SaveChangesAsync();

        var result = await noteWriteRepository.GetOwnedByUserAsync(userBsNote.Id.Value, userA.Value);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOwnedByUserAsync_returns_the_note_for_its_actual_owner()
    {
        await using var context = _fixture.CreateContext();
        var noteWriteRepository = new NoteWriteRepository(context);

        var userA = UserId.CreateFromDatabase(Guid.NewGuid());
        var note = Note.Create(userA, NoteType.Text, "My note", "content");
        await noteWriteRepository.CreateAsync(note, default);
        await context.SaveChangesAsync();

        var result = await noteWriteRepository.GetOwnedByUserAsync(note.Id.Value, userA.Value);

        Assert.NotNull(result);
        Assert.Equal(note.Id.Value, result.Id.Value);
    }

    [Fact]
    public async Task Search_never_returns_another_users_notes_even_for_a_matching_term()
    {
        await using var context = _fixture.CreateContext();
        var noteWriteRepository = new NoteWriteRepository(context);
        var noteReadRepository = new NoteReadRepository(new TestDbConnectionFactory(_fixture.ConnectionString));

        var userA = UserId.CreateFromDatabase(Guid.NewGuid());
        var userB = UserId.CreateFromDatabase(Guid.NewGuid());

        const string sharedTerm = "kubernetes";

        var noteA = Note.Create(userA, NoteType.Text, null, $"Notes about {sharedTerm} deployments");
        var noteB = Note.Create(userB, NoteType.Text, null, $"User B's {sharedTerm} cheatsheet");

        await noteWriteRepository.CreateAsync(noteA, default);
        await noteWriteRepository.CreateAsync(noteB, default);
        await context.SaveChangesAsync();

        var results = await noteReadRepository.SearchAsync(userA.Value, sharedTerm, tag: null);

        Assert.Contains(results, r => r.Id == noteA.Id.Value);
        Assert.DoesNotContain(results, r => r.Id == noteB.Id.Value);
    }

    [Fact]
    public async Task Search_scoped_by_tag_never_returns_another_users_note_even_with_the_same_tag_name()
    {
        await using var context = _fixture.CreateContext();
        var noteWriteRepository = new NoteWriteRepository(context);
        var tagWriteRepository = new TagWriteRepository(context);
        var noteReadRepository = new NoteReadRepository(new TestDbConnectionFactory(_fixture.ConnectionString));

        var userA = UserId.CreateFromDatabase(Guid.NewGuid());
        var userB = UserId.CreateFromDatabase(Guid.NewGuid());

        // Both users independently use a tag literally named "work" - tags are per-user
        // (specs/knowledge-vault + the unique (user_id, name) index), so these are two distinct rows.
        var tagA = Tag.Create(userA, "work");
        var tagB = Tag.Create(userB, "work");
        await tagWriteRepository.CreateAsync(tagA, default);
        await tagWriteRepository.CreateAsync(tagB, default);

        var noteA = Note.Create(userA, NoteType.Text, null, "A's work note");
        noteA.AddTag(tagA);
        var noteB = Note.Create(userB, NoteType.Text, null, "B's work note");
        noteB.AddTag(tagB);

        await noteWriteRepository.CreateAsync(noteA, default);
        await noteWriteRepository.CreateAsync(noteB, default);
        await context.SaveChangesAsync();

        var results = await noteReadRepository.SearchAsync(userA.Value, searchTerm: null, tag: "work");

        Assert.Contains(results, r => r.Id == noteA.Id.Value);
        Assert.DoesNotContain(results, r => r.Id == noteB.Id.Value);
    }
}
