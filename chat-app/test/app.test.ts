import assert from 'node:assert/strict';
import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, test } from 'node:test';
import { ChatService, replayPrompt } from '../src/main/chat.ts';
import { claudeArgs, parseClaudeLine, parseClaudeStatus, startClaudeTurn } from '../src/main/claude.ts';
import { buildSpawn, childEnv } from '../src/main/cli.ts';
import {
  classifyCodexError,
  codexArgs,
  configuredModel,
  mcpServerNames,
  parseCodexLimits,
  parseCodexStatus,
  parseModelCache,
} from '../src/main/codex.ts';
import { ProviderError } from '../src/main/provider.ts';
import type { Provider, TurnRequest } from '../src/main/provider.ts';
import { Store } from '../src/main/store.ts';
import type { ChatEvent, Conversation, ProviderId } from '../src/shared/types.ts';

const temp = mkdtempSync(path.join(tmpdir(), 'errchat-test-'));
after(() => rmSync(temp, { recursive: true, force: true }));

const fakeCli = path.resolve('test/fake-cli.cjs');
const shim = path.join(temp, 'fake claude.cmd');
writeFileSync(shim, `@node "${fakeCli}" %*\r\n`);

function turnRequest(prompt: string, overrides: Partial<TurnRequest> = {}) {
  const seen = { text: '', session: '', usage: [] as unknown[] };
  const req: TurnRequest = {
    prompt,
    model: 'haiku',
    cwd: temp,
    onSession: (id) => (seen.session = id),
    onDelta: (t) => (seen.text += t),
    onUsage: (w) => seen.usage.push(w),
    ...overrides,
  };
  return { req, seen };
}

// --- subscription safety ---

test('API keys never reach the CLI, so it uses the subscription login', () => {
  const env = childEnv({
    ANTHROPIC_API_KEY: 'a',
    anthropic_auth_token: 'b',
    OPENAI_API_KEY: 'c',
    CODEX_API_KEY: 'd',
    CLAUDECODE: '1',
    CLAUDE_CODE_SIMPLE: '1',
    CLAUDE_CODE_OAUTH_TOKEN: 'keep',
    PATH: 'keep',
  });
  assert.deepEqual(env, { CLAUDE_CODE_OAUTH_TOKEN: 'keep', PATH: 'keep' });
});

test('status reports when a CLI bills an API key instead of the plan', () => {
  assert.equal(parseClaudeStatus('{"loggedIn":true,"authMethod":"claude.ai","subscriptionType":"pro"}').detail, 'Pro plan');
  assert.equal(parseClaudeStatus('{"loggedIn":true,"authMethod":"api_key"}').usesSubscription, false);
  assert.equal(parseClaudeStatus('{"loggedIn":false}').loggedIn, false);
  assert.equal(parseClaudeStatus('not json').loggedIn, false);
  assert.equal(parseCodexStatus('Logged in using ChatGPT').usesSubscription, true);
  assert.equal(parseCodexStatus('Logged in using an API key').usesSubscription, false);
  assert.equal(parseCodexStatus('Not logged in').loggedIn, false);
});

// --- chat-only lockdown ---

test('Claude runs with no tools, MCP servers, skills or user settings', () => {
  const args = claudeArgs('sonnet');
  assert.equal(args[args.indexOf('--tools') + 1], '');
  assert.equal(args[args.indexOf('--setting-sources') + 1], '');
  assert.ok(args.includes('--strict-mcp-config'));
  assert.ok(args.includes('--disable-slash-commands'));
  assert.ok(!args.includes('--resume'));
  assert.deepEqual(claudeArgs('sonnet', 'abc-1').slice(-2), ['--resume', 'abc-1']);
});

test('Codex turns off command, browser and computer tools and every user MCP server', () => {
  const toml = '[mcp_servers.docs]\nurl="x"\n[mcp_servers.docs.env]\nA="1"\n[mcp_servers."my server"]\ncommand="y"\n[tui]\n';
  assert.deepEqual(mcpServerNames(toml), ['docs', 'my server']);
  const args = codexArgs(toml);
  for (const feature of ['shell_tool', 'unified_exec', 'code_mode_host', 'browser_use', 'computer_use', 'view_image', 'apps']) {
    assert.equal(args[args.indexOf(feature) - 1], '--disable', feature);
  }
  assert.ok(args.includes('mcp_servers.docs.enabled=false'));
  assert.ok(args.includes('mcp_servers."my server".enabled=false'));
  assert.ok(args.includes('web_search=disabled'));
});

test('ids with shell characters are refused before reaching a command line', () => {
  assert.throws(() => claudeArgs('sonnet & calc'), ProviderError);
  assert.throws(() => claudeArgs('sonnet', 'x" --dangerous'), ProviderError);
  assert.doesNotThrow(() => claudeArgs('claude-opus-5-5[1m]'));
});

test('.cmd shims go through cmd.exe, and unsafe characters are refused', () => {
  const direct = buildSpawn('C:\\a\\claude.exe', ['-p', '']);
  assert.deepEqual(direct, { command: 'C:\\a\\claude.exe', args: ['-p', ''], verbatim: false });
  const shimmed = buildSpawn('C:\\a b\\claude.cmd', ['--tools', '']);
  assert.equal(shimmed.verbatim, true);
  assert.equal(shimmed.args.at(-1), '""C:\\a b\\claude.cmd" "--tools" """');
  assert.throws(() => buildSpawn('C:\\a\\claude.cmd', ['%PATH%']), ProviderError);
});

// --- Claude stream ---

test('Claude stream lines become events', () => {
  assert.deepEqual(parseClaudeLine('{"type":"system","subtype":"init","session_id":"s"}'), { type: 'session', id: 's' });
  assert.deepEqual(parseClaudeLine('{"type":"stream_event","event":{"delta":{"type":"text_delta","text":"x"}}}'), { type: 'delta', text: 'x' });
  assert.equal(parseClaudeLine('{"type":"stream_event","event":{"delta":{"type":"thinking_delta"}}}'), null);
  assert.equal(parseClaudeLine('garbage'), null);
  assert.deepEqual(parseClaudeLine('{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":0.14,"resetsAt":100}}}}'), {
    type: 'usage',
    windows: [{ label: '5h', usedPercent: 14, resetsAt: 100000 }],
  });
});

test('a full Claude turn streams through a .cmd shim with a space in its path', async () => {
  const argsFile = path.join(temp, 'args.json');
  process.env.FAKE_ARGS_FILE = argsFile;
  const { req, seen } = turnRequest('hi "there" & %PATH%');
  await startClaudeTurn(req, shim).done;
  delete process.env.FAKE_ARGS_FILE;
  assert.equal(seen.text, 'Hello');
  assert.equal(seen.session, 'session-1');
  assert.equal(seen.usage.length, 1);
  const args = JSON.parse(readFileSync(argsFile, 'utf8'));
  assert.equal(args[args.indexOf('--tools') + 1], '', 'the empty --tools value survives cmd.exe');
  assert.ok(args.includes('You are a helpful assistant in a general-purpose desktop chat app. Answer the user directly in conversation. Use Markdown when it helps. You have no access to files, commands or the web here, so do not try to use tools.'));
});

for (const [scenario, kind] of [
  ['SCENARIO_AUTH', 'auth'],
  ['SCENARIO_LIMIT', 'limit'],
  ['SCENARIO_MISSING', 'session_missing'],
] as const) {
  test(`Claude failure ${scenario} is reported as ${kind}`, async () => {
    const { req } = turnRequest(scenario);
    await assert.rejects(startClaudeTurn(req, shim).done, (e: ProviderError) => e.kind === kind);
  });
}

test('stopping kills a hung Claude process and keeps the partial text', async () => {
  const { req, seen } = turnRequest('SCENARIO_HANG');
  const turn = startClaudeTurn(req, shim);
  while (!seen.text) await new Promise((r) => setTimeout(r, 20));
  turn.stop();
  await turn.done;
  assert.equal(seen.text, 'partial');
});

test('a missing CLI is reported, not thrown', async () => {
  const { req } = turnRequest('hi');
  await assert.rejects(startClaudeTurn(req, null as unknown as string).done, (e: ProviderError) => e.kind === 'not_found' || e.kind === 'failed');
});

// --- Codex parsing ---

test('Codex limits, errors and models are parsed', () => {
  assert.deepEqual(parseCodexLimits({ primary: { usedPercent: 12.4, windowDurationMins: 300, resetsAt: 10 }, secondary: { usedPercent: 1, windowDurationMins: 10080 } }), [
    { label: '5h', usedPercent: 12, resetsAt: 10000 },
    { label: '7d', usedPercent: 1, resetsAt: undefined },
  ]);
  assert.equal(classifyCodexError('stream error: 401 Unauthorized').kind, 'auth');
  assert.equal(classifyCodexError('You reached your usage limit').kind, 'limit');
  assert.equal(classifyCodexError('boom').kind, 'failed');
  assert.deepEqual(parseModelCache('{"models":[{"slug":"a","visibility":"list","display_name":"A"},{"slug":"b","visibility":"hide"}]}'), [{ id: 'a', label: 'A' }]);
  assert.deepEqual(parseModelCache('nope'), []);
  assert.equal(configuredModel('model = "gpt-x"\n[tui]\nmodel = "other"'), 'gpt-x');
});

// --- conversations ---

type Script = (req: TurnRequest, call: number) => Promise<void>;

function fakeProvider(script: Script): Provider & { calls: TurnRequest[] } {
  const calls: TurnRequest[] = [];
  return {
    id: 'claude',
    name: 'Fake',
    calls,
    models: () => [],
    login() {},
    status: () => Promise.reject(new Error('unused')),
    start(req) {
      calls.push(req);
      return { done: script(req, calls.length), stop() {} };
    },
  };
}

function service(provider: Provider) {
  const dir = mkdtempSync(path.join(temp, 'store-'));
  const store = new Store(path.join(dir, 'c.json'));
  const events: ChatEvent[] = [];
  const chat = new ChatService(store, { claude: provider, codex: provider } as Record<ProviderId, Provider>, dir, (e) => events.push(e));
  const finished = async (c: Conversation) => {
    while (chat.isActive(c.id) || c.messages.at(-1)?.status === 'streaming') await new Promise((r) => setTimeout(r, 5));
  };
  return { chat, store, events, finished, dir };
}

const sendReq = { conversationId: null, provider: 'claude' as const, model: 'sonnet' };

test('a reply is streamed, saved, and the next message resumes the session', async () => {
  const provider = fakeProvider(async (req) => {
    req.onSession('sess');
    req.onDelta('Hi');
    req.onDelta(' there');
  });
  const { chat, finished, events } = service(provider);
  const c = chat.send({ ...sendReq, text: '  hello  ' });
  await finished(c);
  assert.equal(c.title, 'hello');
  assert.equal(c.messages[0].content, 'hello');
  assert.equal(c.messages[1].content, 'Hi there');
  assert.equal(c.messages[1].status, undefined);
  assert.ok(events.some((e) => e.type === 'delta'));
  chat.send({ ...sendReq, conversationId: c.id, text: 'again' });
  await finished(c);
  assert.equal(provider.calls[1].sessionId, 'sess');
  assert.equal(provider.calls[1].prompt, 'again');
});

test('a session the CLI has lost is rebuilt from local history', async () => {
  const provider = fakeProvider(async (req, call) => {
    if (call === 2) throw new ProviderError('session_missing', 'gone');
    req.onSession(`sess-${call}`);
    req.onDelta(`reply ${call}`);
  });
  const { chat, finished } = service(provider);
  const c = chat.send({ ...sendReq, text: 'first' });
  await finished(c);
  chat.send({ ...sendReq, conversationId: c.id, text: 'second' });
  await finished(c);
  assert.equal(provider.calls.length, 3);
  assert.equal(provider.calls[2].sessionId, undefined);
  assert.ok(provider.calls[2].prompt.includes('User: first'));
  assert.ok(provider.calls[2].prompt.includes('Assistant: reply 1'));
  assert.ok(provider.calls[2].prompt.endsWith('second'));
  assert.equal(c.messages.at(-1)?.content, 'reply 3');
  assert.equal(c.messages.at(-1)?.status, undefined);
  assert.equal(c.sessionId, 'sess-3');
});

test('errors are kept on the message, and retry replaces the failed reply', async () => {
  const provider = fakeProvider(async (req, call) => {
    if (call === 1) throw new ProviderError('limit', 'Limit reached');
    req.onDelta('ok');
  });
  const { chat, finished } = service(provider);
  const c = chat.send({ ...sendReq, text: 'x' });
  await finished(c);
  assert.equal(c.messages[1].status, 'error');
  assert.deepEqual(c.messages[1].error, { kind: 'limit', message: 'Limit reached' });
  chat.retry(c.id);
  await finished(c);
  assert.equal(c.messages.length, 2);
  assert.equal(c.messages[1].content, 'ok');
  assert.equal(c.messages[1].error, undefined);
});

test('an empty reply is an error, not a blank bubble', async () => {
  const { chat, finished } = service(fakeProvider(async () => {}));
  const c = chat.send({ ...sendReq, text: 'x' });
  await finished(c);
  assert.equal(c.messages[1].status, 'error');
});

test('stop marks the reply stopped; a second send during a reply is refused', async () => {
  let release = () => {};
  const provider: Provider = {
    ...fakeProvider(async () => {}),
    start(req) {
      req.onDelta('part');
      return { done: new Promise<void>((r) => (release = r)), stop: () => release() };
    },
  };
  const { chat, finished } = service(provider);
  const c = chat.send({ ...sendReq, text: 'x' });
  assert.throws(() => chat.send({ ...sendReq, conversationId: c.id, text: 'y' }), /in progress/);
  chat.stop(c.id);
  await finished(c);
  assert.equal(c.messages[1].status, 'stopped');
  assert.equal(c.messages[1].content, 'part');
});

test('invalid sends are refused', () => {
  const { chat } = service(fakeProvider(async () => {}));
  assert.throws(() => chat.send({ ...sendReq, text: '   ' }), /empty/);
  assert.throws(() => chat.send({ ...sendReq, conversationId: 'nope', text: 'x' }), /not found/);
});

test('replay prompt keeps the order and skips empty messages', () => {
  const m = (role: 'user' | 'assistant', content: string) => ({ id: '1', role, content, createdAt: 0 });
  const prompt = replayPrompt([m('user', 'a'), m('assistant', ''), m('assistant', 'b')], 'c');
  assert.ok(prompt.includes('User: a\n\nAssistant: b'));
  assert.ok(prompt.endsWith('c'));
});

// --- storage ---

test('history survives a restart, and a mid-stream reply comes back as stopped', () => {
  const dir = mkdtempSync(path.join(temp, 'persist-'));
  const file = path.join(dir, 'c.json');
  const store = new Store(file);
  store.add({
    id: 'c1', title: 't', provider: 'claude', model: 'm', createdAt: 1, updatedAt: 1,
    messages: [{ id: 'm1', role: 'assistant', content: 'part', createdAt: 1, status: 'streaming' }],
  });
  store.flush();
  const reloaded = new Store(file);
  assert.equal(reloaded.find('c1')?.messages[0].status, 'stopped');
  assert.equal(reloaded.find('c1')?.messages[0].content, 'part');
});

test('a corrupt history file is set aside, never overwritten', () => {
  const dir = mkdtempSync(path.join(temp, 'corrupt-'));
  const file = path.join(dir, 'c.json');
  writeFileSync(file, '{not json');
  const store = new Store(file);
  assert.deepEqual(store.data.conversations, []);
  const saved = readdirSync(dir).find((f) => f.includes('.corrupt-'));
  assert.ok(saved);
  assert.equal(readFileSync(path.join(dir, saved), 'utf8'), '{not json');
});
