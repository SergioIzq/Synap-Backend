from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Runtime configuration for the AI service, loaded from environment variables.

    Embedding model and LLM provider are resolved here (design.md's Open Questions on this are
    no longer open): fastembed's BAAI/bge-small-en-v1.5 for embeddings (ONNX, no PyTorch/GPU
    needed - see design.md Decision 2's VPS constraint) and Groq's free tier for generation.
    """

    model_config = SettingsConfigDict(env_prefix="SYNAP_AI_")

    database_url: str = "postgresql://synap:synap@localhost:5432/synap"

    # Shared secret checked on every /internal/* route - the AI service is not meant to be
    # reachable by anyone other than the .NET API (see AiServiceClient on the .NET side).
    internal_api_key: str = "local-dev-only-internal-key-change-me"

    embedding_model: str = "BAAI/bge-small-en-v1.5"

    llm_provider: str = "groq"
    groq_api_key: str = ""
    groq_model: str = "llama-3.1-8b-instant"


settings = Settings()
