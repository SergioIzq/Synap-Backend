"""assistant-agent-foundations task 3.3 - hybrid search against a real Postgres (tests/conftest.py):
exact words find a note whatever its vector says, one common shared word is not enough, only
current-model vectors count, and another user's notes are never returned."""

import pytest

from app.embeddings.search import hybrid_search
from tests.conftest import EMBEDDING_DIMENSIONS, insert_note_with_embedding

USER_A = "00000000-0000-0000-0000-00000000000a"
USER_B = "00000000-0000-0000-0000-00000000000b"


def axis(i: int) -> list[float]:
    vector = [0.0] * EMBEDDING_DIMENSIONS
    vector[i] = 1.0
    return vector


QUERY = axis(0)
CLOSE = axis(0)  # similarity 1
FAR = axis(1)  # similarity 0


@pytest.mark.asyncio
async def test_exact_term_found_despite_low_similarity(db_pool):
    note = await insert_note_with_embedding(db_pool, USER_A, "dotnet restore fallaba con NU1605", FAR, title="Error de paquetes")

    results = await hybrid_search(USER_A, "NU1605", QUERY)

    assert [str(r["id"]) for r in results] == [note]
    assert results[0]["matched_lexemes"] == 1


@pytest.mark.asyncio
async def test_similar_meaning_found_without_shared_words(db_pool):
    note = await insert_note_with_embedding(db_pool, USER_A, "lentejas pardinas con chorizo", CLOSE)

    results = await hybrid_search(USER_A, "¿qué lleva el guiso de legumbres?", QUERY)

    assert [str(r["id"]) for r in results] == [note]


@pytest.mark.asyncio
async def test_one_common_shared_word_is_not_enough(db_pool):
    await insert_note_with_embedding(db_pool, USER_A, "Configuré nginx como proxy inverso", FAR)

    results = await hybrid_search(USER_A, "¿Cómo configuro Redis como caché?", QUERY)

    assert results == []


@pytest.mark.asyncio
async def test_two_shared_words_are_enough(db_pool):
    note = await insert_note_with_embedding(db_pool, USER_A, "Configuré nginx como proxy inverso", FAR)

    results = await hybrid_search(USER_A, "¿Cómo configuré el proxy?", QUERY)

    assert [str(r["id"]) for r in results] == [note]


@pytest.mark.asyncio
async def test_previous_model_vectors_are_ignored(db_pool):
    await insert_note_with_embedding(db_pool, USER_A, "texto sin relación", CLOSE, model="BAAI/bge-small-en-v1.5")

    assert await hybrid_search(USER_A, "pregunta cualquiera", QUERY) == []


@pytest.mark.asyncio
async def test_never_returns_another_users_note(db_pool):
    mine = await insert_note_with_embedding(db_pool, USER_A, "NU1605 en mi proyecto", CLOSE)
    await insert_note_with_embedding(db_pool, USER_B, "NU1605 en su proyecto", CLOSE)

    results = await hybrid_search(USER_A, "NU1605 proyecto", QUERY, limit=10)

    assert [str(r["id"]) for r in results] == [mine]


@pytest.mark.asyncio
async def test_results_carry_tags_and_are_limited(db_pool):
    ids = [await insert_note_with_embedding(db_pool, USER_A, f"nota docker {i}", CLOSE) for i in range(4)]
    async with db_pool.acquire() as connection:
        await connection.execute("INSERT INTO tags (id, user_id, name) VALUES ('10000000-0000-0000-0000-000000000001', $1, 'docker')", USER_A)
        await connection.execute("INSERT INTO note_tags (note_id, tag_id) VALUES ($1, '10000000-0000-0000-0000-000000000001')", ids[0])

    results = await hybrid_search(USER_A, "docker", QUERY, limit=2)

    assert len(results) == 2
    tagged = await hybrid_search(USER_A, "docker", QUERY, limit=10)
    assert next(r for r in tagged if str(r["id"]) == ids[0])["tags"] == ["docker"]
