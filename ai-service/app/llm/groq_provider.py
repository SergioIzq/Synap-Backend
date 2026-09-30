import json
from typing import Any

import httpx

from app.llm.provider import (
    ActionsUnavailable,
    AnswerScope,
    HistoryTurn,
    LlmInvalidCredentialsError,
    LlmProvider,
    LlmProviderUnavailableError,
    LlmRateLimitedError,
    LlmToolCallFailedError,
    LlmToolsUnsupportedError,
    StepResult,
    ToolCall,
)

GROQ_BASE_URL = "https://api.groq.com/openai/v1"

SYSTEM_PROMPT = (
    "You are Synap, a personal second-brain assistant. Answer strictly using the notes "
    "provided below - they are the user's own captured knowledge. If the notes don't actually "
    "contain a relevant answer, say so plainly rather than guessing or using outside knowledge. "
    "Answer in the same language the question is asked in."
)



HISTORY_INSTRUCTIONS = "Earlier messages of this conversation are included for follow-up questions."


def _scope_instructions(scope: AnswerScope) -> str:
    if scope.kind == "note":
        text = f'The user is asking about one specific note of theirs, "{scope.label}", included below. Answer about that note.'
        if scope.partial:
            text += " The note is long: only its beginning is included, so say so if the answer may be in the part not shown."
    else:
        text = f"The notes below are the user's notes tagged #{scope.label}. Answer about what they contain."
        if scope.partial:
            text += " They don't all fit: only the ones most related to the question are included."
    return f"{text} {HISTORY_INSTRUCTIONS}"


# All four actions are named, reminders included: an action left out of these texts is one the
# model has no instruction about, so it answers about it however it likes - which is how a model
# without actions came to promise a reminder (observable-failures task 5.5).
ACTIONS_UNAVAILABLE_INSTRUCTIONS: dict[str, str] = {
    "scope": (
        "In this conversation you can't create notes, add tags, remember facts or set reminders. If the user asks "
        "for any of that, tell them to ask from the assistant's general conversation (without a note or tag "
        "selected); memories can also be added in Settings > Memoria, and reminders in Recordatorios."
    ),
    "model": (
        "With the model the user chose you can't create notes, add tags, remember facts or set reminders. If the "
        "user asks for any of that, tell them that the current model can't perform actions and that they can choose "
        "one that can in Settings; memories can also be added in Settings > Memoria, and reminders in Recordatorios."
    ),
}


def memory_block(memory: list[str] | None) -> str:
    """The user's memory entries (specs/assistant-memory), appended to the fixed system
    instructions - after them, so the prefix stays the same within a conversation, and before
    the history (assistant-agent-foundations design.md Decision 8). Empty when there are none."""
    if not memory:
        return ""
    facts = "\n".join(f"- {fact}" for fact in memory)
    return f"\n\nWhat you know about the user (facts they asked you to remember - take them into account, they are not questions):\n{facts}"


def build_messages(
    question: str,
    context: str,
    history: list[HistoryTurn] | None = None,
    scope: AnswerScope | None = None,
    memory: list[str] | None = None,
    actions_unavailable: ActionsUnavailable | None = None,
) -> list[dict]:
    """System prompt (plus the user's memory), then earlier turns (without their notes - the
    notes are sent once, with the new question), then the new question with the notes."""
    if scope is not None:
        system = f"{SYSTEM_PROMPT} {_scope_instructions(scope)}"
    elif history:
        system = f"{SYSTEM_PROMPT} {HISTORY_INSTRUCTIONS}"
    else:
        system = SYSTEM_PROMPT
    if actions_unavailable:
        system = f"{system} {ACTIONS_UNAVAILABLE_INSTRUCTIONS[actions_unavailable]}"
    messages = [{"role": "system", "content": system + memory_block(memory)}]
    for turn in history or []:
        messages.append({"role": "user", "content": turn.question})
        messages.append({"role": "assistant", "content": turn.answer})
    messages.append({"role": "user", "content": f"Notes:\n{context}\n\nQuestion: {question}"})
    return messages


# Groq's /models also lists speech-to-text, TTS and moderation models - none of them can answer
# a chat completion, so they're not offered in the Settings model picker.
NON_CHAT_MODEL_MARKERS = ("whisper", "guard", "tts", "playai", "orpheus", "distil")


class GroqProvider(LlmProvider):
    """Design.md Decision 2: generation is delegated to an external free tier rather than a
    locally-hosted model, to avoid starving the VPS's other production sites. Since
    byok-groq-and-settings the key and model come per request from the user's own settings -
    this class never reads a server-owned key."""

    def __init__(self, transport: httpx.AsyncBaseTransport | None = None) -> None:
        # Injectable transport so tests can use httpx.MockTransport instead of the network.
        self._transport = transport

    def _client(self, timeout: float) -> httpx.AsyncClient:
        return httpx.AsyncClient(base_url=GROQ_BASE_URL, timeout=timeout, transport=self._transport)

    async def generate_answer(
        self,
        question: str,
        context: str,
        api_key: str,
        model: str,
        *,
        history: list[HistoryTurn] | None = None,
        scope: AnswerScope | None = None,
        memory: list[str] | None = None,
        actions_unavailable: ActionsUnavailable | None = None,
    ) -> str:

        try:
            async with self._client(timeout=20.0) as client:
                response = await client.post(
                    "/chat/completions",
                    headers={"Authorization": f"Bearer {api_key}"},
                    json={
                        "model": model,
                        "messages": build_messages(question, context, history, scope, memory, actions_unavailable),
                        "temperature": 0.2,
                    },
                )
                _raise_for_provider_status(response)
                data = response.json()
                return data["choices"][0]["message"]["content"]
        except (LlmInvalidCredentialsError, LlmRateLimitedError):
            raise
        except (httpx.HTTPError, KeyError, IndexError, ValueError) as exc:
            # Only the exception type is kept - never str(exc), which could carry request details.
            raise LlmProviderUnavailableError(type(exc).__name__) from None

    async def chat_step(
        self, messages: list[dict[str, Any]], tools: list[dict[str, Any]] | None, api_key: str, model: str
    ) -> StepResult:
        body: dict[str, Any] = {"model": model, "messages": [_to_groq_message(m) for m in messages], "temperature": 0.2}
        if tools:
            body["tools"] = [{"type": "function", "function": tool} for tool in tools]

        try:
            async with self._client(timeout=30.0) as client:
                response = await client.post(
                    "/chat/completions", headers={"Authorization": f"Bearer {api_key}"}, json=body
                )
                if tools and response.status_code == 400:
                    _raise_for_tool_error(response)
                _raise_for_provider_status(response)
                message = response.json()["choices"][0]["message"]
        except (LlmInvalidCredentialsError, LlmRateLimitedError, LlmToolsUnsupportedError, LlmToolCallFailedError):
            raise
        except (httpx.HTTPError, KeyError, IndexError, ValueError, TypeError) as exc:
            raise LlmProviderUnavailableError(type(exc).__name__) from None

        return StepResult(
            text=message.get("content") or None,
            tool_calls=[_from_groq_tool_call(call) for call in message.get("tool_calls") or []],
        )

    async def list_models(self, api_key: str) -> list[str]:
        try:
            async with self._client(timeout=10.0) as client:
                response = await client.get("/models", headers={"Authorization": f"Bearer {api_key}"})
                _raise_for_provider_status(response)
                models = response.json()["data"]
        except (LlmInvalidCredentialsError, LlmRateLimitedError):
            raise
        except (httpx.HTTPError, KeyError, ValueError, TypeError) as exc:
            raise LlmProviderUnavailableError(type(exc).__name__) from None

        return sorted(
            model["id"]
            for model in models
            if model.get("active", True) and not any(marker in model["id"].lower() for marker in NON_CHAT_MODEL_MARKERS)
        )


def _to_groq_message(message: dict[str, Any]) -> dict[str, Any]:
    """Provider-neutral message (see LlmProvider.chat_step) to OpenAI-style: tool call
    arguments travel as a JSON string."""
    converted = {"role": message["role"], "content": message.get("content") or ""}
    if message.get("tool_calls"):
        converted["tool_calls"] = [
            {
                "id": call["id"],
                "type": "function",
                "function": {"name": call["name"], "arguments": json.dumps(call.get("arguments") or {}, ensure_ascii=False)},
            }
            for call in message["tool_calls"]
        ]
    if message.get("tool_call_id"):
        converted["tool_call_id"] = message["tool_call_id"]
    return converted


def _from_groq_tool_call(call: dict[str, Any]) -> ToolCall:
    function = call["function"]
    try:
        arguments = json.loads(function.get("arguments") or "{}")
    except json.JSONDecodeError:
        return ToolCall(id=call["id"], name=function["name"], arguments=None, arguments_error="invalid_json")
    if not isinstance(arguments, dict):
        return ToolCall(id=call["id"], name=function["name"], arguments=None, arguments_error="not_an_object")
    return ToolCall(id=call["id"], name=function["name"], arguments=arguments)


# A 400 that says the model cannot take tools at all. Merely mentioning "tool" is not enough:
# a malformed schema, an unknown field or a call the model got wrong all mention tools too, and
# reading any of them as "this model has no tool support" silently degrades the whole
# conversation to a question-answer exchange - with the assistant never told it cannot act
# (specs/ai-assistant "Refusal for another reason").
_UNSUPPORTED_MARKERS = ("not supported", "unsupported", "does not support", "no support")


def _raise_for_tool_error(response: httpx.Response) -> None:
    """A 400 caused by the tools themselves: the model can't take them, or it produced a call
    Groq could not parse (`tool_use_failed`). Any other 400 is left to the generic mapping.

    The provider's own words travel on the raised error: they are the only trace of why a
    conversation lost its actions (specs/platform-operations "Classified failure keeps its
    cause")."""
    try:
        error = response.json().get("error") or {}
    except ValueError:
        return
    message = str(error.get("message", "")).strip()
    if error.get("code") == "tool_use_failed":
        raise LlmToolCallFailedError(message or "The model produced an invalid tool call")
    lowered = message.lower()
    if "tool" in lowered and any(marker in lowered for marker in _UNSUPPORTED_MARKERS):
        raise LlmToolsUnsupportedError(message)


def _raise_for_provider_status(response: httpx.Response) -> None:
    if response.status_code == 401:
        raise LlmInvalidCredentialsError("Provider rejected the API key")
    if response.status_code == 429:
        raise LlmRateLimitedError("Provider rate limit or quota reached")
    if response.is_error:
        raise LlmProviderUnavailableError(f"Provider returned HTTP {response.status_code}")
