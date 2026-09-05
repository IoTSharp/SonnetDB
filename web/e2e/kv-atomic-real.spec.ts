import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';

const baseUrl = process.env.SONNETDB_KV_REAL_BASE_URL;
const token = process.env.SONNETDB_KV_REAL_TOKEN;
const database = process.env.SONNETDB_KV_REAL_DATABASE;
const enabled = Boolean(baseUrl && token && database);

interface ValueResponse {
  found: boolean;
  value?: string | null;
  version?: number | null;
}

interface AtomicResponse {
  applied?: boolean;
  versionText?: string | null;
  previous?: ValueResponse;
  mutationVersionText?: string | null;
}

async function selectOption(page: Page, select: Locator, label: string): Promise<void> {
  await select.click();
  await page.locator('.n-base-select-option').getByText(label, { exact: true }).click();
}

async function capture(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  await expect(page.locator('.n-message')).toHaveCount(0, { timeout: 8_000 });
  const path = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage: true });
  await testInfo.attach(name, { path, contentType: 'image/png' });
}

function realJourney(name: string, viewport: { width: number; height: number }): void {
  test.describe(`KV atomic real server ${name}`, () => {
    test.skip(!enabled, 'Requires the isolated real KV server URL, token and database.');
    test.describe.configure({ retries: 0 });
    test.use({ viewport });

    test('NX, empty exchange and repeated get-and-delete use approvals and authoritative state', async ({ page }, testInfo) => {
      test.setTimeout(120_000);
      const origin = baseUrl!;
      const db = database!;
      const keyspace = `kv_ui_${name}_${Date.now()}_${testInfo.workerIndex}`;
      const key = 'journey:empty';
      const path = (action: string) => `/v1/db/${encodeURIComponent(db)}/kv/${encodeURIComponent(keyspace)}/${action}`;
      const request = async <T,>(action: string, data: Record<string, unknown>): Promise<T> => {
        const response = await page.request.post(new URL(path(action), origin).href, {
          headers: { Authorization: `Bearer ${token!}` }, data, timeout: 10_000,
        });
        try {
          expect(response.ok(), `KV ${action} HTTP status ${response.status()}`).toBeTruthy();
          return await response.json() as T;
        } finally { await response.dispose(); }
      };

      // The parent runner owns this isolated database and its cleanup lifecycle.
      await request('set', { key: '__fixture__', value: '', expiresAtUtc: null });
      await page.addInitScript(({ authToken, dbName }) => {
        localStorage.setItem('sndb.auth', JSON.stringify({ username: 'kv-real-test', token: authToken, tokenId: 'kv-real-test', isSuperuser: true }));
        localStorage.setItem('sndb.connection.library.v1', JSON.stringify({
          profiles: [{ id: 'managed-local', name: 'KV real test', kind: 'managed-local', baseUrl: '/', defaultDatabase: dbName,
            tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: 'managed-local', activeDatabase: dbName,
        }));
        localStorage.setItem('sndb.sql.console.tabs.v1', JSON.stringify({
          tabs: [{ id: 'kv-real', title: 'KV real journey', db: dbName, sql: '', results: [], summary: '', errorMsg: '',
            ranOnce: false, source: 'manual', createdAt: 1, updatedAt: 1 }], activeTabId: 'kv-real',
        }));
      }, { authToken: token!, dbName: db });
      await page.goto(new URL('/admin/app/sql?tool=kv', origin).href);
      const surface = page.getByTestId('workbench-kv');
      await expect(surface).toBeVisible();
      await expect(surface.locator('.kv-toolbar__keyspace')).toBeEnabled();
      await selectOption(page, surface.locator('.kv-toolbar__keyspace'), keyspace);
      await expect(surface.locator('.kv-toolbar__title')).toHaveText(keyspace);
      await surface.locator('.workbench-section-tabs').getByRole('button', { name: '批量操作', exact: true }).click();

      const stage = async (operation: 'Set' | 'Get and set' | 'Get and delete', value: string) => {
        const drawer = page.locator('[data-workbench-result-drawer]');
        if (await drawer.isVisible()) await drawer.getByTitle('关闭结果').click();
        await selectOption(page, surface.locator('[aria-label="KV operation"]'), operation);
        await surface.getByPlaceholder('Key', { exact: true }).fill(key);
        if (operation !== 'Get and delete') await surface.getByPlaceholder('Value', { exact: true }).fill(value);
        if (operation === 'Set') await selectOption(page, surface.locator('[aria-label="Set condition"]'), 'Only if absent (NX)');
        const label = operation === 'Set' ? 'Stage set' : operation === 'Get and set' ? 'Stage exchange' : 'Stage get and delete';
        const button = surface.getByRole('button', { name: label, exact: true });
        await button.scrollIntoViewIfNeeded();
        const box = await button.boundingBox();
        expect(box, 'KV staging control must be visible').not.toBeNull();
        expect(box!.x).toBeGreaterThanOrEqual(0);
        expect(box!.x + box!.width).toBeLessThanOrEqual(viewport.width + 1);
        await button.click({ timeout: 10_000 });
        const approval = page.getByRole('dialog', { name: 'KV operation batch' });
        await expect(approval).toBeVisible();
        await expect(approval).toContainText(`${db}.${keyspace}`);
        await expect(approval).toContainText(key);
        if (operation === 'Get and delete') await approval.getByRole('checkbox').check();
        return approval;
      };
      const confirm = async (approval: Locator, action: string): Promise<AtomicResponse> => {
        const responsePromise = page.waitForResponse((response) => response.request().method() === 'POST'
          && new URL(response.url()).pathname === path(action), { timeout: 15_000 });
        await approval.getByRole('button', { name: /确认执行/ }).click();
        const response = await responsePromise;
        expect(response.ok(), `UI KV ${action} HTTP status ${response.status()}`).toBeTruthy();
        const result = await response.json() as AtomicResponse;
        await expect(approval).toBeHidden();
        await expect(surface.locator('.kv-alert')).toHaveCount(0);
        await expect(surface.getByPlaceholder('Key', { exact: true })).toHaveValue(key);
        return result;
      };
      const displayed = async (): Promise<Record<string, unknown>> => {
        const drawer = page.locator('[data-workbench-result-drawer]');
        if (!(await drawer.isVisible())) await surface.getByTitle('查看 KV 结果').click();
        await expect(drawer).toBeVisible();
        await drawer.locator('.n-tabs-tab').filter({ hasText: /^JSON$/ }).click();
        if (viewport.width < 600) {
          const command = await drawer.locator('.sql-result-card__sql').boundingBox();
          const tabs = await drawer.locator('.n-tabs').boundingBox();
          expect(command!.y + command!.height).toBeLessThanOrEqual(tabs!.y);
        }
        const result = JSON.parse(await drawer.locator('.sql-result-card__pre').innerText()) as Record<string, unknown>[];
        expect(result).toHaveLength(1);
        return result[0]!;
      };

      const firstApproval = await stage('Set', '');
      await expect(firstApproval).toContainText('NX');
      await capture(page, testInfo, `${name}-nx-approval`);
      expect((await confirm(firstApproval, 'set-conditional')).applied).toBe(true);
      expect(await displayed()).toMatchObject({ applied: true, affected: 1 });
      expect(await request<ValueResponse>('get', { key })).toMatchObject({ found: true, value: '' });

      expect((await confirm(await stage('Set', 'must-not-replace'), 'set-conditional')).applied).toBe(false);
      expect(await displayed()).toMatchObject({ applied: false, affected: 0, state: 'not-applied' });
      expect(await request<ValueResponse>('get', { key })).toMatchObject({ found: true, value: '' });

      const exchange = await confirm(await stage('Get and set', 'after'), 'get-and-set');
      expect(exchange.previous).toMatchObject({ found: true, value: '' });
      expect(await displayed()).toMatchObject({ previousFound: true, previousValueBase64: '', affected: 1 });
      expect(await request<ValueResponse>('get', { key })).toMatchObject({ found: true, value: 'YWZ0ZXI=' });
      await capture(page, testInfo, `${name}-exchange-empty-result`);

      const removed = await confirm(await stage('Get and delete', ''), 'get-and-delete');
      expect(removed.previous).toMatchObject({ found: true, value: 'YWZ0ZXI=' });
      expect(await displayed()).toMatchObject({ previousFound: true, previousValueBase64: 'YWZ0ZXI=', affected: 1 });
      expect(await request<ValueResponse>('get', { key })).toMatchObject({ found: false });

      const repeated = await confirm(await stage('Get and delete', ''), 'get-and-delete');
      expect(repeated.previous).toMatchObject({ found: false });
      expect(await displayed()).toMatchObject({ previousFound: false, mutationVersion: null, affected: 0 });
      expect(await request<ValueResponse>('get', { key })).toMatchObject({ found: false });
      await capture(page, testInfo, `${name}-repeated-delete-result`);
    });
  });
}

realJourney('desktop', { width: 1600, height: 1000 });
realJourney('mobile', { width: 390, height: 844 });
