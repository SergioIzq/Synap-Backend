from abc import ABC, abstractmethod


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
    async def generate_answer(self, question: str, context: str, api_key: str, model: str) -> str:
        """Generates with the requesting user's own key and model (byok-groq-and-settings) -
        never a server-owned key. Raises an LlmProviderError subclass on any failure, never
        returns a partial/garbled answer."""

    @abstractmethod
    async def list_models(self, api_key: str) -> list[str]:
        """Returns the chat-capable model ids available to `api_key`. Doubles as key validation:
        raises LlmInvalidCredentialsError when the provider rejects the key."""
