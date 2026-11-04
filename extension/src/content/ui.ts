// The injected UI:
// - a status icon in the corner of the chat's input box (like Grammarly's), which
//   shows that errchat is running, when it's thinking, and how many fixes it has
// - a suggestion bar and a grammar card that float just above the input box
//
// Everything lives in a Shadow DOM so the chat site's CSS can't touch it and ours
// can't leak out. Model output is only ever set through textContent, never innerHTML.

import type { CorrectResponse } from "../shared";

export type IconState =
  | "off" // no chat open
  | "idle" // ready
  | "busy" // waiting for Claude
  | "fix" // grammar fixes available; count shown on the icon
  | "error";

export interface Status {
  state: IconState;
  tip: string;
  count?: number;
}

const STYLE = `
:host { all: initial; }
.root {
  --bg: #ffffff; --fg: #111b21; --muted: #667781; --border: #d1d7db;
  --chip: #f0f2f5; --chip-hover: #e2e6e9; --accent: #008069; --accent-fg: #ffffff;
  --del: #c62828; --ins: #1b7f3b; --idle: #15a37f; --off: #8696a0; --fix: #e5484d;
  font: 13px/1.4 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
  color: var(--fg);
}
.root.dark {
  --bg: #233138; --fg: #e9edef; --muted: #8696a0; --border: #3b4a54;
  --chip: #2a3942; --chip-hover: #34454f; --accent: #00a884; --accent-fg: #111b21;
  --del: #ef9a9a; --ins: #81c995; --idle: #00a884; --off: #667781; --fix: #f2555a;
}
.panels {
  position: fixed; display: flex; flex-direction: column; gap: 6px;
}
.panel {
  background: var(--bg); border: 1px solid var(--border); border-radius: 10px;
  box-shadow: 0 2px 10px rgba(0,0,0,.12); padding: 8px;
}
.bar { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.label { color: var(--muted); font-size: 12px; margin-right: 2px; }
.chip {
  font: inherit; color: var(--fg); background: var(--chip);
  border: 0; border-radius: 16px; padding: 6px 12px; cursor: pointer;
  max-width: 100%; text-align: left;
}
.chip:hover, .chip:focus-visible { background: var(--chip-hover); outline: none; }
.icon {
  font: inherit; color: var(--muted); background: none; border: 0; cursor: pointer;
  width: 28px; height: 28px; border-radius: 50%; margin-left: auto;
}
.icon + .icon { margin-left: 0; }
.icon:hover { background: var(--chip); color: var(--fg); }
.muted { color: var(--muted); }
.error { color: var(--del); }
.fixed { font-weight: 600; margin: 2px 0 6px; word-break: break-word; }
.changes { margin: 0 0 8px; padding: 0; list-style: none; color: var(--muted); font-size: 12px; }
.changes del { color: var(--del); }
.changes ins { color: var(--ins); text-decoration: none; }
.apply {
  font: inherit; font-weight: 600; color: var(--accent-fg); background: var(--accent);
  border: 0; border-radius: 16px; padding: 6px 14px; cursor: pointer;
}

/* status icon */
.badge {
  position: fixed; width: 24px; height: 24px; padding: 0; border: 0; border-radius: 50%;
  display: grid; place-items: center; cursor: pointer;
  font: 700 13px/1 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; color: #fff;
  background: var(--idle); box-shadow: 0 1px 4px rgba(0,0,0,.25);
  transition: transform .12s ease, background-color .2s ease;
}
.badge:hover { transform: scale(1.12); }
.badge[data-state="off"] { background: var(--off); }
.badge[data-state="fix"] { background: var(--fix); }
.badge[data-state="error"] { background: var(--off); }
.badge[data-state="busy"]::after {
  content: ""; position: absolute; inset: -3px; border-radius: 50%;
  border: 2px solid transparent; border-top-color: var(--idle);
  animation: spin .8s linear infinite;
}
@keyframes spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) {
  .badge[data-state="busy"]::after { animation-duration: 2.4s; }
}
[hidden] { display: none !important; }
`;

const GAP_PX = 8;
const BADGE_PX = 24;
const BADGE_INSET_PX = 8;

export class Overlay {
  private host?: HTMLElement;
  private root!: HTMLElement;
  private panels!: HTMLElement;
  private grammar!: HTMLElement;
  private suggest!: HTMLElement;
  private badge!: HTMLButtonElement;

  constructor(
    private getAnchor: () => HTMLElement | null,
    private isDark: () => boolean,
    private onBadgeClick: () => void,
  ) {}

  setStatus(status: Status): void {
    this.ensure();
    const b = this.badge;
    b.dataset.state = status.state;
    b.textContent =
      status.state === "fix" ? String(status.count ?? "") : status.state === "error" ? "!" : "e";
    b.title = status.tip;
    b.setAttribute("aria-label", status.tip);
    this.reposition();
  }

  suggestionsVisible(): boolean {
    return !!this.host && !this.suggest.hidden;
  }

  correctionVisible(): boolean {
    return !!this.host && !this.grammar.hidden;
  }

  showSuggestionsLoading(onRefresh: () => void): void {
    this.renderSuggest(onRefresh, (bar) => bar.append(text("span", "Thinking of replies…", "muted")));
  }

  showSuggestions(replies: string[], onPick: (reply: string) => void, onRefresh: () => void): void {
    if (!replies.length) return this.hideSuggestions();
    this.renderSuggest(onRefresh, (bar) => {
      for (const reply of replies) {
        const chip = text("button", reply, "chip");
        chip.title = "Put this reply in the message box";
        chip.addEventListener("click", () => onPick(reply));
        bar.append(chip);
      }
    });
  }

  showSuggestionsError(message: string, onRefresh: () => void): void {
    this.renderSuggest(onRefresh, (bar) => bar.append(text("span", message, "error")));
  }

  hideSuggestions(): void {
    if (this.host) this.suggest.hidden = true;
  }

  showCorrection(result: CorrectResponse, onApply: () => void, onDismiss: () => void): void {
    this.ensure();
    const g = this.grammar;
    g.replaceChildren();

    const head = el("div", "bar");
    head.append(text("span", "Suggested fix", "label"), iconButton("×", "Dismiss", onDismiss));
    g.append(head, text("div", result.corrected, "fixed"));

    if (result.changes.length) {
      const list = el("ul", "changes");
      for (const c of result.changes) {
        const li = el("li");
        li.append(text("del", c.original), " → ", text("ins", c.replacement));
        if (c.reason) li.append(` · ${c.reason}`);
        list.append(li);
      }
      g.append(list);
    }

    const apply = text("button", "Apply", "apply");
    apply.addEventListener("click", onApply);
    g.append(apply);

    g.hidden = false;
    this.reposition();
  }

  hideCorrection(): void {
    if (this.host) this.grammar.hidden = true;
  }

  hideAll(): void {
    this.hideSuggestions();
    this.hideCorrection();
  }

  /** Keep the icon and panels glued to the input box (it moves and grows). */
  reposition(): void {
    if (!this.host) return;
    this.root.classList.toggle("dark", this.isDark());
    const anchor = this.getAnchor();
    if (!anchor) {
      // No input box: park the icon in the window corner so you can still see
      // errchat is loaded and read its tooltip.
      Object.assign(this.badge.style, { left: "", top: "", right: "16px", bottom: "16px" });
      this.panels.hidden = true;
      return;
    }
    const r = anchor.getBoundingClientRect();
    // Centered on a one-line box, bottom-right corner once it grows.
    const top =
      r.height < BADGE_PX * 2.5
        ? r.top + (r.height - BADGE_PX) / 2
        : r.bottom - BADGE_PX - BADGE_INSET_PX;
    Object.assign(this.badge.style, {
      right: "",
      bottom: "",
      left: `${r.right - BADGE_PX - BADGE_INSET_PX}px`,
      top: `${top}px`,
    });
    this.panels.hidden = false;
    Object.assign(this.panels.style, {
      left: `${r.left}px`,
      width: `${r.width}px`,
      bottom: `${window.innerHeight - r.top + GAP_PX}px`,
    });
  }

  private renderSuggest(onRefresh: () => void, fill: (bar: HTMLElement) => void): void {
    this.ensure();
    const bar = el("div", "bar");
    fill(bar);
    bar.append(
      iconButton("↻", "New suggestions (uses what you've typed)", onRefresh),
      iconButton("×", "Hide suggestions", () => this.hideSuggestions()),
    );
    this.suggest.replaceChildren(bar);
    this.suggest.hidden = false;
    this.reposition();
  }

  private ensure(): void {
    if (this.host) return;
    this.host = document.createElement("errchat-overlay");
    this.host.style.cssText = "position:fixed;top:0;left:0;width:0;height:0;z-index:2147483647;";
    const shadow = this.host.attachShadow({ mode: "open" });
    const style = document.createElement("style");
    style.textContent = STYLE;

    this.root = el("div", "root");
    this.panels = el("div", "panels");
    this.grammar = el("div", "panel");
    this.suggest = el("div", "panel");
    this.grammar.hidden = this.suggest.hidden = true;
    this.panels.append(this.grammar, this.suggest);

    this.badge = document.createElement("button");
    this.badge.className = "badge";
    this.badge.addEventListener("mousedown", (e) => e.preventDefault()); // keep input focus
    this.badge.addEventListener("click", this.onBadgeClick);

    this.root.append(this.panels, this.badge);
    shadow.append(style, this.root);
    document.body.append(this.host);
    window.addEventListener("resize", () => this.reposition());
  }
}

function el(tag: string, className = ""): HTMLElement {
  const node = document.createElement(tag);
  if (className) node.className = className;
  return node;
}

function text(tag: string, content: string, className = ""): HTMLElement {
  const node = el(tag, className);
  node.textContent = content;
  return node;
}

function iconButton(symbol: string, label: string, onClick: () => void): HTMLElement {
  const b = text("button", symbol, "icon");
  b.title = label;
  b.setAttribute("aria-label", label);
  b.addEventListener("click", onClick);
  return b;
}
