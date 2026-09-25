from typing import Literal

from fastapi import APIRouter, Depends
from pydantic import BaseModel

from app.core.config import settings
from app.core.security import verify_internal_api_key
from app.embeddings import repository
from app.embeddings.model import embed_text
from app.llm.factory import get_llm_provider
from app.llm.provider import (
    LlmInvalidCredentialsError,
    LlmProvider,
    LlmProviderUnavailableError,
    LlmRateLimitedError,
)

router = APIRouter(prefix="/internal/assistant", dependencies=[Depends(verify_internal_api_key)])

# Cosine similarity cutoff below which a match is treated as "not actually relevant" rather
# than grounding an answer - tunable; deferrable per design.md (doesn't change the spec).
MIN_RELEVANT_SIMILARITY = 0.2

NOTHING_RELEVANT_MESSAGE = "No he encontrado nada relevante sobre eso en tus notas."
INVALID_KEY_MESSAGE = "Tu API key de Groq ya no es válida. Actualízala en Configuración."
RATE_LIMITED_MESSAGE = "Has alcanzado el límite de tu cuota de Groq. Inténtalo de nuevo más tarde."
UNAVAILABLE_MESSAGE = "El asistente no está disponible temporalmente. Inténtalo de nuevo en un momento."

# Sources without a title are shown by a preview of their content instead.
SOURCE_PREVIEW_CHARS = 60

AnswerStatus = Literal["ok", "no_relevant_notes", "invalid_key", "rate_limited", "unavailable"]


class AskRequest(BaseModel):
    user_id: str
    question: str
    # The requesting user's own key, decrypted by the .NET API for this request only - never
    # stored or logged here (byok-groq-and-settings design.md Decision 2).
    groq_api_key: str
    groq_model: str | None = None


class AnswerSource(BaseModel):
    id: str
    title: str


class AskResponse(BaseModel):
    answer: str
    # Kept alongside `sources` for clients deployed before mobile-and-ux-polish.
    source_note_ids: list[str]
    sources: list[AnswerSource] = []
    grounded: bool
    status: AnswerStatus


def _failed(message: str, status: AnswerStatus) -> AskResponse:
    return AskResponse(answer=message, source_note_ids=[], sources=[], grounded=False, status=status)


def _source_title(match: dict) -> str:
    title = (match["title"] or "").strip()
    if title:
        return title
    content = " ".join(match["content"].split())
    return content if len(content) <= SOURCE_PREVIEW_CHARS else content[:SOURCE_PREVIEW_CHARS].rstrip() + "…"


@router.post("/ask", response_model=AskResponse)
async def ask(request: AskRequest, provider: LlmProvider = Depends(get_llm_provider)) -> AskResponse:
    """specs/ai-assistant "Natural-language assistant queries". Always returns 200: a missing
    match or a provider failure is a normal, successful response with `grounded=False`, a typed
    `status` and a clear Spanish message - never an exception bubbling up as a raw error."""
    question_embedding = embed_text(request.question)
    matches = await repository.search_similar(request.user_id, question_embedding)

    relevant = [m for m in matches if m["similarity"] >= MIN_RELEVANT_SIMILARITY]

    if not relevant:
        return _failed(NOTHING_RELEVANT_MESSAGE, "no_relevant_notes")

    context = "\n\n---\n\n".join(f"[{m['title'] or 'Untitled'}]\n{m['content']}" for m in relevant)
    model = request.groq_model or settings.groq_model

    try:
        answer = await provider.generate_answer(request.question, context, request.groq_api_key, model)
    except LlmInvalidCredentialsError:
        return _failed(INVALID_KEY_MESSAGE, "invalid_key")
    except LlmRateLimitedError:
        return _failed(RATE_LIMITED_MESSAGE, "rate_limited")
    except LlmProviderUnavailableError:
        return _failed(UNAVAILABLE_MESSAGE, "unavailable")

    return AskResponse(
        answer=answer,
        source_note_ids=[str(m["id"]) for m in relevant],
        sources=[AnswerSource(id=str(m["id"]), title=_source_title(m)) for m in relevant],
        grounded=True,
        status="ok",
    )
