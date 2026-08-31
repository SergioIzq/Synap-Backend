from app.core.config import settings
from app.llm.groq_provider import GroqProvider
from app.llm.provider import LlmProvider


def get_llm_provider() -> LlmProvider:
    """Provider-behind-an-interface (design.md Decision 2) - swapping providers later is a
    one-line change here, not a rewrite of app/api/assistant.py."""
    if settings.llm_provider == "groq":
        return GroqProvider()

    raise ValueError(f"Unknown LLM provider configured: {settings.llm_provider!r}")
