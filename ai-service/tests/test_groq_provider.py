"""byok-groq-and-settings tasks 1.1/1.2/1.5 - GroqProvider maps each provider failure to its own
error type, uses the per-request key and model, and never leaks the key into errors or logs."""

import json
import logging

import httpx
import pytest

from app.llm.groq_provider import SYSTEM_PROMPT, GroqProvider
from app.llm.provider import (
    AnswerScope,
    HistoryTurn,
    LlmInvalidCredentialsError,
    LlmProviderUnavailableError,
    LlmRateLimitedError,
)

SECRET_KEY = "gsk_test_super_secret_value_1234"


def _provider(handler) -> GroqProvider:
    return GroqProvider(transport=httpx.MockTransport(handler))


def _chat_ok(request: httpx.Request) -> httpx.Response:
    return httpx.Response(200, json={"choices": [{"message": {"content": "respuesta"}}]})


@pytest.mark.asyncio
async def test_generate_answer_uses_per_request_key_and_model():
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["auth"] = request.headers["Authorization"]
        seen["model"] = json.loads(request.content)["model"]
        return _chat_ok(request)

    answer = await _provider(handler).generate_answer("q", "ctx", SECRET_KEY, "some/model")

    assert answer == "respuesta"
    assert seen == {"auth": f"Bearer {SECRET_KEY}", "model": "some/model"}


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("status_code", "expected"),
    [
        (401, LlmInvalidCredentialsError),
        (429, LlmRateLimitedError),
        (500, LlmProviderUnavailableError),
        (404, LlmProviderUnavailableError),
    ],
)
async def test_generate_answer_maps_provider_status(status_code, expected):
    provider = _provider(lambda request: httpx.Response(status_code, json={"error": {}}))

    with pytest.raises(expected):
        await provider.generate_answer("q", "ctx", SECRET_KEY, "m")


@pytest.mark.asyncio
async def test_network_error_is_unavailable_and_does_not_leak_key(caplog):
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError(f"boom with {request.headers['Authorization']}", request=request)

    caplog.set_level(logging.DEBUG)

    with pytest.raises(LlmProviderUnavailableError) as exc_info:
        await _provider(handler).generate_answer("q", "ctx", SECRET_KEY, "m")

    assert SECRET_KEY not in str(exc_info.value)
    assert exc_info.value.__cause__ is None
    assert SECRET_KEY not in caplog.text


@pytest.mark.asyncio
async def test_list_models_filters_non_chat_and_inactive_models():
    payload = {
        "data": [
            {"id": "llama-3.3-70b-versatile", "active": True},
            {"id": "whisper-large-v3", "active": True},
            {"id": "meta-llama/llama-guard-4-12b", "active": True},
            {"id": "playai-tts", "active": True},
            {"id": "canopylabs/orpheus-v1-english", "active": True},
            {"id": "old-model", "active": False},
            {"id": "qwen/qwen3-32b"},
        ]
    }
    provider = _provider(lambda request: httpx.Response(200, json=payload))

    assert await provider.list_models(SECRET_KEY) == ["llama-3.3-70b-versatile", "qwen/qwen3-32b"]


@pytest.mark.asyncio
async def test_list_models_invalid_key():
    provider = _provider(lambda request: httpx.Response(401, json={}))

    with pytest.raises(LlmInvalidCredentialsError):
        await provider.list_models(SECRET_KEY)


def _capture_messages(seen: dict):
    def handler(request: httpx.Request) -> httpx.Response:
        seen["messages"] = json.loads(request.content)["messages"]
        return _chat_ok(request)

    return handler


@pytest.mark.asyncio
async def test_unscoped_prompt_is_unchanged():
    """scoped-assistant task 1.4 - without scope/history the request is the original two messages."""
    seen = {}

    await _provider(_capture_messages(seen)).generate_answer("¿q?", "ctx", SECRET_KEY, "m")

    assert seen["messages"] == [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user", "content": "Notes:\nctx\n\nQuestion: ¿q?"},
    ]


@pytest.mark.asyncio
async def test_scoped_prompt_has_scope_line_and_earlier_turns_before_the_question():
    seen = {}
    history = [HistoryTurn("resume la nota", "1. uno\n2. dos"), HistoryTurn("¿y el 1?", "Uno es...")]

    await _provider(_capture_messages(seen)).generate_answer(
        "desarrolla el punto 2",
        "[CORS]\ncontenido",
        SECRET_KEY,
        "m",
        history=history,
        scope=AnswerScope(kind="note", label="CORS", partial=True),
    )

    messages = seen["messages"]
    assert [m["role"] for m in messages] == ["system", "user", "assistant", "user", "assistant", "user"]
    assert messages[0]["content"].startswith(SYSTEM_PROMPT)
    assert '"CORS"' in messages[0]["content"] and "only its beginning" in messages[0]["content"]
    assert messages[1]["content"] == "resume la nota"
    assert messages[2]["content"] == "1. uno\n2. dos"
    # Notes are sent once, with the new question - not repeated for earlier turns.
    assert messages[-1]["content"] == "Notes:\n[CORS]\ncontenido\n\nQuestion: desarrolla el punto 2"
    assert all("contenido" not in m["content"] for m in messages[1:-1])


@pytest.mark.asyncio
async def test_tag_scope_line_names_the_tag():
    seen = {}

    await _provider(_capture_messages(seen)).generate_answer(
        "q", "ctx", SECRET_KEY, "m", scope=AnswerScope(kind="tag", label="docker")
    )

    assert "#docker" in seen["messages"][0]["content"]
