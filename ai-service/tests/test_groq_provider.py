"""byok-groq-and-settings tasks 1.1/1.2/1.5 - GroqProvider maps each provider failure to its own
error type, uses the per-request key and model, and never leaks the key into errors or logs."""

import json
import logging

import httpx
import pytest

from app.llm.groq_provider import ACTIONS_UNAVAILABLE_INSTRUCTIONS, SYSTEM_PROMPT, GroqProvider
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


# --- chat_step (assistant-agent-foundations task 4.1) ---------------------------------------

from app.llm.provider import LlmToolCallFailedError, LlmToolsUnsupportedError  # noqa: E402

TOOLS = [{"name": "search_notes", "description": "Busca", "parameters": {"type": "object", "properties": {}}}]


def _tool_calls_response(*calls) -> httpx.Response:
    return httpx.Response(200, json={"choices": [{"message": {"content": None, "tool_calls": [
        {"id": call_id, "type": "function", "function": {"name": name, "arguments": arguments}}
        for call_id, name, arguments in calls
    ]}}]})


@pytest.mark.asyncio
async def test_chat_step_returns_text():
    result = await _provider(_chat_ok).chat_step([{"role": "user", "content": "hola"}], TOOLS, SECRET_KEY, "m")

    assert result.text == "respuesta"
    assert result.tool_calls == []


@pytest.mark.asyncio
async def test_chat_step_parses_tool_calls_and_flags_invalid_json():
    handler = lambda request: _tool_calls_response(  # noqa: E731
        ("c1", "search_notes", '{"query": "docker"}'), ("c2", "search_notes", "{not json")
    )

    result = await _provider(handler).chat_step([{"role": "user", "content": "q"}], TOOLS, SECRET_KEY, "m")

    assert result.text is None
    first, second = result.tool_calls
    assert (first.id, first.name, first.arguments, first.arguments_error) == ("c1", "search_notes", {"query": "docker"}, None)
    assert (second.arguments, second.arguments_error) == (None, "invalid_json")


@pytest.mark.asyncio
async def test_chat_step_translates_neutral_messages_and_tools():
    seen = {}

    def handler(request):
        seen.update(json.loads(request.content))
        return _chat_ok(request)

    messages = [
        {"role": "user", "content": "etiqueta mi nota"},
        {"role": "assistant", "content": "", "tool_calls": [{"id": "c1", "name": "search_notes", "arguments": {"query": "nota"}}]},
        {"role": "tool", "tool_call_id": "c1", "content": "[]"},
    ]
    await _provider(handler).chat_step(messages, TOOLS, SECRET_KEY, "m")

    assert seen["tools"] == [{"type": "function", "function": TOOLS[0]}]
    assistant = seen["messages"][1]
    assert assistant["tool_calls"] == [
        {"id": "c1", "type": "function", "function": {"name": "search_notes", "arguments": '{"query": "nota"}'}}
    ]
    assert seen["messages"][2] == {"role": "tool", "content": "[]", "tool_call_id": "c1"}


@pytest.mark.asyncio
async def test_chat_step_without_tools_sends_none():
    seen = {}

    def handler(request):
        seen.update(json.loads(request.content))
        return _chat_ok(request)

    await _provider(handler).chat_step([{"role": "user", "content": "q"}], None, SECRET_KEY, "m")

    assert "tools" not in seen


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("status_code", "error", "expected"),
    [
        (400, {"message": "`tools` is not supported with this model", "type": "invalid_request_error"}, LlmToolsUnsupportedError),
        (400, {"message": "Failed to call a function.", "code": "tool_use_failed"}, LlmToolCallFailedError),
        (400, {"message": "context length exceeded"}, LlmProviderUnavailableError),
        # Mentions tools but is not about tool support: a schema Groq rejected. Reading this as
        # "the model has no tools" is what silently dropped the whole conversation's actions
        # (specs/ai-assistant "Refusal for another reason").
        (400, {"message": "Invalid schema for function 'set_reminder': unknown field"}, LlmProviderUnavailableError),
        (401, {}, LlmInvalidCredentialsError),
        (429, {}, LlmRateLimitedError),
        (503, {}, LlmProviderUnavailableError),
    ],
)
async def test_chat_step_maps_errors(status_code, error, expected):
    provider = _provider(lambda request: httpx.Response(status_code, json={"error": error}))

    with pytest.raises(expected):
        await provider.chat_step([{"role": "user", "content": "q"}], TOOLS, SECRET_KEY, "m")


@pytest.mark.asyncio
async def test_a_tools_refusal_keeps_what_groq_said():
    """specs/platform-operations "Classified failure keeps its cause": the category alone leaves
    whoever is debugging with nothing to go on."""
    said = "`tools` is not supported with this model"
    provider = _provider(lambda request: httpx.Response(400, json={"error": {"message": said}}))

    with pytest.raises(LlmToolsUnsupportedError) as raised:
        await provider.chat_step([{"role": "user", "content": "q"}], TOOLS, SECRET_KEY, "m")

    assert said in str(raised.value)


# --- memory (assistant-agent-foundations task 4.3) ------------------------------------------


@pytest.mark.asyncio
async def test_memory_goes_after_the_instructions_and_before_the_history():
    seen = {}
    history = [HistoryTurn("¿y nginx?", "Lo configuraste así.")]

    await _provider(_capture_messages(seen)).generate_answer(
        "¿y cómo lo reinicio?", "ctx", SECRET_KEY, "m", history=history, memory=["prefiero respuestas cortas", "uso Ubuntu"]
    )

    system = seen["messages"][0]["content"]
    assert system.startswith(SYSTEM_PROMPT)
    assert system.index("follow-up") < system.index("- prefiero respuestas cortas") < system.index("- uso Ubuntu")
    assert seen["messages"][1] == {"role": "user", "content": "¿y nginx?"}
    assert seen["messages"][-1]["content"].endswith("Question: ¿y cómo lo reinicio?")


@pytest.mark.asyncio
async def test_empty_memory_leaves_the_prompt_as_before():
    with_empty, without = {}, {}

    await _provider(_capture_messages(with_empty)).generate_answer("¿q?", "ctx", SECRET_KEY, "m", memory=[])
    await _provider(_capture_messages(without)).generate_answer("¿q?", "ctx", SECRET_KEY, "m")

    assert with_empty["messages"] == without["messages"]
    assert without["messages"][0] == {"role": "system", "content": SYSTEM_PROMPT}


@pytest.mark.asyncio
@pytest.mark.parametrize(("reason", "hint"), [("scope", "general conversation"), ("model", "current model can't perform actions")])
async def test_actions_unavailable_explains_why_before_the_memory(reason, hint):
    seen = {}

    await _provider(_capture_messages(seen)).generate_answer(
        "apúntame algo", "ctx", SECRET_KEY, "m", actions_unavailable=reason, memory=["uso Ubuntu"]
    )

    system = seen["messages"][0]["content"]
    assert hint in system
    assert system.index(hint) < system.index("- uso Ubuntu")


@pytest.mark.parametrize("reason", ["scope", "model"])
def test_every_action_is_named_including_reminders(reason):
    """observable-failures task 5.5 - reminders were the fourth action and the only one left out
    of both texts, so a model without actions had no instruction about them and promised one."""
    text = ACTIONS_UNAVAILABLE_INSTRUCTIONS[reason]

    assert "reminders" in text
    assert "Recordatorios" in text
    for action in ("create notes", "add tags", "remember facts"):
        assert action in text
