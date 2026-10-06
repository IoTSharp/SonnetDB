import { expect, test as base, type Page, type Route } from '@playwright/test';

const Bridge = 'http://127.0.0.1:43181/studio-bridge';
const BridgeToken = 'studio-host-client-fixture-token';
const Database = 'FactoryDB';
const Endpoint = 'http://127.0.0.1:5080/api';

type Lifecycle = 'running' | 'external-running' | 'unhealthy' | 'stopped' | 'failed';

interface HostStatus {
  isRunning: boolean;
  startedByStudio: boolean;
  healthy: boolean;
  processId: number | null;
  url: string;
  dataRoot: string;
  error: string | null;
  processOwner: 'studio' | 'external' | 'none';
  lifecycleState: Lifecycle;
  canStop: boolean;
}

interface Profile {
  id: string;
  name: string;
  kind: 'managed-local' | 'remote';
  baseUrl: string;
  defaultDatabase: string;
  tokenMode: 'current-session';
  createdAt: number;
  updatedAt: number;
  identity: { host: 'studio-desktop'; profileId: string; baseUrl: string; database: string };
}

interface HostFixture {
  status: HostStatus;
  profiles: Profile[];
  activeProfileId: string;
  activeDatabase: string;
  bridgeRequests: string[];
  lifecycleRequests: string[];
  failBridge: boolean;
  holdStatus: boolean;
  releaseStatus: (() => void) | null;
  requestCount: number;
}

const test = base.extend<{ host: HostFixture }>({
  host: async ({ context, baseURL }, use) => {
    if (!baseURL) throw new Error('Studio host fixture requires Vite baseURL.');
    const fixture = makeFixture();

    await context.addInitScript(({ bridge, token }) => {
      localStorage.setItem('sndb.auth', JSON.stringify({
        username: 'studio-e2e', token: 'database-token', tokenId: 'studio-e2e-token', isSuperuser: true,
      }));
      Object.defineProperty(globalThis, 'nativeWeb', {
        configurable: true,
        value: {
          invoke: async (name: string) => {
            if (name !== 'studio.bridge.bootstrap.request') throw new Error(`Unexpected native handler: ${name}`);
            window.dispatchEvent(new CustomEvent('nativeWeb:studio.bridge.bootstrap', {
              detail: { endpointUrl: bridge, token },
            }));
            return null;
          },
        },
      });
    }, { bridge: Bridge, token: BridgeToken });

    await context.route('**/*', async (route) => {
      const request = route.request();
      const url = new URL(request.url());
      const endpointRequest = url.origin === new URL(Endpoint).origin && url.pathname.startsWith('/api/');
      const path = endpointRequest ? url.pathname.slice('/api'.length) : url.pathname;
      const bridgeRequest = url.origin === new URL(Bridge).origin && url.pathname.startsWith('/studio-bridge');
      const apiRequest = path.startsWith('/v1/') || path === '/healthz';
      if (!bridgeRequest && !apiRequest && url.origin === new URL(baseURL).origin) return route.continue();
      fixture.requestCount += 1;
      if (fixture.requestCount > 100) return route.abort('blockedbyclient');
      if (bridgeRequest) {
        fixture.bridgeRequests.push(`${request.method()} ${url.pathname}`);
        if (request.method() === 'OPTIONS') return json(route, {}, 200);
        return handleBridge(route, fixture, url);
      }
      if (url.origin !== new URL(baseURL).origin && !endpointRequest) return route.abort('blockedbyclient');
      if (path === '/healthz') return json(route, { status: 'ok' });
      if (path === '/v1/setup/status') return json(route, {
        needsSetup: false, suggestedServerId: 'studio-e2e', serverId: 'studio-e2e', userCount: 1, databaseCount: 1,
      });
      if (path === '/v1/db') return json(route, { databases: [Database, 'RemoteDB', 'NewDB'] });
      if (path.startsWith('/v1/') && path.endsWith('/schema')) return json(route, {
        measurements: [],
        tables: [{
          name: 'orders',
          columns: [
            { name: 'id', dataType: 'INT64', isPrimaryKey: true, isNullable: false, ordinal: 0 },
            { name: 'status', dataType: 'STRING', isPrimaryKey: false, isNullable: false, ordinal: 1 },
          ],
          primaryKey: ['id'], indexes: [], foreignKeys: [], createdUtc: '2026-10-06T00:00:00Z',
        }],
        documentCollections: [], indexes: [],
        backupStatus: { backupCapable: true, segmentCount: 0, walFileCount: 0, totalBytes: 0, memTablePointCount: 0 },
      });
      if (path.startsWith('/v1/') && path.endsWith('/sql')) return json(route, {
        columns: ['id'], rows: [], rowCount: 0, recordsAffected: -1, elapsedMs: 0,
      });
      if (!path.startsWith('/v1/')) return route.continue();
      return json(route, {});
    });

    try { await use(fixture); }
    finally { fixture.releaseStatus?.(); }
  },
});

test.skip(
  !['StudioHostClient', 'StudioNative'].includes(process.env.SONNETDB_E2E_RUNTIME ?? ''),
  'Run with SONNETDB_E2E_RUNTIME=StudioHostClient (or the existing StudioNative runner).',
);
test.setTimeout(30_000);

test('renders the active Studio host identity with original profile endpoint and database', async ({ page, host }) => {
  await openWorkbench(page);
  await expect(identity(page)).toContainText('studio-desktop');
  await expect(identity(page)).toContainText('managed-local');
  await expect(identity(page)).toContainText(Endpoint);
  await expect(identity(page)).toContainText(Database);
  expect(host.bridgeRequests).toContain('GET /studio-bridge/manifest');
  expect(host.bridgeRequests).toContain('GET /studio-bridge/connections');
});

test('switching profiles with the same endpoint keeps the selected profile and database identity', async ({ page, host }) => {
  host.profiles.push(profile('remote-same-endpoint', 'Remote same endpoint', 'remote', 'RemoteDB'));
  await openWorkbench(page);
  await chooseProfile(page, 'Remote same endpoint');
  await expect(identity(page)).toContainText('remote-same-endpoint');
  await expect(identity(page)).toContainText('RemoteDB');
  await expect(identity(page)).toContainText(Endpoint);
  await expect.poll(() => host.activeProfileId).toBe('remote-same-endpoint');
  await expect.poll(() => host.activeDatabase).toBe('RemoteDB');
});

test('external healthy and unhealthy instances reject both Start and Stop while retaining Health', async ({ page, host }) => {
  host.status = status('external-running', false, true);
  await openWorkbench(page);
  await expect(state(page)).toHaveText('外部实例运行中');
  await expect(start(page)).toHaveCount(0);
  await expect(stop(page)).toHaveCount(0);
  await expect(health(page)).toBeVisible();
  await page.evaluate(() => {
    window.dispatchEvent(new CustomEvent('nativeWeb:studio.desktop-action', { detail: { id: 'server.start' } }));
    window.dispatchEvent(new CustomEvent('nativeWeb:studio.desktop-action', { detail: { id: 'server.stop' } }));
  });
  await health(page).click();
  expect(host.lifecycleRequests).toContain('GET /studio-bridge/server/status');
  expect(host.lifecycleRequests.filter((item) => item.startsWith('POST '))).toHaveLength(0);

  host.status = status('unhealthy', false, false);
  await health(page).click();
  await expect(state(page)).toHaveText('外部实例不健康');
  await expect(start(page)).toHaveCount(0);
  await expect(stop(page)).toHaveCount(0);
  await expect(health(page)).toBeVisible();
});

test('Studio-owned running and unhealthy instances permit Stop, including native menu action', async ({ page, host }) => {
  host.status = status('running', true, true);
  await openWorkbench(page);
  await expect(state(page)).toHaveText('Studio 运行中');
  await expect(stop(page)).toBeVisible();
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('nativeWeb:studio.desktop-action', { detail: { id: 'server.stop' } })));
  await expect(state(page)).toHaveText('Studio 已停止');
  expect(host.lifecycleRequests.filter((item) => item === 'POST /studio-bridge/server/stop')).toHaveLength(1);

  host.status = status('unhealthy', true, false);
  await health(page).click();
  await expect(state(page)).toHaveText('Studio 不健康');
  await expect(stop(page)).toBeVisible();
  await stop(page).click();
  await expect(state(page)).toHaveText('Studio 已停止');
});

test('stopped and failed host states stay distinct and do not expose Stop', async ({ page, host }) => {
  host.status = status('stopped', false, false);
  await openWorkbench(page);
  await expect(state(page)).toHaveText('Studio 已停止');
  await expect(stop(page)).toHaveCount(0);
  await expect(start(page)).toBeVisible();

  host.status = status('failed', false, false, '启动失败');
  await health(page).click();
  await expect(state(page)).toHaveText('Studio 启动失败');
  await expect(stop(page)).toHaveCount(0);
  await expect(start(page)).toBeVisible();
});

test('missing or inconsistent lifecycle contract fails closed and leaves Health available', async ({ page, host }) => {
  host.status = { ...status('running', true, true), canStop: false };
  await openWorkbench(page);
  await expect(contractWarning(page)).toHaveText('无法确认宿主合同');
  await expect(start(page)).toHaveCount(0);
  await expect(stop(page)).toHaveCount(0);
  await expect(health(page)).toBeVisible();
});

test('late status response after profile and database switch cannot overwrite current identity', async ({ page, host }) => {
  host.profiles.push(profile('remote-same-endpoint', 'Remote same endpoint', 'remote', 'NewDB'));
  host.holdStatus = true;
  await openWorkbench(page);
  await health(page).click();
  await expect.poll(() => host.releaseStatus !== null, { timeout: 5_000 }).toBe(true);
  await chooseProfile(page, 'Remote same endpoint');
  host.releaseStatus?.();
  host.releaseStatus = null;
  await expect(identity(page)).toContainText('remote-same-endpoint');
  await expect(identity(page)).toContainText('NewDB');
  await expect(identity(page)).not.toContainText('FactoryDB');
});

test('bridge failure clears fake health without blocking the workbench', async ({ page, host }) => {
  await page.setViewportSize({ width: 640, height: 760 });
  host.failBridge = true;
  await page.goto('/admin/app/sql?tool=table');
  await expect(page.locator('.workbench-page')).toBeVisible();
  await expect(page.getByTestId('studio-managed-state').first()).toHaveCount(0);
  await expect(page.getByText('Local healthy', { exact: true })).toHaveCount(0);
  await expect(page.locator('.workspace-tabs').first()).not.toHaveClass(/workspace-tabs--native/u);
  await expect(page.locator('.workspace-tabs__tools').first()).toBeHidden();
});

test('1100px breakpoint retains visible host state and an operable Health action', async ({ page, host }) => {
  await page.setViewportSize({ width: 1100, height: 760 });
  await openResponsiveWorkbench(page);
  await expect(state(page)).toHaveText('Studio 运行中');
  await expect(health(page)).not.toHaveClass(/n-button--loading/u);
  const previous = host.lifecycleRequests.filter((item) => item === 'GET /studio-bridge/server/status').length;
  await assertNativeControlsFit(page);
  await health(page).click();
  await expect.poll(() => host.lifecycleRequests.filter((item) => item === 'GET /studio-bridge/server/status').length).toBe(previous + 1);
  await expect(health(page)).not.toHaveClass(/n-button--loading/u);
  await assertNativeControlsFit(page);
  expect(host.lifecycleRequests.filter((item) => item.startsWith('POST '))).toHaveLength(0);
});

test('950px fixture CSS width preserves ordinary owned Health Stop Start and busy state', async ({ page, host }) => {
  await page.setViewportSize({ width: 950, height: 760 });
  await openResponsiveWorkbench(page);
  await ownedLifecycle(page, host);

  host.holdStatus = true;
  await health(page).click();
  await expect.poll(() => host.releaseStatus !== null, { timeout: 5_000 }).toBe(true);
  await expect(health(page)).toHaveClass(/n-button--loading/u);
  await expect(stop(page)).toHaveClass(/n-button--loading/u);
  await assertNativeControlsFit(page);
  host.holdStatus = false;
  host.releaseStatus?.();
  host.releaseStatus = null;
  await expect(health(page)).not.toHaveClass(/n-button--loading/u);
  await expect(state(page)).toHaveText('Studio 运行中');
});

test('640px and 500px CSS widths retain original identity and ordinary owned lifecycle without clipping', async ({ page, host }) => {
  await page.setViewportSize({ width: 640, height: 760 });
  await openResponsiveWorkbench(page);
  await ownedLifecycle(page, host);
  await page.setViewportSize({ width: 500, height: 760 });
  await ownedLifecycle(page, host);
  await expect(identity(page)).toHaveAttribute('title', `studio-desktop · managed-local · ${Endpoint} · ${Database}`);
});

test('500px CSS width keeps external unhealthy failed and inconsistent ownership contracts distinct', async ({ page, host }) => {
  await page.setViewportSize({ width: 500, height: 760 });
  host.status = status('external-running', false, true);
  await openResponsiveWorkbench(page);
  await expect(state(page)).toHaveText('外部实例运行中');
  await expect(start(page)).toHaveCount(0);
  await expect(stop(page)).toHaveCount(0);
  await assertNativeControlsFit(page);

  host.status = status('unhealthy', false, false);
  await health(page).click();
  await expect(state(page)).toHaveText('外部实例不健康');
  await expect(start(page)).toHaveCount(0);
  await expect(stop(page)).toHaveCount(0);
  await assertNativeControlsFit(page);

  host.status = status('unhealthy', true, false);
  await health(page).click();
  await expect(state(page)).toHaveText('Studio 不健康');
  await expect(stop(page)).toBeVisible();
  await expect(start(page)).toHaveCount(0);
  await assertNativeControlsFit(page);

  host.status = { ...status('running', true, true), canStop: false };
  await health(page).click();
  await expect(contractWarning(page)).toHaveText('无法确认宿主合同');
  await expect(start(page)).toHaveCount(0);
  await expect(stop(page)).toHaveCount(0);
  await assertNativeControlsFit(page);

  host.status = status('failed', false, false, '启动失败');
  await health(page).click();
  await expect(state(page)).toHaveText('Studio 启动失败');
  await expect(contractWarning(page)).toHaveCount(0);
  await expect(start(page)).toBeVisible();
  await expect(stop(page)).toHaveCount(0);
  await assertNativeControlsFit(page);
  expect(host.lifecycleRequests.filter((item) => item.startsWith('POST '))).toHaveLength(0);
});

async function openWorkbench(page: Page): Promise<void> {
  await page.goto('/admin/app/sql?tool=table');
  await expect(identity(page)).toBeVisible();
}

async function openResponsiveWorkbench(page: Page): Promise<void> {
  await openWorkbench(page);
  // The existing <=1099px Explorer is an overlay. Use its ordinary control;
  // these fixture assertions prove the explicitly collapsed workspace only.
  const frame = page.locator('.workbench-frame');
  const expand = page.getByTitle('展开资源浏览器', { exact: true });
  const alreadyCollapsed = await expand.isVisible()
    || /(?:^|\s)is-explorer-collapsed(?:\s|$)/u.test(await frame.getAttribute('class') ?? '');
  if (!alreadyCollapsed) {
    const collapse = page.getByTitle('收起资源浏览器', { exact: true });
    await expect(collapse).toHaveCount(1);
    await expect(collapse).toBeVisible();
    await collapse.click();
  }
  await expect(frame).toHaveClass(/is-explorer-collapsed/u);
  await expect(expand).toBeVisible();
}

async function assertNativeControlsFit(page: Page): Promise<void> {
  await expect(identity(page)).toBeVisible();
  await expect(state(page)).toBeVisible();
  await expect(health(page)).toBeVisible();
  const bounds = await page.locator('.workspace-tabs').first().evaluate((element) => {
    const toolbar = element.getBoundingClientRect();
    const controls = Array.from(element.querySelectorAll<HTMLElement>('[data-testid^="studio-managed-"], [data-testid="studio-host-identity"]'));
    if (controls.length > 6) throw new Error('Responsive native control count exceeded.');
    return {
      toolbarLeft: toolbar.left, toolbarRight: toolbar.right,
      clientWidth: element.clientWidth, scrollWidth: element.scrollWidth,
      viewportWidth: window.innerWidth, documentWidth: document.documentElement.scrollWidth,
      controls: controls.map((control) => {
        const rectangle = control.getBoundingClientRect();
        return { id: control.dataset.testid, left: rectangle.left, right: rectangle.right, width: rectangle.width, height: rectangle.height };
      }),
    };
  });
  expect(bounds.toolbarLeft).toBeGreaterThanOrEqual(0);
  expect(bounds.toolbarRight).toBeLessThanOrEqual(bounds.viewportWidth + 1);
  expect(bounds.documentWidth).toBeLessThanOrEqual(bounds.viewportWidth + 1);
  expect(bounds.scrollWidth).toBeLessThanOrEqual(bounds.clientWidth + 1);
  expect(bounds.controls.every((control) => control.width > 0 && control.height > 0
    && control.left >= bounds.toolbarLeft - 1 && control.right <= bounds.toolbarRight + 1)).toBe(true);
}

async function ownedLifecycle(page: Page, host: HostFixture): Promise<void> {
  await expect(state(page)).toHaveText('Studio 运行中');
  await expect(health(page)).not.toHaveClass(/n-button--loading/u);
  await assertNativeControlsFit(page);
  const previousPosts = host.lifecycleRequests.filter((item) => item.startsWith('POST ')).length;
  const previousHealth = host.lifecycleRequests.filter((item) => item === 'GET /studio-bridge/server/status').length;
  await health(page).click();
  await expect.poll(() => host.lifecycleRequests.filter((item) => item === 'GET /studio-bridge/server/status').length).toBe(previousHealth + 1);
  await expect(health(page)).not.toHaveClass(/n-button--loading/u);
  await stop(page).click();
  await expect(state(page)).toHaveText('Studio 已停止');
  await expect(start(page)).toBeVisible();
  await expect(stop(page)).toHaveCount(0);
  await assertNativeControlsFit(page);

  const stoppedHealth = host.lifecycleRequests.filter((item) => item === 'GET /studio-bridge/server/status').length;
  await health(page).click();
  await expect.poll(() => host.lifecycleRequests.filter((item) => item === 'GET /studio-bridge/server/status').length).toBe(stoppedHealth + 1);
  await expect(health(page)).not.toHaveClass(/n-button--loading/u);
  await expect(state(page)).toHaveText('Studio 已停止');
  await start(page).click();
  await expect(state(page)).toHaveText('Studio 运行中');
  await expect(stop(page)).toBeVisible();
  await expect(start(page)).toHaveCount(0);
  await assertNativeControlsFit(page);
  await expect(identity(page)).toContainText(`studio-desktop · managed-local · ${Endpoint} · ${Database}`);
  expect(host.lifecycleRequests.filter((item) => item.startsWith('POST ')).slice(previousPosts)).toEqual([
    'POST /studio-bridge/server/stop', 'POST /studio-bridge/server/start',
  ]);
}

function identity(page: Page) { return page.getByTestId('studio-host-identity').first(); }
function state(page: Page) { return page.getByTestId('studio-managed-state').first(); }
function start(page: Page) { return page.getByTestId('studio-managed-start').first(); }
function stop(page: Page) { return page.getByTestId('studio-managed-stop').first(); }
function health(page: Page) { return page.getByTestId('studio-managed-health').first(); }
function contractWarning(page: Page) { return page.getByTestId('studio-managed-contract-warning').first(); }

async function chooseProfile(page: Page, label: string): Promise<void> {
  await page.getByRole('button', { name: /Managed Local|Remote same endpoint/ }).first().click();
  await page.getByText(new RegExp(label, 'u')).click();
}

function makeFixture(): HostFixture {
  const managed = profile('managed-local', 'Managed Local', 'managed-local', Database);
  return {
    status: status('running', true, true), profiles: [managed], activeProfileId: managed.id, activeDatabase: Database,
    bridgeRequests: [], lifecycleRequests: [], failBridge: false, holdStatus: false, releaseStatus: null, requestCount: 0,
  };
}

function profile(id: string, name: string, kind: 'managed-local' | 'remote', database: string): Profile {
  return {
    id, name, kind, baseUrl: Endpoint, defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1,
    identity: { host: 'studio-desktop', profileId: id, baseUrl: Endpoint, database },
  };
}

function status(lifecycle: Lifecycle, startedByStudio: boolean, healthy: boolean, error: string | null = null): HostStatus {
  const isRunning = lifecycle === 'running' || lifecycle === 'external-running' || lifecycle === 'unhealthy';
  const processOwner = isRunning ? (startedByStudio ? 'studio' : 'external') : 'none';
  return {
    isRunning, startedByStudio, healthy, processId: isRunning ? (startedByStudio ? 4242 : 8787) : null,
    url: Endpoint, dataRoot: 'C:\\SonnetDB\\Studio\\data', error,
    processOwner, lifecycleState: lifecycle, canStop: isRunning && startedByStudio,
  };
}

async function handleBridge(route: Route, fixture: HostFixture, url: URL): Promise<void> {
  if (fixture.failBridge) return route.abort('connectionrefused');
  if (url.pathname.endsWith('/manifest')) {
    return json(route, {
      mode: 'desktop', version: 'wb15-fixture', serverUrl: Endpoint, managedServerUrl: Endpoint,
      dataRoot: 'C:\\SonnetDB\\Studio\\data', capabilities: ['server.managedLocal', 'menu.native'], managedServer: fixture.status,
    });
  }
  if (url.pathname.endsWith('/connections')) {
    if (route.request().method() === 'PUT') {
      const snapshot = route.request().postDataJSON() as { profiles?: Profile[]; activeProfileId?: string; activeDatabase?: string };
      if (Array.isArray(snapshot.profiles)) fixture.profiles = snapshot.profiles.map((item) => ({
        ...item,
        identity: {
          host: 'studio-desktop', profileId: item.id, baseUrl: item.baseUrl, database: item.defaultDatabase,
        },
      }));
      if (typeof snapshot.activeProfileId === 'string') fixture.activeProfileId = snapshot.activeProfileId;
      if (typeof snapshot.activeDatabase === 'string') fixture.activeDatabase = snapshot.activeDatabase;
    }
    const activeProfile = fixture.profiles.find((item) => item.id === fixture.activeProfileId);
    return json(route, {
      profiles: fixture.profiles, activeProfileId: fixture.activeProfileId, activeDatabase: fixture.activeDatabase,
      activeIdentity: activeProfile ? { ...activeProfile.identity, database: fixture.activeDatabase } : null,
    });
  }
  if (url.pathname.endsWith('/server/status')) {
    fixture.lifecycleRequests.push(`${route.request().method()} ${url.pathname}`);
    if (fixture.holdStatus) await new Promise<void>((resolve) => {
      const timeout = setTimeout(resolve, 10_000);
      fixture.releaseStatus = () => { clearTimeout(timeout); resolve(); };
    });
    return json(route, fixture.status);
  }
  if (url.pathname.endsWith('/server/start') || url.pathname.endsWith('/server/stop')) {
    fixture.lifecycleRequests.push(`${route.request().method()} ${url.pathname}`);
    if (url.pathname.endsWith('/server/start')) fixture.status = status('running', true, true);
    else fixture.status = status('stopped', false, false);
    return json(route, fixture.status);
  }
  if (url.pathname.endsWith('/dialogs/select-directory')) return json(route, { canceled: true, path: null, error: null });
  return json(route, {});
}

async function json(route: Route, body: unknown, statusCode = 200): Promise<void> {
  await route.fulfill({
    status: statusCode,
    contentType: 'application/json',
    headers: {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Methods': 'GET,POST,PUT,OPTIONS',
      'Access-Control-Allow-Headers': 'Content-Type,X-SonnetDB-Studio-Bridge-Token',
    },
    body: JSON.stringify(body),
  });
}
