// What core.ts needs from a chat site. Each site implements this in
// adapters/<site>.ts; only the adapter changes when a site redesigns its DOM.

import type { ChatMessage } from "../shared";

export interface ChatAdapter {
  /** Something that identifies the open chat (e.g. its title), or null if none is open. */
  getChatId(): string | null;
  /** Messages currently on screen, oldest first. */
  getMessages(): ChatMessage[];
  /** The message input box, or null if none is visible. */
  getInputBox(): HTMLElement | null;
  getInputText(): string;
  /** Replace the input box's content. Must go through the site's editor so it notices. */
  setInputText(text: string): void;
  /** Whether the site is in dark mode, so the overlay can match. */
  isDark(): boolean;
}
