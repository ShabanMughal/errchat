// Runs conversations: creates messages, drives a provider, reports progress.
// No Electron imports, so it can be tested with a fake provider.

import { randomUUID } from 'node:crypto';
import type { ChatEvent, ChatMessage, Conversation, ProviderId, SendRequest } from '../shared/types.ts';
import { ProviderError } from './provider.ts';
import type { Provider, RunningTurn } from './provider.ts';
import type { Store } from './store.ts';

const TITLE_LENGTH = 60;
export const MAX_MESSAGE_CHARS = 200_000;

function makeTitle(text: string): string {
  const line = text.trim().replace(/\s+/g, ' ');
  return line.length > TITLE_LENGTH ? `${line.slice(0, TITLE_LENGTH).trimEnd()}…` : line;
}

function message(role: ChatMessage['role'], content: string, status?: ChatMessage['status']): ChatMessage {
  return { id: randomUUID(), role, content, createdAt: Date.now(), status };
}

/**
 * For when the CLI no longer has the session (Claude deletes old transcripts,
 * for example): resend the local history so the conversation can carry on.
 */
export function replayPrompt(history: ChatMessage[], latest: string): string {
  const lines = history
    .filter((m) => m.content)
    .map((m) => `${m.role === 'user' ? 'User' : 'Assistant'}: ${m.content}`)
    .join('\n\n');
  return (
    'Here is our conversation so far, for context:\n' +
    `<previous_conversation>\n${lines}\n</previous_conversation>\n\n` +
    `Continue it by replying to my latest message:\n\n${latest}`
  );
}

export class ChatService {
  private store: Store;
  private providers: Record<ProviderId, Provider>;
  private cwd: string;
  private emit: (event: ChatEvent) => void;
  private active = new Map<string, { turn: RunningTurn; stopRequested: boolean }>();

  constructor(
    store: Store,
    providers: Record<ProviderId, Provider>,
    cwd: string,
    emit: (event: ChatEvent) => void,
  ) {
    this.store = store;
    this.providers = providers;
    this.cwd = cwd;
    this.emit = emit;
  }

  isActive(id: string): boolean {
    return this.active.has(id);
  }

  send(req: SendRequest): Conversation {
    const text = req.text.trim();
    if (!text) throw new Error('Message is empty.');
    if (text.length > MAX_MESSAGE_CHARS) throw new Error('Message is too long.');

    let conversation = req.conversationId ? this.store.find(req.conversationId) : undefined;
    if (req.conversationId && !conversation) throw new Error('Conversation not found.');
    if (conversation && this.isActive(conversation.id)) throw new Error('A reply is already in progress.');
    if (!conversation) {
      const now = Date.now();
      conversation = {
        id: randomUUID(),
        title: makeTitle(text),
        provider: req.provider,
        model: req.model,
        createdAt: now,
        updatedAt: now,
        messages: [],
      };
      this.store.add(conversation);
    } else {
      conversation.model = req.model;
    }
    conversation.messages.push(message('user', text));
    void this.runTurn(conversation);
    return conversation;
  }

  /** Runs the last user message again, replacing a failed or stopped reply. */
  retry(id: string): void {
    const conversation = this.store.find(id);
    if (!conversation || this.isActive(id)) return;
    const last = conversation.messages.at(-1);
    if (last?.role === 'assistant' && (last.status === 'error' || last.status === 'stopped')) {
      conversation.messages.pop();
    }
    if (conversation.messages.at(-1)?.role !== 'user') return;
    void this.runTurn(conversation);
  }

  setModel(id: string, model: string): void {
    const conversation = this.store.find(id);
    if (!conversation) return;
    conversation.model = model;
    this.store.save();
  }

  stop(id: string): void {
    const running = this.active.get(id);
    if (!running) return;
    running.stopRequested = true;
    running.turn.stop();
  }

  stopAll(): void {
    for (const id of this.active.keys()) this.stop(id);
  }

  remove(id: string): void {
    this.stop(id);
    this.store.remove(id);
  }

  private changed(conversation: Conversation): void {
    conversation.updatedAt = Date.now();
    this.store.save();
    this.emit({ type: 'update', conversation });
  }

  private async runTurn(conversation: Conversation): Promise<void> {
    const latest = conversation.messages.at(-1)!;
    const history = conversation.messages.slice(0, -1);
    const reply = message('assistant', '', 'streaming');
    conversation.messages.push(reply);
    this.changed(conversation);

    // Without a session, earlier messages can only reach the model by resending them.
    const hasHistory = history.some((m) => m.role === 'assistant' && m.content);
    const firstPrompt = !conversation.sessionId && hasHistory ? replayPrompt(history, latest.content) : latest.content;

    const provider = this.providers[conversation.provider];
    const attempt = async (prompt: string) => {
      const turn = provider.start({
        prompt,
        model: conversation.model,
        sessionId: conversation.sessionId,
        cwd: this.cwd,
        onSession: (id) => {
          conversation.sessionId = id;
          this.store.save();
        },
        onDelta: (text) => {
          reply.content += text;
          this.store.save();
          this.emit({ type: 'delta', conversationId: conversation.id, messageId: reply.id, text });
        },
        onUsage: (windows) => this.emit({ type: 'usage', provider: provider.id, windows }),
      });
      const running = this.active.get(conversation.id);
      if (running) running.turn = turn;
      else this.active.set(conversation.id, { turn, stopRequested: false });
      await turn.done;
    };

    try {
      try {
        await attempt(firstPrompt);
      } catch (e) {
        const stopRequested = this.active.get(conversation.id)?.stopRequested;
        if (!(e instanceof ProviderError) || e.kind !== 'session_missing' || reply.content || stopRequested) throw e;
        conversation.sessionId = undefined;
        await attempt(replayPrompt(history, latest.content));
      }
      if (this.active.get(conversation.id)?.stopRequested) reply.status = 'stopped';
      else if (!reply.content) throw new ProviderError('failed', 'No reply came back. Try again.');
      else reply.status = undefined;
    } catch (e) {
      reply.status = 'error';
      reply.error =
        e instanceof ProviderError
          ? { kind: e.kind, message: e.message }
          : { kind: 'failed', message: (e as Error).message };
    } finally {
      this.active.delete(conversation.id);
    }
    this.changed(conversation);
  }
}
