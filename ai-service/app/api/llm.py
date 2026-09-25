from typing import Literal

from fastapi import APIRouter, Depends
from pydantic import BaseModel

from app.core.security import verify_internal_api_key
from app.llm.factory import get_llm_provider
from app.llm.provider import (
    LlmInvalidCredentialsError,
    LlmProvider,
    LlmProviderUnavailableError,
    LlmRateLimitedError,
)

router = APIRouter(prefix="/internal/llm", dependencies=[Depends(verify_internal_api_key)])


class ListModelsRequest(BaseModel):
    api_key: str


class ListModelsResponse(BaseModel):
    status: Literal["ok", "invalid_key", "rate_limited", "unavailable"]
    models: list[str]


@router.post("/models", response_model=ListModelsResponse)
async def list_models(request: ListModelsRequest, provider: LlmProvider = Depends(get_llm_provider)) -> ListModelsResponse:
    """Validates a user's Groq key and lists its chat models in one call (byok-groq-and-settings
    design.md Decision 3) - used by the .NET API both when saving a key and for the model picker.
    POST rather than GET so the key travels in the body, never in a URL that could be logged."""
    try:
        models = await provider.list_models(request.api_key)
    except LlmInvalidCredentialsError:
        return ListModelsResponse(status="invalid_key", models=[])
    except LlmRateLimitedError:
        return ListModelsResponse(status="rate_limited", models=[])
    except LlmProviderUnavailableError:
        return ListModelsResponse(status="unavailable", models=[])

    return ListModelsResponse(status="ok", models=models)
