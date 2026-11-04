// Conversations and settings, kept in one local JSON file.

import { existsSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import type { Conversation, Settings } from '../shared/types.ts';

interface StoreData {
  version: 1;
  conversations: Conversation[];
  settings: Settings;
}

const SAVE_DELAY_MS = 500;

export class Store {
  data: StoreData;
  private file: string;
  private timer: NodeJS.Timeout | undefined;

  constructor(file: string) {
    this.file = file;
    this.data = this.load();
  }

  private load(): StoreData {
    const empty: StoreData = { version: 1, conversations: [], settings: { provider: 'claude', models: {} } };
    if (!existsSync(this.file)) return empty;
    try {
      const parsed = JSON.parse(readFileSync(this.file, 'utf8'));
      if (!Array.isArray(parsed.conversations)) throw new Error('bad shape');
      const data: StoreData = { ...empty, ...parsed };
      // A reply that was mid-stream when the app last closed is finished now.
      for (const c of data.conversations) {
        for (const m of c.messages) if (m.status === 'streaming') m.status = 'stopped';
      }
      return data;
    } catch {
      // Keep the unreadable file so history can still be recovered by hand.
      renameSync(this.file, `${this.file}.corrupt-${Date.now()}`);
      return empty;
    }
  }

  find(id: string): Conversation | undefined {
    return this.data.conversations.find((c) => c.id === id);
  }

  add(conversation: Conversation): void {
    this.data.conversations.unshift(conversation);
    this.save();
  }

  remove(id: string): void {
    this.data.conversations = this.data.conversations.filter((c) => c.id !== id);
    this.save();
  }

  save(): void {
    if (this.timer) return;
    this.timer = setTimeout(() => this.flush(), SAVE_DELAY_MS);
    this.timer.unref();
  }

  flush(): void {
    clearTimeout(this.timer);
    this.timer = undefined;
    // Write then rename, so a crash can't leave a half-written history.
    const temp = `${this.file}.tmp`;
    try {
      writeFileSync(temp, JSON.stringify(this.data), 'utf8');
      renameSync(temp, this.file);
    } catch (e) {
      // A locked or full disk shouldn't take the chat down; the next save tries again.
      console.error('Could not save conversations:', e);
    }
  }
}
