from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any, Literal

ScopeKind = Literal["note", "tag"]

# Why a question is answered without actions, so the answer can say so when the user asks for
# one (specs/ai-assistant "No actions in a scoped conversation", "Answering without actions").
ActionsUnavailable = Literal["scope", "model"]


@dataclass(frozen=True)
class HistoryTurn:
    """An earlier question and answer of the same scoped conversation (scoped-assistant)."""

    question: str
    answer: str


@dataclass(frozen=True)
class AnswerScope:
    """What a scoped question is about - shapes the system prompt (scoped-assistant design.md
    Decision 4). `label` is the note title or the tag name."""

    kind: ScopeKind
    label: str
    partial: bool = False


class LlmProviderError(Exception):
    """Base for every generation-provider failure - the caller (app/api/assistant.py) turns each
    subclass into its own typed `status` instead of a raw 500 (specs/ai-assistant "Graceful
    handling of generation provider failure")."""


class LlmInvalidCredentialsError(LlmProviderError):
    """The provider rejected the user's own API key (revoked, mistyped, deleted in Groq)."""


class LlmRateLimitedError(LlmProviderError):
    """The user's own key hit its provider-side rate limit or quota."""


class LlmProviderUnavailableError(LlmProviderError):
    """The provider can't be reached, timed out, or failed in any other way."""


class LlmToolsUnsupportedError(LlmProviderError):
    """The chosen model does not accept tools - the caller answers without actions instead
    (assistant-agent-foundations design.md Decision 4)."""


class LlmToolCallFailedError(LlmProviderError):
    """The model tried to call a tool but produced a call the provider could not parse - the
    caller retries the step or answers without actions."""


@dataclass(frozen=True)
class ToolCall:
    """One tool call requested by the model. `arguments` is None when they were not valid JSON
    (`arguments_error` then says why) - the caller answers it with an error result."""

    id: str
    name: str
    arguments: dict[str, Any] | None
    arguments_error: str | None = None


@dataclass(frozen=True)
class StepResult:
    """One generation step of the assistant's loop: either final text or tool calls."""

    text: str | None
    tool_calls: list[ToolCall] = field(default_factory=list)


class LlmProvider(ABC):
    @abstractmethod
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
        """Generates with the requesting user's own key and model (byok-groq-and-settings) -
        never a server-owned key. Raises an LlmProviderError subclass on any failure, never
        returns a partial/garbled answer. `scope` is only given for scoped questions, `history`
        for follow-ups and `memory` for users with memory entries; without any of them the
        prompt is exactly the original unscoped one."""

    @abstractmethod
    async def chat_step(
        self, messages: list[dict[str, Any]], tools: list[dict[str, Any]] | None, api_key: str, model: str
    ) -> StepResult:
        """One request of the assistant's tool loop (assistant-agent-foundations design.md
        Decision 3). `messages` use a provider-neutral shape - roles system/user/assistant/tool;
        an assistant message may carry `tool_calls: [{id, name, arguments: dict}]` and a tool
        message carries `tool_call_id` - and `tools` are `{name, description, parameters}` with
        a JSON Schema. Each provider translates both. Without `tools` the model must answer with
        text. Raises an LlmProviderError subclass on failure."""

    @abstractmethod
    async def list_models(self, api_key: str) -> list[str]:
        """Returns the chat-capable model ids available to `api_key`. Doubles as key validation:
        raises LlmInvalidCredentialsError when the provider rejects the key."""
