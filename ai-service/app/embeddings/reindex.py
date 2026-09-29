"""Background reindex after an embedding model change (assistant-agent-foundations design.md
Decision 5): every note whose embedding is missing or was produced by another model gets a new
one from the configured model. Runs as a low-priority task started by main.py's lifespan, so the
service answers requests throughout - searches meanwhile only compare vectors of the current
model, and full-text search still finds the notes not reindexed yet.
"""

import asyncio
import logging

from app.core.config import settings
from app.embeddings import repository
from app.embeddings.model import embed_texts, note_text

logger = logging.getLogger(__name__)

BATCH_SIZE = 32
# Breathing room between batches: the VPS is shared with other sites.
PAUSE_SECONDS = 0.5


async def reindex_all(pause_seconds: float = PAUSE_SECONDS) -> int:
    """Returns how many notes were (re)embedded. A note that fails is logged and skipped for the
    rest of this run; the next start retries it."""
    model = settings.embedding_model
    failed: list = []
    done = 0

    while True:
        notes = await repository.notes_needing_embedding(model, BATCH_SIZE, failed)
        if not notes:
            break

        vectors = await _embed(notes, failed)
        for note, vector in vectors:
            try:
                await repository.upsert_embedding(str(note["id"]), str(note["user_id"]), vector, model)
                done += 1
            except Exception:
                logger.exception("Reindex: could not store the embedding of note %s", note["id"])
                failed.append(note["id"])

        logger.info("Reindex with %s: %d notes done, %d failed", model, done, len(failed))
        await asyncio.sleep(pause_seconds)

    if done or failed:
        logger.info("Reindex with %s finished: %d notes embedded, %d failed", model, done, len(failed))
    return done


async def _embed(notes: list[dict], failed: list) -> list[tuple[dict, list[float]]]:
    """The whole batch in one call; if that fails, note by note to isolate the bad one.
    Embedding is CPU-bound, so it runs off the event loop."""
    texts = [note_text(n["title"], n["content"]) for n in notes]
    try:
        return list(zip(notes, await asyncio.to_thread(embed_texts, texts)))
    except Exception:
        results = []
        for note, text in zip(notes, texts):
            try:
                results.append((note, (await asyncio.to_thread(embed_texts, [text]))[0]))
            except Exception:
                logger.exception("Reindex: could not embed note %s", note["id"])
                failed.append(note["id"])
        return results


async def run_reindex() -> None:
    """Entry point for the startup task: an unexpected failure (e.g. the database going away) is
    logged instead of dying silently; the next start picks up where this one stopped."""
    try:
        await reindex_all()
    except asyncio.CancelledError:
        raise
    except Exception:
        logger.exception("Reindex aborted; it will resume on the next start")
