// The only bridge between the page and the main process.

import { contextBridge, ipcRenderer } from 'electron';
import type { ErrchatApi } from './shared/types.ts';

const api: ErrchatApi = {
  bootstrap: () => ipcRenderer.invoke('bootstrap'),
  send: (req) => ipcRenderer.invoke('send', req),
  retry: (id) => ipcRenderer.invoke('retry', id),
  stop: (id) => ipcRenderer.invoke('stop', id),
  remove: (id) => ipcRenderer.invoke('remove', id),
  setModel: (id, model) => ipcRenderer.invoke('setModel', id, model),
  saveSettings: (settings) => ipcRenderer.invoke('saveSettings', settings),
  checkProviders: () => ipcRenderer.invoke('checkProviders'),
  login: (provider) => ipcRenderer.invoke('login', provider),
  copy: (text) => ipcRenderer.invoke('copy', text),
  openExternal: (url) => ipcRenderer.invoke('openExternal', url),
  onEvent: (handler) => {
    ipcRenderer.on('chat:event', (_e, event) => handler(event));
  },
};

contextBridge.exposeInMainWorld('errchat', api);
