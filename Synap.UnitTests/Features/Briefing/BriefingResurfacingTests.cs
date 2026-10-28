using Synap.Application.Features.Briefing;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using Xunit;

namespace Synap.UnitTests.Features.Briefing;

/// <summary>
/// smart-resurfacing 1.9 — sección Resurfaced de BriefingContentService:
/// nota dormida rescatada, IsEmpty, ai-service no disponible, sin actividad reciente.
/// </summary>
public class BriefingResurfacingTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly FakeResurfacingRepository _repo = new();
    private readonly FakeResurfacingAi _ai = new();

    private BriefingContentService Service(TimeSpan? resurfacingTimeout = null)
        => new(_repo, new NullCalendarReader(), _ai, resurfacingTimeout: resurfacingTimeout);

    [Fact]
    public async Task Resurfaced_note_appears_when_dormant_and_related_to_recent()
    {
        var dormant = Guid.NewGuid();
        var anchor = Guid.NewGuid();
        _repo.Anchors.Add(anchor);
        _repo.Candidates.Add(dormant);
        _ai.Pairs.Add(new SimilarityPair(dormant, anchor, 0.80));
        _repo.NoteDetails[dormant] = new BriefingNoteDetail(dormant, "Kubernetes", "contenido", Now - TimeSpan.FromDays(40));
        _repo.NoteDetails[anchor] = new BriefingNoteDetail(anchor, "Helm", "motivo", Now - TimeSpan.FromDays(1));

        var content = await Service().BuildAsync(UserId, Now, null);

        Assert.Single(content.ResurfacedNotes);
        Assert.Equal(dormant, content.ResurfacedNotes[0].Id);
        Assert.Equal(anchor, content.ResurfacedNotes[0].Reason.Id);
        Assert.Equal(40, content.ResurfacedNotes[0].DormantForDays);
    }

    [Fact]
    public async Task Solo_notas_para_retomar_is_not_empty()
    {
        var dormant = Guid.NewGuid();
        var anchor = Guid.NewGuid();
        _repo.Anchors.Add(anchor);
        _repo.Candidates.Add(dormant);
        _ai.Pairs.Add(new SimilarityPair(dormant, anchor, 0.80));
        _repo.NoteDetails[dormant] = new BriefingNoteDetail(dormant, "Dormida", "contenido", Now - TimeSpan.FromDays(31));
        _repo.NoteDetails[anchor] = new BriefingNoteDetail(anchor, "Reciente", "motivo", Now - TimeSpan.FromDays(1));

        var content = await Service().BuildAsync(UserId, Now, null);

        Assert.False(content.IsEmpty);
    }

    [Fact]
    public async Task Nada_que_retomar_returns_empty_section()
    {
        // Sin candidatas ni motivos → sección vacía
        var content = await Service().BuildAsync(UserId, Now, null);

        Assert.Empty(content.ResurfacedNotes);
    }

    [Fact]
    public async Task Ai_service_no_disponible_returns_empty_section()
    {
        var dormant = Guid.NewGuid();
        var anchor = Guid.NewGuid();
        _repo.Anchors.Add(anchor);
        _repo.Candidates.Add(dormant);
        _ai.Throws = true;

        var content = await Service(resurfacingTimeout: TimeSpan.FromSeconds(1)).BuildAsync(UserId, Now, null);

        Assert.Empty(content.ResurfacedNotes);
    }

    [Fact]
    public async Task Sin_actividad_reciente_returns_empty_section()
    {
        // Hay candidatas pero no hay motivos recientes
        _repo.Candidates.Add(Guid.NewGuid());

        var content = await Service().BuildAsync(UserId, Now, null);

        Assert.Empty(content.ResurfacedNotes);
    }

    // ── fakes ────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeResurfacingRepository : IBriefingReadRepository
    {
        public List<Guid> Anchors { get; } = [];
        public List<Guid> Candidates { get; } = [];
        public Dictionary<Guid, BriefingNoteDetail> NoteDetails { get; } = [];

        public Task<BriefingSection<BriefingReminder>> ListRemindersDueAsync(
            Guid userId, DateTime fromUtc, DateTime toUtcExclusive, int limit, CancellationToken ct = default)
            => Task.FromResult(BriefingSection<BriefingReminder>.Empty);

        public Task<BriefingSection<BriefingNote>> ListUntaggedNotesAsync(
            Guid userId, DateTime sinceUtc, int limit, CancellationToken ct = default)
            => Task.FromResult(BriefingSection<BriefingNote>.Empty);

        public Task<BriefingSection<BriefingNote>> ListInProgressNotesAsync(
            Guid userId, int limit, CancellationToken ct = default)
            => Task.FromResult(BriefingSection<BriefingNote>.Empty);

        public Task<BriefingSection<BriefingNote>> ListPendingNotesAsync(
            Guid userId, DateTime pausedBeforeUtc, DateTime nowUtc, int limit, CancellationToken ct = default)
            => Task.FromResult(BriefingSection<BriefingNote>.Empty);

        public Task<IReadOnlyList<Guid>> ListRecentAnchorIdsAsync(
            Guid userId, DateTime recentSinceUtc, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(Anchors);

        public Task<IReadOnlyList<Guid>> ListDormantCandidateIdsAsync(
            Guid userId, DateTime dormantBeforeUtc, DateTime cooldownBeforeUtc, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(Candidates);

        public Task<IReadOnlyList<BriefingNoteDetail>> ListNoteDetailsByIdsAsync(
            Guid userId, IReadOnlyList<Guid> noteIds, CancellationToken ct = default)
        {
            IReadOnlyList<BriefingNoteDetail> result = noteIds
                .Select(id => NoteDetails.TryGetValue(id, out var d) ? d : null)
                .Where(d => d is not null)
                .Cast<BriefingNoteDetail>()
                .ToList();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeResurfacingAi : IAiServiceClient
    {
        public List<SimilarityPair> Pairs { get; } = [];
        public bool Throws { get; set; }

        public Task GenerateEmbeddingAsync(Guid noteId, Guid userId, string? title, string content, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<RelatedNote>> GetRelatedNotesAsync(Guid noteId, Guid userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RelatedNote>>([]);

        public Task<AssistantAnswer> AskAsync(Guid userId, string question, string groqApiKey, string? groqModel,
            AssistantScope? scope = null, IReadOnlyList<AssistantTurn>? history = null,
            IReadOnlyList<string>? memory = null, ActionsUnavailableReason? actionsUnavailable = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LlmModelsResult> ListModelsAsync(string groqApiKey, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentStepResult> StepAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<AgentTool>? tools,
            string groqApiKey, string? groqModel, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<NoteSearchHit>> SearchAsync(Guid userId, string query, int limit,
            double? minSimilarity = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NoteSearchHit>>([]);

        public Task<NoteConnectionsResult> GetConnectionsAsync(Guid noteId, Guid userId, double minSimilarity,
            int limit, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SimilarityPair>> GetSimilarityPairsAsync(
            Guid userId, IReadOnlyList<Guid> anchorIds, IReadOnlyList<Guid> candidateIds,
            double minSimilarity, int limit, CancellationToken ct = default)
        {
            if (Throws) throw new System.Net.Http.HttpRequestException("ai-service no disponible (fake)");
            return Task.FromResult<IReadOnlyList<SimilarityPair>>(Pairs);
        }
    }

    private sealed class NullCalendarReader : ICalendarReader
    {
        public Task<CalendarReadResult> ListEventsAsync(Guid userId, DateTime fromUtc, DateTime toUtcExclusive,
            int limit, CancellationToken ct = default)
            => Task.FromResult<CalendarReadResult>(new CalendarReadResult.NotConnected());
    }
}
