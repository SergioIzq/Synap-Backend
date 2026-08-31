from fastapi import FastAPI

from app.api import health

# Embeddings (task 4.1), the async embedding pipeline (task 4.3), semantic
# related-notes (task 4.4), RAG retrieval (task 4.5) and the external LLM
# provider (task 4.6) are wired in as the AI Assistant capability is built.
app = FastAPI(title="Synap AI Service")

app.include_router(health.router)
