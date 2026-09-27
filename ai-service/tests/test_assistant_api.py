"""byok-groq-and-settings tasks 1.3/1.4 - /internal/assistant/ask and /internal/llm/models return
a typed `status` for every outcome, with Spanish messages, using a fake provider (no network)."""

import pytest
from fastapi.testclient import TestClient

from app.api import assistant as assistant_api
from app.core.config import settings
from app.llm.factory import get_llm_provider
from app.llm.provider import (
    LlmInvalidCredentialsError,
    LlmProvider,
    LlmProviderUnavailableError,
    LlmRateLimitedError,
)
from main import app

HEADERS = {"X-Internal-Api-Key": settings.internal_api_key}
ASK_BODY = {"user_id": "u1", "question": "¿cómo lo arreglé?", "groq_api_key": "gsk_x"}


class FakeProvider(LlmProvider):
    def __init__(self, error: Exception | None = None) -> None:
        self.error = error
        self.calls: list[tuple[str, str]] = []

    async def generate_answer(self, question, context, api_key, model, **scoped):
        self.calls.append((api_key, model))
        self.last = {"question": question, "context": context, **scoped}
        if self.error:
            raise self.error
        return "Lo arreglaste reiniciando."

    async def list_models(self, api_key):
        if self.error:
            raise self.error
        return ["model-a", "model-b"]


@pytest.fixture
def client(monkeypatch):
    monkeypatch.setattr(assistant_api, "embed_text", lambda text: [0.0])

    async def fake_search(user_id, embedding):
        return [{"id": "n1", "title": "Nota", "content": "reinicia", "similarity": 0.9}]

    monkeypatch.setattr(assistant_api.repository, "search_similar", fake_search)
    # Not a context manager on purpose: skips the lifespan (DB pool + embedding model preload).
    yield TestClient(app)
    app.dependency_overrides.clear()


def _use(provider: FakeProvider) -> FakeProvider:
    app.dependency_overrides[get_llm_provider] = lambda: provider
    return provider


def test_ask_ok_uses_user_key_and_default_model(client):
    provider = _use(FakeProvider())

    body = client.post("/internal/assistant/ask", json=ASK_BODY, headers=HEADERS).json()

    assert body["status"] == "ok"
    assert body["grounded"] is True
    assert body["source_note_ids"] == ["n1"]
    assert body["sources"] == [{"id": "n1", "title": "Nota"}]
    assert provider.calls == [("gsk_x", settings.groq_model)]


def test_ask_untitled_source_uses_content_preview(client, monkeypatch):
    _use(FakeProvider())
    long_content = "Reinicia   el servidor " + "x" * 100

    async def untitled(user_id, embedding):
        return [
            {"id": "n1", "title": None, "content": long_content, "similarity": 0.9},
            {"id": "n2", "title": "  ", "content": "corto", "similarity": 0.8},
        ]

    monkeypatch.setattr(assistant_api.repository, "search_similar", untitled)

    sources = client.post("/internal/assistant/ask", json=ASK_BODY, headers=HEADERS).json()["sources"]

    assert sources[0]["title"].startswith("Reinicia el servidor x")
    assert sources[0]["title"].endswith("…")
    assert len(sources[0]["title"]) == assistant_api.SOURCE_PREVIEW_CHARS + 1
    assert sources[1] == {"id": "n2", "title": "corto"}


def test_ask_uses_chosen_model(client):
    provider = _use(FakeProvider())

    client.post("/internal/assistant/ask", json={**ASK_BODY, "groq_model": "model-b"}, headers=HEADERS)

    assert provider.calls == [("gsk_x", "model-b")]


def test_ask_no_relevant_notes(client, monkeypatch):
    _use(FakeProvider())

    async def no_matches(user_id, embedding):
        return []

    monkeypatch.setattr(assistant_api.repository, "search_similar", no_matches)

    body = client.post("/internal/assistant/ask", json=ASK_BODY, headers=HEADERS).json()

    assert body["status"] == "no_relevant_notes"
    assert body["answer"] == assistant_api.NOTHING_RELEVANT_MESSAGE


@pytest.mark.parametrize(
    ("error", "status", "message"),
    [
        (LlmInvalidCredentialsError("x"), "invalid_key", assistant_api.INVALID_KEY_MESSAGE),
        (LlmRateLimitedError("x"), "rate_limited", assistant_api.RATE_LIMITED_MESSAGE),
        (LlmProviderUnavailableError("x"), "unavailable", assistant_api.UNAVAILABLE_MESSAGE),
    ],
)
def test_ask_provider_failures(client, error, status, message):
    _use(FakeProvider(error))

    response = client.post("/internal/assistant/ask", json=ASK_BODY, headers=HEADERS)

    assert response.status_code == 200
    body = response.json()
    assert body == {"answer": message, "source_note_ids": [], "sources": [], "grounded": False, "status": status}


def test_ask_requires_user_key(client):
    _use(FakeProvider())

    body = {k: v for k, v in ASK_BODY.items() if k != "groq_api_key"}

    assert client.post("/internal/assistant/ask", json=body, headers=HEADERS).status_code == 422


def test_models_ok(client):
    _use(FakeProvider())

    body = client.post("/internal/llm/models", json={"api_key": "gsk_x"}, headers=HEADERS).json()

    assert body == {"status": "ok", "models": ["model-a", "model-b"]}


@pytest.mark.parametrize(
    ("error", "status"),
    [
        (LlmInvalidCredentialsError("x"), "invalid_key"),
        (LlmRateLimitedError("x"), "rate_limited"),
        (LlmProviderUnavailableError("x"), "unavailable"),
    ],
)
def test_models_failures(client, error, status):
    _use(FakeProvider(error))

    body = client.post("/internal/llm/models", json={"api_key": "gsk_x"}, headers=HEADERS).json()

    assert body == {"status": status, "models": []}


def test_models_requires_internal_key(client):
    _use(FakeProvider())

    response = client.post("/internal/llm/models", json={"api_key": "gsk_x"}, headers={"X-Internal-Api-Key": "wrong"})

    assert response.status_code == 401


# --- scoped-assistant task 1.3 ------------------------------------------------------------


NOTE_ID = "11111111-1111-1111-1111-111111111111"


def _note(id_, content, note_type="Text", title="Nota"):
    return {"id": id_, "title": title, "content": content, "note_type": note_type}


@pytest.fixture
def scoped(client, monkeypatch):
    """Records which repository calls a scoped question makes - the global search must not run."""
    seen = {"search_similar": 0, "embedded": []}

    async def search_similar(user_id, embedding):
        seen["search_similar"] += 1
        return []

    monkeypatch.setattr(assistant_api.repository, "search_similar", search_similar)
    monkeypatch.setattr(assistant_api, "embed_text", lambda text: seen["embedded"].append(text) or [0.0])
    return seen


def test_note_scope_answers_from_that_note_only(client, scoped, monkeypatch):
    provider = _use(FakeProvider())

    async def get_owned_note(note_id, user_id):
        assert (note_id, user_id) == (NOTE_ID, "u1")
        return _note(NOTE_ID, "Reinicia el contenedor de la API.")

    monkeypatch.setattr(assistant_api.repository, "get_owned_note", get_owned_note)

    body = client.post(
        "/internal/assistant/ask", json={**ASK_BODY, "scope_note_id": NOTE_ID}, headers=HEADERS
    ).json()

    assert body["status"] == "ok"
    assert body["sources"] == [{"id": NOTE_ID, "title": "Nota"}]
    assert body["partial_context"] is False
    assert body["scope"] == {"note_id": NOTE_ID}
    assert "Reinicia el contenedor" in provider.last["context"]
    assert provider.last["scope"].kind == "note"
    assert scoped == {"search_similar": 0, "embedded": []}


def test_note_scope_long_note_is_partial(client, scoped, monkeypatch):
    provider = _use(FakeProvider())
    monkeypatch.setattr(assistant_api.settings, "context_budget_chars", 500)

    async def get_owned_note(note_id, user_id):
        return _note(NOTE_ID, "línea\n" * 1_000)

    monkeypatch.setattr(assistant_api.repository, "get_owned_note", get_owned_note)

    body = client.post(
        "/internal/assistant/ask", json={**ASK_BODY, "scope_note_id": NOTE_ID}, headers=HEADERS
    ).json()

    assert body["partial_context"] is True
    assert len(provider.last["context"]) <= 500
    assert provider.last["scope"].partial is True


def test_note_scope_bookmark_is_not_supported(client, scoped, monkeypatch):
    provider = _use(FakeProvider())

    async def get_owned_note(note_id, user_id):
        return _note(NOTE_ID, "https://example.com", note_type="Bookmark")

    monkeypatch.setattr(assistant_api.repository, "get_owned_note", get_owned_note)

    body = client.post(
        "/internal/assistant/ask", json={**ASK_BODY, "scope_note_id": NOTE_ID}, headers=HEADERS
    ).json()

    assert body["status"] == "scope_unsupported"
    assert body["answer"] == assistant_api.SCOPE_UNSUPPORTED_MESSAGE
    assert provider.calls == []


def test_note_scope_missing_note_contacts_no_provider(client, scoped, monkeypatch):
    provider = _use(FakeProvider())

    async def get_owned_note(note_id, user_id):
        return None

    monkeypatch.setattr(assistant_api.repository, "get_owned_note", get_owned_note)

    body = client.post(
        "/internal/assistant/ask", json={**ASK_BODY, "scope_note_id": NOTE_ID}, headers=HEADERS
    ).json()

    assert body["status"] == "no_relevant_notes"
    assert provider.calls == []


def test_tag_scope_sends_every_note_when_they_fit(client, scoped, monkeypatch):
    provider = _use(FakeProvider())
    tagged = [_note("a", "docker compose up"), _note("b", "docker logs -f")]

    async def get_notes_with_tag(user_id, tag):
        assert (user_id, tag) == ("u1", "docker")
        return tagged

    monkeypatch.setattr(assistant_api.repository, "get_notes_with_tag", get_notes_with_tag)

    body = client.post("/internal/assistant/ask", json={**ASK_BODY, "scope_tag": "docker"}, headers=HEADERS).json()

    assert body["status"] == "ok"
    assert [s["id"] for s in body["sources"]] == ["a", "b"]
    assert body["scope"] == {"tag": "docker"}
    assert body["partial_context"] is False
    assert provider.last["scope"].label == "docker"
    # Everything fits: no ranking, so no embedding.
    assert scoped["embedded"] == []


def test_tag_scope_ranks_when_notes_exceed_the_budget(client, scoped, monkeypatch):
    provider = _use(FakeProvider())
    monkeypatch.setattr(assistant_api.settings, "context_budget_chars", 2_500)
    tagged = [_note(i, str(i) * 1_000) for i in ("a", "b", "c")]

    async def get_notes_with_tag(user_id, tag):
        return tagged

    async def rank(user_id, note_ids, embedding):
        assert note_ids == ["a", "b", "c"]
        return ["c", "a", "b"]

    monkeypatch.setattr(assistant_api.repository, "get_notes_with_tag", get_notes_with_tag)
    monkeypatch.setattr(assistant_api.repository, "rank_notes_by_similarity", rank)

    body = client.post(
        "/internal/assistant/ask",
        json={**ASK_BODY, "scope_tag": "x", "history": [{"question": "¿qué es docker?", "answer": "..."}]},
        headers=HEADERS,
    ).json()

    assert [s["id"] for s in body["sources"]] == ["c", "a"]
    assert body["partial_context"] is True
    assert len(provider.last["context"]) <= 2_500
    # The follow-up is searched together with the previous question.
    assert scoped["embedded"] == ["¿qué es docker?\n¿cómo lo arreglé?"]


def test_tag_scope_without_notes_contacts_no_provider(client, scoped, monkeypatch):
    provider = _use(FakeProvider())

    async def get_notes_with_tag(user_id, tag):
        return []

    monkeypatch.setattr(assistant_api.repository, "get_notes_with_tag", get_notes_with_tag)

    body = client.post("/internal/assistant/ask", json={**ASK_BODY, "scope_tag": "ajena"}, headers=HEADERS).json()

    assert body["status"] == "no_relevant_notes"
    assert body["answer"] == assistant_api.NOTHING_RELEVANT_MESSAGE
    assert provider.calls == []


def test_scoped_history_is_capped_to_the_last_three_turns(client, scoped, monkeypatch):
    provider = _use(FakeProvider())

    async def get_owned_note(note_id, user_id):
        return _note(NOTE_ID, "c")

    monkeypatch.setattr(assistant_api.repository, "get_owned_note", get_owned_note)
    history = [{"question": f"q{i}", "answer": "r" * 5_000} for i in range(5)]

    response = client.post(
        "/internal/assistant/ask", json={**ASK_BODY, "scope_note_id": NOTE_ID, "history": history}, headers=HEADERS
    )

    assert response.status_code == 200
    turns = provider.last["history"]
    assert [t.question for t in turns] == ["q2", "q3", "q4"]
    assert all(len(t.answer) == assistant_api.MAX_HISTORY_ANSWER_CHARS for t in turns)


def test_unscoped_question_ignores_history_and_keeps_its_response_shape(client):
    provider = _use(FakeProvider())

    body = client.post(
        "/internal/assistant/ask", json={**ASK_BODY, "history": [{"question": "q", "answer": "a"}]}, headers=HEADERS
    ).json()

    assert "history" not in provider.last and "scope" not in provider.last
    assert set(body) == {"answer", "source_note_ids", "sources", "grounded", "status"}


def test_both_scopes_at_once_is_rejected(client):
    _use(FakeProvider())

    response = client.post(
        "/internal/assistant/ask", json={**ASK_BODY, "scope_note_id": NOTE_ID, "scope_tag": "x"}, headers=HEADERS
    )

    assert response.status_code == 422
