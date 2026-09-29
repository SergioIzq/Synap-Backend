using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Application.Features.Assistant.Agent;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Assistant.Queries;

public sealed class AskAssistantQueryHandler : IQueryHandler<AskAssistantQuery, AssistantAnswer>
{
    public static readonly Error InvalidScope = Error.Validation("Indica una nota o una etiqueta sobre la que preguntar, no ambas.");
    public static readonly Error NoteNotFound = Error.NotFound("Nota no encontrada.");

    // Short memory of every conversation (scoped-assistant design.md Decision 1; global ones too
    // since assistant-agent-foundations).
    public const int MaxHistoryTurns = 3;
    public const int MaxHistoryQuestionChars = 1_000;
    public const int MaxHistoryAnswerChars = 2_000;

    private readonly IAiServiceClient _aiServiceClient;
    private readonly IUserContext _userContext;
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly INoteWriteRepository _noteWriteRepository;
    private readonly IMemoryEntryReadRepository _memoryEntryReadRepository;
    private readonly AssistantAgent _assistantAgent;

    public AskAssistantQueryHandler(
        IAiServiceClient aiServiceClient,
        IUserContext userContext,
        IUserWriteRepository userWriteRepository,
        ISecretProtector secretProtector,
        INoteWriteRepository noteWriteRepository,
        IMemoryEntryReadRepository memoryEntryReadRepository,
        AssistantAgent assistantAgent)
    {
        _noteWriteRepository = noteWriteRepository;
        _memoryEntryReadRepository = memoryEntryReadRepository;
        _assistantAgent = assistantAgent;
        _aiServiceClient = aiServiceClient;
        _userContext = userContext;
        _userWriteRepository = userWriteRepository;
        _secretProtector = secretProtector;
    }

    public async Task<Result<AssistantAnswer>> Handle(AskAssistantQuery request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        var scope = NormalizeScope(request.Scope);
        if (scope is { IsFailure: true })
        {
            return Result.Failure<AssistantAnswer>(scope.Error);
        }

        var user = await _userWriteRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<AssistantAnswer>(Error.NotFound("Usuario no encontrado."));
        }

        // No shared fallback key (specs/ai-assistant "Answers generated with the user's own
        // credentials"): without the user's own key the provider is never contacted.
        if (!user.HasGroqApiKey)
        {
            return Result.Success(AssistantAnswer.Failed(AssistantAnswer.KeyMissingMessage, AssistantAnswerStatus.KeyMissing));
        }

        // A key that no longer decrypts (e.g. the master key was lost/rotated) is treated like
        // one the provider rejected: the fix for the user is the same - re-enter it.
        if (!_secretProtector.TryUnprotect(user.GroqApiKeyEncrypted!, out var groqApiKey))
        {
            return Result.Success(AssistantAnswer.Failed(AssistantAnswer.InvalidKeyMessage, AssistantAnswerStatus.InvalidKey));
        }

        if (scope?.Value.NoteId is Guid noteId)
        {
            // Ownership before anything reaches the AI service: another user's note looks exactly
            // like a missing one (same isolation rule as GetRelatedNotesQueryHandler).
            var note = await _noteWriteRepository.GetOwnedByUserAsync(noteId, userId, cancellationToken);
            if (note is null)
            {
                return Result.Failure<AssistantAnswer>(NoteNotFound);
            }

            // A bookmark only holds its link - nothing to answer from yet.
            if (note.Type == NoteType.Bookmark)
            {
                return Result.Success(AssistantAnswer.Failed(AssistantAnswer.ScopeUnsupportedMessage, AssistantAnswerStatus.ScopeUnsupported));
            }
        }

        var history = TrimHistory(request.History);
        // The whole memory goes with every question: it is bounded by construction (specs/assistant-memory).
        var memory = (await _memoryEntryReadRepository.ListByUserAsync(userId, cancellationToken)).Select(e => e.Text).ToList();

        // A tag needs no lookup here: the AI service only searches the user's own tags, so a tag
        // that only exists in someone else's vault ends up as "nothing relevant", like an unused one.
        // Scoped conversations never perform actions (specs/ai-assistant).
        if (scope is not null)
        {
            return Result.Success(await _aiServiceClient.AskAsync(
                userId, request.Question, groqApiKey, user.GroqModel, scope.Value, history, memory,
                ActionsUnavailableReason.Scope, cancellationToken));
        }

        // The global conversation is the agent (assistant-agent-foundations design.md Decision 6);
        // when the model can't use tools, the same question is answered without actions.
        var outcome = await _assistantAgent.RunAsync(userId, request.Question, history, memory, groqApiKey, user.GroqModel, cancellationToken);
        if (outcome.Answer is not null)
        {
            return Result.Success(outcome.Answer);
        }

        var answer = await _aiServiceClient.AskAsync(
            userId, request.Question, groqApiKey, user.GroqModel, null, history, memory,
            ActionsUnavailableReason.Model, cancellationToken);
        return Result.Success(answer with { Actions = outcome.ActionsSoFar ?? [] });
    }

    /// <summary>Null when unscoped; a failure unless exactly one of note or tag is given.</summary>
    private static Result<AssistantScope>? NormalizeScope(AssistantScope? scope)
    {
        if (scope is null)
        {
            return null;
        }

        // Same normalisation as tag names are stored with (TagAssignment.Normalize).
        var tag = scope.Tag?.Trim().TrimStart('#').Trim();
        var hasNote = scope.NoteId is Guid id && id != Guid.Empty;
        var hasTag = !string.IsNullOrEmpty(tag);

        if (hasNote == hasTag)
        {
            return Result.Failure<AssistantScope>(InvalidScope);
        }

        return Result.Success(hasNote ? new AssistantScope(scope.NoteId, null) : new AssistantScope(null, tag));
    }

    /// <summary>The last turns only, each cut to size - longer histories are trimmed, never rejected.</summary>
    private static IReadOnlyList<AssistantTurn> TrimHistory(IReadOnlyList<AssistantTurn>? history)
        => (history ?? [])
            .Where(t => t is not null)
            .TakeLast(MaxHistoryTurns)
            .Select(t => new AssistantTurn(Truncate(t.Question, MaxHistoryQuestionChars), Truncate(t.Answer, MaxHistoryAnswerChars)))
            .ToList();

    private static string Truncate(string? text, int max)
        => text is null ? string.Empty : text.Length <= max ? text : text[..max];
}
