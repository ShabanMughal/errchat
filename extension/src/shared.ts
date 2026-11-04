// Types and settings shared by the background worker, content scripts and options page.
// Request/response shapes mirror server/errchat_server/main.py.

export type Tone = "match" | "casual" | "friendly" | "formal";

export interface ChatMessage {
  sender: "me" | "them";
  text: string;
  author?: string;
}

export interface SuggestRequest {
  messages: ChatMessage[];
  style_examples: string[];
  tone: Tone;
  count: number;
  draft: string;
}

export interface SuggestResponse {
  replies: string[];
}

export interface CorrectRequest {
  text: string;
  messages: ChatMessage[];
  tone: Tone;
}

export interface Change {
  original: string;
  replacement: string;
  reason: string;
}

export interface CorrectResponse {
  corrected: string;
  changes: Change[];
}

// Server error kinds (brain.py), plus "offline" when the server can't be reached
// and "http" for anything else non-2xx.
export interface ApiError {
  kind: string;
  message: string;
}

// Content script → background worker.
export type BackgroundRequest =
  | { type: "suggest"; body: SuggestRequest }
  | { type: "correct"; body: CorrectRequest };

export type BackgroundResponse<T> =
  | { ok: true; data: T }
  | { ok: false; error: ApiError }
  // A newer request of the same type from the same tab replaced this one.
  | { ok: false; aborted: true };

export interface Settings {
  port: number;
  tone: Tone;
  count: number;
  suggestEnabled: boolean;
  correctEnabled: boolean;
}

export const DEFAULT_SETTINGS: Settings = {
  port: 8787,
  tone: "match",
  count: 3,
  suggestEnabled: true,
  correctEnabled: true,
};

export async function loadSettings(): Promise<Settings> {
  const stored = await chrome.storage.sync.get(DEFAULT_SETTINGS);
  return { ...DEFAULT_SETTINGS, ...stored } as Settings;
}
