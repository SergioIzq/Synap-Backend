from fastapi import APIRouter, Depends
from pydantic import BaseModel

from app.core.security import verify_internal_api_key
from app.embeddings import repository
from app.embeddings.model import embed_text
from app.llm.factory import get_llm_provider
from app.llm.provider import LlmProviderUnavailableError

router = APIRouter(prefix="/internal/assistant", dependencies=[Depends(verify_internal_api_key)])

# Cosine similarity cutoff below which a match is treated as "not actually relevant" rather
# than grounding an answer - tunable; deferrable per design.md (doesn't change the spec).
MIN_RELEVANT_SIMILARITY = 0.2

NOTHING_RELEVANT_MESSAGE = "I couldn't find anything relevant to that in your notes yet."
UNAVAILABLE_MESSAGE = "The assistant is temporarily unavailable - please try again shortly."


class AskRequest(BaseModel):
    user_id: str
    question: str


class AskResponse(BaseModel):
    answer: str
    source_note_ids: list[str]
    grounded: bool


@router.post("/ask", response_model=AskResponse)
async def ask(request: AskRequest) -> AskResponse:
    """specs/ai-assistant "Natural-language assistant queries". Always returns 200: a missing
    match or an unavailable LLM provider is a normal, successful response with `grounded=False`
    and a clear message - never an exception bubbling up as a raw error."""
    question_embedding = embed_text(request.question)
    matches = await repository.search_similar(request.user_id, question_embedding)

    relevant = [m for m in matches if m["similarity"] >= MIN_RELEVANT_SIMILARITY]

    if not relevant:
        return AskResponse(answer=NOTHING_RELEVANT_MESSAGE, source_note_ids=[], grounded=False)

    context = "\n\n---\n\n".join(f"[{m['title'] or 'Untitled'}]\n{m['content']}" for m in relevant)

    try:
        answer = await get_llm_provider().generate_answer(request.question, context)
    except LlmProviderUnavailableError:
        return AskResponse(answer=UNAVAILABLE_MESSAGE, source_note_ids=[], grounded=False)

    return AskResponse(
        answer=answer,
        source_note_ids=[str(m["id"]) for m in relevant],
        grounded=True,
    )
