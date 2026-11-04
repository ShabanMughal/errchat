"""Headless Claude Code calls, billed to the logged-in Claude subscription.

Adapted from Harvey's brain (D:\\job\\harvey\\brain.py). Each call runs
``claude -p`` with no tools, no MCP servers and no skills, in an empty working
directory so no project CLAUDE.md is picked up. Chat text is untrusted (anyone
can message you), so the model gets nothing it could act with.
"""

import asyncio
import json
import logging
import shutil
import tempfile
from pathlib import Path

logger = logging.getLogger(__name__)

# stderr fragments that mean retrying is pointless
_AUTH_PATTERNS = (
    "not logged in",
    "please run `claude login`",
    "please run /login",
    "invalid api key",
    "unauthorized",
)

# One retry for transient failures (non-zero exit). Timeouts are not retried:
# a suggestion that arrives a minute late is useless.
_MAX_ATTEMPTS = 2
_RETRY_DELAY_SECONDS = 1.0


class BrainError(Exception):
    """A failed model call. ``kind`` is one of: not_found, auth, timeout,
    failed, bad_output."""

    def __init__(self, kind: str, message: str):
        super().__init__(message)
        self.kind = kind


def resolve_claude_command() -> list[str]:
    """Absolute command for the Claude CLI, or BrainError(not_found).

    Resolved through PATH up front because on Windows the CLI may be a .cmd
    shim, which CreateProcess cannot start directly.
    """
    path = shutil.which("claude")
    if not path:
        raise BrainError(
            "not_found",
            "Claude CLI not found on PATH. Install it from https://claude.com/download",
        )
    if path.lower().endswith((".cmd", ".bat")):
        return ["cmd", "/c", path]
    return [path]


def parse_cli_output(raw: str) -> dict:
    """Extract the structured result from ``--output-format json`` stdout."""
    try:
        payload = json.loads(raw)
    except json.JSONDecodeError as e:
        raise BrainError("bad_output", f"CLI output was not JSON: {raw[:200]}") from e
    if not isinstance(payload, dict) or payload.get("type") != "result":
        raise BrainError("bad_output", "CLI output had no result object")
    if payload.get("is_error"):
        raise BrainError("failed", str(payload.get("result") or "model call failed")[:300])

    structured = payload.get("structured_output")
    if isinstance(structured, dict):
        return structured
    # Fall back to parsing the text result, in case a CLI version omits
    # structured_output.
    try:
        result = json.loads(payload.get("result") or "")
    except json.JSONDecodeError as e:
        raise BrainError("bad_output", "Model did not return JSON") from e
    if not isinstance(result, dict):
        raise BrainError("bad_output", "Model returned JSON that is not an object")
    return result


class ClaudeCLI:
    def __init__(self, model: str, timeout_seconds: float, max_concurrent_calls: int):
        self.model = model
        self.timeout_seconds = timeout_seconds
        self._semaphore = asyncio.Semaphore(max_concurrent_calls)
        # Empty directory so the CLI never loads a project's CLAUDE.md.
        self._workdir = Path(tempfile.mkdtemp(prefix="errchat-"))

    def build_command(self, system_prompt: str, schema: dict) -> list[str]:
        return [
            *resolve_claude_command(),
            "-p",  # the prompt itself goes via stdin
            "--output-format", "json",
            "--model", self.model,
            "--system-prompt", system_prompt,
            "--json-schema", json.dumps(schema),
            "--tools", "",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--no-session-persistence",
        ]

    async def ask(self, prompt: str, system_prompt: str, schema: dict) -> dict:
        """Run one model call and return its JSON object, or raise BrainError."""
        cmd = self.build_command(system_prompt, schema)
        async with self._semaphore:
            last_error = ""
            for attempt in range(_MAX_ATTEMPTS):
                if attempt:
                    await asyncio.sleep(_RETRY_DELAY_SECONDS)
                stdout, stderr, returncode = await self._run(cmd, prompt)
                if returncode == 0:
                    return parse_cli_output(stdout)
                last_error = stderr.strip() or stdout.strip()
                logger.warning("Claude exited with code %s: %s", returncode, last_error[:300])
                if any(p in last_error.lower() for p in _AUTH_PATTERNS):
                    raise BrainError("auth", "Claude CLI is not logged in. Run `claude login`.")
            raise BrainError("failed", f"Claude call failed: {last_error[:300]}")

    async def _run(self, cmd: list[str], prompt: str) -> tuple[str, str, int]:
        try:
            process = await asyncio.create_subprocess_exec(
                *cmd,
                cwd=self._workdir,
                stdin=asyncio.subprocess.PIPE,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
        except FileNotFoundError as e:
            raise BrainError("not_found", "Claude CLI could not be started") from e

        try:
            stdout, stderr = await asyncio.wait_for(
                process.communicate(prompt.encode("utf-8")),
                timeout=self.timeout_seconds,
            )
        except asyncio.TimeoutError:
            await _kill(process)
            raise BrainError("timeout", f"Claude call timed out after {self.timeout_seconds:.0f}s")
        except asyncio.CancelledError:
            # The HTTP client went away; don't leave an orphaned CLI process.
            await _kill(process)
            raise
        return (
            stdout.decode("utf-8", errors="replace"),
            stderr.decode("utf-8", errors="replace"),
            process.returncode,
        )


async def _kill(process: asyncio.subprocess.Process) -> None:
    try:
        process.kill()
        await process.wait()
    except ProcessLookupError:
        pass
