from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Runtime configuration for the AI service, loaded from environment variables.

    Embeddings use fastembed's multilingual paraphrase-multilingual-MiniLM-L12-v2 (ONNX, no
    PyTorch/GPU needed - see synap-mvp design.md Decision 2's VPS constraint), chosen over the
    original English-only BAAI/bge-small-en-v1.5 by assistant-agent-foundations' spike (its
    design.md "Spike results"). Changing it triggers a background reindex of every note
    (app/embeddings/reindex.py). Generation goes to Groq.

    There is deliberately no Groq API key here: since byok-groq-and-settings every user brings
    their own, which the .NET API passes per request. `groq_model` is only the default model for
    users who haven't picked one - it costs the owner nothing.
    """

    model_config = SettingsConfigDict(env_prefix="SYNAP_AI_")

    database_url: str = "postgresql://synap:synap@localhost:5432/synap"

    # Shared secret checked on every /internal/* route - the AI service is not meant to be
    # reachable by anyone other than the .NET API (see AiServiceClient on the .NET side).
    internal_api_key: str = "local-dev-only-internal-key-change-me"

    embedding_model: str = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2"

    # Hybrid search keep rule (assistant-agent-foundations design.md "Spike results"): a note is
    # relevant with at least this cosine similarity, or when it matches at least
    # min(2, query lexemes) of the question's words in full-text search.
    min_relevant_similarity: float = 0.45

    # Upper bound on the notes' text sent with one scoped question (scoped-assistant design.md
    # Decision 3) - characters, not tokens, kept conservative so a question plus its short
    # history stays well under the free Groq tier's tokens-per-minute limit.
    context_budget_chars: int = 12_000

    # Groq models the assistant may use tools with (assistant-agent-foundations design.md
    # Decision 4) - exact ids or id prefixes. Groq's /models doesn't say which models accept
    # tools, and probing would spend the user's quota. PROVISIONAL: the families Groq documents
    # as tool-capable; spike task 1.2 replaces it with the models verified against Synap's tools.
    # Overridable with SYNAP_AI_TOOL_CAPABLE_MODELS='["id-or-prefix", ...]'.
    tool_capable_models: list[str] = [
        "llama-3.3-70b",
        "meta-llama/llama-4",
        "openai/gpt-oss",
        "moonshotai/kimi-k2",
        "qwen/qwen3",
    ]

    llm_provider: str = "groq"
    groq_model: str = "qwen/qwen3.8-27b"


settings = Settings()
