// Claude backend: `claude -p`, one process per reply, continued with --resume.

import { createInterface } from 'node:readline';
import type { ModelOption, ProviderStatus, UsageWindow } from '../shared/types.ts';
import { appendTail, capture, findCli, killTree, openLoginConsole, startCli } from './cli.ts';
import { assertSafeId, CHAT_INSTRUCTIONS, IDLE_TIMEOUT_MS, ProviderError } from './provider.ts';
import type { Provider, RunningTurn, TurnRequest } from './provider.ts';

const MODELS: ModelOption[] = [
  { id: 'sonnet', label: 'Sonnet' },
  { id: 'opus', label: 'Opus' },
  { id: 'haiku', label: 'Haiku' },
  // On Pro this model needs paid usage credits rather than the plan's allowance.
  { id: 'fable', label: 'Fable (may use credits)' },
];

export function claudeArgs(model: string, sessionId?: string): string[] {
  assertSafeId(model, 'model');
  const args = [
    '-p', // the prompt goes through stdin
    '--output-format', 'stream-json',
    '--verbose',
    '--include-partial-messages',
    '--model', model,
    '--system-prompt', CHAT_INSTRUCTIONS,
    // Chat only: nothing the model could act with.
    '--tools', '',
    '--strict-mcp-config',
    '--disable-slash-commands',
    '--setting-sources', '',
  ];
  if (sessionId) {
    assertSafeId(sessionId, 'session id');
    args.push('--resume', sessionId);
  }
  return args;
}

export type ClaudeEvent =
  | { type: 'session'; id: string }
  | { type: 'delta'; text: string }
  | { type: 'usage'; windows: UsageWindow[] }
  | { type: 'result'; isError: boolean; text: string };

/** Turns one stream-json line into an event, or null for lines we don't use. */
export function parseClaudeLine(line: string): ClaudeEvent | null {
  let event: any;
  try {
    event = JSON.parse(line);
  } catch {
    return null;
  }
  switch (event?.type) {
    case 'system':
      return event.subtype === 'init' && event.session_id ? { type: 'session', id: event.session_id } : null;
    case 'stream_event': {
      const delta = event.event?.delta;
      if (event.parent_tool_use_id || delta?.type !== 'text_delta') return null;
      return { type: 'delta', text: String(delta.text ?? '') };
    }
    case 'rate_limit_event': {
      const unified = event.rate_limit_info?.unifiedWindows;
      if (!unified) return null;
      const windows: UsageWindow[] = [];
      for (const [key, label] of [['five_hour', '5h'], ['seven_day', '7d']] as const) {
        const w = unified[key];
        if (typeof w?.utilization === 'number') {
          windows.push({
            label,
            usedPercent: Math.round(w.utilization * 100),
            resetsAt: typeof w.resetsAt === 'number' ? w.resetsAt * 1000 : undefined,
          });
        }
      }
      return windows.length ? { type: 'usage', windows } : null;
    }
    case 'result':
      return { type: 'result', isError: Boolean(event.is_error), text: String(event.result ?? '') };
    default:
      return null;
  }
}

export function classifyClaudeError(text: string): ProviderError {
  const message = text.trim().slice(0, 500) || 'Claude stopped without an answer.';
  const lower = message.toLowerCase();
  if (lower.includes('no conversation found')) return new ProviderError('session_missing', message);
  if (/not logged in|\/login|claude login|invalid api key|unauthorized|failed to authenticate|token has expired/.test(lower)) {
    return new ProviderError('auth', 'Claude is not signed in. Sign in to continue.');
  }
  if (/hit your .*limit|usage limit|rate limit|limit reached|resets? (at|in)/.test(lower)) {
    return new ProviderError('limit', message);
  }
  return new ProviderError('failed', message);
}

/** executable can be set by tests. */
export function startClaudeTurn(req: TurnRequest, executable = findCli('claude')): RunningTurn {
  if (!executable) {
    return {
      done: Promise.reject(new ProviderError('not_found', 'The Claude CLI is not installed.')),
      stop() {},
    };
  }
  const child = startCli(executable, claudeArgs(req.model, req.sessionId), { cwd: req.cwd });
  let stopped = false;
  let timedOut = false;
  let sentText = false;
  let result: Extract<ClaudeEvent, { type: 'result' }> | undefined;
  let stderr = '';

  let idle: NodeJS.Timeout;
  const resetIdle = () => {
    clearTimeout(idle);
    idle = setTimeout(() => {
      timedOut = true;
      killTree(child);
    }, IDLE_TIMEOUT_MS);
  };
  resetIdle();

  createInterface({ input: child.stdout! }).on('line', (line) => {
    resetIdle();
    const event = parseClaudeLine(line);
    if (!event) return;
    if (event.type === 'session') req.onSession(event.id);
    else if (event.type === 'usage') req.onUsage(event.windows);
    else if (event.type === 'delta') {
      if (event.text) sentText = true;
      req.onDelta(event.text);
    } else result = event;
  });
  child.stderr!.on('data', (d) => (stderr = appendTail(stderr, String(d))));
  child.stdin!.on('error', () => {}); // a CLI that exits early closes stdin; 'close' reports it
  child.stdin!.end(req.prompt, 'utf8');

  const done = new Promise<void>((resolve, reject) => {
    child.on('error', (e: NodeJS.ErrnoException) => {
      clearTimeout(idle);
      reject(new ProviderError(e.code === 'ENOENT' ? 'not_found' : 'failed', `Could not start Claude: ${e.message}`));
    });
    child.on('close', (code) => {
      clearTimeout(idle);
      if (stopped) return resolve();
      if (timedOut) return reject(new ProviderError('timeout', 'Claude stopped responding. Try again.'));
      // Some failures (a missing session, for one) leave the result text empty
      // and only explain themselves on stderr.
      if (result?.isError || code !== 0 || !result) return reject(classifyClaudeError(result?.text || stderr));
      // Some replies arrive without partial chunks; the result still has the text.
      if (!sentText && result.text) req.onDelta(result.text);
      resolve();
    });
  });

  return {
    done,
    stop() {
      stopped = true;
      killTree(child);
    },
  };
}

export const claudeProvider: Provider = {
  id: 'claude',
  name: 'Claude',
  models: () => MODELS,
  start: (req) => startClaudeTurn(req),
  login() {
    const exe = findCli('claude');
    if (exe) openLoginConsole(exe, ['auth', 'login', '--claudeai']);
  },
  async status(): Promise<ProviderStatus> {
    const base = { id: 'claude' as const, name: 'Claude', models: MODELS, defaultModel: 'sonnet' };
    const exe = findCli('claude');
    if (!exe) return { ...base, installed: false, loggedIn: false, usesSubscription: false, detail: 'Not installed' };
    const out = await capture(exe, ['auth', 'status']);
    return { ...base, installed: true, ...parseClaudeStatus(out.stdout) };
  },
};

export function parseClaudeStatus(stdout: string): Pick<ProviderStatus, 'loggedIn' | 'usesSubscription' | 'detail'> {
  try {
    const s = JSON.parse(stdout);
    if (!s.loggedIn) return { loggedIn: false, usesSubscription: false, detail: 'Not signed in' };
    const usesSubscription = s.authMethod === 'claude.ai';
    const plan = s.subscriptionType ? `${s.subscriptionType[0].toUpperCase()}${s.subscriptionType.slice(1)} plan` : 'Subscription';
    return { loggedIn: true, usesSubscription, detail: usesSubscription ? plan : 'Signed in with an API key (billed per use)' };
  } catch {
    return { loggedIn: false, usesSubscription: false, detail: 'Status unknown' };
  }
}
