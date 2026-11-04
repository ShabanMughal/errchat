// Service worker: the only code that talks to the errchat server.
//
// - One in-flight request per (tab, type). A newer one aborts the older, and the
//   server then kills its Claude CLI process, freeing one of its few slots.
// - Small in-memory cache, so switching back to a chat doesn't call Claude again.
//   The worker can be stopped at any time, so the cache is best-effort only.

import type { ApiError, BackgroundRequest, BackgroundResponse } from "./shared";
import { loadSettings } from "./shared";

const CACHE_LIMIT = 100;
const cache = new Map<string, unknown>();
const inFlight = new Map<string, AbortController>();

chrome.runtime.onMessage.addListener((msg: BackgroundRequest, sender, sendResponse) => {
  handle(msg, sender.tab?.id ?? -1).then(sendResponse);
  return true; // keep the channel open for the async response
});

async function handle(msg: BackgroundRequest, tabId: number): Promise<BackgroundResponse<unknown>> {
  const cacheKey = msg.type + JSON.stringify(msg.body);
  if (cache.has(cacheKey)) {
    return { ok: true, data: cache.get(cacheKey) };
  }

  const slot = `${tabId}:${msg.type}`;
  inFlight.get(slot)?.abort();
  const controller = new AbortController();
  inFlight.set(slot, controller);

  try {
    const data = await post(msg.type, msg.body, controller.signal);
    remember(cacheKey, data);
    return { ok: true, data };
  } catch (e) {
    if (controller.signal.aborted) return { ok: false, aborted: true };
    return { ok: false, error: e as ApiError };
  } finally {
    if (inFlight.get(slot) === controller) inFlight.delete(slot);
  }
}

async function post(path: string, body: unknown, signal: AbortSignal): Promise<unknown> {
  const { port } = await loadSettings();
  let resp: Response;
  try {
    resp = await fetch(`http://127.0.0.1:${port}/${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal,
    });
  } catch (e) {
    if (signal.aborted) throw e;
    throw {
      kind: "offline",
      message: "errchat server is not running. Start it with: python -m errchat_server",
    } satisfies ApiError;
  }

  const payload = await resp.json().catch(() => null);
  if (resp.ok) return payload;
  // Server errors look like {"detail": {"kind", "message"}}; validation errors
  // (422) have a list in detail instead.
  const detail = payload?.detail;
  if (detail && typeof detail === "object" && "kind" in detail) throw detail as ApiError;
  throw { kind: "http", message: `Server returned ${resp.status}` } satisfies ApiError;
}

function remember(key: string, value: unknown): void {
  cache.delete(key);
  cache.set(key, value);
  if (cache.size > CACHE_LIMIT) {
    // Maps iterate in insertion order, so the first key is the oldest.
    cache.delete(cache.keys().next().value!);
  }
}
