from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Runtime configuration for the AI service, loaded from environment variables.

    Embedding model choice and the external LLM provider are deferred (see
    design.md Open Questions) and land with tasks 4.1/4.6 - this only wires
    the connection to the shared Postgres database.
    """

    model_config = SettingsConfigDict(env_prefix="SYNAP_AI_")

    database_url: str = "postgresql://synap:synap@localhost:5432/synap"


settings = Settings()
