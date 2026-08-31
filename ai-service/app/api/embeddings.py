from fastapi import APIRouter, Depends
from pydantic import BaseModel

from app.core.security import verify_internal_api_key
from app.embeddings import repository
from app.embeddings.model import embed_text

router = APIRouter(prefix="/internal/embeddings", dependencies=[Depends(verify_internal_api_key)])


class GenerateEmbeddingRequest(BaseModel):
    note_id: str
    user_id: str
    content: str


@router.post("/generate")
async def generate_embedding(request: GenerateEmbeddingRequest) -> dict[str, str]:
    """Called by the .NET API's background job on note create/edit (specs/ai-assistant
    "Embedding generation"/"Embedding refreshed on edit") - never called synchronously from a
    user-facing request."""
    embedding = embed_text(request.content)
    await repository.upsert_embedding(request.note_id, request.user_id, embedding)
    return {"status": "ok"}
