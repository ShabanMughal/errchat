// Site-independent content-script logic: watch the chat, ask for reply suggestions
// when a message comes in, check grammar when you pause typing, and keep the
// status icon up to date.

import type {
  ApiError,
  BackgroundRequest,
  BackgroundResponse,
  ChatMessage,
  CorrectResponse,
  Settings,
  SuggestResponse,
} from "../shared";
import { DEFAULT_SETTINGS, loadSettings } from "../shared";
import type { ChatAdapter } from "./adapter";
import { Overlay, type Status } from "./ui";

// Wait for the DOM to settle after a burst of changes (new message, chat switch).
const DOM_SETTLE_MS = 300;
// Pause in typing before a grammar check.
const TYPING_PAUSE_MS = 800;

// Context sent to the server; must stay within the limits in server/main.py.
const SUGGEST_CONTEXT = 20;
const CORRECT_CONTEXT = 6;
const STYLE_EXAMPLES = 30;
const MAX_TEXT = 4000;
const MAX_DRAFT = 2000;
const MAX_AUTHOR = 100;

const log = (...args: unknown[]) => console.info("[errchat]", ...args);

export function start(adapter: ChatAdapter): void {
  log("loaded");
  let settings: Settings = DEFAULT_SETTINGS;
  loadSettings().then((s) => (settings = s));
  chrome.storage.onChanged.addListener(() => loadSettings().then((s) => (settings = s)));

  const ui = new Overlay(() => adapter.getInputBox(), () => adapter.isDark(), onBadgeClick);

  let chatId: string | null = null;
  let messageCount = 0;
  let lastIncoming = ""; // last incoming message we suggested replies for
  let suggestToken = 0; // bumped per request, so late answers for an old chat are dropped
  let lastChecked = ""; // last text we ran (or skipped) a grammar check on
  let fix: CorrectResponse | null = null; // grammar fix waiting behind the icon
  let busy = 0;
  let lastError = "";

  // --- status icon ------------------------------------------------------------

  function updateStatus(): void {
    let status: Status;
    if (!chatId) {
      status = { state: "off", tip: "errchat: open a chat to start" };
    } else if (busy) {
      status = { state: "busy", tip: "errchat: asking Claude…" };
    } else if (fix) {
      const n = fix.changes.length;
      status = { state: "fix", count: n, tip: `errchat: ${n} fix${n === 1 ? "" : "es"} — click to review` };
    } else if (lastError) {
      status = { state: "error", tip: `errchat: ${lastError}` };
    } else if (!adapter.getInputBox()) {
      status = { state: "error", tip: "errchat: can't find the message box on this page" };
    } else if (!messageCount) {
      status = { state: "error", tip: "errchat: can't read any messages in this chat" };
    } else {
      status = {
        state: "idle",
        tip: `errchat is on · read ${messageCount} messages · click for reply ideas`,
      };
    }
    ui.setStatus(status);
  }

  function onBadgeClick(): void {
    if (fix) {
      if (ui.correctionVisible()) ui.hideCorrection();
      else showFix();
    } else if (ui.suggestionsVisible()) {
      ui.hideSuggestions();
    } else {
      suggest(adapter.getMessages(), adapter.getInputText().trim());
    }
  }

  async function track<T>(msg: BackgroundRequest): Promise<BackgroundResponse<T>> {
    busy++;
    updateStatus();
    try {
      const res = await send<T>(msg);
      if (res.ok) lastError = "";
      else if (!("aborted" in res)) lastError = errorText(res.error);
      return res;
    } finally {
      busy--;
      updateStatus();
    }
  }

  // --- reply suggestions ------------------------------------------------------

  function onDomSettled(): void {
    const id = adapter.getChatId();
    if (id !== chatId) {
      chatId = id;
      lastIncoming = "";
      suggestToken++;
      fix = null;
      ui.hideAll();
      log(id ? `chat opened: ${id}` : "no chat open");
    }
    const messages = id ? adapter.getMessages() : [];
    if (messages.length !== messageCount) {
      messageCount = messages.length;
      log(`read ${messageCount} messages`, messages.at(-1));
    }
    updateStatus();

    const last = messages.at(-1);
    if (!last) return;
    if (last.sender === "me") {
      // You answered (or sent something new): old suggestions no longer apply.
      lastIncoming = "";
      ui.hideSuggestions();
      return;
    }
    const signature = `${id}\n${last.author}\n${last.text}`;
    if (signature === lastIncoming) return;
    lastIncoming = signature;
    // Ask right away, before you even click the input box, so the replies are
    // usually ready by the time you look.
    if (settings.suggestEnabled) suggest(messages, "");
  }

  async function suggest(messages: ChatMessage[], draft: string): Promise<void> {
    const token = ++suggestToken;
    const refresh = () => suggest(adapter.getMessages(), adapter.getInputText().trim());
    if (!messages.length) {
      return ui.showSuggestionsError("Can't read any messages in this chat yet.", refresh);
    }
    ui.showSuggestionsLoading(refresh);

    const res = await track<SuggestResponse>({
      type: "suggest",
      body: {
        messages: clip(messages.slice(-SUGGEST_CONTEXT)),
        style_examples: styleExamples(messages),
        tone: settings.tone,
        count: settings.count,
        draft: draft.slice(0, MAX_DRAFT),
      },
    });
    if (token !== suggestToken || "aborted" in res) return;
    if (!res.ok) {
      log("suggest failed", res.error);
      return ui.showSuggestionsError(errorText(res.error), refresh);
    }

    ui.showSuggestions(
      res.data.replies,
      (reply) => {
        lastChecked = reply; // don't grammar-check the model's own reply
        adapter.setInputText(reply);
        ui.hideAll();
      },
      refresh,
    );
  }

  // --- grammar correction -----------------------------------------------------

  async function checkGrammar(): Promise<void> {
    if (!settings.correctEnabled) return;
    const text = adapter.getInputText().trim();
    if (text === lastChecked) return;
    lastChecked = text;
    if (text.length < 3 || text.length > MAX_TEXT || !/\p{L}/u.test(text)) return;

    const res = await track<CorrectResponse>({
      type: "correct",
      body: {
        text,
        messages: clip(adapter.getMessages().slice(-CORRECT_CONTEXT)),
        tone: settings.tone,
      },
    });
    // Ignore the answer if you kept typing in the meantime.
    if (!res.ok || adapter.getInputText().trim() !== text) return;
    if (!res.data.changes.length || res.data.corrected.trim() === text) return;
    // Like Grammarly: the icon shows the number of fixes; click it to review.
    fix = res.data;
    updateStatus();
  }

  function showFix(): void {
    if (!fix) return;
    const current = fix;
    ui.showCorrection(
      current,
      () => {
        lastChecked = current.corrected.trim();
        adapter.setInputText(current.corrected);
        fix = null;
        ui.hideCorrection();
        updateStatus();
      },
      () => ui.hideCorrection(),
    );
  }

  // --- wiring -----------------------------------------------------------------

  const settle = debounce(onDomSettled, DOM_SETTLE_MS);
  // The observer callback only restarts a timer, so the cost per mutation is tiny.
  new MutationObserver(settle).observe(document.body, { childList: true, subtree: true });
  settle();

  const pause = debounce(checkGrammar, TYPING_PAUSE_MS);
  document.addEventListener(
    "input",
    (e) => {
      const box = adapter.getInputBox();
      if (!box || !box.contains(e.target as Node)) return;
      // The text changed, so any fix we had is stale.
      if (fix) {
        fix = null;
        updateStatus();
      }
      ui.hideCorrection();
      ui.reposition(); // the box grows as you type more lines
      if (!adapter.getInputText().trim()) lastChecked = "";
      pause();
    },
    true,
  );
}

async function send<T>(msg: BackgroundRequest): Promise<BackgroundResponse<T>> {
  try {
    return await chrome.runtime.sendMessage(msg);
  } catch {
    // The extension was reloaded or updated; this old content script is orphaned.
    return { ok: false, error: { kind: "reload", message: "errchat was updated. Reload this tab." } };
  }
}

function clip(messages: ChatMessage[]): ChatMessage[] {
  return messages.map((m) => ({
    sender: m.sender,
    text: m.text.slice(0, MAX_TEXT),
    author: (m.author ?? "").slice(0, MAX_AUTHOR),
  }));
}

/** Your own recent messages, so suggestions copy how you write. */
function styleExamples(messages: ChatMessage[]): string[] {
  const mine = messages.filter((m) => m.sender === "me").map((m) => m.text.slice(0, 300));
  return [...new Set(mine)].slice(-STYLE_EXAMPLES);
}

function errorText(error: ApiError): string {
  switch (error.kind) {
    case "auth":
      return "Claude is not logged in. Run `claude login`.";
    case "timeout":
      return "Claude took too long. Try ↻.";
    default:
      return error.message;
  }
}

function debounce(fn: () => void, ms: number): () => void {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return () => {
    clearTimeout(timer);
    timer = setTimeout(fn, ms);
  };
}
