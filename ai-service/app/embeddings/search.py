"""Hybrid retrieval: by meaning (pgvector) and by exact words (Spanish full text), merged with
Reciprocal Rank Fusion (assistant-agent-foundations design.md Decision 5 and "Spike results").

The keep rule is what makes "no he encontrado nada" possible: a note only counts as relevant
when it is close enough in meaning, or shares enough of the query's words. One shared common
word ("configurar") is not enough, but a single-word query ("NU1605") needs only that word.
"""

from app.core.config import settings
from app.core.db import get_pool

CANDIDATES = 20
RRF_K = 60
MAX_LEXEMES_REQUIRED = 2

# Lexemes of the query OR-ed together: websearch_to_tsquery would AND them, and a whole question
# almost never shares every word with a note.
_OR_QUERY = "replace(plainto_tsquery('public.spanish_unaccent', $2)::text, '&', '|')::tsquery"

_VECTOR_SQL = """
    SELECT e.note_id AS id, 1 - (e.embedding <=> $2) AS similarity
    FROM note_embeddings e
    WHERE e.user_id = $1 AND e.model = $3
    ORDER BY e.embedding <=> $2
    LIMIT $4
"""

_FULL_TEXT_SQL = f"""
    WITH q AS (
        SELECT {_OR_QUERY} AS query,
               tsvector_to_array(to_tsvector('public.spanish_unaccent', $2)) AS lexemes
    )
    SELECT n.id,
           (SELECT count(*) FROM unnest(q.lexemes) l WHERE l = ANY(tsvector_to_array(n.search_vector))) AS matched
    FROM notes n, q
    WHERE n.user_id = $1 AND n.search_vector @@ q.query
    ORDER BY ts_rank_cd(n.search_vector, q.query) DESC
    LIMIT $3
"""

_LEXEME_COUNT_SQL = "SELECT coalesce(array_length(tsvector_to_array(to_tsvector('public.spanish_unaccent', $1)), 1), 0)"

_NOTES_SQL = """
    SELECT n.id, n.title, n.content, n.note_type,
           coalesce(array_agg(t.name ORDER BY t.name) FILTER (WHERE t.name IS NOT NULL), '{}') AS tags
    FROM notes n
    LEFT JOIN note_tags nt ON nt.note_id = n.id
    LEFT JOIN tags t ON t.id = nt.tag_id AND t.user_id = $1
    WHERE n.user_id = $1 AND n.id = ANY($2::uuid[])
    GROUP BY n.id
"""


async def hybrid_search(user_id: str, query_text: str, query_embedding: list[float], limit: int = 5) -> list[dict]:
    """The user's most relevant notes for the query, best first, each with `similarity` (None
    when it was only found by words), `matched_lexemes` and `tags`. Empty when nothing clears
    the keep rule. Every query filters by `user_id`."""
    pool = get_pool()
    async with pool.acquire() as connection:
        vector_rows = await connection.fetch(_VECTOR_SQL, user_id, query_embedding, settings.embedding_model, CANDIDATES)
        query_lexemes = await connection.fetchval(_LEXEME_COUNT_SQL, query_text)
        # A query of only stop words has no lexemes; to_tsquery would match nothing anyway.
        text_rows = await connection.fetch(_FULL_TEXT_SQL, user_id, query_text, CANDIDATES) if query_lexemes else []

        similarity = {r["id"]: float(r["similarity"]) for r in vector_rows}
        matched = {r["id"]: r["matched"] for r in text_rows}
        required = min(MAX_LEXEMES_REQUIRED, query_lexemes)

        scores: dict = {}
        for ranking in ([r["id"] for r in vector_rows], [r["id"] for r in text_rows]):
            for rank, note_id in enumerate(ranking, start=1):
                scores[note_id] = scores.get(note_id, 0.0) + 1.0 / (RRF_K + rank)

        kept = [
            note_id
            for note_id in sorted(scores, key=scores.get, reverse=True)
            if similarity.get(note_id, 0.0) >= settings.min_relevant_similarity
            or (required and matched.get(note_id, 0) >= required)
        ][:limit]
        if not kept:
            return []

        notes = {r["id"]: dict(r) for r in await connection.fetch(_NOTES_SQL, user_id, kept)}

    results = []
    for note_id in kept:
        if note_id in notes:
            note = notes[note_id]
            note["tags"] = list(note["tags"])
            note["similarity"] = similarity.get(note_id)
            note["matched_lexemes"] = matched.get(note_id, 0)
            results.append(note)
    return results
