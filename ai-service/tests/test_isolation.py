"""Task 5.2 - proves specs/ai-assistant's "never cross users" invariant (semantic search and
related-notes) against a real Postgres with pgvector, not mocks: a bug in a repository's WHERE
clause is exactly what a mocked connection would never catch.

Requires a reachable Postgres with the `vector` extension available (e.g. `docker compose up
postgres` from the workspace repo) and SYNAP_AI_DATABASE_URL pointed at it - ideally a scratch
database, since this creates and drops its own `notes`/`note_embeddings` tables. The fixture
creates the `vector` extension itself, so an empty database is enough.
"""

import uuid

import asyncpg
import pytest
import pytest_asyncio

from app.core.config import settings
from app.core.db import close_pool, get_pool, init_pool
from app.embeddings import repository

EMBEDDING_DIMENSIONS = 384


@pytest_asyncio.fixture
async def db_pool():
    # The pool registers the pgvector codec on every connection it opens, which fails on a fresh
    # database without the extension - so create it over a plain connection first.
    connection = await asyncpg.connect(settings.database_url)
    try:
        await connection.execute("CREATE EXTENSION IF NOT EXISTS vector")
    finally:
        await connection.close()

    await init_pool()
    pool = get_pool()

    async with pool.acquire() as connection:
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
        await connection.execute(
            """
            CREATE TABLE IF NOT EXISTS tags (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL,
                name varchar(100) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                UNIQUE (user_id, name)
            )
            """
        )
        await connection.execute(
            """
            CREATE TABLE IF NOT EXISTS note_tags (
                note_id uuid NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
                tag_id uuid NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY (note_id, tag_id)
            )
            """
        )

    yield pool

    async with pool.acquire() as connection:
        await connection.execute("DROP TABLE IF EXISTS note_tags")
        await connection.execute("DROP TABLE IF EXISTS tags")
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


async def _tag(pool, user_id: str, note_id: str, name: str) -> None:
    async with pool.acquire() as connection:
        tag_id = await connection.fetchval("SELECT id FROM tags WHERE user_id = $1 AND name = $2", user_id, name)
        if tag_id is None:
            tag_id = uuid.uuid4()
            await connection.execute("INSERT INTO tags (id, user_id, name) VALUES ($1, $2, $3)", tag_id, user_id, name)
        await connection.execute("INSERT INTO note_tags (note_id, tag_id) VALUES ($1, $2)", note_id, tag_id)


@pytest.mark.asyncio
async def test_get_owned_note_never_returns_another_users_note(db_pool):
    user_a = str(uuid.uuid4())
    user_b = str(uuid.uuid4())
    note_a = await _insert_note_with_embedding(db_pool, user_a, "A's note", [0.3] * EMBEDDING_DIMENSIONS)

    assert (await repository.get_owned_note(note_a, user_a))["content"] == "A's note"
    assert await repository.get_owned_note(note_a, user_b) is None


@pytest.mark.asyncio
async def test_get_notes_with_tag_never_crosses_users(db_pool):
    user_a = str(uuid.uuid4())
    user_b = str(uuid.uuid4())
    embedding = [0.4] * EMBEDDING_DIMENSIONS
    note_a = await _insert_note_with_embedding(db_pool, user_a, "A docker", embedding)
    note_b = await _insert_note_with_embedding(db_pool, user_b, "B docker", embedding)
    await _insert_note_with_embedding(db_pool, user_a, "A untagged", embedding)
    await _tag(db_pool, user_a, note_a, "docker")
    await _tag(db_pool, user_b, note_b, "docker")
    await _tag(db_pool, user_b, note_b, "solo-de-b")

    assert [str(n["id"]) for n in await repository.get_notes_with_tag(user_a, "docker")] == [note_a]
    assert await repository.get_notes_with_tag(user_a, "solo-de-b") == []


@pytest.mark.asyncio
async def test_rank_notes_by_similarity_never_crosses_users(db_pool):
    user_a = str(uuid.uuid4())
    user_b = str(uuid.uuid4())
    near = [1.0] + [0.0] * (EMBEDDING_DIMENSIONS - 1)
    far = [0.0, 1.0] + [0.0] * (EMBEDDING_DIMENSIONS - 2)
    note_far = await _insert_note_with_embedding(db_pool, user_a, "far", far)
    note_near = await _insert_note_with_embedding(db_pool, user_a, "near", near)
    note_b = await _insert_note_with_embedding(db_pool, user_b, "B", near)

    ranked = await repository.rank_notes_by_similarity(user_a, [note_far, note_near, note_b], near)

    assert [str(i) for i in ranked] == [note_near, note_far]
