"""assistant-agent-foundations task 3.4 - /internal/search returns the requesting user's hybrid
search results as short snippets."""

import pytest
from fastapi.testclient import TestClient

from app.api import search as search_api
from app.core.config import settings
from main import app

HEADERS = {"X-Internal-Api-Key": settings.internal_api_key}


@pytest.fixture
def calls(monkeypatch):
    seen = []
    monkeypatch.setattr(search_api, "embed_text", lambda text: [0.5])

    async def fake_hybrid(user_id, query_text, embedding, limit=5):
        seen.append((user_id, query_text, embedding, limit))
        return [
            {"id": "n1", "title": "Docker", "note_type": "Text", "tags": ["infra"], "content": "x " * 300, "similarity": 0.72},
            # Found by its words only, which hybrid_search reports as no similarity at all.
            {"id": "n2", "title": "NU1605", "note_type": "Text", "tags": [], "content": "corto", "similarity": None},
        ]

    monkeypatch.setattr(search_api, "hybrid_search", fake_hybrid)
    return seen


def test_search_returns_snippets_for_the_requesting_user(calls):
    response = TestClient(app).post("/internal/search", json={"user_id": "u1", "query": "docker", "limit": 3}, headers=HEADERS)

    assert response.status_code == 200
    [result, by_words] = response.json()
    assert result["id"] == "n1" and result["type"] == "Text" and result["tags"] == ["infra"]
    assert len(result["snippet"]) <= search_api.SNIPPET_CHARS + 1 and result["snippet"].endswith("…")
    assert calls == [("u1", "docker", [0.5], 3)]
    # calendar-integration task 5.3: the score travels, so a caller can be stricter than the keep
    # rule - and a note found only by its words says so rather than reporting a score of zero.
    assert result["similarity"] == 0.72
    assert by_words["similarity"] is None


@pytest.mark.parametrize("body", [{"user_id": "u1", "query": ""}, {"user_id": "u1", "query": "x", "limit": 9}])
def test_invalid_requests_rejected(calls, body):
    assert TestClient(app).post("/internal/search", json=body, headers=HEADERS).status_code == 422


def test_rejects_a_wrong_internal_key(calls):
    response = TestClient(app).post(
        "/internal/search", json={"user_id": "u1", "query": "x"}, headers={"X-Internal-Api-Key": "wrong"}
    )

    assert response.status_code == 401
    assert calls == []


def test_min_similarity_filters_low_semantic_hits(calls):
    # n1 has similarity=0.72; n2 has similarity=None (words-only).
    # With min_similarity=0.80, n1 should be filtered out; n2 should be kept (words-only notes are
    # never filtered by the threshold - calendar-integration design.md Decision 8).
    response = TestClient(app).post(
        "/internal/search",
        json={"user_id": "u1", "query": "docker", "min_similarity": 0.80},
        headers=HEADERS,
    )

    assert response.status_code == 200
    results = response.json()
    ids = [r["id"] for r in results]
    assert "n1" not in ids, "note below threshold should be filtered out"
    assert "n2" in ids, "words-only note should always pass the threshold"


def test_without_min_similarity_results_unchanged(calls):
    without = TestClient(app).post("/internal/search", json={"user_id": "u1", "query": "docker"}, headers=HEADERS).json()
    with_none = TestClient(app).post("/internal/search", json={"user_id": "u1", "query": "docker", "min_similarity": None}, headers=HEADERS).json()

    assert without == with_none
