from contextlib import asynccontextmanager

from fastapi import FastAPI

from app.api import assistant, embeddings, health, notes
from app.core.db import close_pool, init_pool
from app.embeddings.model import get_embedding_model


@asynccontextmanager
async def lifespan(app: FastAPI):
    await init_pool()
    get_embedding_model()  # preload at startup: fail fast, and the first real request isn't slow.
    yield
    await close_pool()


app = FastAPI(title="Synap AI Service", lifespan=lifespan)

app.include_router(health.router)
app.include_router(embeddings.router)
app.include_router(notes.router)
app.include_router(assistant.router)
