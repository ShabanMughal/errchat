# ErrChat for Windows

A desktop chat app that uses your **Claude** or **ChatGPT** subscription. It needs
no API keys: it drives the official `claude` and `codex` CLIs, which are already
signed in to your accounts.

## Use it

1. Install and sign in to at least one CLI: `claude auth login` or `codex login`.
   The app also has **Sign in** buttons that open these for you.
2. Run `release\ErrChat Setup 0.1.0.exe`, or start `release\win-unpacked\ErrChat.exe`
   without installing.

Pick Claude or Codex and a model under the message box, then type. **Enter** sends,
**Shift+Enter** adds a line, **Ctrl+N** starts a new chat. A chat stays with the
provider it started on, but you can change the model at any time.

The sidebar shows how much of your plan's limits you've used. The numbers appear
after your first message to each provider.

## Good to know

- **Replies count against your plan's limits**, the same as using the CLIs directly.
- **Chat only.** The model can't run commands, read or write files, browse the web
  or use your MCP servers.
- **Your chats are saved in plain text** at `%APPDATA%\ErrChat\conversations.json`,
  and the CLIs keep their own session transcripts in `~/.claude` and `~/.codex`.
  Deleting a chat in the app doesn't remove those transcripts.
- **Text only.** Attaching images and files isn't supported yet.
- Fable on the Claude Pro plan needs paid usage credits, so it's labelled in the list.
- The installer isn't code-signed, so Windows SmartScreen may warn the first time:
  **More info → Run anyway**.

## Develop

```bash
npm install
npm start           # build and run
npm run typecheck
npm test            # no real CLI calls, no quota used
npm run e2e         # real app and real CLIs; uses a little quota
npm run dist        # build the installer into release/
```

How it works is described in section 4.5 of [`../ARCHITECTURE.md`](../ARCHITECTURE.md).
