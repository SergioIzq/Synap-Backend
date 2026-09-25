using Synap.Domain;
using Synap.Infrastructure.Persistence.Data.Notes;
using Synap.Shared.Domain.ValueObjects.Ids;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// backend-hardening tasks 4.2/4.3 - specs/knowledge-vault "Full-text search" against real
/// Postgres: Spanish stemming, accent-insensitive matching (AddNoteSearchVector), type filter
/// and pagination. Each test uses a fresh user, so the shared database doesn't interfere.
/// </summary>
[Collection(PostgresCollection.Name)]
public class NoteSearchTests
{
    private readonly PostgresFixture _fixture;

    public NoteSearchTests(PostgresFixture fixture) => _fixture = fixture;

    private async Task<(Guid UserId, NoteReadRepository Repository)> SeedAsync(params (NoteType Type, string? Title, string Content)[] notes)
    {
        await using var context = _fixture.CreateContext();
        var writeRepository = new NoteWriteRepository(context);
        var user = UserId.CreateFromDatabase(Guid.NewGuid());

        foreach (var (type, title, content) in notes)
        {
            await writeRepository.CreateAsync(Note.Create(user, type, title, content), default);
        }

        await context.SaveChangesAsync();
        return (user.Value, new NoteReadRepository(new TestDbConnectionFactory(_fixture.ConnectionString)));
    }

    private static NoteSearchCriteria Criteria(string? term = null, NoteType? type = null, int page = 1, int pageSize = 20)
        => new(term, null, type, page, pageSize);

    [Fact]
    public async Task Matches_ignoring_accents()
    {
        var (userId, repository) = await SeedAsync((NoteType.Text, "Configuración del proxy", "Pasos para nginx"));

        var result = await repository.SearchAsync(userId, Criteria("configuracion"));

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Matches_spanish_word_variants()
    {
        var (userId, repository) = await SeedAsync((NoteType.Text, null, "Una nota sobre despliegues"));

        var result = await repository.SearchAsync(userId, Criteria("notas"));

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Title_matches_rank_above_content_matches()
    {
        var (userId, repository) = await SeedAsync(
            (NoteType.Text, null, "Algo sobre docker de pasada"),
            (NoteType.Text, "Docker", "Guía completa"));

        var result = await repository.SearchAsync(userId, Criteria("docker"));

        Assert.Equal("Docker", result.Items[0].Title);
    }

    [Fact]
    public async Task Filters_by_note_type()
    {
        var (userId, repository) = await SeedAsync(
            (NoteType.Text, null, "texto"),
            (NoteType.CodeSnippet, null, "console.log(1)"),
            (NoteType.Bookmark, null, "https://example.com"));

        var result = await repository.SearchAsync(userId, Criteria(type: NoteType.CodeSnippet));

        Assert.Equal(NoteType.CodeSnippet, Assert.Single(result.Items).Type);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task Pages_through_results_with_a_total()
    {
        var (userId, repository) = await SeedAsync(
            Enumerable.Range(1, 5).Select(i => (NoteType.Text, (string?)null, $"nota número {i}")).ToArray());

        var first = await repository.SearchAsync(userId, Criteria(pageSize: 2));
        var last = await repository.SearchAsync(userId, Criteria(page: 3, pageSize: 2));
        var beyond = await repository.SearchAsync(userId, Criteria(page: 4, pageSize: 2));

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(5, first.TotalCount);
        Assert.True(first.HasNextPage);

        Assert.Single(last.Items);
        Assert.False(last.HasNextPage);

        Assert.Empty(beyond.Items);
        Assert.Equal(5, beyond.TotalCount);
    }

    [Fact]
    public async Task No_matches_is_an_empty_page_not_an_error()
    {
        var (userId, repository) = await SeedAsync((NoteType.Text, null, "algo"));

        var result = await repository.SearchAsync(userId, Criteria("inexistente \"con comillas\" -raro OR"));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }
}
