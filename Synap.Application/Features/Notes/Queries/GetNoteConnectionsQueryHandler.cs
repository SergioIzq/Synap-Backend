using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Notes.Queries;

public sealed class GetNoteConnectionsQueryHandler : IQueryHandler<GetNoteConnectionsQuery, NoteConnectionsResult>
{
    private readonly INoteWriteRepository _noteWriteRepository;
    private readonly INoteReadRepository _noteReadRepository;
    private readonly IAiServiceClient _aiServiceClient;
    private readonly IUserContext _userContext;

    public GetNoteConnectionsQueryHandler(
        INoteWriteRepository noteWriteRepository,
        INoteReadRepository noteReadRepository,
        IAiServiceClient aiServiceClient,
        IUserContext userContext)
    {
        _noteWriteRepository = noteWriteRepository;
        _noteReadRepository = noteReadRepository;
        _aiServiceClient = aiServiceClient;
        _userContext = userContext;
    }

    public async Task<Result<NoteConnectionsResult>> Handle(GetNoteConnectionsQuery request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        var note = await _noteWriteRepository.GetOwnedByUserAsync(request.NoteId, userId, cancellationToken);
        if (note is null)
        {
            return Result.Failure<NoteConnectionsResult>(Error.NotFound("Nota no encontrada."));
        }

        NoteConnectionsResult connections;
        try
        {
            connections = await _aiServiceClient.GetConnectionsAsync(
                request.NoteId,
                userId,
                RelevanceLimits.RelatedNoteSimilarityFloor,
                RelevanceLimits.MaxConnections,
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<NoteConnectionsResult>(
                Error.Failure("ai-service-unavailable", "Failure", "El servicio de análisis no está disponible."));
        }

        if (connections.Status == NoteConnectionStatus.Pending || connections.Notes.Count == 0)
        {
            return Result.Success(connections);
        }

        var topic = await ResolveTopicAsync(userId, connections.Notes, cancellationToken);
        return Result.Success(connections with { Topic = topic });
    }

    private async Task<string?> ResolveTopicAsync(
        Guid userId,
        IReadOnlyList<RelatedNote> notes,
        CancellationToken cancellationToken)
    {
        var noteIds = notes.Select(n => n.Id).ToList();
        var tagRows = await _noteReadRepository.GetTagsByNoteIdsAsync(userId, noteIds, cancellationToken);

        // Contar cuántas notas distintas llevan cada etiqueta.
        var countByTag = tagRows
            .GroupBy(r => r.TagName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Tag: g.Key, Count: g.Select(r => r.NoteId).Distinct().Count()))
            .Where(t => t.Count >= 2)
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return countByTag.Count > 0 ? countByTag[0].Tag : null;
    }
}
