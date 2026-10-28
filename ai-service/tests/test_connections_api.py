"""smart-resurfacing 1.3 — GET /internal/notes/{id}/connections.

Cubre los scenarios de la spec:
- «Notas poco parecidas no son conexiones» (umbral mínimo de similitud)
- «Nota aún sin analizar» (sin embedding o con embedding de otro modelo)
- «Más conexiones de las que se muestran» (límite respetado)
- «Las conexiones nunca cruzan usuarios»

Requiere Postgres con pgvector (ver tests/conftest.py).
"""

import uuid

import pytest

from app.embeddings import repository
from tests.conftest import (
    EMBEDDING_DIMENSIONS,
    insert_note,
    insert_note_with_embedding,
)


def _vec(base: float) -> list[float]:
    return [base] * EMBEDDING_DIMENSIONS


@pytest.mark.asyncio
async def test_notas_poco_parecidas_no_son_conexiones(db_pool):
    """La nota solo llega si supera min_similarity; una nota casi sin parecido no aparece."""
    user = str(uuid.uuid4())
    anchor = await insert_note_with_embedding(db_pool, user, "Helm charts", _vec(0.1))
    # Nota con embedding ortogonal → similitud cercana a 0
    await insert_note_with_embedding(db_pool, user, "Otra nota", _vec(0.9))

    result = await repository.find_note_connections(anchor, user, min_similarity=0.55, limit=5)

    assert result["indexed"] is True
    assert result["notes"] == []


@pytest.mark.asyncio
async def test_nota_aun_sin_analizar(db_pool):
    """Una nota sin embedding devuelve indexed=False, no una lista vacía que parezca sin conexiones."""
    user = str(uuid.uuid4())
    anchor = await insert_note(db_pool, user, "Nota recién creada")

    result = await repository.find_note_connections(anchor, user, min_similarity=0.55, limit=5)

    assert result["indexed"] is False
    assert result["notes"] == []


@pytest.mark.asyncio
async def test_nota_con_embedding_de_otro_modelo(db_pool):
    """Un embedding de un modelo distinto cuenta como 'aún sin analizar'."""
    user = str(uuid.uuid4())
    anchor = await insert_note_with_embedding(
        db_pool, user, "Nota con modelo antiguo", _vec(0.5), model="old-model"
    )

    result = await repository.find_note_connections(anchor, user, min_similarity=0.55, limit=5)

    assert result["indexed"] is False


@pytest.mark.asyncio
async def test_mas_conexiones_de_las_que_se_muestran(db_pool):
    """El límite se respeta aunque haya más notas que lo superen el umbral."""
    user = str(uuid.uuid4())
    base = _vec(0.5)
    anchor = await insert_note_with_embedding(db_pool, user, "Anchor", base)
    for i in range(4):
        await insert_note_with_embedding(db_pool, user, f"Nota {i}", base)

    result = await repository.find_note_connections(anchor, user, min_similarity=0.99, limit=2)

    assert result["indexed"] is True
    assert len(result["notes"]) <= 2


@pytest.mark.asyncio
async def test_las_conexiones_nunca_cruzan_usuarios(db_pool):
    """Las conexiones solo devuelven notas del mismo usuario."""
    user_a = str(uuid.uuid4())
    user_b = str(uuid.uuid4())
    base = _vec(0.5)
    anchor = await insert_note_with_embedding(db_pool, user_a, "Anchor de A", base)
    note_b = await insert_note_with_embedding(db_pool, user_b, "Nota de B", base)

    result = await repository.find_note_connections(anchor, user_a, min_similarity=0.0, limit=10)

    assert result["indexed"] is True
    ids = [n["id"] for n in result["notes"]]
    assert note_b not in ids
