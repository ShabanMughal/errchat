# errchat architecture

errchat is a Grammarly-style assistant for web chat apps, with one extra feature:
besides fixing the grammar of what you type, it suggests whole replies to the
messages you receive.

| Feature | Example |
| --- | --- |
| **Grammar correction** | You type `how r u doing` → errchat offers `How are you doing?` and lists each change with a reason. |
| **Reply suggestions** | Someone sends `how are you?` → errchat shows chips like `I'm good, you?`, `Doing fine 🙂`, `Bit tired honestly`. Click one to put it in the input box. |

All model calls go through the **Claude Code CLI** running on your own machine, so
they use your **Claude subscription**. No API key is needed and no other backend is
involved.

> Status: the **server** (`server/`) is built and tested. The **Chrome extension**
> (`extension/`) is built for **WhatsApp Web**; more sites come as more adapters.

---

## 1. The big picture

```
┌──────────────────────── Chrome ─────────────────────────┐
│                                                         │
│  Chat site (WhatsApp Web, Messenger, Discord, ...)      │
│   ┌───────────────────────────────────────────────┐     │
│   │ Content script                                │     │
│   │  - reads recent messages from the page DOM    │     │
│   │  - watches the input box                      │     │
│   │  - shows suggestion chips + grammar popup     │     │
│   └──────────────────────┬────────────────────────┘     │
│                          │ chrome.runtime messages      │
│   ┌──────────────────────▼────────────────────────┐     │
│   │ Background service worker                     │     │
│   │  - debounces, caches, cancels stale requests  │     │
│   │  - only part that talks to the server         │     │
│   └──────────────────────┬────────────────────────┘     │
└──────────────────────────┼──────────────────────────────┘
                           │ HTTP  127.0.0.1:8787
                           │ Origin: chrome-extension://<id>
┌──────────────────────────▼──────────────────────────────┐
│ errchat-server (FastAPI, Python)            server/     │
│   main.py     routes, validation, origin check          │
│   prompts.py  system prompts, JSON schemas, prompt text │
│   brain.py    runs `claude -p`, retries, timeouts       │
└──────────────────────────┬──────────────────────────────┘
                           │ subprocess (stdin → stdout JSON)
┌──────────────────────────▼──────────────────────────────┐
│ Claude Code CLI  (`claude -p`, logged in to your plan)  │
└──────────────────────────┬──────────────────────────────┘
                           ▼
                 Claude (billed to your subscription)
```

Why a local server instead of calling Claude straight from the extension:

- A browser extension can't run the `claude` CLI. A small local process can.
- Your Claude subscription only works through the logged-in CLI, not with an API
  key that you would have to put in the extension.
- Prompts, schemas and security rules stay in one place that is easy to test.

---

## 2. Request flows

### 2.1 Reply suggestions

```
1. A new incoming message appears in the chat (MutationObserver in content script)
2. Content script collects the last ~20 messages as {sender: me|them, text, author}
   plus some of your own past messages as style examples
3. → service worker → POST /suggest
4. Server builds the prompt (prompts.build_suggest_prompt) and calls Claude
5. Claude returns {"replies": [...]} that follows SUGGEST_SCHEMA
6. Server trims replies, drops empty ones, caps at `count` → returns them
7. Content script shows them as chips above the input box; a click fills the input
   (errchat never sends a message for you)
```

If you've already started typing, the extension sends it as `draft`, and every
suggestion continues or completes that draft (autocomplete mode).

### 2.2 Grammar correction

```
1. You stop typing for ~800 ms (debounce) or press the shortcut
2. Content script sends the input text + the last few messages as context
3. → service worker → POST /correct
4. Claude returns {"corrected": "...", "changes": [{original, replacement, reason}]}
5. If changes is empty → nothing is shown
   Otherwise → underline / small badge; clicking it shows the fix and "Apply"
```

The conversation is passed as **context only** (so `their` vs `there` can be judged
correctly) and is never corrected itself.

---

## 3. Server (`server/errchat_server/`)

| File | Role |
| --- | --- |
| `__main__.py` | Entry point: `python -m errchat_server` starts uvicorn. |
| `config.py` | `Settings`, read once from environment variables. |
| `main.py` | FastAPI app: routes, Pydantic models, origin check, error → HTTP status mapping. |
| `prompts.py` | System prompts, tone descriptions, JSON output schemas, prompt builders. |
| `brain.py` | `ClaudeCLI`: runs the CLI as a subprocess and parses its JSON output. |

### 3.1 API

All bodies are JSON. Limits are enforced by Pydantic *before* any model call.

**`GET /health`** → `{"ok": true, "model": "haiku"}`

**`POST /suggest`**

```jsonc
// request
{
  "messages": [                       // 1–50, oldest first
    {"sender": "them", "text": "how are you?", "author": "Ali"}
  ],
  "style_examples": ["haha nah", "im good bro"],  // ≤30, your own past messages
  "tone": "match",                    // match | casual | friendly | formal
  "count": 3,                         // 1–5
  "draft": ""                         // what you've typed so far, optional
}
// response
{"replies": ["im good, you?", "doing fine 🙂", "bit tired tbh"]}
```

**`POST /correct`**

```jsonc
// request
{
  "text": "how r u doing",            // 1–4000 chars
  "messages": [ ... ],                // ≤20, context only
  "tone": "match"
}
// response
{
  "corrected": "How are you doing?",
  "changes": [
    {"original": "how r u", "replacement": "How are you", "reason": "spelling"}
  ]
}
```

**Errors** use `{"detail": {"kind": ..., "message": ...}}`:

| `kind` | HTTP | Meaning / what the extension should show |
| --- | --- | --- |
| `auth` | 401 | CLI not logged in → "Run `claude login`" |
| `not_found` | 503 | CLI not installed / not on PATH |
| `timeout` | 504 | Took longer than `ERRCHAT_TIMEOUT` → hide quietly |
| `failed`, `bad_output` | 502 | Model call failed (e.g. usage limit reached) |
| — | 403 | Request came from a website, not the extension |
| — | 422 | Invalid request body |

### 3.2 How a model call works (`brain.py`)

```
claude -p
  --output-format json          # one JSON result object on stdout
  --model <ERRCHAT_MODEL>       # default: haiku (fast, cheap on plan usage)
  --system-prompt <...>         # SUGGEST_SYSTEM or CORRECT_SYSTEM
  --json-schema <...>           # forces the shape of the answer
  --tools ""                    # no tools at all
  --strict-mcp-config           # no MCP servers
  --disable-slash-commands      # no skills / commands
  --no-session-persistence      # nothing saved to history
(prompt text is sent on stdin; cwd is an empty temp dir so no CLAUDE.md loads)
```

- **Concurrency:** an `asyncio.Semaphore` allows `ERRCHAT_MAX_CONCURRENT` (default 2)
  CLI processes at a time. Each process has startup cost and uses plan quota.
- **Retries:** one retry after 1 s for a non-zero exit. Auth errors fail at once.
  Timeouts are never retried, because a late suggestion is useless.
- **Cancellation:** if the HTTP client disconnects (e.g. a new message made the old
  request stale), the CLI process is killed instead of left running.
- **Parsing:** prefers `structured_output` from the CLI result and falls back to
  parsing `result` as JSON.
- **Windows:** `claude` is often a `.cmd` shim, so it runs through `cmd /c`.

### 3.3 Configuration

| Env var | Default | Notes |
| --- | --- | --- |
| `ERRCHAT_HOST` | `127.0.0.1` | Keep it on localhost. |
| `ERRCHAT_PORT` | `8787` | Must match the extension's setting. |
| `ERRCHAT_MODEL` | `haiku` | Any `claude --model` value (`haiku`, `sonnet`, a full id). |
| `ERRCHAT_TIMEOUT` | `60` | Seconds per call. |
| `ERRCHAT_MAX_CONCURRENT` | `2` | Parallel CLI processes. |

---

## 4. Chrome extension (`extension/`)

### 4.0 Tech stack: chosen for load speed

The content script loads into every chat tab, so it has to be as small as possible
and do no work until the chat is ready.

| Choice | Why |
| --- | --- |
| **Manifest V3** | Required by Chrome today. |
| **TypeScript, no UI framework** | The UI is just chips and a popup. Plain DOM is ~0 KB of runtime, while React adds ~45 KB to parse in every tab. |
| **esbuild** as the bundler | Builds in milliseconds and outputs one small, minified file per entry point. |
| **Shadow DOM** for the injected UI | errchat's styles can't break the chat site, and the site's CSS can't break errchat. No CSS framework needed. |
| **One content-script bundle per site** | WhatsApp users never download the Discord adapter. Each bundle is core + one adapter. |
| **`run_at: "document_idle"`** | Runs after the chat page finishes loading, so it never slows the site down. |
| **Lazy UI** | Chip and popup DOM is built only the first time it's needed. |

Target: **each content-script bundle under ~15 KB minified**, with no runtime
dependencies.

**Where the real latency is:** the extension adds only milliseconds. The slow part
is the model call (CLI startup plus generation, a few seconds). The extension hides
that by:

- **Prefetching:** request suggestions as soon as an incoming message arrives,
  not when you click the input box, so they're usually ready by the time you look.
- **Caching:** results are cached by conversation tail, so switching chats back
  doesn't call Claude again.
- **Cancelling** stale requests so they don't take up the 2 concurrent CLI slots.

If the options page ever needs a richer UI, use **Preact** (~4 KB) there only. It
loads only when that page is opened, never in chat tabs.

### 4.0.1 Layout

```
extension/
  package.json           devDeps only: typescript, esbuild, @types/chrome
  build.mjs              esbuild script → dist/
  manifest.json          host_permissions: http://127.0.0.1:8787/*, chat sites
  src/
    shared.ts            API types (mirror server/main.py) + settings
    background.ts        service worker: the only code that calls the server
    content/
      adapter.ts         the ChatAdapter interface every site implements
      core.ts            shared logic: observe, debounce, suggest, grammar check
      ui.ts              Shadow DOM suggestion bar + grammar card (styles inlined)
      adapters/
        whatsapp.ts      WhatsApp Web DOM selectors (built)
        (messenger.ts, discord.ts, ... later)
      entry-whatsapp.ts  core + whatsapp adapter → dist/content-whatsapp.js
    options/
      options.html/.ts   on/off per feature, tone, count, port, server status
  dist/                  what Chrome loads ("Load unpacked")
```

Current build sizes (minified): `content-whatsapp.js` 8.1 KB,
`background.js` 1.2 KB, `options.js` 1.1 KB.

**UI:** the suggestion bar shows the replies as chips, plus **↻** (new suggestions
that continue what you've typed, i.e. draft mode) and **×**. The grammar card shows
the corrected text, each change (~~old~~ → new · reason), **Apply** and **×**. It
disappears as soon as you type again.

### 4.1 Site adapters

Every chat site builds its DOM differently, so each site gets a small adapter with
the same interface. Only adapters change when a site redesigns its UI.

```ts
interface ChatAdapter {
  getChatId(): string | null;          // identifies the open chat (e.g. its title)
  getMessages(): ChatMessage[];        // {sender: "me" | "them", text, author}, oldest first
  getInputBox(): HTMLElement | null;   // usually contenteditable
  getInputText(): string;
  setInputText(text: string): void;    // must go through the site's editor so it notices
  isDark(): boolean;                   // so the overlay matches the site theme
}
```

`core.ts` handles observing: one `MutationObserver` on the page whose callback
only restarts a 300 ms timer, then reads the chat through the adapter. Adding a
site means writing one adapter, one `entry-<site>.ts`, and a `content_scripts`
entry in `manifest.json`.

WhatsApp adapter notes: messages are `.message-in` / `.message-out`, the text is in
`.copyable-text[data-pre-plain-text] span.selectable-text`, the author comes from
`data-pre-plain-text` (`"[10:30, 17/10/2026] Ali: "`), and emoji are read from
`<img alt>`. Text is put in the input box with `execCommand("insertText")`, because
WhatsApp's editor ignores direct DOM edits.

### 4.2 Service worker responsibilities

- **Debounce** grammar checks while typing (~800 ms) and suggestion requests
  when several messages arrive at once.
- **Cancel stale requests** with `AbortController` when the chat or input changes.
  The server then kills the CLI process.
- **Cache** results by a hash of (conversation tail + draft) so switching chats
  back and forth doesn't call Claude again.
- **Errors:** if the server can't be reached, the suggestion bar says so and shows
  how to start it. The options page checks `/health` and shows the status and model.

### 4.3 Style examples

For `tone: "match"`, the extension sends up to 30 of your own recent messages
(`sender: "me"`) as `style_examples`. That's how suggestions copy your casing,
slang, emoji and message length without any training or stored profile.

---

## 4.4 Desktop helper (`server/errchat_server/desktop.py`)

For the installed apps (WhatsApp Desktop, Teams, Outlook, ...) that a Chrome
extension can't reach. Start it with `errchat-desktop.bat` (or
`python -m errchat_server.desktop`). It starts the server in the same process
unless one is already running.

| Hotkey | Request |
| --- | --- |
| `Ctrl+Alt+G` | `/correct` with `mode: "improve"`, `tone: "match"` (better version, same tone) |
| `Ctrl+Alt+P` | `mode: "improve"`, `tone: "formal"` (better version, professional) |
| `Ctrl+Alt+F` | `mode: "fix"` (grammar and spelling only) |

Flow: wait for the hotkey's modifier keys to be released, then send Ctrl+C. If the
clipboard doesn't change because nothing was selected, send Ctrl+A and Ctrl+C. Then
call `/correct`, paste the result with Ctrl+V, and restore the old clipboard (text
only). If you switch windows while it's working, it doesn't paste. The result is
left on the clipboard instead. A small badge that never takes focus shows
progress and errors. Hotkeys use `RegisterHotKey` (no keyboard hook) and the
Win32 code is plain `ctypes`, so there are no extra dependencies.

`/correct` takes `mode`: `fix` (default, grammar only: what the extension uses)
or `improve` (rewrites into a clearer, more natural message without adding content).

---

## 4.5 Chat app (`chat-app/`)

A standalone Windows chat app (Electron, TypeScript, esbuild, no UI framework). It
doesn't use the server above: it starts the CLIs itself. See `chat-app/README.md`
for usage.

| File | Role |
| --- | --- |
| `src/main/main.ts` | Window, security settings, IPC handlers. |
| `src/main/chat.ts` | Conversation logic: messages, turns, stop, retry, lost-session recovery. No Electron imports. |
| `src/main/claude.ts` | `claude -p --output-format stream-json`, one process per reply, continued with `--resume`. |
| `src/main/codex.ts` | `codex app-server` over stdio (JSON-RPC), one process per reply. |
| `src/main/cli.ts` | Finding and starting the CLIs, cleaning their environment, killing process trees. |
| `src/main/store.ts` | History in `%APPDATA%\ErrChat\conversations.json`, written atomically. |
| `src/renderer/` | The UI: plain DOM, with Markdown through `marked` and `DOMPurify`. |

Decisions that aren't obvious from the code:

- **Subscription only.** `childEnv` removes `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` and
  similar variables, because either CLI would otherwise bill the key. If a CLI is
  signed in with a key anyway, the UI says so.
- **Codex uses `app-server`, not `exec`.** `codex exec --json` only returns finished
  messages, so streaming needs `app-server`. OpenAI labels it experimental, so a Codex
  update could break it: look in `codex.ts` first if Codex chats start failing.
- **A Codex turn ends when its process exits**, not at `turn/completed`. The process
  holds the thread's writer lock, so a follow-up sent sooner is refused.
- **Chat only, in two layers for Codex.** Claude gets `--tools ""`. Codex has no such
  switch, so its tool features and the user's MCP servers are disabled one by one, in
  a read-only sandbox with approvals off. A new Codex tool feature would be on by
  default, so check `DISABLED_FEATURES` after major updates. `test/e2e.mjs` tries to
  read a canary file and write a file through each provider.
- **Sessions can disappear.** Claude deletes old transcripts, so when a CLI can't find
  a session, `chat.ts` starts a new one and resends the local history as context.
- **Model output is untrusted in the renderer.** It's sanitized, the CSP blocks
  scripts and remote images (which could leak chat text through their URLs), and links
  open in the browser, never inside the app.

---

## 4.6 Typing helper (`keyboard/`)

This is the main desktop product, and is self-contained so it can be published on its own. It
replaces the hotkey-only helper in section 4.4 and doesn't need the server. See
`keyboard/README.md` for usage and `keyboard/docs/ARCHITECTURE.md` for how it works.

---

## 5. Security and privacy

Chat text comes from **other people**, so it's treated as untrusted input:

1. **The model can't act.** No tools, MCP or skills, and an empty working directory.
   The worst a prompt-injection message can do is change the suggested text, and
   you still choose whether to send it.
2. **Prompt tagging.** Chat content goes inside `<conversation>`, `<message>`,
   `<draft>` and `<style_examples>` tags, and the system prompt says text inside
   them is never an instruction.
3. **Localhost only.** The server binds to `127.0.0.1`.
4. **Origin check.** Browsers add an `Origin` header to cross-origin requests. Any
   origin other than `chrome-extension://<id>` gets a 403, so a website you visit
   can't use your Claude plan through `localhost:8787`. (Requests with no Origin,
   such as curl, are allowed: they can only come from software already on your
   machine.)
5. **Size limits** on every field cap how much text, and plan usage, one request
   can take.
6. **Nothing stored.** The server keeps no database or logs of message text, and
   CLI sessions aren't saved.
7. **Never auto-sends.** The extension only fills the input box.

---

## 6. Running and testing

```bash
cd server
python -m venv .venv && .venv\Scripts\activate     # Windows
pip install -e ".[dev]"
claude login                                       # once, with your Claude account
python -m errchat_server                           # http://127.0.0.1:8787
pytest                                             # uses a FakeBrain, no real CLI calls
```

```bash
cd extension
npm install
npm run build        # → dist/   (npm run watch while developing)
npm run typecheck
```

Then in Chrome open `chrome://extensions`, turn on **Developer mode**, click
**Load unpacked** and pick `extension/dist`. After each rebuild, click reload on the
extension card and reload the WhatsApp tab.

---

## 7. Known limitations and next steps

- **Latency:** each call starts a new CLI process, which adds a few seconds.
  The extension's prefetching and caching (section 4.0) hide most of it, and
  `haiku` stays the default model.
- **Plan usage:** every suggestion and grammar check uses Claude subscription quota.
  Debouncing and caching in the extension matter.
- **Windows quoting:** arguments passed through `cmd /c` (especially the JSON schema)
  depend on `cmd` quoting rules. Calling the CLI's real executable directly would be
  more robust.
- **`/health`** doesn't yet check that the CLI is installed and logged in.
- **Streaming:** suggestions arrive all at once. Streaming would let the first chip
  appear sooner.
- **WhatsApp selectors** break when WhatsApp changes its markup. Fixes go in
  `adapters/whatsapp.ts` only.
- **Next:** more site adapters (Messenger, Discord, Telegram Web, ...), a keyboard
  shortcut to accept a suggestion, and extension-side tests against saved DOM
  snapshots.
