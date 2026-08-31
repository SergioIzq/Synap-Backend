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
