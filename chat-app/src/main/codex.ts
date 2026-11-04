// Codex backend: `codex app-server` over stdio, one process per reply. `codex
// exec --json` only returns finished messages, so streaming needs this
// (experimentally labelled) JSON-RPC protocol.

import { existsSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { createInterface } from 'node:readline';
import type { ModelOption, ProviderStatus, UsageWindow } from '../shared/types.ts';
import { appendTail, capture, findCli, killTree, openLoginConsole, startCli } from './cli.ts';
import { assertSafeId, CHAT_INSTRUCTIONS, IDLE_TIMEOUT_MS, ProviderError } from './provider.ts';
import type { Provider, RunningTurn, TurnRequest } from './provider.ts';

const STOP_GRACE_MS = 2000;
const LIMITS_READ_MS = 1500;
const RESUME_ATTEMPTS = 6;
const RESUME_RETRY_MS = 500;

// Chat only. Everything that lets the model act on the machine or the web is
// off; the read-only sandbox below is the second layer.
const DISABLED_FEATURES = [
  'shell_tool', 'unified_exec', 'code_mode_host', 'apps', 'plugins', 'browser_use', 'browser_use_external',
  'in_app_browser', 'computer_use', 'multi_agent', 'goals', 'memories', 'tool_search', 'tool_suggest',
  'image_generation', 'view_image', 'hooks', 'skill_search',
];

function codexHome(): string {
  return process.env.CODEX_HOME || path.join(homedir(), '.codex');
}

function readUserConfig(): string {
  try {
    return readFileSync(path.join(codexHome(), 'config.toml'), 'utf8');
  } catch {
    return '';
  }
}

/** Names of the user's MCP servers, so each one can be switched off for chat. */
export function mcpServerNames(toml: string): string[] {
  const names = new Set<string>();
  for (const m of toml.matchAll(/^\s*\[mcp_servers\.(?:"([^"]+)"|'([^']+)'|([A-Za-z0-9_-]+))[\].]/gm)) {
    names.add(m[1] ?? m[2] ?? m[3]);
  }
  return [...names];
}

export function codexArgs(toml: string): string[] {
  const args = ['app-server'];
  for (const feature of DISABLED_FEATURES) args.push('--disable', feature);
  args.push('-c', 'web_search=disabled', '-c', 'project_doc_max_bytes=0');
  for (const name of mcpServerNames(toml)) {
    const key = /^[A-Za-z0-9_-]+$/.test(name) ? name : JSON.stringify(name);
    args.push('-c', `mcp_servers.${key}.enabled=false`);
  }
  return args;
}

function windowLabel(minutes: unknown): string {
  if (typeof minutes !== 'number') return 'Limit';
  return minutes % 1440 === 0 ? `${minutes / 1440}d` : `${Math.round(minutes / 60)}h`;
}

export function parseCodexLimits(snapshot: any): UsageWindow[] {
  const windows: UsageWindow[] = [];
  for (const w of [snapshot?.primary, snapshot?.secondary]) {
    if (typeof w?.usedPercent === 'number') {
      windows.push({
        label: windowLabel(w.windowDurationMins),
        usedPercent: Math.round(w.usedPercent),
        resetsAt: typeof w.resetsAt === 'number' ? w.resetsAt * 1000 : undefined,
      });
    }
  }
  return windows;
}

export function classifyCodexError(text: string): ProviderError {
  const message = text.trim().slice(0, 500) || 'Codex stopped without an answer.';
  const lower = message.toLowerCase();
  if (/\b401\b|unauthorized|not logged in|please log ?in|sign in|authentication/.test(lower)) {
    return new ProviderError('auth', 'Codex is not signed in. Sign in to continue.');
  }
  if (/usage limit|rate limit|quota|\b429\b|limit reached/.test(lower)) return new ProviderError('limit', message);
  return new ProviderError('failed', message);
}

/** executable and toml can be set by tests. */
export function startCodexTurn(
  req: TurnRequest,
  executable = findCli('codex'),
  toml = readUserConfig(),
): RunningTurn {
  if (!executable) {
    return { done: Promise.reject(new ProviderError('not_found', 'The Codex CLI is not installed.')), stop() {} };
  }
  assertSafeId(req.model, 'model');
  if (req.sessionId) assertSafeId(req.sessionId, 'session id');

  const child = startCli(executable, codexArgs(toml), { cwd: req.cwd });
  let nextId = 0;
  const pending = new Map<number, { resolve(v: any): void; reject(e: Error): void }>();
  let threadId: string | undefined;
  let turnId: string | undefined;
  let stopped = false;
  let settled = false;
  let completed = false;
  let outcome: ProviderError | undefined;
  let stderr = '';
  let lastError = '';
  let lastItemId: string | undefined;
  let sentText = false;

  let resolveDone!: () => void;
  let rejectDone!: (e: ProviderError) => void;
  const done = new Promise<void>((resolve, reject) => {
    resolveDone = resolve;
    rejectDone = reject;
  });

  let idle: NodeJS.Timeout;
  let stopTimer: NodeJS.Timeout | undefined;
  const settle = (error?: ProviderError) => {
    if (settled) return;
    settled = true;
    clearTimeout(idle);
    clearTimeout(stopTimer);
    if (error) rejectDone(error);
    else resolveDone();
  };
  const resetIdle = () => {
    clearTimeout(idle);
    idle = setTimeout(() => {
      settle(new ProviderError('timeout', 'Codex stopped responding. Try again.'));
      killTree(child);
    }, IDLE_TIMEOUT_MS);
  };
  resetIdle();

  const write = (message: object) => {
    if (child.stdin!.writable) child.stdin!.write(JSON.stringify(message) + '\n');
  };
  const call = (method: string, params?: object) =>
    new Promise<any>((resolve, reject) => {
      const id = ++nextId;
      pending.set(id, { resolve, reject });
      write({ id, method, params });
    });

  const finish = async () => {
    // The turn is over: fetch the latest usage on the way out, then end the process.
    const timer = setTimeout(() => killTree(child), LIMITS_READ_MS);
    try {
      const limits = await call('account/rateLimits/read');
      const windows = parseCodexLimits(limits?.rateLimits);
      if (windows.length) req.onUsage(windows);
    } catch {
      // usage is optional
    }
    clearTimeout(timer);
    killTree(child);
  };

  createInterface({ input: child.stdout! }).on('line', (line) => {
    resetIdle();
    let m: any;
    try {
      m = JSON.parse(line);
    } catch {
      return;
    }
    if (m.id !== undefined && !m.method) {
      const handler = pending.get(m.id);
      pending.delete(m.id);
      if (m.error) handler?.reject(new Error(String(m.error.message ?? JSON.stringify(m.error))));
      else handler?.resolve(m.result);
      return;
    }
    if (m.id !== undefined) {
      // The server is asking for something (an approval or user input). Chat
      // never grants anything, so refuse.
      write({ id: m.id, error: { code: -32601, message: 'Not available in chat.' } });
      return;
    }
    const params = m.params ?? {};
    if (m.method === 'item/agentMessage/delta') {
      // One reply can have several message items; keep them as paragraphs.
      if (sentText && params.itemId !== lastItemId) req.onDelta('\n\n');
      lastItemId = params.itemId;
      sentText = true;
      req.onDelta(String(params.delta ?? ''));
    } else if (m.method === 'error') {
      if (!params.willRetry && params.error?.message) lastError = params.error.message;
    } else if (m.method === 'account/rateLimits/updated') {
      const windows = parseCodexLimits(params.rateLimits);
      if (windows.length) req.onUsage(windows);
    } else if (m.method === 'turn/completed') {
      const turn = params.turn;
      completed = true;
      if (turn?.status === 'failed') outcome = classifyCodexError(turn.error?.message || lastError);
      else if (turn?.status === 'interrupted' && !stopped) outcome = classifyCodexError(lastError || 'The reply was interrupted.');
      void finish();
    }
  });

  child.stderr!.on('data', (d) => (stderr = appendTail(stderr, String(d))));
  child.stdin!.on('error', () => {});
  child.on('error', (e: NodeJS.ErrnoException) => {
    settle(new ProviderError(e.code === 'ENOENT' ? 'not_found' : 'failed', `Could not start Codex: ${e.message}`));
  });
  child.on('close', () => {
    for (const handler of pending.values()) handler.reject(new Error('Codex exited.'));
    pending.clear();
    // Only now is the turn over: a running process holds the thread's writer
    // lock, and a follow-up sent any sooner would be refused.
    if (completed) settle(outcome);
    else if (stopped) settle();
    else settle(classifyCodexError(lastError || stderr.replace(/\x1b\[[0-9;]*m/g, '').split('\n').filter(Boolean).pop() || ''));
  });

  const resumeThread = async (id: string, thread: object) => {
    for (let attempt = 1; ; attempt++) {
      try {
        await call('thread/resume', { threadId: id, ...thread });
        return;
      } catch (e) {
        const message = (e as Error).message;
        if (/not found|no rollout|no such|invalid thread/i.test(message)) throw new ProviderError('session_missing', message);
        // The lock of a process that was just killed can take a moment to clear.
        if (!/active writer/i.test(message) || attempt >= RESUME_ATTEMPTS) throw e;
        await new Promise((resolve) => setTimeout(resolve, RESUME_RETRY_MS));
      }
    }
  };

  const run = async () => {
    await call('initialize', {
      clientInfo: { name: 'errchat', title: 'ErrChat', version: '0.1.0' },
      capabilities: null,
    });
    write({ method: 'initialized' });
    const thread = {
      cwd: req.cwd,
      model: req.model,
      approvalPolicy: 'never',
      sandbox: 'read-only',
      developerInstructions: CHAT_INSTRUCTIONS,
    };
    if (req.sessionId) {
      await resumeThread(req.sessionId, thread);
      threadId = req.sessionId;
    } else {
      threadId = (await call('thread/start', thread)).thread.id as string;
      req.onSession(threadId);
    }
    if (stopped) return killTree(child);
    const started = await call('turn/start', {
      threadId,
      input: [{ type: 'text', text: req.prompt, text_elements: [] }],
    });
    turnId = started?.turn?.id;
    if (stopped) interrupt();
  };

  const interrupt = () => {
    if (threadId && turnId) void call('turn/interrupt', { threadId, turnId }).catch(() => {});
  };

  run().catch((e) => {
    if (stopped) return;
    settle(e instanceof ProviderError ? e : classifyCodexError((e as Error).message));
    killTree(child);
  });

  return {
    done,
    stop() {
      if (stopped || settled) return;
      stopped = true;
      if (!turnId) {
        killTree(child);
        return;
      }
      // Interrupting keeps the thread clean for the next message.
      interrupt();
      stopTimer = setTimeout(() => killTree(child), STOP_GRACE_MS);
    },
  };
}

/** Models come from the CLI's own cache; its format isn't a public contract, so failures are fine. */
export function parseModelCache(json: string): ModelOption[] {
  try {
    const models = JSON.parse(json).models;
    return models
      .filter((m: any) => m?.visibility === 'list' && typeof m.slug === 'string')
      .map((m: any) => ({ id: m.slug, label: String(m.display_name ?? m.slug) }));
  } catch {
    return [];
  }
}

export function configuredModel(toml: string): string | undefined {
  return toml.match(/^\s*model\s*=\s*"([^"]+)"/m)?.[1];
}

function codexModels(): ModelOption[] {
  const cache = path.join(codexHome(), 'models_cache.json');
  const models = existsSync(cache) ? parseModelCache(readFileSync(cache, 'utf8')) : [];
  const configured = configuredModel(readUserConfig());
  if (configured && !models.some((m) => m.id === configured)) models.unshift({ id: configured, label: configured });
  return models;
}

export const codexProvider: Provider = {
  id: 'codex',
  name: 'Codex',
  models: codexModels,
  start: (req) => startCodexTurn(req),
  login() {
    const exe = findCli('codex');
    if (exe) openLoginConsole(exe, ['login']);
  },
  async status(): Promise<ProviderStatus> {
    const models = codexModels();
    const base = {
      id: 'codex' as const,
      name: 'Codex',
      models,
      defaultModel: configuredModel(readUserConfig()) ?? models[0]?.id ?? '',
    };
    const exe = findCli('codex');
    if (!exe) return { ...base, installed: false, loggedIn: false, usesSubscription: false, detail: 'Not installed' };
    const out = await capture(exe, ['login', 'status']);
    return { ...base, installed: true, ...parseCodexStatus(out.stdout + out.stderr) };
  },
};

export function parseCodexStatus(text: string): Pick<ProviderStatus, 'loggedIn' | 'usesSubscription' | 'detail'> {
  const lower = text.toLowerCase();
  if (!lower.includes('logged in') || lower.includes('not logged in')) {
    return { loggedIn: false, usesSubscription: false, detail: 'Not signed in' };
  }
  const usesSubscription = lower.includes('chatgpt');
  return { loggedIn: true, usesSubscription, detail: usesSubscription ? 'ChatGPT plan' : 'Signed in with an API key (billed per use)' };
}
