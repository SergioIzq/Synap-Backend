import asyncio
import contextlib
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app.api import assistant, embeddings, health, llm, notes, search
from app.core.db import close_pool, init_pool
from app.embeddings.model import get_embedding_model
from app.embeddings.reindex import run_reindex


@asynccontextmanager
async def lifespan(app: FastAPI):
    await init_pool()
    get_embedding_model()  # preload at startup: fail fast, and the first real request isn't slow.
    # Embeddings from a previous model are regenerated in the background; requests are served
    # meanwhile (assistant-agent-foundations design.md Decision 5).
    reindex = asyncio.create_task(run_reindex())
    yield
    reindex.cancel()
    with contextlib.suppress(asyncio.CancelledError):
        await reindex
    await close_pool()


app = FastAPI(title="Synap AI Service", lifespan=lifespan)

app.include_router(health.router)
app.include_router(embeddings.router)
app.include_router(notes.router)
app.include_router(assistant.router)
app.include_router(llm.router)
app.include_router(search.router)
