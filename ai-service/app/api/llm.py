from typing import Any, Literal

from fastapi import APIRouter, Depends
from pydantic import BaseModel, Field

from app.core.config import settings
from app.core.security import verify_internal_api_key
from app.llm.factory import get_llm_provider
from app.llm.provider import (
    LlmInvalidCredentialsError,
    LlmProvider,
    LlmProviderUnavailableError,
    LlmRateLimitedError,
    LlmToolCallFailedError,
    LlmToolsUnsupportedError,
)

router = APIRouter(prefix="/internal/llm", dependencies=[Depends(verify_internal_api_key)])


class ListModelsRequest(BaseModel):
    api_key: str


class ModelInfo(BaseModel):
    id: str
    supports_actions: bool


class ListModelsResponse(BaseModel):
    status: Literal["ok", "invalid_key", "rate_limited", "unavailable"]
    models: list[ModelInfo]


def supports_actions(model_id: str) -> bool:
    """Whether the assistant may use tools with this model (settings.tool_capable_models)."""
    return any(model_id == allowed or model_id.startswith(allowed) for allowed in settings.tool_capable_models)


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

    return ListModelsResponse(status="ok", models=[ModelInfo(id=m, supports_actions=supports_actions(m)) for m in models])


# --- One step of the assistant's tool loop (assistant-agent-foundations design.md Decision 1) ---
# The loop itself runs in the .NET API, which executes the tools; this only makes the call.


class StepToolCall(BaseModel):
    id: str
    name: str
    arguments: dict[str, Any] | None = None
    arguments_error: str | None = None


class StepMessage(BaseModel):
    role: Literal["system", "user", "assistant", "tool"]
    content: str | None = None
    tool_calls: list[StepToolCall] | None = None
    tool_call_id: str | None = None


class StepTool(BaseModel):
    name: str
    description: str
    parameters: dict[str, Any]


class StepRequest(BaseModel):
    messages: list[StepMessage] = Field(min_length=1)
    tools: list[StepTool] | None = None
    # The requesting user's own key, decrypted by the .NET API for this request only.
    groq_api_key: str
    groq_model: str | None = None


StepStatus = Literal["ok", "invalid_key", "rate_limited", "unavailable", "tools_unsupported", "tool_call_failed"]


class StepResponse(BaseModel):
    status: StepStatus
    text: str | None = None
    tool_calls: list[StepToolCall] = []


@router.post("/step", response_model=StepResponse)
async def step(request: StepRequest, provider: LlmProvider = Depends(get_llm_provider)) -> StepResponse:
    """Always 200 with a typed `status`, like /internal/assistant/ask - never a raw error."""
    model = request.groq_model or settings.groq_model
    # The allowlist is the single source of truth for action support (design.md Decision 4):
    # the .NET API just offers tools and falls back when told they're unsupported - without a
    # wasted provider call.
    if request.tools and not supports_actions(model):
        return StepResponse(status="tools_unsupported")

    messages = [m.model_dump(exclude_none=True) for m in request.messages]
    tools = [t.model_dump() for t in request.tools] if request.tools else None
    try:
        result = await provider.chat_step(messages, tools, request.groq_api_key, model)
    except LlmInvalidCredentialsError:
        return StepResponse(status="invalid_key")
    except LlmRateLimitedError:
        return StepResponse(status="rate_limited")
    except LlmToolsUnsupportedError:
        return StepResponse(status="tools_unsupported")
    except LlmToolCallFailedError:
        return StepResponse(status="tool_call_failed")
    except LlmProviderUnavailableError:
        return StepResponse(status="unavailable")

    return StepResponse(
        status="ok",
        text=result.text,
        tool_calls=[
            StepToolCall(id=c.id, name=c.name, arguments=c.arguments, arguments_error=c.arguments_error)
            for c in result.tool_calls
        ],
    )
