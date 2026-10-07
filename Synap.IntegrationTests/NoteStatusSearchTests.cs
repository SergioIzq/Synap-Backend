using Synap.Domain;
using Synap.Infrastructure.Persistence.Data.Notes;
using Synap.Infrastructure.Persistence.Data.Tags;
using Synap.Shared.Domain.ValueObjects.Ids;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// note-status tasks 2.3 and 3.2/3.5 - specs/knowledge-vault "Search and listing filtered by
/// status" against real Postgres. The test that matters most here is
/// <see cref="A_note_without_a_status_is_never_hidden_by_default"/>: a bare `status &lt;&gt;
/// 'Completed'` passes every other test in this file and silently empties the list (design.md
/// Decision 2). Each test uses a fresh user, so the shared database doesn't interfere.
/// </summary>
[Collection(PostgresCollection.Name)]
public class NoteStatusSearchTests
{
    private readonly PostgresFixture _fixture;

    public NoteStatusSearchTests(PostgresFixture fixture) => _fixture = fixture;

    private async Task<(Guid UserId, NoteReadRepository Repository)> SeedAsync(
        params (string Title, NoteStatus? Status)[] notes)
    {
        await using var context = _fixture.CreateContext();
        var writeRepository = new NoteWriteRepository(context);
        var user = UserId.CreateFromDatabase(Guid.NewGuid());

        foreach (var (title, status) in notes)
        {
            await writeRepository.CreateAsync(
                Note.Create(user, NoteType.Text, title, "contenido sobre despliegues", status), default);
        }

        await context.SaveChangesAsync();
        return (user.Value, new NoteReadRepository(new TestDbConnectionFactory(_fixture.ConnectionString)));
    }

    private static NoteSearchCriteria Criteria(NoteStatusFilter? status = null, string? term = null)
        => new(term, null, null, 1, 50, status ?? NoteStatusFilter.Default);

    private static string[] Titles(PagedResult<NoteSearchResult> result)
        => result.Items.Select(i => i.Title!).OrderBy(t => t).ToArray();

    /// <summary>task 2.3 - specs/knowledge-vault "Status in search results".</summary>
    [Fact]
    public async Task A_marked_note_reports_its_status_and_an_unmarked_one_reports_none()
    {
        var (userId, repository) = await SeedAsync(("Marcada", NoteStatus.InProgress), ("Sin marcar", null));

        var result = await repository.SearchAsync(userId, Criteria());

        Assert.Equal(NoteStatus.InProgress, result.Items.Single(i => i.Title == "Marcada").Status);
        Assert.Null(result.Items.Single(i => i.Title == "Sin marcar").Status);
    }

    /// <summary>task 2.3 - specs/knowledge-vault "Status when a single note is retrieved".</summary>
    [Fact]
    public async Task A_single_note_is_retrieved_with_its_status()
    {
        var (userId, repository) = await SeedAsync(("Marcada", NoteStatus.Paused));
        var noteId = (await repository.SearchAsync(userId, Criteria())).Items[0].Id;

        var note = await repository.GetByIdAsync(userId, noteId);

        Assert.Equal(NoteStatus.Paused, note!.Status);
    }

    /// <summary>
    /// design.md Decision 2, the whole reason it is a spec requirement: `NULL &lt;&gt; 'Completed'`
    /// is NULL, which WHERE discards, so the naive filter hides most of the vault.
    /// </summary>
    [Fact]
    public async Task A_note_without_a_status_is_never_hidden_by_default()
    {
        var (userId, repository) = await SeedAsync(
            ("Sin estado uno", null), ("Sin estado dos", null), ("Pendiente", NoteStatus.Pending));

        var result = await repository.SearchAsync(userId, Criteria());

        Assert.Equal(["Pendiente", "Sin estado dos", "Sin estado uno"], Titles(result));
        Assert.Equal(3, result.TotalCount);
    }

    /// <summary>specs/knowledge-vault "Completed notes are left out by default".</summary>
    [Fact]
    public async Task Completed_notes_are_left_out_by_default()
    {
        var (userId, repository) = await SeedAsync(
            ("Hecha", NoteStatus.Completed), ("Pendiente", NoteStatus.Pending), ("Sin estado", null));

        var result = await repository.SearchAsync(userId, Criteria());

        Assert.Equal(["Pendiente", "Sin estado"], Titles(result));
        Assert.Equal(2, result.TotalCount);
    }

    /// <summary>specs/knowledge-vault "Filtered to one status".</summary>
    [Fact]
    public async Task Filtered_to_one_status()
    {
        var (userId, repository) = await SeedAsync(
            ("Pendiente", NoteStatus.Pending), ("En curso", NoteStatus.InProgress), ("Sin estado", null));

        var result = await repository.SearchAsync(userId, Criteria(NoteStatusFilter.Of(NoteStatus.Pending)));

        Assert.Equal(["Pendiente"], Titles(result));
    }

    /// <summary>specs/knowledge-vault "Filtered to several statuses at once".</summary>
    [Fact]
    public async Task Filtered_to_several_statuses_at_once()
    {
        var (userId, repository) = await SeedAsync(
            ("Pendiente", NoteStatus.Pending), ("En curso", NoteStatus.InProgress),
            ("Pausada", NoteStatus.Paused), ("Sin estado", null));

        var result = await repository.SearchAsync(
            userId, Criteria(NoteStatusFilter.Of(NoteStatus.Pending, NoteStatus.InProgress)));

        Assert.Equal(["En curso", "Pendiente"], Titles(result));
    }

    /// <summary>specs/knowledge-vault "Filtered to notes without a status".</summary>
    [Fact]
    public async Task Filtered_to_notes_without_a_status()
    {
        var (userId, repository) = await SeedAsync(
            ("Pendiente", NoteStatus.Pending), ("Sin estado uno", null), ("Sin estado dos", null));

        var result = await repository.SearchAsync(userId, Criteria(NoteStatusFilter.WithoutStatus));

        Assert.Equal(["Sin estado dos", "Sin estado uno"], Titles(result));
    }

    /// <summary>specs/knowledge-vault "Statuses and \"no status\" filtered together".</summary>
    [Fact]
    public async Task Statuses_and_no_status_filtered_together()
    {
        var (userId, repository) = await SeedAsync(
            ("Pendiente", NoteStatus.Pending), ("En curso", NoteStatus.InProgress), ("Sin estado", null));

        var result = await repository.SearchAsync(
            userId, Criteria(NoteStatusFilter.Of([NoteStatus.Pending], includeWithoutStatus: true)));

        Assert.Equal(["Pendiente", "Sin estado"], Titles(result));
    }

    /// <summary>specs/knowledge-vault "Completed notes asked for explicitly".</summary>
    [Fact]
    public async Task Completed_notes_asked_for_explicitly()
    {
        var (userId, repository) = await SeedAsync(
            ("Hecha", NoteStatus.Completed), ("Pendiente", NoteStatus.Pending));

        var result = await repository.SearchAsync(userId, Criteria(NoteStatusFilter.Of(NoteStatus.Completed)));

        Assert.Equal(["Hecha"], Titles(result));
    }

    /// <summary>specs/knowledge-vault "Everything asked for explicitly".</summary>
    [Fact]
    public async Task Everything_asked_for_explicitly()
    {
        var (userId, repository) = await SeedAsync(
            ("Hecha", NoteStatus.Completed), ("Pendiente", NoteStatus.Pending),
            ("Pausada", NoteStatus.Paused), ("En curso", NoteStatus.InProgress), ("Sin estado", null));

        var result = await repository.SearchAsync(userId, Criteria(NoteStatusFilter.Of(
            [NoteStatus.Pending, NoteStatus.InProgress, NoteStatus.Paused, NoteStatus.Completed],
            includeWithoutStatus: true)));

        Assert.Equal(5, result.TotalCount);
    }

    /// <summary>specs/knowledge-vault "Status filter combined with a search term".</summary>
    [Fact]
    public async Task Status_filter_combined_with_a_search_term()
    {
        var (userId, repository) = await SeedAsync(
            ("Pendiente", NoteStatus.Pending), ("En curso", NoteStatus.InProgress));

        var result = await repository.SearchAsync(
            userId, Criteria(NoteStatusFilter.Of(NoteStatus.InProgress), term: "despliegues"));

        Assert.Equal(["En curso"], Titles(result));
    }

    /// <summary>specs/knowledge-vault "Status filter combined with a tag or type filter".</summary>
    [Fact]
    public async Task Status_filter_combined_with_a_type_filter()
    {
        await using var context = _fixture.CreateContext();
        var writeRepository = new NoteWriteRepository(context);
        var user = UserId.CreateFromDatabase(Guid.NewGuid());

        await writeRepository.CreateAsync(
            Note.Create(user, NoteType.Text, "Texto pendiente", "contenido", NoteStatus.Pending), default);
        await writeRepository.CreateAsync(
            Note.Create(user, NoteType.CodeSnippet, "Snippet pendiente", "retry()", NoteStatus.Pending), default);
        await writeRepository.CreateAsync(
            Note.Create(user, NoteType.CodeSnippet, "Snippet hecho", "done()", NoteStatus.Completed), default);
        await context.SaveChangesAsync();

        var repository = new NoteReadRepository(new TestDbConnectionFactory(_fixture.ConnectionString));
        var result = await repository.SearchAsync(
            user.Value,
            new NoteSearchCriteria(null, null, NoteType.CodeSnippet, 1, 50, NoteStatusFilter.Of(NoteStatus.Pending)));

        Assert.Equal(["Snippet pendiente"], Titles(result));
    }

    /// <summary>specs/knowledge-vault "Status filter combined with a tag or type filter".</summary>
    [Fact]
    public async Task Status_filter_combined_with_a_tag_filter()
    {
        await using var context = _fixture.CreateContext();
        var writeRepository = new NoteWriteRepository(context);
        var tagWriteRepository = new TagWriteRepository(context);
        var user = UserId.CreateFromDatabase(Guid.NewGuid());

        var tag = Tag.Create(user, "infra");
        await tagWriteRepository.CreateAsync(tag, default);

        var tagged = Note.Create(user, NoteType.Text, "Etiquetada pendiente", "contenido", NoteStatus.Pending);
        tagged.AddTag(tag);
        var taggedDone = Note.Create(user, NoteType.Text, "Etiquetada hecha", "contenido", NoteStatus.Completed);
        taggedDone.AddTag(tag);

        await writeRepository.CreateAsync(tagged, default);
        await writeRepository.CreateAsync(taggedDone, default);
        await writeRepository.CreateAsync(
            Note.Create(user, NoteType.Text, "Sin etiqueta pendiente", "contenido", NoteStatus.Pending), default);
        await context.SaveChangesAsync();

        var repository = new NoteReadRepository(new TestDbConnectionFactory(_fixture.ConnectionString));
        var result = await repository.SearchAsync(
            user.Value,
            new NoteSearchCriteria(null, "infra", null, 1, 50, NoteStatusFilter.Of(NoteStatus.Pending)));

        Assert.Equal(["Etiquetada pendiente"], Titles(result));
    }

    /// <summary>
    /// The total past the last page comes from CountAsync, a second query with its own copy of the
    /// WHERE clause - so the status filter has to be in both or the count contradicts the page.
    /// </summary>
    [Fact]
    public async Task The_total_past_the_last_page_respects_the_status_filter()
    {
        var (userId, repository) = await SeedAsync(
            ("Hecha", NoteStatus.Completed), ("Pendiente", NoteStatus.Pending), ("Sin estado", null));

        var result = await repository.SearchAsync(userId, new NoteSearchCriteria(null, null, null, 9, 20, NoteStatusFilter.Default));

        Assert.Empty(result.Items);
        Assert.Equal(2, result.TotalCount);
    }
}
