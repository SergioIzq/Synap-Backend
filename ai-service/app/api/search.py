from fastapi import APIRouter, Depends
from pydantic import BaseModel, Field

from app.core.security import verify_internal_api_key
from app.embeddings.model import embed_text
from app.embeddings.search import hybrid_search

router = APIRouter(prefix="/internal/search", dependencies=[Depends(verify_internal_api_key)])

# What the assistant's search_notes tool gets back per note (assistant-agent-foundations
# design.md Decision 2): enough to decide whether to read it, not the whole note.
SNIPPET_CHARS = 400
MAX_LIMIT = 8


class SearchRequest(BaseModel):
    user_id: str
    query: str = Field(min_length=1)
    limit: int = Field(default=5, ge=1, le=MAX_LIMIT)


class SearchResult(BaseModel):
    id: str
    title: str | None
    type: str
    tags: list[str]
    snippet: str


def snippet(content: str) -> str:
    text = " ".join(content.split())
    return text if len(text) <= SNIPPET_CHARS else text[:SNIPPET_CHARS].rstrip() + "…"


@router.post("", response_model=list[SearchResult])
async def search(request: SearchRequest) -> list[SearchResult]:
    """Hybrid search over the requesting user's own notes - the only vault it can reach, since
    every query filters by `user_id` (specs/ai-assistant "Assistant actions never cross users")."""
    notes = await hybrid_search(request.user_id, request.query, embed_text(request.query), request.limit)
    return [
        SearchResult(id=str(n["id"]), title=n["title"], type=n["note_type"], tags=n["tags"], snippet=snippet(n["content"]))
        for n in notes
    ]
