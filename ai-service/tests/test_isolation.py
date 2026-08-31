"""Task 5.2 - proves specs/ai-assistant's "never cross users" invariant (semantic search and
related-notes) against a real Postgres with pgvector, not mocks: a bug in a repository's WHERE
clause is exactly what a mocked connection would never catch.

Requires a reachable Postgres with the `vector` extension available (e.g. `docker compose up
postgres` from the workspace repo) and SYNAP_AI_DATABASE_URL pointed at it - ideally a scratch
database, since this creates and drops its own `notes`/`note_embeddings` tables.

Could not be run at all in the original development environment (no Python interpreter
installed there) - written carefully against the real schema (matching the .NET migrations'
column names and the enum's PascalCase string conversion), but genuinely unverified until run
somewhere with Python and a real Postgres.
"""

import uuid

import pytest
import pytest_asyncio

from app.core.db import close_pool, get_pool, init_pool
from app.embeddings import repository

EMBEDDING_DIMENSIONS = 384


@pytest_asyncio.fixture
async def db_pool():
    await init_pool()
    pool = get_pool()

    async with pool.acquire() as connection:
        await connection.execute("CREATE EXTENSION IF NOT EXISTS vector")
        await connection.execute(
            """
            CREATE TABLE IF NOT EXISTS notes (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL,
                note_type varchar(20) NOT NULL,
                title varchar(200),
                content text NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                created_at timestamptz NOT NULL DEFAULT now()
            )
            """
        )
        await connection.execute(
            f"""
            CREATE TABLE IF NOT EXISTS note_embeddings (
                note_id uuid PRIMARY KEY REFERENCES notes(id) ON DELETE CASCADE,
                user_id uuid NOT NULL,
                embedding vector({EMBEDDING_DIMENSIONS}) NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now()
            )
            """
        )

    yield pool

    async with pool.acquire() as connection:
        await connection.execute("DROP TABLE IF EXISTS note_embeddings")
        await connection.execute("DROP TABLE IF EXISTS notes")

    await close_pool()


async def _insert_note_with_embedding(pool, user_id: str, content: str, embedding: list[float]) -> str:
    note_id = str(uuid.uuid4())
    async with pool.acquire() as connection:
        await connection.execute(
            "INSERT INTO notes (id, user_id, note_type, title, content) VALUES ($1, $2, 'Text', NULL, $3)",
            note_id,
            user_id,
            content,
        )
    await repository.upsert_embedding(note_id, user_id, embedding)
    return note_id


@pytest.mark.asyncio
async def test_search_similar_never_returns_another_users_note(db_pool):
    user_a = str(uuid.uuid4())
    user_b = str(uuid.uuid4())
    identical_embedding = [0.1] * EMBEDDING_DIMENSIONS

    await _insert_note_with_embedding(db_pool, user_a, "User A's note", identical_embedding)
    note_b_id = await _insert_note_with_embedding(db_pool, user_b, "User B's note", identical_embedding)

    results = await repository.search_similar(user_a, identical_embedding, limit=10)

    assert all(str(row["id"]) != note_b_id for row in results)


@pytest.mark.asyncio
async def test_find_related_notes_never_crosses_users(db_pool):
    user_a = str(uuid.uuid4())
    user_b = str(uuid.uuid4())
    identical_embedding = [0.2] * EMBEDDING_DIMENSIONS

    note_a1 = await _insert_note_with_embedding(db_pool, user_a, "A's first note", identical_embedding)
    await _insert_note_with_embedding(db_pool, user_a, "A's second note", identical_embedding)
    note_b = await _insert_note_with_embedding(db_pool, user_b, "B's note", identical_embedding)

    related = await repository.find_related_notes(note_a1, user_a, limit=10)

    assert all(str(row["id"]) != note_b for row in related)
