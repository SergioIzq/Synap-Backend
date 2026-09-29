"""pgvector queries. The Python service owns note_embeddings entirely (design.md Decision 1);
`notes` is read-only from here, joined in for the content needed to build a RAG prompt or show
a related note - never written to (the .NET API owns writes to the relational schema).

Vectors are only compared with vectors of the configured embedding model (`model` column):
during a reindex, rows from the previous model are ignored rather than compared meaninglessly
(assistant-agent-foundations design.md Decision 5).

Every query filters by user_id explicitly - the per-user isolation invariant applies here just
as much as on the .NET side (specs/ai-assistant "Related notes never cross users" /
"Assistant queries never cross users").
"""

from app.core.config import settings
from app.core.db import get_pool

DEFAULT_LIMIT = 5


async def upsert_embedding(note_id: str, user_id: str, embedding: list[float], model: str) -> None:
    """`model` is the embedding model that produced the vector, so rows from any other model can
    be found and reindexed (assistant-agent-foundations design.md Decision 5)."""
    pool = get_pool()
    async with pool.acquire() as connection:
        await connection.execute(
            """
            INSERT INTO note_embeddings (note_id, user_id, embedding, model, updated_at)
            VALUES ($1, $2, $3, $4, now())
            ON CONFLICT (note_id) DO UPDATE
                SET embedding = EXCLUDED.embedding,
                    user_id = EXCLUDED.user_id,
                    model = EXCLUDED.model,
                    updated_at = now()
            """,
            note_id,
            user_id,
            embedding,
            model,
        )


async def find_related_notes(note_id: str, user_id: str, limit: int = DEFAULT_LIMIT) -> list[dict]:
    pool = get_pool()
    async with pool.acquire() as connection:
        rows = await connection.fetch(
            """
            SELECT n.id, n.title, n.content, n.note_type,
                   1 - (target.embedding <=> other.embedding) AS similarity
            FROM note_embeddings AS target
            JOIN note_embeddings AS other
                ON other.user_id = target.user_id AND other.note_id != target.note_id AND other.model = $4
            JOIN notes n ON n.id = other.note_id
            WHERE target.note_id = $1 AND target.user_id = $2 AND target.model = $4
            ORDER BY target.embedding <=> other.embedding
            LIMIT $3
            """,
            note_id,
            user_id,
            limit,
            settings.embedding_model,
        )
        return [dict(row) for row in rows]


async def search_similar(user_id: str, query_embedding: list[float], limit: int = DEFAULT_LIMIT) -> list[dict]:
    pool = get_pool()
    async with pool.acquire() as connection:
        rows = await connection.fetch(
            """
            SELECT n.id, n.title, n.content, n.note_type,
                   1 - (e.embedding <=> $2) AS similarity
            FROM note_embeddings e
            JOIN notes n ON n.id = e.note_id
            WHERE e.user_id = $1 AND e.model = $4
            ORDER BY e.embedding <=> $2
            LIMIT $3
            """,
            user_id,
            query_embedding,
            limit,
            settings.embedding_model,
        )
        return [dict(row) for row in rows]


# --- Scoped questions (scoped-assistant) ---------------------------------------------------
# The .NET API has already checked the note's ownership, but every query here filters by
# user_id again: a scope must never reach another user's vault.


async def get_owned_note(note_id: str, user_id: str) -> dict | None:
    pool = get_pool()
    async with pool.acquire() as connection:
        row = await connection.fetchrow(
            """
            SELECT n.id, n.title, n.content, n.note_type
            FROM notes n
            WHERE n.id = $1 AND n.user_id = $2
            """,
            note_id,
            user_id,
        )
        return dict(row) if row else None


async def get_notes_with_tag(user_id: str, tag: str) -> list[dict]:
    """Newest first. The tag is matched by exact name within the user's own tags - a tag that
    only exists in another user's vault simply matches nothing."""
    pool = get_pool()
    async with pool.acquire() as connection:
        rows = await connection.fetch(
            """
            SELECT n.id, n.title, n.content, n.note_type
            FROM notes n
            JOIN note_tags nt ON nt.note_id = n.id
            JOIN tags t ON t.id = nt.tag_id
            WHERE n.user_id = $1 AND t.user_id = $1 AND t.name = $2
            ORDER BY n.created_at DESC
            """,
            user_id,
            tag,
        )
        return [dict(row) for row in rows]


async def rank_notes_by_similarity(user_id: str, note_ids: list, query_embedding: list[float]) -> list:
    """Ids of `note_ids` ordered by similarity to the query, most similar first. Notes without
    an embedding yet (just created) come last rather than being dropped."""
    pool = get_pool()
    async with pool.acquire() as connection:
        rows = await connection.fetch(
            """
            SELECT n.id
            FROM notes n
            LEFT JOIN note_embeddings e ON e.note_id = n.id AND e.user_id = $1 AND e.model = $4
            WHERE n.user_id = $1 AND n.id = ANY($2::uuid[])
            ORDER BY e.embedding <=> $3 NULLS LAST, n.created_at DESC
            """,
            user_id,
            note_ids,
            query_embedding,
            settings.embedding_model,
        )
        return [row["id"] for row in rows]


# --- Reindexing (assistant-agent-foundations design.md Decision 5) ---------------------------
# Reads `notes` (read-only, as everywhere in this service) and writes only note_embeddings.


async def notes_needing_embedding(model: str, limit: int, skip_ids: list) -> list[dict]:
    """Notes with no embedding yet, or one produced by a different model. `skip_ids` are notes
    that already failed in this run, so one bad note can't make the reindex loop forever."""
    pool = get_pool()
    async with pool.acquire() as connection:
        rows = await connection.fetch(
            """
            SELECT n.id, n.user_id, n.title, n.content
            FROM notes n
            LEFT JOIN note_embeddings e ON e.note_id = n.id
            WHERE (e.note_id IS NULL OR e.model <> $1) AND n.id <> ALL($3::uuid[])
            ORDER BY n.id
            LIMIT $2
            """,
            model,
            limit,
            skip_ids,
        )
        return [dict(row) for row in rows]
