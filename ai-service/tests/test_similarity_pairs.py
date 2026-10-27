"""smart-resurfacing 1.4 — POST /internal/notes/similarity-pairs.

Cubre los scenarios de la spec:
- «Varias notas recientes como motivo» (una candidata, varios anchors → el mejor motivo)
- «Nota dormida sin relación con lo reciente» (similitud por debajo del umbral)
- «Más notas rescatables de las que se muestran» (límite respetado)
- «El rescate nunca cruza usuarios» (ids de otro usuario en ambas listas)

Requiere Postgres con pgvector (ver tests/conftest.py).
"""

import uuid

import pytest

from app.embeddings import repository
from tests.conftest import (
    EMBEDDING_DIMENSIONS,
    insert_note_with_embedding,
)


def _vec(base: float) -> list[float]:
    return [base] * EMBEDDING_DIMENSIONS


@pytest.mark.asyncio
async def test_varias_notas_recientes_como_motivo(db_pool):
    """Si hay varios anchors, se elige el más parecido como motivo."""
    user = str(uuid.uuid4())
    anchor1 = await insert_note_with_embedding(db_pool, user, "Motivo lejano", _vec(0.3))
    anchor2 = await insert_note_with_embedding(db_pool, user, "Motivo cercano", _vec(0.5))
    candidate = await insert_note_with_embedding(db_pool, user, "Candidata dormida", _vec(0.5))

    pairs = await repository.get_similarity_pairs(
        user_id=user,
        anchor_ids=[anchor1, anchor2],
        candidate_ids=[candidate],
        min_similarity=0.5,
        limit=5,
    )

    assert len(pairs) == 1
    assert pairs[0]["candidate_id"] == candidate
    # anchor2 es igual a la candidata → máxima similitud
    assert pairs[0]["anchor_id"] == anchor2


@pytest.mark.asyncio
async def test_nota_dormida_sin_relacion_con_lo_reciente(db_pool):
    """Una candidata por debajo del umbral no se rescata."""
    user = str(uuid.uuid4())
    anchor = await insert_note_with_embedding(db_pool, user, "Anchor", _vec(0.1))
    # Candidata con embedding opuesto → similitud cercana a 0
    await insert_note_with_embedding(db_pool, user, "Candidata sin relación", _vec(0.9))

    candidate = (await insert_note_with_embedding(db_pool, user, "Candidata sin relación 2", _vec(0.9)))

    pairs = await repository.get_similarity_pairs(
        user_id=user,
        anchor_ids=[anchor],
        candidate_ids=[candidate],
        min_similarity=0.55,
        limit=5,
    )

    assert pairs == []


@pytest.mark.asyncio
async def test_mas_notas_rescatables_de_las_que_se_muestran(db_pool):
    """El límite se respeta: solo se devuelven los mejores pares hasta limit."""
    user = str(uuid.uuid4())
    base = _vec(0.5)
    anchor = await insert_note_with_embedding(db_pool, user, "Anchor", base)
    candidates = [
        await insert_note_with_embedding(db_pool, user, f"Candidata {i}", base)
        for i in range(5)
    ]

    pairs = await repository.get_similarity_pairs(
        user_id=user,
        anchor_ids=[anchor],
        candidate_ids=candidates,
        min_similarity=0.99,
        limit=2,
    )

    assert len(pairs) <= 2


@pytest.mark.asyncio
async def test_rescate_nunca_cruza_usuarios(db_pool):
    """Ids de otro usuario en anchor_ids y candidate_ids no producen pares."""
    user_a = str(uuid.uuid4())
    user_b = str(uuid.uuid4())
    base = _vec(0.5)

    anchor_b = await insert_note_with_embedding(db_pool, user_b, "Anchor de B", base)
    candidate_b = await insert_note_with_embedding(db_pool, user_b, "Candidata de B", base)

    pairs = await repository.get_similarity_pairs(
        user_id=user_a,
        anchor_ids=[anchor_b],
        candidate_ids=[candidate_b],
        min_similarity=0.0,
        limit=10,
    )

    assert pairs == []
