using Synap.Application.Features.Notes.Queries;
using Synap.Domain;
using Synap.UnitTests.Features.Settings;

namespace Synap.UnitTests.Features.Notes;

/// <summary>backend-hardening task 4.3 - input validation before the repository is ever called.</summary>
public class SearchNotesQueryHandlerTests
{
    private sealed class RecordingRepository : INoteReadRepository
    {
        public NoteSearchCriteria? LastCriteria { get; private set; }

        public Task<PagedResult<NoteSearchResult>> SearchAsync(Guid userId, NoteSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            LastCriteria = criteria;
            return Task.FromResult(PagedResult<NoteSearchResult>.Empty(criteria.Page, criteria.PageSize));
        }

        public Task<NoteSearchResult?> GetByIdAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
            => Task.FromResult<NoteSearchResult?>(null);

        public Task<IReadOnlyList<string>> ListTagsAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private readonly RecordingRepository _repository = new();

    private SearchNotesQueryHandler Handler() => new(_repository, new FakeUserContext(Guid.NewGuid()));

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    public async Task Rejects_invalid_paging(int page, int pageSize)
    {
        var result = await Handler().Handle(new SearchNotesQuery(null, null, null, page, pageSize), default);

        Assert.True(result.IsFailure);
        Assert.Null(_repository.LastCriteria);
    }

    [Theory]
    [InlineData("text", NoteType.Text)]
    [InlineData("codeSnippet", NoteType.CodeSnippet)]
    [InlineData("BOOKMARK", NoteType.Bookmark)]
    public async Task Parses_type_names(string wire, NoteType expected)
    {
        var result = await Handler().Handle(new SearchNotesQuery(null, null, wire), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, _repository.LastCriteria!.Type);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("1")]
    public async Task Rejects_unknown_types(string wire)
    {
        var result = await Handler().Handle(new SearchNotesQuery(null, null, wire), default);

        Assert.Equal(SearchNotesQueryHandler.InvalidType.Message, result.Error.Message);
    }

    [Fact]
    public async Task Blank_filters_become_null_and_defaults_apply()
    {
        await Handler().Handle(new SearchNotesQuery("  ", " ", ""), default);

        Assert.Equal(new NoteSearchCriteria(null, null, null, 1, NoteSearchCriteria.DefaultPageSize), _repository.LastCriteria);
    }

    [Fact]
    public async Task Max_page_size_is_allowed()
    {
        var result = await Handler().Handle(new SearchNotesQuery("x", null, null, 3, NoteSearchCriteria.MaxPageSize), default);

        Assert.True(result.IsSuccess);
    }
}
