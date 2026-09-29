"""assistant-agent-foundations task 3.1 - /internal/embeddings/generate embeds the title with the
content and records which model produced the vector."""

import pytest
from fastapi.testclient import TestClient

from app.api import embeddings as embeddings_api
from app.core.config import settings
from app.embeddings.model import note_text
from main import app

HEADERS = {"X-Internal-Api-Key": settings.internal_api_key}


@pytest.fixture
def captured(monkeypatch):
    calls = {"embedded": [], "stored": []}
    monkeypatch.setattr(embeddings_api, "embed_text", lambda text: calls["embedded"].append(text) or [0.1])

    async def fake_upsert(note_id, user_id, embedding, model):
        calls["stored"].append((note_id, user_id, embedding, model))

    monkeypatch.setattr(embeddings_api.repository, "upsert_embedding", fake_upsert)
    return calls


def test_title_is_embedded_with_the_content(captured):
    body = {"note_id": "n1", "user_id": "u1", "title": "Proxy inverso", "content": "nginx en el VPS"}

    response = TestClient(app).post("/internal/embeddings/generate", json=body, headers=HEADERS)

    assert response.status_code == 200
    assert captured["embedded"] == ["Proxy inverso\n\nnginx en el VPS"]


def test_stored_row_records_the_current_model(captured):
    body = {"note_id": "n1", "user_id": "u1", "content": "texto"}

    TestClient(app).post("/internal/embeddings/generate", json=body, headers=HEADERS)

    assert captured["stored"] == [("n1", "u1", [0.1], settings.embedding_model)]


def test_request_without_title_still_accepted(captured):
    # An API deployed before this change sends no title.
    body = {"note_id": "n1", "user_id": "u1", "content": "solo contenido"}

    response = TestClient(app).post("/internal/embeddings/generate", json=body, headers=HEADERS)

    assert response.status_code == 200
    assert captured["embedded"] == ["solo contenido"]


@pytest.mark.parametrize(("title", "expected"), [(None, "c"), ("", "c"), ("  ", "c"), (" T ", "T\n\nc")])
def test_note_text(title, expected):
    assert note_text(title, "c") == expected
