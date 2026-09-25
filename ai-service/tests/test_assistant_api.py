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

    async def generate_answer(self, question, context, api_key, model):
        self.calls.append((api_key, model))
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
