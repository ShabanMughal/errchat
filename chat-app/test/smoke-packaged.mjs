// Runs the packaged exe with the short PATH a Start-menu launch can have, and an
// API key in the environment that must not be used. Uses a little quota.
// Usage: npm run dist && node test/smoke-packaged.mjs [screenshot-dir]

import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { _electron as electron } from 'playwright-core';

const shots = process.argv[2] ?? tmpdir();
const userData = mkdtempSync(path.join(tmpdir(), 'errchat-smoke-'));
const system32 = path.join(process.env.SystemRoot, 'System32');
const env = {
  SystemRoot: process.env.SystemRoot,
  USERPROFILE: process.env.USERPROFILE,
  HOMEDRIVE: process.env.HOMEDRIVE,
  HOMEPATH: process.env.HOMEPATH,
  APPDATA: process.env.APPDATA,
  LOCALAPPDATA: process.env.LOCALAPPDATA,
  TEMP: process.env.TEMP,
  TMP: process.env.TMP,
  PATH: system32,
  ANTHROPIC_API_KEY: 'sk-invalid-must-not-be-used',
  OPENAI_API_KEY: 'sk-invalid-must-not-be-used',
  ERRCHAT_USER_DATA: userData,
};

const app = await electron.launch({ executablePath: path.resolve('release/win-unpacked/ErrChat.exe'), args: [], env });
const errors = [];
try {
  const page = await app.firstWindow();
  page.on('pageerror', (e) => errors.push(e.message));
  await page.waitForSelector('.provider-option');
  const accounts = await page.textContent('#accounts');
  console.log('accounts:', accounts);
  assert.match(accounts, /Claude.*Pro plan/);
  assert.match(accounts, /Codex.*ChatGPT plan/);

  for (const [provider, model] of [['Claude', 'haiku'], ['Codex', 'gpt-5.6-luna']]) {
    await page.click('#new-chat');
    await page.click(`.provider-option:has-text("${provider}")`);
    await page.selectOption('#model', model);
    for (const [text, expected] of [
      ['Remember the word "lantern". Reply with just: ok', /ok/i],
      ['Which word did I ask you to remember? Reply with just that word.', /lantern/i],
    ]) {
      await page.fill('#input', text);
      await page.keyboard.press('Enter');
      await page.waitForFunction(() => document.querySelector('#send').textContent === 'Stop');
      await page.waitForFunction(() => document.querySelector('#send').textContent === 'Send', null, { timeout: 120000 });
      const reply = page.locator('article.message.assistant').last();
      assert.equal(await reply.locator('.message-error').count(), 0, await reply.innerText());
      const shown = await reply.locator('.markdown').innerText();
      console.log(`${provider}: ${shown}`);
      assert.match(shown, expected);
    }
  }

  await page.setViewportSize({ width: 480, height: 700 });
  await page.waitForTimeout(200);
  const sendBox = await page.locator('#send').boundingBox();
  const modelBox = await page.locator('#model').boundingBox();
  assert.ok(Math.abs(sendBox.y + sendBox.height / 2 - (modelBox.y + modelBox.height / 2)) < 2, 'Send stays on the model row');
  await page.screenshot({ path: path.join(shots, '08-packaged-narrow.png') });
  assert.deepEqual(errors, []);
  console.log('SMOKE PASSED');
} catch (e) {
  console.error('SMOKE FAILED:', e);
  process.exitCode = 1;
} finally {
  await app.close().catch(() => {});
  try {
    rmSync(userData, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
  } catch {
    console.warn('Could not remove temp folder:', userData);
  }
}
