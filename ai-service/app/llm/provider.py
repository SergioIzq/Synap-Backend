from abc import ABC, abstractmethod


class LlmProviderUnavailableError(Exception):
    """Raised when the external provider can't be reached, times out, or is rate-limited -
    the caller (app/api/assistant.py) turns this into the graceful "temporarily unavailable"
    answer required by specs/ai-assistant, never a raw 500."""


class LlmProvider(ABC):
    @abstractmethod
    async def generate_answer(self, question: str, context: str) -> str:
        """Raises LlmProviderUnavailableError on any failure - never returns a partial/garbled answer."""
