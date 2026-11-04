// Electron entry point: the window, its security settings, and the IPC surface.

import { app, BrowserWindow, clipboard, ipcMain, Menu, nativeTheme, shell } from 'electron';
import type { IpcMainInvokeEvent } from 'electron';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import type { ChatEvent, ProviderId, SendRequest, Settings } from '../shared/types.ts';
import { ChatService } from './chat.ts';
import { claudeProvider } from './claude.ts';
import { codexProvider } from './codex.ts';
import { Store } from './store.ts';

// Lets automated tests run against a throwaway history.
if (process.env.ERRCHAT_USER_DATA) app.setPath('userData', process.env.ERRCHAT_USER_DATA);

const providers = { claude: claudeProvider, codex: codexProvider };
let window: BrowserWindow | null = null;
let store: Store;
let chat: ChatService;

const isExternalUrl = (url: string) => /^(https?:|mailto:)/i.test(url);

function createWindow(): void {
  window = new BrowserWindow({
    width: 1120,
    height: 780,
    minWidth: 480,
    minHeight: 480,
    title: 'ErrChat',
    icon: path.join(__dirname, 'icon.png'),
    backgroundColor: nativeTheme.shouldUseDarkColors ? '#14181b' : '#ffffff',
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
    },
  });
  window.once('ready-to-show', () => window?.show());
  window.on('closed', () => (window = null));
  window.on('focus', () => send({ type: 'focus' }));

  // Model output can contain links: they open in the browser, never in the app.
  window.webContents.setWindowOpenHandler(({ url }) => {
    if (isExternalUrl(url)) void shell.openExternal(url);
    return { action: 'deny' };
  });
  window.webContents.on('will-navigate', (event, url) => {
    event.preventDefault();
    if (isExternalUrl(url)) void shell.openExternal(url);
  });
  window.webContents.on('context-menu', (_event, params) => {
    const items: Electron.MenuItemConstructorOptions[] = params.isEditable
      ? [{ role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { type: 'separator' }, { role: 'selectAll' }]
      : params.selectionText
        ? [{ role: 'copy' }]
        : [];
    if (items.length) Menu.buildFromTemplate(items).popup();
  });

  void window.loadFile(path.join(__dirname, 'renderer', 'index.html'));
}

function send(event: ChatEvent): void {
  window?.webContents.send('chat:event', event);
}

/** Every handler checks that the call came from our own page. */
function handle<A extends unknown[], R>(channel: string, fn: (...args: A) => R): void {
  ipcMain.handle(channel, (event: IpcMainInvokeEvent, ...args: unknown[]) => {
    if (!event.senderFrame?.url.startsWith('file://')) throw new Error('Blocked');
    return fn(...(args as A));
  });
}

function providerId(value: unknown): ProviderId {
  if (value !== 'claude' && value !== 'codex') throw new Error('Unknown provider');
  return value;
}

const statuses = () => Promise.all([claudeProvider.status(), codexProvider.status()]);

function registerIpc(): void {
  handle('bootstrap', async () => ({
    conversations: store.data.conversations,
    settings: store.data.settings,
    providers: await statuses(),
  }));
  handle('send', (req: SendRequest) =>
    chat.send({
      conversationId: typeof req.conversationId === 'string' ? req.conversationId : null,
      text: String(req.text),
      provider: providerId(req.provider),
      model: String(req.model),
    }),
  );
  handle('retry', (id: string) => chat.retry(String(id)));
  handle('stop', (id: string) => chat.stop(String(id)));
  handle('remove', (id: string) => chat.remove(String(id)));
  handle('setModel', (id: string, model: string) => chat.setModel(String(id), String(model)));
  handle('saveSettings', (settings: Settings) => {
    store.data.settings = { provider: providerId(settings.provider), models: { ...settings.models } };
    store.save();
  });
  handle('checkProviders', statuses);
  handle('login', (provider: ProviderId) => providers[providerId(provider)].login());
  handle('copy', (text: string) => clipboard.writeText(String(text)));
  handle('openExternal', (url: string) => {
    if (isExternalUrl(String(url))) void shell.openExternal(String(url));
  });
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (window?.isMinimized()) window.restore();
    window?.focus();
  });

  app.whenReady().then(() => {
    app.setAppUserModelId('com.errchat.chat');
    Menu.setApplicationMenu(null);
    const dataDir = app.getPath('userData');
    const workspace = path.join(dataDir, 'workspace');
    mkdirSync(workspace, { recursive: true });
    store = new Store(path.join(dataDir, 'conversations.json'));
    chat = new ChatService(store, providers, workspace, send);
    registerIpc();
    createWindow();
  });

  app.on('window-all-closed', () => app.quit());
  app.on('before-quit', () => {
    chat?.stopAll();
    store?.flush();
  });
}
