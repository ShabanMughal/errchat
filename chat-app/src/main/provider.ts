// The contract each CLI backend implements.

import type { ErrorKind, ModelOption, ProviderId, ProviderStatus, UsageWindow } from '../shared/types.ts';

export class ProviderError extends Error {
  kind: ErrorKind;

  constructor(kind: ErrorKind, message: string) {
    super(message);
    this.kind = kind;
  }
}

export interface TurnRequest {
  prompt: string;
  model: string;
  sessionId?: string;
  /** Stable empty directory. Claude finds its sessions by working directory. */
  cwd: string;
  onSession(id: string): void;
  onDelta(text: string): void;
  onUsage(windows: UsageWindow[]): void;
}

export interface RunningTurn {
  /** Resolves when the reply is finished or stopped; rejects with ProviderError. */
  done: Promise<void>;
  stop(): void;
}

export interface Provider {
  id: ProviderId;
  name: string;
  status(): Promise<ProviderStatus>;
  models(): ModelOption[];
  start(req: TurnRequest): RunningTurn;
  login(): void;
}

/** A silent CLI after this long is treated as hung. */
export const IDLE_TIMEOUT_MS = 5 * 60 * 1000;

const SAFE_ID = /^[\w.:\[\]-]{1,128}$/;

/** Model and session ids reach a command line, so only plain tokens are accepted. */
export function assertSafeId(value: string, what: string): void {
  if (!SAFE_ID.test(value)) throw new ProviderError('failed', `Invalid ${what}: ${value.slice(0, 60)}`);
}

export const CHAT_INSTRUCTIONS =
  'You are a helpful assistant in a general-purpose desktop chat app. ' +
  'Answer the user directly in conversation. Use Markdown when it helps. ' +
  'You have no access to files, commands or the web here, so do not try to use tools.';
