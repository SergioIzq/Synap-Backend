"""byok-groq-and-settings tasks 1.1/1.2/1.5 - GroqProvider maps each provider failure to its own
error type, uses the per-request key and model, and never leaks the key into errors or logs."""

import json
import logging

import httpx
import pytest

from app.llm.groq_provider import GroqProvider
from app.llm.provider import LlmInvalidCredentialsError, LlmProviderUnavailableError, LlmRateLimitedError

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
