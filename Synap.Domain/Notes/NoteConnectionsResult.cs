namespace Synap.Domain;

/// <summary>
/// Resultado de la consulta de conexiones de una nota (smart-resurfacing, Decisión 1).
/// </summary>
public sealed record NoteConnectionsResult(NoteConnectionStatus Status, IReadOnlyList<RelatedNote> Notes, string? Topic = null);

public enum NoteConnectionStatus
{
    /// <summary>La nota tiene embedding del modelo actual; las conexiones son definitivas.</summary>
    Ready,
    /// <summary>La nota aún no tiene embedding del modelo actual; el análisis no ha terminado.</summary>
    Pending,
}
