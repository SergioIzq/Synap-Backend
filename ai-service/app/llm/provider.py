from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Literal

ScopeKind = Literal["note", "tag"]


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
    ) -> str:
        """Generates with the requesting user's own key and model (byok-groq-and-settings) -
        never a server-owned key. Raises an LlmProviderError subclass on any failure, never
        returns a partial/garbled answer. `history` and `scope` are only given for scoped
        questions; without them the prompt is exactly the unscoped one."""

    @abstractmethod
    async def list_models(self, api_key: str) -> list[str]:
        """Returns the chat-capable model ids available to `api_key`. Doubles as key validation:
        raises LlmInvalidCredentialsError when the provider rejects the key."""
