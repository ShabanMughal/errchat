// Types shared by the main process, preload and renderer.

export type ProviderId = 'claude' | 'codex';

export type ErrorKind = 'not_found' | 'auth' | 'limit' | 'session_missing' | 'timeout' | 'failed';

export interface MessageError {
  kind: ErrorKind;
  message: string;
}

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  createdAt: number;
  status?: 'streaming' | 'stopped' | 'error';
  error?: MessageError;
}

export interface Conversation {
  id: string;
  title: string;
  provider: ProviderId;
  model: string;
  /** The CLI's own session or thread id, used to continue the conversation. */
  sessionId?: string;
  createdAt: number;
  updatedAt: number;
  messages: ChatMessage[];
}

export interface Settings {
  provider: ProviderId;
  models: Partial<Record<ProviderId, string>>;
}

export interface ModelOption {
  id: string;
  label: string;
}

export interface UsageWindow {
  label: string;
  usedPercent: number;
  /** Milliseconds since the epoch. */
  resetsAt?: number;
}

export interface ProviderStatus {
  id: ProviderId;
  name: string;
  installed: boolean;
  loggedIn: boolean;
  /** False when the CLI is signed in with an API key, which bills per use. */
  usesSubscription: boolean;
  detail: string;
  models: ModelOption[];
  defaultModel: string;
}

export type ChatEvent =
  | { type: 'update'; conversation: Conversation }
  | { type: 'delta'; conversationId: string; messageId: string; text: string }
  | { type: 'usage'; provider: ProviderId; windows: UsageWindow[] }
  | { type: 'focus' };

export interface Bootstrap {
  conversations: Conversation[];
  settings: Settings;
  providers: ProviderStatus[];
}

export interface SendRequest {
  conversationId: string | null;
  text: string;
  provider: ProviderId;
  model: string;
}

/** What the preload script exposes to the page as window.errchat. */
export interface ErrchatApi {
  bootstrap(): Promise<Bootstrap>;
  send(req: SendRequest): Promise<Conversation>;
  retry(conversationId: string): Promise<void>;
  stop(conversationId: string): Promise<void>;
  remove(conversationId: string): Promise<void>;
  setModel(conversationId: string, model: string): Promise<void>;
  saveSettings(settings: Settings): Promise<void>;
  checkProviders(): Promise<ProviderStatus[]>;
  login(provider: ProviderId): Promise<void>;
  copy(text: string): Promise<void>;
  openExternal(url: string): Promise<void>;
  onEvent(handler: (event: ChatEvent) => void): void;
}
