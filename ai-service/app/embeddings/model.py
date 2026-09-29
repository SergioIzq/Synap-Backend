from fastembed import TextEmbedding

from app.core.config import settings

_model: TextEmbedding | None = None


def get_embedding_model() -> TextEmbedding:
    """Loaded lazily but called once eagerly at startup (see main.py's lifespan) so a broken
    model/download fails fast instead of on a user's first request."""
    global _model
    if _model is None:
        _model = TextEmbedding(model_name=settings.embedding_model)
    return _model


def embed_text(text: str) -> list[float]:
    model = get_embedding_model()
    # embed() is a generator - list()[0] gets the single vector for our one input string.
    embedding = list(model.embed([text]))[0]
    return embedding.tolist()


def embed_texts(texts: list[str]) -> list[list[float]]:
    """One call for a batch - used by the background reindex."""
    model = get_embedding_model()
    return [embedding.tolist() for embedding in model.embed(texts)]


def note_text(title: str | None, content: str) -> str:
    """What a note's embedding is computed from: its title too, so a note is found by what it is
    called and not only by what it says (assistant-agent-foundations design.md Decision 5)."""
    title = (title or "").strip()
    return f"{title}\n\n{content}" if title else content
