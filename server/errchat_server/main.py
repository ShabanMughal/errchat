"""HTTP API for the errchat extension.

Bound to localhost only. Browsers attach an Origin header to cross-origin
requests, so any request whose Origin is not a Chrome extension (e.g. a random
website probing localhost) is refused: otherwise any page you visit could spend
your Claude plan.
"""

import logging
import re
from typing import Literal

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from errchat_server import prompts
from errchat_server.brain import BrainError, ClaudeCLI
from errchat_server.config import Settings, load_settings

logger = logging.getLogger(__name__)

_ALLOWED_ORIGIN = re.compile(r"^chrome-extension://[a-p]{32}$")

# HTTP status per BrainError kind
_ERROR_STATUS = {"auth": 401, "not_found": 503, "timeout": 504}

Tone = Literal["match", "casual", "friendly", "formal"]


class Message(BaseModel):
    sender: Literal["me", "them"]
    text: str = Field(min_length=1, max_length=4000)
    author: str = Field(default="", max_length=100)


class SuggestRequest(BaseModel):
    messages: list[Message] = Field(min_length=1, max_length=50)
    style_examples: list[str] = Field(default_factory=list, max_length=30)
    tone: Tone = "match"
    count: int = Field(default=3, ge=1, le=5)
    draft: str = Field(default="", max_length=2000)


class SuggestResponse(BaseModel):
    replies: list[str]


class CorrectRequest(BaseModel):
    text: str = Field(min_length=1, max_length=4000)
    messages: list[Message] = Field(default_factory=list, max_length=20)
    tone: Tone = "match"
    # fix: grammar/spelling only. improve: rewrite into a clearer, more natural message.
    mode: Literal["fix", "improve"] = "fix"


class Change(BaseModel):
    original: str
    replacement: str
    reason: str


class CorrectResponse(BaseModel):
    corrected: str
    changes: list[Change]


def _to_chat(messages: list[Message]) -> list[prompts.ChatMessage]:
    return [prompts.ChatMessage(m.sender, m.text, m.author) for m in messages]


def create_app(settings: Settings | None = None, brain: ClaudeCLI | None = None) -> FastAPI:
    settings = settings or load_settings()
    brain = brain or ClaudeCLI(
        model=settings.model,
        timeout_seconds=settings.timeout_seconds,
        max_concurrent_calls=settings.max_concurrent_calls,
    )
    app = FastAPI(title="errchat", version="0.1.0")

    @app.middleware("http")
    async def reject_web_origins(request: Request, call_next):
        origin = request.headers.get("origin")
        if origin and not _ALLOWED_ORIGIN.match(origin):
            return JSONResponse({"detail": "Origin not allowed"}, status_code=403)
        return await call_next(request)

    async def ask(prompt: str, system: str, schema: dict) -> dict:
        try:
            return await brain.ask(prompt, system, schema)
        except BrainError as e:
            logger.error("Model call failed (%s): %s", e.kind, e)
            raise HTTPException(
                status_code=_ERROR_STATUS.get(e.kind, 502),
                detail={"kind": e.kind, "message": str(e)},
            ) from e

    @app.get("/health")
    async def health():
        return {"ok": True, "model": settings.model}

    @app.post("/suggest", response_model=SuggestResponse)
    async def suggest(req: SuggestRequest):
        prompt = prompts.build_suggest_prompt(
            _to_chat(req.messages), req.style_examples, req.tone, req.count, req.draft
        )
        result = await ask(prompt, prompts.SUGGEST_SYSTEM, prompts.SUGGEST_SCHEMA)
        replies = [r.strip() for r in result.get("replies", []) if r.strip()]
        return SuggestResponse(replies=replies[: req.count])

    @app.post("/correct", response_model=CorrectResponse)
    async def correct(req: CorrectRequest):
        prompt = prompts.build_correct_prompt(req.text, _to_chat(req.messages), req.tone)
        system = prompts.IMPROVE_SYSTEM if req.mode == "improve" else prompts.CORRECT_SYSTEM
        result = await ask(prompt, system, prompts.CORRECT_SCHEMA)
        return CorrectResponse.model_validate(result)

    return app
