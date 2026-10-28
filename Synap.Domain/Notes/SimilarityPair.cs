namespace Synap.Domain;

/// <summary>
/// Par (candidata, motivo) devuelto por el ai-service en el rescate del briefing
/// (smart-resurfacing, Decisión 5).
/// </summary>
public sealed record SimilarityPair(Guid CandidateId, Guid AnchorId, double Similarity);
