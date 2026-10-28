from fastapi import APIRouter, Body, Depends, Query

from app.core.security import verify_internal_api_key
from app.embeddings import repository

router = APIRouter(prefix="/internal/notes", dependencies=[Depends(verify_internal_api_key)])


@router.get("/{note_id}/related")
async def get_related_notes(
    note_id: str,
    user_id: str = Query(...),
    limit: int = Query(5, le=20),
) -> list[dict]:
    """specs/ai-assistant "Semantic relations between notes" - ownership of note_id is already
    verified by the .NET API before this is called; user_id here scopes the similarity search
    itself so a match can never come from another user's vault."""
    rows = await repository.find_related_notes(note_id, user_id, limit)
    return [
        {
            "id": str(row["id"]),
            "title": row["title"],
            "content": row["content"],
            "type": row["note_type"],
            "similarity": float(row["similarity"]),
        }
        for row in rows
    ]


@router.get("/{note_id}/connections")
async def get_note_connections(
    note_id: str,
    user_id: str = Query(...),
    min_similarity: float = Query(0.55, ge=0.0, le=1.0),
    limit: int = Query(5, ge=1, le=20),
) -> dict:
    """smart-resurfacing Decisión 1 — conexiones de una nota con umbral.

    Devuelve ``{"indexed": bool, "notes": [...]}``.  ``indexed`` es False cuando la nota
    aún no tiene embedding del modelo actual.
    La propiedad de la nota la comprueba el .NET antes de llamar aquí; user_id acota la búsqueda.
    """
    return await repository.find_note_connections(note_id, user_id, min_similarity, limit)


@router.post("/similarity-pairs")
async def get_similarity_pairs(
    body: dict = Body(...),
) -> list[dict]:
    """smart-resurfacing Decisión 5 — pares (candidata, motivo) para el rescate.

    Cuerpo: ``{"user_id", "anchor_ids", "candidate_ids", "min_similarity", "limit"}``.
    Devuelve lista de ``{"candidate_id", "anchor_id", "similarity"}`` ordenada de mayor
    a menor similitud.
    """
    return await repository.get_similarity_pairs(
        user_id=body["user_id"],
        anchor_ids=body["anchor_ids"],
        candidate_ids=body["candidate_ids"],
        min_similarity=body["min_similarity"],
        limit=body["limit"],
    )
