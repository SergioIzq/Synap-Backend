from fastapi import Header, HTTPException, status

from app.core.config import settings


async def verify_internal_api_key(x_internal_api_key: str = Header(...)) -> None:
    """Every /internal/* route depends on this - only the .NET API is meant to call them."""
    if x_internal_api_key != settings.internal_api_key:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid internal API key")
