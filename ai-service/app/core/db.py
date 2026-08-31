import asyncpg
from pgvector.asyncpg import register_vector

from app.core.config import settings

_pool: asyncpg.Pool | None = None


async def _init_connection(connection: asyncpg.Connection) -> None:
    await register_vector(connection)


async def init_pool() -> None:
    global _pool
    _pool = await asyncpg.create_pool(settings.database_url, init=_init_connection)


async def close_pool() -> None:
    global _pool
    if _pool is not None:
        await _pool.close()
        _pool = None


def get_pool() -> asyncpg.Pool:
    if _pool is None:
        raise RuntimeError("Database pool is not initialized - init_pool() must run at startup.")
    return _pool
