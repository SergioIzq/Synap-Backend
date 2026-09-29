"""assistant-agent-foundations task 3.2 - the startup reindex regenerates every embedding that is
missing or from another model, against a real Postgres (tests/conftest.py)."""

import pytest

from app.core.config import settings
from app.embeddings import reindex
from tests.conftest import EMBEDDING_DIMENSIONS, insert_note, insert_note_with_embedding

OLD_MODEL = "BAAI/bge-small-en-v1.5"
OLD_VECTOR = [0.1] * EMBEDDING_DIMENSIONS
NEW_VECTOR = [0.2] * EMBEDDING_DIMENSIONS


async def _models(pool) -> dict[str, str | None]:
    async with pool.acquire() as connection:
        rows = await connection.fetch(
            "SELECT n.id, e.model FROM notes n LEFT JOIN note_embeddings e ON e.note_id = n.id"
        )
    return {str(r["id"]): r["model"] for r in rows}


@pytest.mark.asyncio
async def test_old_model_and_missing_embeddings_are_regenerated(db_pool, monkeypatch):
    embedded = []
    monkeypatch.setattr(reindex, "embed_texts", lambda texts: embedded.extend(texts) or [NEW_VECTOR] * len(texts))
    old = [await insert_note_with_embedding(db_pool, "00000000-0000-0000-0000-00000000000a", f"old {i}", OLD_VECTOR, OLD_MODEL) for i in range(3)]
    missing = await insert_note(db_pool, "00000000-0000-0000-0000-00000000000b", "sin embedding", title="Título")
    current = await insert_note_with_embedding(db_pool, "00000000-0000-0000-0000-00000000000a", "ya al día", OLD_VECTOR)

    done = await reindex.reindex_all(pause_seconds=0)

    assert done == 4
    models = await _models(db_pool)
    assert all(models[n] == settings.embedding_model for n in [*old, missing, current])
    # The title goes into the text, and an up-to-date note is not embedded again.
    assert "Título\n\nsin embedding" in embedded
    assert "ya al día" not in embedded


@pytest.mark.asyncio
async def test_one_failing_note_does_not_stop_the_rest(db_pool, monkeypatch):
    def fake_embed(texts):
        if any("ROTA" in t for t in texts):
            raise RuntimeError("model failure")
        return [NEW_VECTOR] * len(texts)

    monkeypatch.setattr(reindex, "embed_texts", fake_embed)
    user = "00000000-0000-0000-0000-00000000000a"
    good = [await insert_note_with_embedding(db_pool, user, f"bien {i}", OLD_VECTOR, OLD_MODEL) for i in range(2)]
    bad = await insert_note_with_embedding(db_pool, user, "nota ROTA", OLD_VECTOR, OLD_MODEL)

    done = await reindex.reindex_all(pause_seconds=0)

    assert done == 2
    models = await _models(db_pool)
    assert all(models[n] == settings.embedding_model for n in good)
    assert models[bad] == OLD_MODEL


@pytest.mark.asyncio
async def test_more_notes_than_one_batch(db_pool, monkeypatch):
    monkeypatch.setattr(reindex, "embed_texts", lambda texts: [NEW_VECTOR] * len(texts))
    for i in range(reindex.BATCH_SIZE + 5):
        await insert_note(db_pool, "00000000-0000-0000-0000-00000000000a", f"nota {i}")

    assert await reindex.reindex_all(pause_seconds=0) == reindex.BATCH_SIZE + 5
    assert await reindex.reindex_all(pause_seconds=0) == 0
