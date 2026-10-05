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

    private static SearchNotesQuery Query(string? status)
        => new(null, null, null, 1, NoteSearchCriteria.DefaultPageSize, status);

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

        Assert.Equal(
            new NoteSearchCriteria(null, null, null, 1, NoteSearchCriteria.DefaultPageSize, NoteStatusFilter.Default),
            _repository.LastCriteria);
    }

    /// <summary>
    /// note-status task 3.3 - specs/knowledge-vault "Completed notes are left out by default":
    /// no status named means everything live, unmarked notes included.
    /// </summary>
    [Fact]
    public async Task No_status_named_means_the_default_filter()
    {
        var result = await Handler().Handle(new SearchNotesQuery(null, null), default);

        Assert.True(result.IsSuccess);
        var filter = _repository.LastCriteria!.StatusFilter;
        Assert.True(filter.IncludeWithoutStatus);
        Assert.Equal(
            new HashSet<NoteStatus> { NoteStatus.Pending, NoteStatus.InProgress, NoteStatus.Paused },
            filter.Statuses);
    }

    [Theory]
    [InlineData("pending", NoteStatus.Pending)]
    [InlineData("inProgress", NoteStatus.InProgress)]
    [InlineData("PAUSED", NoteStatus.Paused)]
    [InlineData("completed", NoteStatus.Completed)]
    public async Task Parses_one_status(string wire, NoteStatus expected)
    {
        var result = await Handler().Handle(Query(wire), default);

        Assert.True(result.IsSuccess);
        var filter = _repository.LastCriteria!.StatusFilter;
        Assert.Equal([expected], filter.Statuses);
        Assert.False(filter.IncludeWithoutStatus);
    }

    /// <summary>specs/knowledge-vault "Filtered to several statuses at once".</summary>
    [Fact]
    public async Task Parses_several_statuses()
    {
        await Handler().Handle(Query("pending, inProgress"), default);

        Assert.Equal(
            new HashSet<NoteStatus> { NoteStatus.Pending, NoteStatus.InProgress },
            _repository.LastCriteria!.StatusFilter.Statuses);
    }

    /// <summary>specs/knowledge-vault "Filtered to notes without a status".</summary>
    [Fact]
    public async Task Parses_none_on_its_own()
    {
        await Handler().Handle(Query("none"), default);

        var filter = _repository.LastCriteria!.StatusFilter;
        Assert.Empty(filter.Statuses);
        Assert.True(filter.IncludeWithoutStatus);
    }

    /// <summary>specs/knowledge-vault "Statuses and \"no status\" filtered together".</summary>
    [Fact]
    public async Task Parses_a_status_mixed_with_none()
    {
        await Handler().Handle(Query("pending,none"), default);

        var filter = _repository.LastCriteria!.StatusFilter;
        Assert.Equal([NoteStatus.Pending], filter.Statuses);
        Assert.True(filter.IncludeWithoutStatus);
    }

    /// <summary>specs/knowledge-vault "An unknown value in the status filter".</summary>
    [Theory]
    [InlineData("archived")]
    [InlineData("1")]
    [InlineData("pending,archived")]
    [InlineData(",,")]
    public async Task Rejects_unknown_statuses(string wire)
    {
        var result = await Handler().Handle(Query(wire), default);

        Assert.Equal(SearchNotesQueryHandler.InvalidStatus.Message, result.Error.Message);
        Assert.Null(_repository.LastCriteria);
    }

    /// <summary>The message names what is accepted, so a typo is a clear error, not an empty list.</summary>
    [Fact]
    public async Task The_rejection_names_the_accepted_values()
    {
        var result = await Handler().Handle(Query("archived"), default);

        foreach (var accepted in NoteStatusFilter.AcceptedValues)
        {
            Assert.Contains(accepted, result.Error.Message);
        }
    }

    [Fact]
    public async Task Max_page_size_is_allowed()
    {
        var result = await Handler().Handle(new SearchNotesQuery("x", null, null, 3, NoteSearchCriteria.MaxPageSize), default);

        Assert.True(result.IsSuccess);
    }
}
