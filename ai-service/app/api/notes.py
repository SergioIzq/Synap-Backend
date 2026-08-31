from fastapi import APIRouter, Depends, Query

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
