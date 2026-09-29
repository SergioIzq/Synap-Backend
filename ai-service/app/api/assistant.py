from typing import Literal

from fastapi import APIRouter, Depends
from pydantic import BaseModel, Field, field_validator, model_validator

from app.core.config import settings
from app.core.security import verify_internal_api_key
from app.embeddings import repository
from app.embeddings.model import embed_text
from app.embeddings.search import hybrid_search
from app.llm.context import NotesContext, build_note_context, build_notes_context, fits
from app.llm.factory import get_llm_provider
from app.llm.provider import (
    ActionsUnavailable,
    AnswerScope,
    HistoryTurn,
    LlmInvalidCredentialsError,
    LlmProvider,
    LlmProviderUnavailableError,
    LlmRateLimitedError,
)

router = APIRouter(prefix="/internal/assistant", dependencies=[Depends(verify_internal_api_key)])

NO_NOTES_CONTEXT = "(No relevant notes were found. If the user asked about their notes, say you found nothing relevant.)"
NOTHING_RELEVANT_MESSAGE = "No he encontrado nada relevante sobre eso en tus notas."
INVALID_KEY_MESSAGE = "Tu API key de Groq ya no es válida. Actualízala en Configuración."
RATE_LIMITED_MESSAGE = "Has alcanzado el límite de tu cuota de Groq. Inténtalo de nuevo más tarde."
UNAVAILABLE_MESSAGE = "El asistente no está disponible temporalmente. Inténtalo de nuevo en un momento."
SCOPE_UNSUPPORTED_MESSAGE = (
    "Todavía no puedo responder sobre enlaces: solo guardo la dirección, no el contenido del artículo."
)

# Short memory for scoped conversations (scoped-assistant design.md Decision 1) - also enforced
# by the .NET API; re-applied here so this service never trusts its caller for the prompt size.
MAX_HISTORY_TURNS = 3
MAX_HISTORY_QUESTION_CHARS = 1_000
MAX_HISTORY_ANSWER_CHARS = 2_000

# The user's memory (specs/assistant-memory) - also enforced by the .NET API; re-applied here so
# this service never trusts its caller for the prompt size.
MAX_MEMORY_ENTRIES = 25
MAX_MEMORY_ENTRY_CHARS = 200

# Sources without a title are shown by a preview of their content instead.
SOURCE_PREVIEW_CHARS = 60

AnswerStatus = Literal["ok", "no_relevant_notes", "invalid_key", "rate_limited", "unavailable", "scope_unsupported"]


class HistoryItem(BaseModel):
    question: str
    answer: str


class AskRequest(BaseModel):
    user_id: str
    question: str
    # The requesting user's own key, decrypted by the .NET API for this request only - never
    # stored or logged here (byok-groq-and-settings design.md Decision 2).
    groq_api_key: str
    groq_model: str | None = None
    # At most one scope; without one the question is about the whole vault, as before.
    scope_note_id: str | None = None
    scope_tag: str | None = None
    history: list[HistoryItem] = Field(default_factory=list)
    memory: list[str] = Field(default_factory=list)
    # Set by the .NET API when this question is answered without actions - see ActionsUnavailable.
    actions_unavailable: ActionsUnavailable | None = None

    @model_validator(mode="after")
    def _one_scope(self) -> "AskRequest":
        if self.scope_note_id and self.scope_tag:
            raise ValueError("scope_note_id and scope_tag are mutually exclusive")
        return self

    @field_validator("history")
    @classmethod
    def _cap_history(cls, history: list[HistoryItem]) -> list[HistoryItem]:
        # Longer histories are trimmed, not rejected (specs/ai-assistant "History limited to recent turns").
        return [
            HistoryItem(
                question=item.question[:MAX_HISTORY_QUESTION_CHARS],
                answer=item.answer[:MAX_HISTORY_ANSWER_CHARS],
            )
            for item in history[-MAX_HISTORY_TURNS:]
        ]


    @field_validator("memory")
    @classmethod
    def _cap_memory(cls, memory: list[str]) -> list[str]:
        entries = (entry.strip()[:MAX_MEMORY_ENTRY_CHARS] for entry in memory[:MAX_MEMORY_ENTRIES])
        return [entry for entry in entries if entry]


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
    # Scoped answers only (left out of unscoped responses, which are unchanged):
    partial_context: bool | None = None
    scope: dict | None = None


def _failed(message: str, status: AnswerStatus) -> AskResponse:
    return AskResponse(answer=message, source_note_ids=[], sources=[], grounded=False, status=status)


def _source_title(match: dict) -> str:
    title = (match["title"] or "").strip()
    if title:
        return title
    content = " ".join(match["content"].split())
    return content if len(content) <= SOURCE_PREVIEW_CHARS else content[:SOURCE_PREVIEW_CHARS].rstrip() + "…"


@router.post("/ask", response_model=AskResponse, response_model_exclude_none=True)
async def ask(request: AskRequest, provider: LlmProvider = Depends(get_llm_provider)) -> AskResponse:
    """specs/ai-assistant "Natural-language assistant queries". Always returns 200: a missing
    match or a provider failure is a normal, successful response with `grounded=False`, a typed
    `status` and a clear Spanish message - never an exception bubbling up as a raw error."""
    if request.scope_note_id:
        return await _ask_about_note(request, provider)
    if request.scope_tag:
        return await _ask_about_tag(request, provider)

    # Hybrid search with its keep rule decides relevance (assistant-agent-foundations design.md
    # Decision 5); nothing kept means an honest "nothing found" without contacting the provider.
    query = _search_query(request)
    relevant = await hybrid_search(request.user_id, query, embed_text(query))

    if not relevant:
        # Without actions the question may be a request ("apúntame…") rather than a question
        # about the notes: the model has to be able to say it can't do it, so it still answers.
        if request.actions_unavailable != "model":
            return _failed(NOTHING_RELEVANT_MESSAGE, "no_relevant_notes")
        return await _generate(request, provider, NO_NOTES_CONTEXT, [])

    context = "\n\n---\n\n".join(f"[{m['title'] or 'Untitled'}]\n{m['content']}" for m in relevant)
    return await _generate(request, provider, context, relevant)


def _search_query(request: AskRequest) -> str:
    """A follow-up like "¿y el segundo?" says little on its own - search with the previous
    question too, without spending an extra LLM call on rewriting it."""
    previous = request.history[-1].question if request.history else ""
    return f"{previous}\n{request.question}".strip()


async def _ask_about_note(request: AskRequest, provider: LlmProvider) -> AskResponse:
    """specs/ai-assistant "Questions scoped to a single note": the note itself, no retrieval."""
    note = await repository.get_owned_note(request.scope_note_id, request.user_id)
    if note is None:
        # The .NET API already answers 404 for this; kept as a safe, non-revealing fallback.
        return _failed(NOTHING_RELEVANT_MESSAGE, "no_relevant_notes")
    if note["note_type"] == "Bookmark":
        return _failed(SCOPE_UNSUPPORTED_MESSAGE, "scope_unsupported")

    notes = build_note_context(note, settings.context_budget_chars)
    scope = AnswerScope(kind="note", label=_source_title(note), partial=notes.partial)
    return await _generate(request, provider, notes.text, notes.sources, scope, notes)


async def _ask_about_tag(request: AskRequest, provider: LlmProvider) -> AskResponse:
    """specs/ai-assistant "Questions scoped to a tag": all its notes when they fit, otherwise the
    ones most similar to the question. No similarity threshold - the user chose the set."""
    tagged = await repository.get_notes_with_tag(request.user_id, request.scope_tag)
    if not tagged:
        # Same answer for an unused tag and for one that only exists in another user's vault.
        return _failed(NOTHING_RELEVANT_MESSAGE, "no_relevant_notes")

    budget = settings.context_budget_chars
    if not fits(tagged, budget):
        embedding = embed_text(_search_query(request))
        order = await repository.rank_notes_by_similarity(request.user_id, [n["id"] for n in tagged], embedding)
        by_id = {n["id"]: n for n in tagged}
        tagged = [by_id[i] for i in order if i in by_id]

    notes = build_notes_context(tagged, budget)
    scope = AnswerScope(kind="tag", label=request.scope_tag, partial=notes.partial)
    return await _generate(request, provider, notes.text, notes.sources, scope, notes)


async def _generate(
    request: AskRequest,
    provider: LlmProvider,
    context: str,
    sources: list[dict],
    scope: AnswerScope | None = None,
    notes: NotesContext | None = None,
) -> AskResponse:
    model = request.groq_model or settings.groq_model
    # Every conversation, global or scoped, has its short history; memory goes with every question.
    extra = {}
    if request.history:
        extra["history"] = [HistoryTurn(h.question, h.answer) for h in request.history]
    if request.memory:
        extra["memory"] = request.memory
    if request.actions_unavailable:
        extra["actions_unavailable"] = request.actions_unavailable
    if scope is not None:
        extra["scope"] = scope

    try:
        answer = await provider.generate_answer(request.question, context, request.groq_api_key, model, **extra)
    except LlmInvalidCredentialsError:
        return _failed(INVALID_KEY_MESSAGE, "invalid_key")
    except LlmRateLimitedError:
        return _failed(RATE_LIMITED_MESSAGE, "rate_limited")
    except LlmProviderUnavailableError:
        return _failed(UNAVAILABLE_MESSAGE, "unavailable")

    response = AskResponse(
        answer=answer,
        source_note_ids=[str(m["id"]) for m in sources],
        sources=[AnswerSource(id=str(m["id"]), title=_source_title(m)) for m in sources],
        grounded=bool(sources),
        status="ok",
    )
    if scope is not None:
        response.partial_context = notes.partial if notes else False
        response.scope = {"note_id": request.scope_note_id} if scope.kind == "note" else {"tag": request.scope_tag}
    return response
