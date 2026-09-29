"""Shared real-Postgres fixture for the repository tests (isolation, hybrid search, reindex).

Requires a reachable Postgres with pgvector (e.g. `docker compose up postgres` from the workspace
repo) and SYNAP_AI_DATABASE_URL pointed at it - ideally a scratch database, since this creates
and drops its own copies of the tables. Extensions and the Spanish full-text configuration are
created here too, so an empty database is enough. The schema mirrors the .NET migrations'
column names and the enum's PascalCase string conversion.
"""

import uuid

import asyncpg
import pytest_asyncio

from app.core.config import settings
from app.core.db import close_pool, get_pool, init_pool
from app.embeddings import repository

EMBEDDING_DIMENSIONS = 384

_SCHEMA = [
    "CREATE EXTENSION IF NOT EXISTS unaccent",
    """
    DO $$ BEGIN
        IF NOT EXISTS (SELECT 1 FROM pg_ts_config WHERE cfgname = 'spanish_unaccent') THEN
            CREATE TEXT SEARCH CONFIGURATION public.spanish_unaccent (COPY = pg_catalog.spanish);
            ALTER TEXT SEARCH CONFIGURATION public.spanish_unaccent
                ALTER MAPPING FOR hword, hword_part, word WITH public.unaccent, pg_catalog.spanish_stem;
        END IF;
    END $$
    """,
    """
    CREATE OR REPLACE FUNCTION public.synap_note_search_vector(title text, content text)
    RETURNS tsvector LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
        SELECT setweight(to_tsvector('public.spanish_unaccent', coalesce(title, '')), 'A')
            || setweight(to_tsvector('public.spanish_unaccent', coalesce(content, '')), 'B')
    $$
    """,
    """
    CREATE TABLE IF NOT EXISTS notes (
        id uuid PRIMARY KEY,
        user_id uuid NOT NULL,
        note_type varchar(20) NOT NULL,
        title varchar(200),
        content text NOT NULL,
        updated_at timestamptz NOT NULL DEFAULT now(),
        created_at timestamptz NOT NULL DEFAULT now(),
        search_vector tsvector GENERATED ALWAYS AS (public.synap_note_search_vector(title, content)) STORED
    )
    """,
    f"""
    CREATE TABLE IF NOT EXISTS note_embeddings (
        note_id uuid PRIMARY KEY REFERENCES notes(id) ON DELETE CASCADE,
        user_id uuid NOT NULL,
        embedding vector({EMBEDDING_DIMENSIONS}) NOT NULL,
        model text NOT NULL DEFAULT 'BAAI/bge-small-en-v1.5',
        updated_at timestamptz NOT NULL DEFAULT now()
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS tags (
        id uuid PRIMARY KEY,
        user_id uuid NOT NULL,
        name varchar(100) NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(),
        UNIQUE (user_id, name)
    )
    """,
    """
    CREATE TABLE IF NOT EXISTS note_tags (
        note_id uuid NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
        tag_id uuid NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
        PRIMARY KEY (note_id, tag_id)
    )
    """,
]


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
        for statement in _SCHEMA:
            await connection.execute(statement)

    yield pool

    async with pool.acquire() as connection:
        await connection.execute("DROP TABLE IF EXISTS note_tags")
        await connection.execute("DROP TABLE IF EXISTS tags")
        await connection.execute("DROP TABLE IF EXISTS note_embeddings")
        await connection.execute("DROP TABLE IF EXISTS notes")

    await close_pool()


async def insert_note(pool, user_id: str, content: str, title: str | None = None) -> str:
    note_id = str(uuid.uuid4())
    async with pool.acquire() as connection:
        await connection.execute(
            "INSERT INTO notes (id, user_id, note_type, title, content) VALUES ($1, $2, 'Text', $3, $4)",
            note_id,
            user_id,
            title,
            content,
        )
    return note_id


async def insert_note_with_embedding(
    pool, user_id: str, content: str, embedding: list[float], model: str | None = None, title: str | None = None
) -> str:
    note_id = await insert_note(pool, user_id, content, title)
    await repository.upsert_embedding(note_id, user_id, embedding, model or settings.embedding_model)
    return note_id
