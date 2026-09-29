"""assistant-agent-foundations task 4.2 - /internal/llm/step makes one provider call with the
user's key and returns text or tool calls, or a typed status for every failure."""

import pytest
from fastapi.testclient import TestClient

from app.core.config import settings
from app.llm.factory import get_llm_provider
from app.llm.provider import (
    LlmInvalidCredentialsError,
    LlmProvider,
    LlmProviderUnavailableError,
    LlmRateLimitedError,
    LlmToolCallFailedError,
    LlmToolsUnsupportedError,
    StepResult,
    ToolCall,
)
from main import app

HEADERS = {"X-Internal-Api-Key": settings.internal_api_key}
TOOL = {"name": "search_notes", "description": "Busca", "parameters": {"type": "object", "properties": {}}}
BODY = {
    "messages": [
        {"role": "system", "content": "Eres Synap."},
        {"role": "user", "content": "etiqueta mi nota"},
        {"role": "assistant", "content": "", "tool_calls": [{"id": "c1", "name": "search_notes", "arguments": {"query": "nota"}}]},
        {"role": "tool", "tool_call_id": "c1", "content": "[]"},
    ],
    "tools": [TOOL],
    "groq_api_key": "gsk_x",
}


class StepProvider(LlmProvider):
    def __init__(self, result=None, error=None):
        self.result, self.error, self.calls = result, error, []

    async def generate_answer(self, *args, **kwargs):
        raise NotImplementedError

    async def list_models(self, api_key):
        raise NotImplementedError

    async def chat_step(self, messages, tools, api_key, model):
        self.calls.append((messages, tools, api_key, model))
        if self.error:
            raise self.error
        return self.result


@pytest.fixture(autouse=True)
def every_model_supports_actions(monkeypatch):
    monkeypatch.setattr(settings, "tool_capable_models", [""])


@pytest.fixture
def use():
    def _use(provider):
        app.dependency_overrides[get_llm_provider] = lambda: provider
        return provider

    yield _use
    app.dependency_overrides.clear()


def test_step_returns_tool_calls_and_passes_messages_through(use):
    provider = use(StepProvider(StepResult(text=None, tool_calls=[
        ToolCall("c2", "add_tags", {"note_id": "n1", "tags": ["docker"]}),
        ToolCall("c3", "search_notes", None, "invalid_json"),
    ])))

    body = TestClient(app).post("/internal/llm/step", json=BODY, headers=HEADERS).json()

    assert body["status"] == "ok"
    assert body["tool_calls"] == [
        {"id": "c2", "name": "add_tags", "arguments": {"note_id": "n1", "tags": ["docker"]}, "arguments_error": None},
        {"id": "c3", "name": "search_notes", "arguments": None, "arguments_error": "invalid_json"},
    ]
    messages, tools, key, model = provider.calls[0]
    assert messages[2]["tool_calls"][0]["arguments"] == {"query": "nota"}
    assert messages[3] == {"role": "tool", "tool_call_id": "c1", "content": "[]"}
    assert tools == [TOOL]
    assert (key, model) == ("gsk_x", settings.groq_model)


def test_step_returns_text_and_uses_chosen_model(use):
    provider = use(StepProvider(StepResult(text="Hecho.")))

    body = TestClient(app).post("/internal/llm/step", json={**BODY, "tools": None, "groq_model": "m2"}, headers=HEADERS).json()

    assert body == {"status": "ok", "text": "Hecho.", "tool_calls": []}
    assert provider.calls[0][1] is None
    assert provider.calls[0][3] == "m2"


@pytest.mark.parametrize(
    ("error", "status"),
    [
        (LlmInvalidCredentialsError(), "invalid_key"),
        (LlmRateLimitedError(), "rate_limited"),
        (LlmProviderUnavailableError(), "unavailable"),
        (LlmToolsUnsupportedError(), "tools_unsupported"),
        (LlmToolCallFailedError(), "tool_call_failed"),
    ],
)
def test_step_failures_are_typed(use, error, status):
    use(StepProvider(error=error))

    response = TestClient(app).post("/internal/llm/step", json=BODY, headers=HEADERS)

    assert response.status_code == 200
    assert response.json()["status"] == status


def test_tools_for_a_model_outside_the_allowlist_are_refused_without_a_provider_call(use, monkeypatch):
    monkeypatch.setattr(settings, "tool_capable_models", ["capable/"])
    provider = use(StepProvider(StepResult(text="no debería llamarse")))

    body = TestClient(app).post("/internal/llm/step", json={**BODY, "groq_model": "plain/model"}, headers=HEADERS).json()

    assert body["status"] == "tools_unsupported"
    assert provider.calls == []


def test_a_step_without_tools_works_with_any_model(use, monkeypatch):
    monkeypatch.setattr(settings, "tool_capable_models", ["capable/"])
    provider = use(StepProvider(StepResult(text="Respuesta final.")))

    body = TestClient(app).post("/internal/llm/step", json={**BODY, "tools": None, "groq_model": "plain/model"}, headers=HEADERS).json()

    assert body["text"] == "Respuesta final."
    assert len(provider.calls) == 1
