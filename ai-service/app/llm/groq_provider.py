import httpx

from app.llm.provider import (
    LlmInvalidCredentialsError,
    LlmProvider,
    LlmProviderUnavailableError,
    LlmRateLimitedError,
)

GROQ_BASE_URL = "https://api.groq.com/openai/v1"

SYSTEM_PROMPT = (
    "You are Synap, a personal second-brain assistant. Answer strictly using the notes "
    "provided below - they are the user's own captured knowledge. If the notes don't actually "
    "contain a relevant answer, say so plainly rather than guessing or using outside knowledge. "
    "Answer in the same language the question is asked in."
)

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

    async def generate_answer(self, question: str, context: str, api_key: str, model: str) -> str:
        user_content = f"Notes:\n{context}\n\nQuestion: {question}"

        try:
            async with self._client(timeout=20.0) as client:
                response = await client.post(
                    "/chat/completions",
                    headers={"Authorization": f"Bearer {api_key}"},
                    json={
                        "model": model,
                        "messages": [
                            {"role": "system", "content": SYSTEM_PROMPT},
                            {"role": "user", "content": user_content},
                        ],
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


def _raise_for_provider_status(response: httpx.Response) -> None:
    if response.status_code == 401:
        raise LlmInvalidCredentialsError("Provider rejected the API key")
    if response.status_code == 429:
        raise LlmRateLimitedError("Provider rate limit or quota reached")
    if response.is_error:
        raise LlmProviderUnavailableError(f"Provider returned HTTP {response.status_code}")
