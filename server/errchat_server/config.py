"""Settings, read once from environment variables."""

import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    host: str = "127.0.0.1"
    port: int = 8787
    # Any value `claude --model` accepts: an alias (haiku, sonnet) or a full id.
    model: str = "haiku"
    timeout_seconds: float = 60.0
    # Parallel CLI processes; each one costs startup time and plan usage.
    max_concurrent_calls: int = 2


def load_settings() -> Settings:
    return Settings(
        host=os.environ.get("ERRCHAT_HOST", Settings.host),
        port=int(os.environ.get("ERRCHAT_PORT", Settings.port)),
        model=os.environ.get("ERRCHAT_MODEL", Settings.model),
        timeout_seconds=float(os.environ.get("ERRCHAT_TIMEOUT", Settings.timeout_seconds)),
        max_concurrent_calls=int(
            os.environ.get("ERRCHAT_MAX_CONCURRENT", Settings.max_concurrent_calls)
        ),
    )
