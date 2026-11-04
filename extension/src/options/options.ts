// Options page: every change is saved immediately; content scripts pick it up
// through chrome.storage.onChanged. Also shows whether the server is reachable.

import type { Settings } from "../shared";
import { loadSettings } from "../shared";

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const status = $("status");

async function init(): Promise<void> {
  const s = await loadSettings();
  $<HTMLInputElement>("suggestEnabled").checked = s.suggestEnabled;
  $<HTMLInputElement>("correctEnabled").checked = s.correctEnabled;
  $<HTMLSelectElement>("tone").value = s.tone;
  $<HTMLInputElement>("count").value = String(s.count);
  $<HTMLInputElement>("port").value = String(s.port);
  document.body.addEventListener("change", save);
  checkServer(s.port);
}

async function save(): Promise<void> {
  const settings: Settings = {
    suggestEnabled: $<HTMLInputElement>("suggestEnabled").checked,
    correctEnabled: $<HTMLInputElement>("correctEnabled").checked,
    tone: $<HTMLSelectElement>("tone").value as Settings["tone"],
    count: clamp(Number($<HTMLInputElement>("count").value), 1, 5, 3),
    port: clamp(Number($<HTMLInputElement>("port").value), 1, 65535, 8787),
  };
  await chrome.storage.sync.set(settings);
  checkServer(settings.port);
}

async function checkServer(port: number): Promise<void> {
  status.textContent = "Checking server…";
  try {
    const resp = await fetch(`http://127.0.0.1:${port}/health`);
    const body = await resp.json();
    status.textContent = `Server running · model: ${body.model}`;
  } catch {
    status.textContent = "Server not running. Start it with: python -m errchat_server";
  }
}

function clamp(n: number, min: number, max: number, fallback: number): number {
  return Number.isFinite(n) ? Math.min(max, Math.max(min, Math.round(n))) : fallback;
}

if (!globalThis.chrome?.storage) {
  // Opened as a plain file instead of through the extension.
  status.textContent =
    "Open this page from chrome://extensions → errchat → Details → Extension options.";
} else {
  init().catch((e) => (status.textContent = `Error: ${e?.message ?? e}`));
}
