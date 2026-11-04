// WhatsApp Web (web.whatsapp.com).
//
// Selectors are WhatsApp's own markup and change whenever they redesign, so all of
// them are kept here. The message classes (message-in / message-out) and the
// data-pre-plain-text attribute have been stable for years; the rest are fallbacks.

import type { ChatMessage } from "../../shared";
import type { ChatAdapter } from "../adapter";

const SEL = {
  main: "#main",
  chatTitle: "#main header span[dir='auto']",
  message: ".message-in, .message-out",
  // Fallback: message rows carry data-id="true_..." (sent) or "false_..." (received).
  messageById: "[data-id^='true_'], [data-id^='false_']",
  // Holds the text; data-pre-plain-text is "[10:30, 17/10/2026] Ali: "
  copyable: ".copyable-text[data-pre-plain-text]",
  text: "span.selectable-text",
  // Tried in order.
  input: [
    "#main footer div[contenteditable='true']",
    "#main div[contenteditable='true'][role='textbox']",
    "footer div[contenteditable='true']",
  ],
};

// "[10:30, 17/10/2026] Ali: " → "Ali"
const PRE_TEXT_AUTHOR = /^\[[^\]]*\]\s*(.*?):\s*$/;

export const whatsapp: ChatAdapter = {
  getChatId() {
    const main = document.querySelector(SEL.main);
    if (!main) return null;
    return document.querySelector(SEL.chatTitle)?.textContent?.trim() || "chat";
  },

  getMessages() {
    const out: ChatMessage[] = [];
    let nodes = document.querySelectorAll<HTMLElement>(`${SEL.main} ${SEL.message}`);
    const byClass = nodes.length > 0;
    if (!byClass) nodes = document.querySelectorAll<HTMLElement>(`${SEL.main} :is(${SEL.messageById})`);

    for (const node of nodes) {
      const copyable = node.querySelector<HTMLElement>(SEL.copyable);
      if (!copyable) continue; // media, stickers, system notices
      // Quoted replies are nested messages; only read the outermost one.
      if (!byClass && node.parentElement?.closest(SEL.messageById)) continue;
      const textNode = copyable.querySelector<HTMLElement>(SEL.text) ?? copyable;
      const text = readText(textNode).trim();
      if (!text) continue;
      const sender = byClass
        ? node.classList.contains("message-out") ? "me" : "them"
        : node.dataset.id!.startsWith("true_") ? "me" : "them";
      const pre = copyable.getAttribute("data-pre-plain-text") ?? "";
      const author = sender === "them" ? (PRE_TEXT_AUTHOR.exec(pre)?.[1] ?? "") : "";
      out.push({ sender, text, author });
    }
    return out;
  },

  getInputBox() {
    for (const selector of SEL.input) {
      const box = document.querySelector<HTMLElement>(selector);
      if (box) return box;
    }
    return null;
  },

  getInputText() {
    const box = this.getInputBox();
    return box ? readText(box) : "";
  },

  setInputText(text) {
    const box = this.getInputBox();
    if (!box) return;
    box.focus();
    // WhatsApp's editor ignores direct DOM edits, but it handles the browser's
    // own editing commands like real typing.
    document.execCommand("selectAll", false);
    document.execCommand("insertText", false, text);
  },

  isDark() {
    return document.body.classList.contains("dark");
  },
};

/** innerText, but with emoji, which WhatsApp renders as <img alt="😀">. */
function readText(root: HTMLElement): string {
  let out = "";
  const walk = (node: Node) => {
    if (node.nodeType === Node.TEXT_NODE) {
      out += node.textContent;
    } else if (node instanceof HTMLImageElement) {
      out += node.alt;
    } else if (node instanceof HTMLBRElement) {
      out += "\n";
    } else {
      // Paragraph blocks in the input box are separate lines.
      if (node instanceof HTMLParagraphElement && out && !out.endsWith("\n")) out += "\n";
      node.childNodes.forEach(walk);
    }
  };
  walk(root);
  return out;
}
