import { expect, test as base, type Page, type Request, type Route } from '@playwright/test';

const Bridge = 'http://127.0.0.1:43181';
const BridgeToken = 'native-bridge-fixture-secret';
const DatabaseToken = 'native-database-fixture-secret';
const Contract = 'm27-browser-direct-v1';
const ToolResult = { contractVersion: '1.0', measurements: ['cpu'] };

interface NativeFixture {
  connected: boolean;
  configured: boolean;
  hasHost: boolean;
  localReady: boolean;
  publicReady: boolean;
  malformedEcho: boolean;
  toolName: string;
  expiresIn: number;
  connectCanceled: boolean;
  holdConnect: boolean;
  holdChat: boolean;
  releaseConnect: (() => void) | null;
  releaseChat: (() => void) | null;
  bridgeRequests: Array<{ path: string; method: string; body: string; headers: Record<string, string> }>;
  mcpRequests: Array<{ body: Record<string, unknown>; headers: Record<string, string> }>;
  forbiddenRequests: string[];
}

// Production Vue Dock, API registration, native bootstrap/bridge and MCP loop.
// The loopback host/public model/MCP responses are controlled browser fixtures;
// host credential-store and upstream HTTP integration have separate .NET tests.
const test = base.extend<{ native: NativeFixture }>({
  native: async ({ context, baseURL }, use) => {
    if (!baseURL) throw new Error('Native fixture requires Vite baseURL.');
    const fixture: NativeFixture = {
      connected: false, configured: true, hasHost: true, localReady: true, publicReady: true,
      malformedEcho: false, toolName: 'list_measurements', expiresIn: 120_000,
      connectCanceled: false, holdConnect: false, holdChat: false,
      releaseConnect: null, releaseChat: null, bridgeRequests: [], mcpRequests: [], forbiddenRequests: [],
    };
    await context.addInitScript(({ bridge, token, databaseToken }) => {
      localStorage.setItem('sndb.auth', JSON.stringify({ username: 'native-user', token: databaseToken, tokenId: 'native-db', isSuperuser: false }));
      (window as unknown as { nativeWeb: { invoke: (name: string) => Promise<void> } }).nativeWeb = {
        async invoke(name) {
          if (name === 'studio.bridge.bootstrap.request') {
            window.dispatchEvent(new CustomEvent('nativeWeb:studio.bridge.bootstrap', {
              detail: { endpointUrl: bridge, token },
            }));
          }
        },
      };
    }, { bridge: Bridge, token: BridgeToken, databaseToken: DatabaseToken });
    await context.route('**/*', async (route) => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.origin === Bridge) {
        if (request.method() === 'OPTIONS') return json(route, {}, baseURL);
        const headers = await request.allHeaders();
        fixture.bridgeRequests.push({ path: url.pathname, method: request.method(), body: request.postData() ?? '', headers });
        if (!fixture.hasHost) return route.abort('connectionrefused');
        if (url.pathname === '/manifest') return json(route, manifest(baseURL), baseURL);
        if (url.pathname === '/connections') return json(route, { profiles: [], activeProfileId: 'managed-local', activeDatabase: 'demo' }, baseURL);
        if (url.pathname === '/copilot/status') return json(route, status(fixture), baseURL);
        if (url.pathname === '/copilot/connect') {
          if (fixture.holdConnect) await hold(fixture, 'releaseConnect');
          fixture.connected = !fixture.connectCanceled;
          return json(route, status(fixture, fixture.connectCanceled), baseURL).catch(() => undefined);
        }
        if (url.pathname === '/copilot/disconnect') {
          fixture.connected = false;
          fixture.connectCanceled = true;
          fixture.releaseConnect?.();
          return json(route, status(fixture), baseURL);
        }
        if (url.pathname === '/copilot/readiness') return json(route,
          { status: fixture.publicReady ? 'ready' : 'down' }, baseURL, fixture.publicReady ? 200 : 503);
        if (url.pathname === '/copilot/chat' || url.pathname === '/copilot/continue') {
          if (fixture.holdChat) await hold(fixture, 'releaseChat');
          const payload = request.postDataJSON() as { runId: string; continuation?: { toolCallId: string; toolName: string; toolResult: string } };
          const envelope = (sequence: number, event: Record<string, unknown>, toolCallId?: string) => ({
            runId: payload.runId, sequence, cursor: `native-${sequence}`, ...(toolCallId ? { toolCallId } : {}), event,
          });
          const events = payload.continuation ? [
            envelope(2, { type: 'tool_result', toolName: payload.continuation.toolName,
              toolResult: fixture.malformedEcho ? 'changed' : payload.continuation.toolResult }, payload.continuation.toolCallId),
            envelope(3, { type: 'final', answer: 'Native 查询完成：cpu' }),
            envelope(4, { type: 'done' }),
          ] : [envelope(1, { type: 'tool_call', toolName: fixture.toolName, toolArguments: '{}' }, 'native-call-1')];
          return route.fulfill({ contentType: 'application/x-ndjson', headers: cors(baseURL), body: events.map((event) => JSON.stringify(event)).join('\n') + '\n' }).catch(() => undefined);
        }
        return json(route, {}, baseURL);
      }
      if (url.origin !== new URL(baseURL).origin || url.pathname === '/v1/copilot/chat/stream') {
        fixture.forbiddenRequests.push(request.url());
        return route.abort('blockedbyclient');
      }
      if (url.pathname === '/mcp/demo') {
        const body = request.postDataJSON() as Record<string, unknown>;
        fixture.mcpRequests.push({ body, headers: await request.allHeaders() });
        if (body.method === 'notifications/initialized') return route.fulfill({ status: 202 });
        const result = body.method === 'initialize' ? { protocolVersion: '2025-11-25' }
          : body.method === 'tools/list' ? { tools: [{
            name: 'list_measurements', inputSchema: { type: 'object', additionalProperties: false },
            outputSchema: { type: 'object', required: ['contractVersion', 'measurements'], properties: {
              contractVersion: { type: 'string' }, measurements: { type: 'array', items: { type: 'string' } },
            } },
            annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
          }] } : { isError: false, structuredContent: ToolResult };
        return json(route, { jsonrpc: '2.0', id: body.id, result });
      }
      if (url.pathname === '/healthz') return json(route, { status: fixture.localReady ? 'ok' : 'down' }, undefined, fixture.localReady ? 200 : 503);
      if (url.pathname.startsWith('/v1/')) return management(route, request, url.pathname);
      if (url.pathname === '/metrics') return route.fulfill({ contentType: 'text/plain', body: '' });
      return route.continue();
    });
    try { await use(fixture); }
    finally { fixture.releaseConnect?.(); fixture.releaseChat?.(); }
  },
});

test.skip(process.env.SONNETDB_E2E_RUNTIME !== 'StudioNative', 'Run with SONNETDB_E2E_RUNTIME=StudioNative.');
test.setTimeout(45_000);

test('production Dock completes native chat, local typed MCP and continuation with isolated credentials', async ({ page, native }) => {
  await openDock(page);
  await connect(page);
  await ask(page);
  await expect(page.locator('.copilot-dock__msg-body').filter({ hasText: 'Native 查询完成：cpu' })).toBeVisible();
  expect(native.bridgeRequests.filter((item) => item.path === '/copilot/chat')).toHaveLength(1);
  const continuations = native.bridgeRequests.filter((item) => item.path === '/copilot/continue');
  expect(continuations).toHaveLength(1);
  expect(JSON.parse(continuations[0].body).continuation).toMatchObject({ previousCursor: 'native-1', toolCallId: 'native-call-1', toolName: 'list_measurements' });
  expect(JSON.parse(JSON.parse(continuations[0].body).continuation.toolResult).structuredContent).toEqual(ToolResult);
  expect(native.mcpRequests.map((item) => item.body.method)).toEqual(['initialize', 'notifications/initialized', 'tools/list', 'tools/call']);
  for (const item of native.bridgeRequests) {
    expect(item.headers['x-sonnetdb-studio-bridge-token']).toBe(BridgeToken);
    expect(item.headers.authorization).toBeUndefined();
    expect(item.headers.cookie).toBeUndefined();
    expect(`${item.path} ${item.body}`).not.toContain(DatabaseToken);
  }
  for (const item of native.mcpRequests) {
    expect(item.headers.authorization).toBe(`Bearer ${DatabaseToken}`);
    expect(item.headers['x-sonnetdb-studio-bridge-token']).toBeUndefined();
  }
  expect(native.forbiddenRequests).toEqual([]);
  expect(await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage }, url: location.href }))).not.toContain(BridgeToken);
  await test.info().attach('studio-native-connected', { body: await page.screenshot(), contentType: 'image/png' });
  await page.getByTestId('copilot-public-disconnect').click();
  await expect(page.getByTestId('copilot-public-auth-status')).toHaveText('未连接');
  await expect(page.getByTestId('copilot-public-connect')).toBeEnabled();
});

test('native authorization cancellation aborts the prompt and allows a fresh connection', async ({ page, native }) => {
  native.holdConnect = true;
  await openDock(page);
  await page.getByTestId('copilot-public-connect').click();
  await expect.poll(() => native.releaseConnect !== null).toBe(true);
  await page.getByTestId('copilot-public-cancel').click();
  await expect(page.getByTestId('copilot-public-connect')).toBeEnabled();
  expect(native.bridgeRequests.some((item) => item.path === '/copilot/disconnect')).toBe(true);
  expect(native.connected).toBe(false);
  native.holdConnect = false;
  native.connectCanceled = false;
  await connect(page);
});

test('native prompt canceled by the user leaves sending disabled', async ({ page, native }) => {
  native.connectCanceled = true;
  await openDock(page);
  await page.getByTestId('copilot-public-connect').click();
  await expect(page.getByRole('alert')).toContainText('连接已取消');
  await expect(page.getByTestId('copilot-public-auth-status')).toHaveText('未连接');
  await page.locator('.copilot-dock__input textarea').fill('列出 measurement');
  await expect(page.getByRole('button', { name: '发送', exact: true })).toBeDisabled();
});

test('missing native host is unavailable with no relay or browser fallback', async ({ page, native }) => {
  native.hasHost = false;
  await page.goto('/admin/app/dashboard');
  await page.locator('.copilot-fab').click();
  await expect(page.getByTestId('copilot-public-auth-status')).toHaveText('尚未配置');
  await expect(page.getByTestId('copilot-public-connect')).toHaveCount(0);
  expect(native.forbiddenRequests).toEqual([]);
});

test('local readiness failure prevents native public authentication or chat requests', async ({ page, native }) => {
  await openDock(page);
  await connect(page);
  native.localReady = false;
  await ask(page);
  await expect(page.locator('.copilot-dock__error')).toContainText('本地端点');
  expect(native.bridgeRequests.filter((item) => item.path === '/copilot/readiness' || item.path === '/copilot/chat')).toEqual([]);
  expect(native.forbiddenRequests).toEqual([]);
});

test('native public readiness failure never falls back to ServerRelay', async ({ page, native }) => {
  native.publicReady = false;
  await openDock(page);
  await connect(page);
  await ask(page);
  await expect(page.locator('.copilot-dock__error')).toContainText('尚未就绪');
  expect(native.bridgeRequests.filter((item) => item.path === '/copilot/chat')).toEqual([]);
  expect(native.forbiddenRequests).toEqual([]);
});

test('native continuation requires an exact local tool result echo', async ({ page, native }) => {
  native.malformedEcho = true;
  await openDock(page);
  await connect(page);
  await ask(page);
  await expect(page.locator('.copilot-dock__error')).toContainText('未逐字回显');
  expect(native.mcpRequests.filter((item) => item.body.method === 'tools/call')).toHaveLength(1);
  await expect(page.locator('.copilot-dock__msg-body').filter({ hasText: 'Native 查询完成：cpu' })).toHaveCount(0);
});

test('native model cannot call tools outside the explicit local egress allowlist', async ({ page, native }) => {
  native.toolName = 'execute_sql';
  await openDock(page);
  await connect(page);
  await ask(page);
  await expect(page.locator('.copilot-dock__error')).toContainText('execute_sql');
  expect(native.mcpRequests).toEqual([]);
  expect(native.bridgeRequests.filter((item) => item.path === '/copilot/continue')).toEqual([]);
});

test('disconnect during native streaming cancels the run before MCP execution', async ({ page, native }) => {
  native.holdChat = true;
  await openDock(page);
  await connect(page);
  await ask(page);
  await expect.poll(() => native.releaseChat !== null).toBe(true);
  await page.getByTestId('copilot-public-disconnect').click();
  await expect(page.getByTestId('copilot-public-auth-status')).toHaveText('未连接');
  native.releaseChat?.();
  await expect(page.getByRole('button', { name: '停止', exact: true })).toHaveCount(0);
  expect(native.mcpRequests).toEqual([]);
});

test('changing database identity clears native credentials and blocks further sends', async ({ page, native }) => {
  await openDock(page);
  await connect(page);
  await page.evaluate("import('/src/stores/auth.ts').then(({ useAuthStore }) => useAuthStore().apply({ username: 'replacement', token: 'replacement-db-token', tokenId: 'replacement', isSuperuser: false }))");
  await expect(page.getByTestId('copilot-public-auth-status')).toHaveText('未连接');
  await expect.poll(() => native.connected).toBe(false);
  expect(native.bridgeRequests.some((item) => item.path === '/copilot/disconnect')).toBe(true);
});

test('expiring native credential disconnects through the fixed bridge operation', async ({ page, native }) => {
  native.expiresIn = 800;
  await openDock(page);
  await connect(page);
  await expect(page.getByTestId('copilot-public-auth-status')).toHaveText('未连接', { timeout: 5_000 });
  await expect.poll(() => native.connected).toBe(false);
  expect(native.bridgeRequests.some((item) => item.path === '/copilot/disconnect')).toBe(true);
});

test('unmounting the production Dock disconnects the native credential', async ({ page, native }) => {
  await openDock(page);
  await connect(page);
  await page.evaluate("import('/src/router/index.ts').then(({ default: router }) => router.push('/'))");
  await expect(page.locator('.copilot-dock')).toHaveCount(0);
  await expect.poll(() => native.connected).toBe(false);
  expect(native.bridgeRequests.some((item) => item.path === '/copilot/disconnect')).toBe(true);
});

test('late native bootstrap after Dock unmount retires an existing host credential', async ({ context, page, native }) => {
  native.connected = true;
  let release!: () => void;
  let manifestPending = false;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const timeout = setTimeout(release, 10_000);
  await context.route(`${Bridge}/manifest`, async (route) => {
    manifestPending = true;
    await gate;
    await route.fallback();
  });
  try {
    await page.goto('/admin/app/dashboard');
    await page.locator('.copilot-fab').click();
    await expect.poll(() => manifestPending).toBe(true);
    await page.evaluate("import('/src/router/index.ts').then(({ default: router }) => router.push('/'))");
    await expect(page.locator('.copilot-dock')).toHaveCount(0);
    release();
    await expect.poll(() => native.connected).toBe(false);
    expect(native.bridgeRequests.some((item) => item.path === '/copilot/disconnect')).toBe(true);
  } finally { clearTimeout(timeout); release(); }
});

async function openDock(page: Page): Promise<void> {
  await page.goto('/admin/app/dashboard');
  await page.locator('.copilot-fab').click();
  await expect(page.getByTestId('copilot-public-auth-status')).toHaveText('未连接');
}

async function connect(page: Page): Promise<void> {
  await page.getByTestId('copilot-public-connect').click();
  await expect(page.getByTestId('copilot-public-auth-status')).toHaveText('已连接');
}

async function ask(page: Page): Promise<void> {
  await page.locator('.copilot-dock__input textarea').fill('列出当前数据库的 measurement');
  await page.getByRole('button', { name: '发送', exact: true }).click();
}

function hold(fixture: NativeFixture, key: 'releaseConnect' | 'releaseChat'): Promise<void> {
  return new Promise((resolve) => {
    const timeout = setTimeout(resolve, 10_000);
    fixture[key] = () => { clearTimeout(timeout); resolve(); };
  });
}

function status(fixture: NativeFixture, canceled = false) {
  return { contractVersion: Contract, configured: fixture.configured, connected: fixture.connected,
    publicBaseUrl: 'https://ai.native.test', expiresAtUtc: fixture.connected ? new Date(Date.now() + fixture.expiresIn).toISOString() : null,
    canceled, error: null };
}

function cors(origin: string): Record<string, string> {
  return { 'Access-Control-Allow-Origin': origin, 'Access-Control-Allow-Methods': 'GET,POST,PUT,OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type,X-SonnetDB-Studio-Bridge-Token,X-SonnetDB-Copilot-Contract',
    'Access-Control-Expose-Headers': 'X-SonnetDB-Copilot-Contract', 'X-SonnetDB-Copilot-Contract': Contract };
}

async function json(route: Route, value: unknown, origin?: string, statusCode = 200): Promise<void> {
  await route.fulfill({ status: statusCode, contentType: 'application/json', ...(origin ? { headers: cors(origin) } : {}), body: JSON.stringify(value) });
}

function manifest(baseURL: string) {
  return { mode: 'native', version: '1.0', serverUrl: baseURL, managedServerUrl: baseURL, dataRoot: '',
    capabilities: ['copilot.nativeBroker.v1'], menu: [], managedServer: {
      isRunning: true, startedByStudio: true, healthy: true, processId: 1, url: baseURL, dataRoot: '', error: null,
    } };
}

async function management(route: Route, request: Request, path: string): Promise<void> {
  let payload: unknown = {};
  if (path === '/v1/setup/status') payload = { needsSetup: false, suggestedServerId: 'native', serverId: 'native', userCount: 1, databaseCount: 1 };
  else if (path === '/v1/db') payload = { databases: ['demo'] };
  else if (path === '/v1/copilot/conversations' && request.method() === 'POST') {
    const body = request.postDataJSON() as { id: string; title: string; database: string };
    payload = { ...body, createdAtUtc: new Date().toISOString(), updatedAtUtc: new Date().toISOString(), messageCount: 0 };
  } else if (path.includes('/conversations')) payload = { conversations: [], messages: [] };
  else if (path.endsWith('/models')) payload = { default: '', candidates: [], groups: [] };
  else if (path.endsWith('/metrics')) payload = { requestCount: 0, totalTokens: 0, toolCalls: 0 };
  await json(route, payload);
}
