import json

import pytest
from fastapi.testclient import TestClient

from errchat_server import prompts
from errchat_server.brain import BrainError, ClaudeCLI, parse_cli_output
from errchat_server.config import Settings
from errchat_server.main import create_app

EXTENSION_ORIGIN = "chrome-extension://" + "a" * 32


class FakeBrain:
    def __init__(self, result=None, error=None):
        self.result = result or {}
        self.error = error
        self.calls = []

    async def ask(self, prompt, system_prompt, schema):
        self.calls.append((prompt, system_prompt, schema))
        if self.error:
            raise self.error
        return self.result


def client_for(brain):
    return TestClient(create_app(Settings(), brain))


def test_suggest_returns_trimmed_replies_capped_at_count():
    brain = FakeBrain({"replies": [" yeah im ok ", "", "not really", "a", "b"]})
    resp = client_for(brain).post(
        "/suggest",
        json={"messages": [{"sender": "them", "text": "are you fine?"}], "count": 2},
        headers={"Origin": EXTENSION_ORIGIN},
    )
    assert resp.status_code == 200
    assert resp.json() == {"replies": ["yeah im ok", "not really"]}
    prompt, system, schema = brain.calls[0]
    assert "Them: are you fine?" in prompt
    assert schema is prompts.SUGGEST_SCHEMA


def test_correct_passes_through_model_result():
    result = {
        "corrected": "How are you?",
        "changes": [{"original": "how r u", "replacement": "How are you?", "reason": "spelling"}],
    }
    resp = client_for(FakeBrain(result)).post("/correct", json={"text": "how r u"})
    assert resp.status_code == 200
    assert resp.json() == result


def test_website_origin_is_rejected():
    brain = FakeBrain({"replies": ["x"]})
    resp = client_for(brain).post(
        "/suggest",
        json={"messages": [{"sender": "them", "text": "hi"}]},
        headers={"Origin": "https://evil.example"},
    )
    assert resp.status_code == 403
    assert brain.calls == []


@pytest.mark.parametrize("kind,status", [("auth", 401), ("timeout", 504), ("failed", 502)])
def test_brain_errors_map_to_http_status(kind, status):
    resp = client_for(FakeBrain(error=BrainError(kind, "boom"))).post(
        "/correct", json={"text": "hi"}
    )
    assert resp.status_code == status
    assert resp.json()["detail"]["kind"] == kind


def test_invalid_request_is_rejected_before_model_call():
    brain = FakeBrain()
    resp = client_for(brain).post("/suggest", json={"messages": []})
    assert resp.status_code == 422
    assert brain.calls == []


def test_parse_cli_output_prefers_structured_output():
    raw = json.dumps({"type": "result", "result": "ignored", "structured_output": {"replies": ["a"]}})
    assert parse_cli_output(raw) == {"replies": ["a"]}


def test_parse_cli_output_falls_back_to_result_text():
    raw = json.dumps({"type": "result", "result": '{"replies": ["a"]}'})
    assert parse_cli_output(raw) == {"replies": ["a"]}


@pytest.mark.parametrize(
    "raw,kind",
    [
        ("not json", "bad_output"),
        (json.dumps({"type": "result", "result": "plain text"}), "bad_output"),
        (json.dumps({"type": "result", "is_error": True, "result": "limit reached"}), "failed"),
    ],
)
def test_parse_cli_output_errors(raw, kind):
    with pytest.raises(BrainError) as exc:
        parse_cli_output(raw)
    assert exc.value.kind == kind


def test_command_disables_tools_and_mcp(monkeypatch):
    monkeypatch.setattr("errchat_server.brain.shutil.which", lambda _: "/bin/claude")
    cmd = ClaudeCLI("haiku", 10, 1).build_command("sys", {"type": "object"})
    assert cmd[:2] == ["/bin/claude", "-p"]
    assert cmd[cmd.index("--tools") + 1] == ""
    assert "--strict-mcp-config" in cmd
    assert cmd[cmd.index("--model") + 1] == "haiku"


def test_suggest_prompt_includes_style_examples_and_draft():
    prompt = prompts.build_suggest_prompt(
        [prompts.ChatMessage("me", "yo"), prompts.ChatMessage("them", "sup", "Ali")],
        ["haha nah"],
        "casual",
        3,
        draft="im",
    )
    assert "- haha nah" in prompt
    assert "Me: yo\nAli: sup" in prompt
    assert "<draft>im</draft>" in prompt


def test_correct_improve_mode_uses_improve_prompt():
    brain = FakeBrain({"corrected": "Sorry, I can't make it today.", "changes": []})
    resp = client_for(brain).post("/correct", json={"text": "sry cant come 2day", "mode": "improve"})
    assert resp.status_code == 200
    assert brain.calls[0][1] is prompts.IMPROVE_SYSTEM


def test_correct_defaults_to_fix_mode():
    brain = FakeBrain({"corrected": "Hi", "changes": []})
    client_for(brain).post("/correct", json={"text": "hi"})
    assert brain.calls[0][1] is prompts.CORRECT_SYSTEM


def test_correct_rejects_unknown_mode():
    brain = FakeBrain()
    resp = client_for(brain).post("/correct", json={"text": "hi", "mode": "shout"})
    assert resp.status_code == 422
    assert brain.calls == []
