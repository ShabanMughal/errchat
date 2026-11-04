// Finding, starting and stopping the CLI executables.

import { spawn, spawnSync } from 'node:child_process';
import type { ChildProcess, SpawnOptions } from 'node:child_process';
import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { ProviderError } from './provider.ts';

// A GUI app can start with a shorter PATH than a terminal, so look in the
// default install locations too.
const FALLBACK_DIRS: Record<string, string[]> = {
  claude: [path.join(homedir(), '.local', 'bin')],
  codex: [path.join(process.env.LOCALAPPDATA ?? '', 'Programs', 'OpenAI', 'Codex', 'bin')],
};

export function findCli(name: string, env: NodeJS.ProcessEnv = process.env): string | null {
  const pathValue = env.PATH ?? env.Path ?? '';
  const dirs = [...pathValue.split(path.delimiter).filter(Boolean), ...(FALLBACK_DIRS[name] ?? [])];
  const extensions = process.platform === 'win32' ? ['.exe', '.cmd', '.bat'] : [''];
  for (const dir of dirs) {
    for (const ext of extensions) {
      const candidate = path.join(dir, name + ext);
      if (existsSync(candidate)) return candidate;
    }
  }
  return null;
}

// Removed from the CLI's environment. An API key here would make the CLI bill
// it instead of the subscription, and the CLAUDE_CODE_ session variables (set
// when launched from inside Claude Code) can change how the CLI authenticates.
const BLOCKED_ENV = new Set([
  'ANTHROPIC_API_KEY',
  'ANTHROPIC_AUTH_TOKEN',
  'ANTHROPIC_BASE_URL',
  'OPENAI_API_KEY',
  'OPENAI_BASE_URL',
  'CODEX_API_KEY',
  'CLAUDECODE',
  'ELECTRON_RUN_AS_NODE',
]);

export function childEnv(env: NodeJS.ProcessEnv = process.env): NodeJS.ProcessEnv {
  const out: NodeJS.ProcessEnv = {};
  for (const [key, value] of Object.entries(env)) {
    const upper = key.toUpperCase();
    if (BLOCKED_ENV.has(upper)) continue;
    if (upper.startsWith('CLAUDE_CODE_') && upper !== 'CLAUDE_CODE_OAUTH_TOKEN') continue;
    out[key] = value;
  }
  return out;
}

/** Node can't start .cmd shims (npm installs) directly, so they go through cmd.exe. */
export function buildSpawn(executable: string, args: string[]): { command: string; args: string[]; verbatim: boolean } {
  if (!/\.(cmd|bat)$/i.test(executable)) return { command: executable, args, verbatim: false };
  for (const arg of [executable, ...args]) {
    if (/["%\r\n]/.test(arg)) throw new ProviderError('failed', 'This argument cannot be passed safely through cmd.exe.');
  }
  const line = [executable, ...args].map((arg) => `"${arg}"`).join(' ');
  return { command: process.env.ComSpec ?? 'cmd.exe', args: ['/d', '/s', '/c', `"${line}"`], verbatim: true };
}

export function startCli(executable: string, args: string[], options: SpawnOptions = {}): ChildProcess {
  const built = buildSpawn(executable, args);
  return spawn(built.command, built.args, {
    env: childEnv(),
    windowsHide: true,
    windowsVerbatimArguments: built.verbatim,
    stdio: ['pipe', 'pipe', 'pipe'],
    ...options,
  });
}

/** The CLIs can have child processes, so stopping kills the whole tree. */
export function killTree(child: ChildProcess): void {
  if (child.pid === undefined || child.exitCode !== null) return;
  if (process.platform === 'win32') {
    spawnSync('taskkill', ['/pid', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
  } else {
    child.kill('SIGKILL');
  }
}

export interface CaptureResult {
  code: number | null;
  stdout: string;
  stderr: string;
}

export function capture(executable: string, args: string[], timeoutMs = 15000): Promise<CaptureResult> {
  return new Promise((resolve) => {
    let child: ChildProcess;
    try {
      child = startCli(executable, args, { stdio: ['ignore', 'pipe', 'pipe'] });
    } catch (e) {
      resolve({ code: null, stdout: '', stderr: String(e) });
      return;
    }
    let stdout = '';
    let stderr = '';
    const timer = setTimeout(() => killTree(child), timeoutMs);
    child.stdout?.on('data', (d) => (stdout += d));
    child.stderr?.on('data', (d) => (stderr += d));
    child.on('error', (e) => {
      clearTimeout(timer);
      resolve({ code: null, stdout, stderr: stderr + String(e) });
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      resolve({ code, stdout, stderr });
    });
  });
}

/** Opens a sign-in command in its own console window. */
export function openLoginConsole(executable: string, args: string[]): void {
  const child = startCli(executable, args, { detached: true, stdio: 'ignore', windowsHide: false });
  child.on('error', () => {});
  child.unref();
}

/** Keeps the end of a stream for error messages without unbounded growth. */
export function appendTail(current: string, chunk: string, max = 8192): string {
  const next = current + chunk;
  return next.length > max ? next.slice(-max) : next;
}
