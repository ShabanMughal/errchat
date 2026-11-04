// The chat UI. Plain DOM: the main process owns all state that matters, and
// this file draws it.

import type {
  ChatEvent,
  ChatMessage,
  Conversation,
  ErrchatApi,
  ProviderId,
  ProviderStatus,
  Settings,
  UsageWindow,
} from '../shared/types.ts';
import { renderMarkdown } from './markdown.ts';

declare global {
  interface Window {
    errchat: ErrchatApi;
  }
}

const api = window.errchat;
const INSTALL_LINKS: Record<ProviderId, string> = {
  claude: 'https://claude.com/download',
  codex: 'https://github.com/openai/codex',
};
const STATUS_RECHECK_MS = 10000;
const STICK_TO_BOTTOM_PX = 80;
const INPUT_MAX_HEIGHT = 240;

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const sidebar = $('sidebar');
const scrim = $('scrim');
const chatList = $('chat-list');
const accounts = $('accounts');
const titleEl = $('title');
const messagesEl = $('messages');
const composer = $<HTMLFormElement>('composer');
const input = $<HTMLTextAreaElement>('input');
const inputLabel = $('input-label');
const providerPicker = $('provider-picker');
const modelSelect = $<HTMLSelectElement>('model');
const sendButton = $<HTMLButtonElement>('send');
const composerNote = $('composer-note');

let conversations: Conversation[] = [];
let providers: ProviderStatus[] = [];
let settings: Settings = { provider: 'claude', models: {} };
let currentId: string | null = null;
let confirmingDelete: string | null = null;
let lastStatusCheck = 0;
const usage = new Map<ProviderId, UsageWindow[]>();
const drafts = new Map<string | null, string>();

// --- small DOM helpers ---

function el<K extends keyof HTMLElementTagNameMap>(tag: K, className?: string, text?: string): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function button(className: string, text: string, onClick: () => void): HTMLButtonElement {
  const node = el('button', className, text);
  node.type = 'button';
  node.addEventListener('click', onClick);
  return node;
}

function svgIcon(path: string): SVGSVGElement {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', 'icon');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  const p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  p.setAttribute('d', path);
  svg.append(p);
  return svg;
}

const ICON_TRASH = 'M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v5M14 11v5';
const ICON_ALERT = 'M12 3 2 21h20L12 3zM12 10v5M12 18v.01';

// --- state helpers ---

const current = () => conversations.find((c) => c.id === currentId) ?? null;
const providerOf = (id: ProviderId) => providers.find((p) => p.id === id);
const activeProvider = (): ProviderId => current()?.provider ?? settings.provider;
const isStreaming = (c: Conversation | null) => c?.messages.at(-1)?.status === 'streaming';

function activeModel(): string {
  const conversation = current();
  if (conversation) return conversation.model;
  const provider = providerOf(settings.provider);
  const saved = settings.models[settings.provider];
  if (saved && provider?.models.some((m) => m.id === saved)) return saved;
  return provider?.defaultModel ?? '';
}

function sortConversations(): void {
  conversations.sort((a, b) => b.updatedAt - a.updatedAt);
}

function formatDate(ms: number): string {
  const date = new Date(ms);
  return date.toDateString() === new Date().toDateString()
    ? date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
    : date.toLocaleDateString([], { month: 'short', day: 'numeric' });
}

function closeDrawer(): void {
  sidebar.classList.remove('open');
  scrim.hidden = true;
}

// --- drawing ---

function renderChatList(): void {
  chatList.replaceChildren();
  if (!conversations.length) {
    chatList.append(el('p', 'chat-empty', 'Your chats will appear here.'));
    return;
  }
  for (const c of conversations) {
    const row = el('div', c.id === currentId ? 'chat-row current' : 'chat-row');
    if (confirmingDelete === c.id) {
      const confirm = el('div', 'chat-confirm');
      confirm.append(
        el('span', '', 'Delete this chat?'),
        button('btn small', 'Delete', () => void removeConversation(c.id)),
        button('btn small', 'Keep', () => {
          confirmingDelete = null;
          renderChatList();
        }),
      );
      row.append(confirm);
    } else {
      const open = button('chat-open', '', () => selectConversation(c.id));
      open.title = c.title;
      if (c.id === currentId) open.setAttribute('aria-current', 'true');
      const name = providerOf(c.provider)?.name ?? c.provider;
      open.append(el('span', 'chat-name', c.title), el('span', 'chat-meta', `${name} · ${formatDate(c.updatedAt)}`));
      const remove = button('icon-btn', '', () => {
        confirmingDelete = c.id;
        renderChatList();
      });
      remove.setAttribute('aria-label', `Delete chat: ${c.title}`);
      remove.title = 'Delete chat';
      remove.append(svgIcon(ICON_TRASH));
      row.append(open, remove);
    }
    chatList.append(row);
  }
}

function usageTitle(windows: UsageWindow[]): string {
  return windows
    .filter((w) => w.resetsAt)
    .map((w) => `${w.label} limit resets ${new Date(w.resetsAt!).toLocaleString()}`)
    .join('\n');
}

function renderAccounts(): void {
  accounts.replaceChildren();
  for (const p of providers) {
    const block = el('div');
    const head = el('div', 'account-head');
    head.append(el('span', 'account-name', p.name));
    if (!p.installed) {
      head.append(button('btn small', 'Install', () => void api.openExternal(INSTALL_LINKS[p.id])));
    } else if (!p.loggedIn) {
      head.append(button('btn small', 'Sign in', () => void api.login(p.id)));
    }
    block.append(head);
    const warning = p.loggedIn && !p.usesSubscription;
    block.append(el('p', warning ? 'account-detail warning' : 'account-detail', p.detail));
    const windows = usage.get(p.id);
    if (windows?.length) {
      const line = el('p', 'account-usage', windows.map((w) => `${w.label}: ${w.usedPercent}% used`).join(' · '));
      line.title = usageTitle(windows);
      block.append(line);
    }
    accounts.append(block);
  }
}

function renderComposer(): void {
  const conversation = current();
  const providerId = activeProvider();
  const provider = providerOf(providerId);
  const started = Boolean(conversation?.messages.length);
  inputLabel.textContent = `Message ${provider?.name ?? ''}`.trim();

  providerPicker.replaceChildren();
  for (const p of providers) {
    const option = button('provider-option', p.name, () => selectProvider(p.id));
    option.setAttribute('role', 'radio');
    option.setAttribute('aria-checked', String(p.id === providerId));
    // A conversation stays with the provider it started on: the sessions aren't portable.
    option.disabled = started;
    if (started) option.title = 'Start a new chat to change this';
    providerPicker.append(option);
  }

  modelSelect.replaceChildren();
  const models = provider?.models ?? [];
  const selected = activeModel();
  for (const m of models) modelSelect.append(new Option(m.label, m.id, false, m.id === selected));
  if (selected && !models.some((m) => m.id === selected)) modelSelect.append(new Option(selected, selected, false, true));
  modelSelect.disabled = !modelSelect.options.length;

  renderSendButton();

  composerNote.replaceChildren();
  if (provider && !provider.installed) {
    composerNote.append(
      el('span', '', `The ${provider.name} CLI isn't installed on this PC.`),
      button('btn small', 'Install', () => void api.openExternal(INSTALL_LINKS[provider.id])),
    );
  } else if (provider && !provider.loggedIn) {
    composerNote.append(
      el('span', '', `${provider.name} isn't signed in.`),
      button('btn small', 'Sign in', () => void api.login(provider.id)),
    );
  } else if (provider && !provider.usesSubscription) {
    composerNote.append(el('span', '', `${provider.name} is using an API key, so messages are billed per use.`));
  }
}

function renderSendButton(): void {
  const streaming = isStreaming(current());
  sendButton.textContent = streaming ? 'Stop' : 'Send';
  sendButton.classList.toggle('primary', !streaming);
  sendButton.disabled = !streaming && !input.value.trim();
}

function renderMessage(m: ChatMessage, conversation: Conversation, isLast: boolean): HTMLElement {
  const wrap = el('article', `message ${m.role}`);
  wrap.dataset.id = m.id;
  if (m.role === 'user') {
    wrap.append(el('div', 'user-text', m.content));
    return wrap;
  }

  const body = el('div', 'markdown');
  if (m.content) renderMarkdown(body, m.content, (code) => void api.copy(code));
  wrap.append(body);

  if (m.status === 'streaming') {
    if (!m.content) {
      const thinking = el('div', 'thinking');
      thinking.append(el('span', 'spinner'), el('span', '', 'Thinking…'));
      wrap.append(thinking);
    }
    return wrap;
  }

  if (m.error) {
    const error = el('div', 'message-error');
    error.setAttribute('role', 'alert');
    error.append(svgIcon(ICON_ALERT), el('span', '', m.error.message));
    wrap.append(error);
  }

  const tools = el('div', 'message-tools');
  if (m.content) {
    const copy = button('text-btn', 'Copy', () => {
      void api.copy(m.content);
      copy.textContent = 'Copied';
      setTimeout(() => (copy.textContent = 'Copy'), 1500);
    });
    tools.append(copy);
  }
  if (isLast && (m.status === 'error' || m.status === 'stopped')) {
    if (m.error?.kind === 'auth') {
      tools.append(button('btn small', 'Sign in', () => void api.login(conversation.provider)));
    } else if (m.error?.kind === 'not_found') {
      tools.append(button('btn small', 'Install', () => void api.openExternal(INSTALL_LINKS[conversation.provider])));
    }
    tools.append(button('btn small', 'Try again', () => void api.retry(conversation.id)));
  }
  if (m.status === 'stopped') tools.append(el('span', 'message-note', 'Stopped'));
  if (tools.childElementCount) wrap.append(tools);
  return wrap;
}

function renderStart(column: HTMLElement): void {
  messagesEl.append(column);
  column.classList.add('start');
  column.append(
    el('h2', '', 'Start a conversation'),
    el('p', '', 'Pick Claude or Codex below. Replies use your signed-in subscription, not an API key, and your chats are saved only on this PC.'),
  );
}

function renderMessages(): void {
  const conversation = current();
  const column = el('div', 'column');
  messagesEl.replaceChildren();
  if (!conversation) {
    renderStart(column);
    return;
  }
  conversation.messages.forEach((m, i) => {
    column.append(renderMessage(m, conversation, i === conversation.messages.length - 1));
  });
  messagesEl.append(column);
}

function scrollToBottom(): void {
  messagesEl.scrollTop = messagesEl.scrollHeight;
}

function isNearBottom(): boolean {
  return messagesEl.scrollHeight - messagesEl.scrollTop - messagesEl.clientHeight < STICK_TO_BOTTOM_PX;
}

function renderAll(): void {
  titleEl.textContent = current()?.title ?? 'New chat';
  titleEl.title = titleEl.textContent;
  renderChatList();
  renderAccounts();
  renderMessages();
  renderComposer();
}

// --- streaming: redraw only the growing message, at most once a frame ---

const dirty = new Set<string>();
let frame = 0;

function queueStreamRender(messageId: string): void {
  dirty.add(messageId);
  if (!frame) frame = requestAnimationFrame(flushStreamRender);
}

function flushStreamRender(): void {
  frame = 0;
  const conversation = current();
  const stick = isNearBottom();
  for (const id of dirty) {
    const message = conversation?.messages.find((m) => m.id === id);
    const node = messagesEl.querySelector<HTMLElement>(`[data-id="${CSS.escape(id)}"]`);
    if (!message || !node) continue;
    node.querySelector('.thinking')?.remove();
    renderMarkdown(node.querySelector<HTMLElement>('.markdown')!, message.content, (code) => void api.copy(code));
  }
  dirty.clear();
  if (stick) scrollToBottom();
}

// --- actions ---

function saveDraft(): void {
  drafts.set(currentId, input.value);
}

function restoreDraft(): void {
  input.value = drafts.get(currentId) ?? '';
  resizeInput();
}

function resizeInput(): void {
  input.style.height = 'auto';
  input.style.height = `${Math.min(input.scrollHeight, INPUT_MAX_HEIGHT)}px`;
  input.style.overflowY = input.scrollHeight > INPUT_MAX_HEIGHT ? 'auto' : 'hidden';
}

function selectConversation(id: string | null): void {
  saveDraft();
  currentId = id;
  confirmingDelete = null;
  restoreDraft();
  renderAll();
  scrollToBottom();
  closeDrawer();
  input.focus();
}

function selectProvider(id: ProviderId): void {
  if (current()?.messages.length) return;
  settings = { ...settings, provider: id };
  void api.saveSettings(settings);
  renderComposer();
}

async function removeConversation(id: string): Promise<void> {
  await api.remove(id);
  conversations = conversations.filter((c) => c.id !== id);
  drafts.delete(id);
  confirmingDelete = null;
  if (currentId === id) selectConversation(null);
  else renderChatList();
}

async function submit(): Promise<void> {
  const text = input.value.trim();
  const conversation = current();
  if (!text || isStreaming(conversation)) return;
  const model = modelSelect.value;
  const provider = activeProvider();
  const previousDraftKey = currentId;
  input.value = '';
  drafts.delete(previousDraftKey);
  resizeInput();
  renderSendButton();
  try {
    const sent = await api.send({ conversationId: conversation?.id ?? null, text, provider, model });
    upsert(sent);
    currentId = sent.id;
    renderAll();
    scrollToBottom();
  } catch (e) {
    input.value = text;
    resizeInput();
    renderSendButton();
    composerNote.replaceChildren(el('span', '', (e as Error).message.replace(/^Error invoking remote method '\w+': (Error: )?/, '')));
  }
}

function upsert(conversation: Conversation): void {
  const index = conversations.findIndex((c) => c.id === conversation.id);
  if (index >= 0) conversations[index] = conversation;
  else conversations.push(conversation);
  sortConversations();
}

async function refreshProviders(): Promise<void> {
  lastStatusCheck = Date.now();
  providers = await api.checkProviders();
  renderAccounts();
  renderComposer();
}

function handleEvent(event: ChatEvent): void {
  switch (event.type) {
    case 'update': {
      upsert(event.conversation);
      // A brand-new conversation's first update arrives before send() returns.
      const isCurrent = event.conversation.id === currentId;
      renderChatList();
      if (isCurrent) {
        const stick = isNearBottom();
        renderMessages();
        renderComposer();
        if (stick) scrollToBottom();
      }
      break;
    }
    case 'delta': {
      const conversation = conversations.find((c) => c.id === event.conversationId);
      const message = conversation?.messages.find((m) => m.id === event.messageId);
      if (!message) break;
      message.content += event.text;
      if (event.conversationId === currentId) queueStreamRender(event.messageId);
      break;
    }
    case 'usage':
      usage.set(event.provider, event.windows);
      renderAccounts();
      break;
    case 'focus':
      // Picks up a sign-in finished in the console window.
      if (providers.some((p) => !p.loggedIn) && Date.now() - lastStatusCheck > STATUS_RECHECK_MS) void refreshProviders();
      break;
  }
}

// --- wiring ---

composer.addEventListener('submit', (e) => {
  e.preventDefault();
  const conversation = current();
  if (conversation && isStreaming(conversation)) void api.stop(conversation.id);
  else void submit();
});

input.addEventListener('input', () => {
  resizeInput();
  renderSendButton();
});

input.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    void submit();
  }
});

modelSelect.addEventListener('change', () => {
  const conversation = current();
  if (conversation) {
    conversation.model = modelSelect.value;
    void api.setModel(conversation.id, modelSelect.value);
  } else {
    settings = { ...settings, models: { ...settings.models, [settings.provider]: modelSelect.value } };
    void api.saveSettings(settings);
  }
});

$('new-chat').addEventListener('click', () => selectConversation(null));
$('menu').addEventListener('click', () => {
  sidebar.classList.add('open');
  scrim.hidden = false;
});
scrim.addEventListener('click', closeDrawer);

document.addEventListener('keydown', (e) => {
  if (e.ctrlKey && e.key.toLowerCase() === 'n') {
    e.preventDefault();
    selectConversation(null);
  } else if (e.key === 'Escape') {
    closeDrawer();
  }
});

api.onEvent(handleEvent);

void (async () => {
  const data = await api.bootstrap();
  conversations = data.conversations;
  settings = data.settings;
  providers = data.providers;
  lastStatusCheck = Date.now();
  sortConversations();
  renderAll();
  input.focus();
})();
