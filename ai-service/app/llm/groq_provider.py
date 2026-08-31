import httpx

from app.core.config import settings
from app.llm.provider import LlmProvider, LlmProviderUnavailableError

GROQ_CHAT_COMPLETIONS_URL = "https://api.groq.com/openai/v1/chat/completions"

SYSTEM_PROMPT = (
    "You are Synap, a personal second-brain assistant. Answer strictly using the notes "
    "provided below - they are the user's own captured knowledge. If the notes don't actually "
    "contain a relevant answer, say so plainly rather than guessing or using outside knowledge."
)


class GroqProvider(LlmProvider):
    """Design.md Decision 2: generation is delegated to an external free tier rather than a
    locally-hosted model, to avoid starving the VPS's other production sites."""

    async def generate_answer(self, question: str, context: str) -> str:
        user_content = f"Notes:\n{context}\n\nQuestion: {question}"

        try:
            async with httpx.AsyncClient(timeout=20.0) as client:
                response = await client.post(
                    GROQ_CHAT_COMPLETIONS_URL,
                    headers={"Authorization": f"Bearer {settings.groq_api_key}"},
                    json={
                        "model": settings.groq_model,
                        "messages": [
                            {"role": "system", "content": SYSTEM_PROMPT},
                            {"role": "user", "content": user_content},
                        ],
                        "temperature": 0.2,
                    },
                )
                response.raise_for_status()
                data = response.json()
                return data["choices"][0]["message"]["content"]
        except (httpx.HTTPError, KeyError, IndexError, ValueError) as exc:
            raise LlmProviderUnavailableError(str(exc)) from exc
