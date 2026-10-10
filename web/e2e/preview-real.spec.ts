import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

// Built Preview Web served by real Kestrel. No token injection replaces UI login.
test.describe.configure({ mode: 'default', timeout: 60_000 });
test.use({ trace: 'off', video: 'off' });
const db = 'PreviewDb';
const password = 'P03:LocalTestOnly!';
let admin = '';
let origin = '';

async function api(request: APIRequestContext, method: string, path: string, data?: unknown) {
  const response = await request.fetch(`${origin}${path}`, { method, data, headers: { Authorization: `Bearer ${admin}` }, timeout: 15_000, maxRetries: 0 });
  expect(response.ok(), `${method} ${path}: HTTP ${response.status()}${response.ok() ? '' : ' ' + (await response.text()).slice(0, 512)}`).toBe(true);
  return response;
}
async function sql(request: APIRequestContext, value: string, control = false) {
  const response = await api(request, 'POST', control ? '/v1/sql' : `/v1/db/${db}/sql`, { sql: value });
  const text = await response.text();
  expect(text).not.toContain('"type":"error"');
  return text;
}
async function login(page: Page, username = 'p03_writer') {
  await page.goto('/admin/login');
  await page.getByPlaceholder('admin', { exact: true }).fill(username);
  await page.getByPlaceholder('输入密码', { exact: true }).fill(password);
  await page.getByRole('button', { name: '登录后台', exact: true }).click();
  await expect(page).toHaveURL(/\/admin\/app\/dashboard$/u);
}
async function workbench(page: Page, tool = 'sql') {
  await page.goto(`/admin/app/sql?db=${db}&tool=${tool}`);
  await expect(page.getByTestId('preview-workbench')).toBeVisible();
  await expect(page.getByRole('button', { name: '清空并重新读取', exact: true })).toBeEnabled();
  if (tool === 'sql') await expect(page.locator('#preview-sql')).toBeVisible();
  else await expect(page.getByTestId(`workbench-${tool === 'bucket' ? 'object' : tool}`)).toBeVisible();
}
async function stage(page: Page, id: number, value: string) {
  const panel = page.getByTestId('workbench-table');
  await panel.getByRole('button', { name: 'Insert row', exact: true }).click();
  const fields = panel.locator('.relation-insert__grid .relation-field');
  await fields.filter({ hasText: 'DeviceID' }).locator('input').fill(String(id));
  await fields.filter({ hasText: 'MixedCaseName' }).locator('input,textarea').fill(value);
  await panel.getByRole('button', { name: 'Stage insert', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText(value);
}

test.beforeAll(async ({ browser, request }) => {
  const configured = process.env.SONNETDB_PREVIEW_REAL_BASE_URL;
  if (!configured) throw new Error('SONNETDB_PREVIEW_REAL_BASE_URL is required; a real isolated Server must be running.');
  const url = new URL(configured);
  expect(url.hostname).toBe('127.0.0.1'); expect(url.protocol).toBe('http:');
  origin = url.origin;
  const page = await browser.newPage({ baseURL: origin });
  try {
    await page.goto('/admin');
    await expect(page).toHaveURL(/\/admin\/(?:setup|login)(?:\?|$)/u);
    if (page.url().endsWith('/admin/setup')) {
      await page.getByPlaceholder('至少一组可记忆的强密码').fill(password);
      await page.getByRole('button', { name: '完成初始化' }).click();
      await expect(page).toHaveURL(/\/admin\/app\/dashboard$/u);
      await page.getByRole('button', { name: '退出登录' }).click();
    }
    await login(page, 'admin');
    admin = await page.evaluate(() => JSON.parse(localStorage.getItem('sndb.auth')!).token as string);
  } finally { await page.close(); }
  const existing = await (await api(request, 'GET', '/v1/db')).json();
  if (existing.databases.includes(db)) return;
  await api(request, 'POST', '/v1/db', { name: db });
  await api(request, 'POST', '/v1/db', { name: 'HiddenDb' });
  await sql(request, `CREATE USER p03_writer WITH PASSWORD '${password}'`, true);
  await sql(request, `CREATE USER p03_reader WITH PASSWORD '${password}'`, true);
  await sql(request, `GRANT WRITE ON DATABASE ${db} TO p03_writer`, true);
  await sql(request, `GRANT READ ON DATABASE ${db} TO p03_reader`, true);
  await sql(request, 'CREATE TABLE "DeviceID_Main" ("DeviceID" INT, "MixedCaseName" STRING, PRIMARY KEY ("DeviceID"))');
  await sql(request, 'INSERT INTO "DeviceID_Main" ("DeviceID", "MixedCaseName") VALUES ' + Array.from({ length: 1001 }, (_, i) => `(${i + 1}, 'P03-private-${i + 1}')`).join(','));
  await sql(request, 'CREATE MEASUREMENT Samples (DeviceID TAG, Reading FIELD FLOAT)');
  await sql(request, "INSERT INTO Samples(time, DeviceID, Reading) VALUES (1780000000000, 'sensor-a', 1.5)");
});

test.beforeEach(async ({ request }) => { await sql(request, `GRANT WRITE ON DATABASE ${db} TO p03_writer`, true); });

test('real username/password login, ordinary grants, seven modules and deferred direct routes', async ({ page }) => {
  await login(page);
  await expect(page.locator('nav[aria-label="全局模块"] button')).toHaveCount(7);
  await expect(page.locator('nav[aria-label="全局模块"] button:disabled')).toHaveCount(4);
  await page.getByRole('button', { name: '设置 · About', exact: true }).click();
  await expect(page.getByTestId('preview-about')).toContainText('尚未提供经过验收的独立发行包');
  await workbench(page);
  await expect(page.getByLabel('数据库', { exact: true })).not.toContainText('HiddenDb');
  for (const path of ['/admin/app/monitoring', '/admin/app/govern', '/admin/auto-login', '/admin/copilot/oauth/callback', '/admin/app/sql?tool=trajectory', '/admin/app/sql?restore=old-tab']) {
    await page.goto(path); await expect(page.getByRole('heading', { name: '本预览未开放' })).toBeVisible();
  }
});

test('real readonly user has readwrite HTTP role but Read database grant and cannot insert', async ({ page, request }) => {
  await login(page, 'p03_reader'); await workbench(page, 'table');
  await expect(page.getByTestId('workbench-table').getByRole('button', { name: 'Insert row', exact: true })).toBeDisabled();
  await expect(page.getByTestId('workbench-table').locator('.relation-grid')).toContainText('P03-private-1');
  const token = await page.evaluate(() => JSON.parse(localStorage.getItem('sndb.auth')!).token as string);
  const access = await request.get(`${origin}/v1/db/${db}/access`, { headers: { Authorization: `Bearer ${token}` } });
  expect(await access.json()).toMatchObject({ httpRole: 'readwrite', databasePermission: 'Read', canRead: true, canWrite: false });
  const denied = await request.post(`${origin}/v1/db/${db}/sql`, { headers: { Authorization: `Bearer ${token}` }, data: { sql: 'DELETE FROM "DeviceID_Main" WHERE "DeviceID" = 1' } });
  expect(await denied.text()).toContain('forbidden');
});

test('real SQL is capped at 1000 with visible truncation and rejects write/multi-statement input', async ({ page }) => {
  await login(page); await workbench(page);
  await page.locator('#preview-sql').fill('SELECT * FROM "DeviceID_Main"');
  await page.getByRole('button', { name: '执行读取' }).click();
  await expect(page.getByRole('status')).toContainText('结果已截断');
  await expect(page.locator('.preview-sql pre')).toContainText('P03-private-1000');
  await expect(page.locator('.preview-sql pre')).not.toContainText('P03-private-1001');
  await page.locator('#preview-sql').fill('SELECT 1; DELETE FROM "DeviceID_Main"');
  await page.getByRole('button', { name: '执行读取' }).click();
  await expect(page.getByRole('alert')).toContainText('只允许单条');
});

test('approved single insert sends once and rejected approval sends nothing', async ({ page, request }) => {
  await login(page); await workbench(page, 'table');
  const writes: string[] = [];
  page.on('request', (entry) => { if (entry.method() === 'POST' && entry.url().endsWith('/sql') && /INSERT/u.test(entry.postData() ?? '')) writes.push(entry.url()); });
  await stage(page, 2001, 'P03-approved');
  await page.getByRole('button', { name: /确认执行 1 项操作/u }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect.poll(async () => (await sql(request, 'SELECT * FROM "DeviceID_Main" WHERE "DeviceID" = 2001')).includes('P03-approved')).toBe(true);
  expect(writes).toHaveLength(1);
  await stage(page, 2002, 'P03-rejected');
  await page.getByRole('dialog').getByRole('contentinfo').getByRole('button', { name: '返回编辑' }).click();
  await page.getByTestId('workbench-table').getByRole('button', { name: 'Discard staged edits' }).click();
  expect(writes).toHaveLength(1);
});

test('real revocation clears payloads and pending approval before submission; login switch clears all data', async ({ page, request }) => {
  await login(page); await workbench(page, 'table');
  await stage(page, 2003, 'P03-revoked-draft');
  await sql(request, `REVOKE ON DATABASE ${db} FROM p03_writer`, true);
  await page.getByRole('button', { name: /确认执行 1 项操作/u }).click();
  await expect(page.getByRole('status')).toContainText('权限已变化');
  await expect(page.getByTestId('workbench-table')).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('P03-private-');
  await expect(page.locator('body')).not.toContainText('P03-revoked-draft');
  await page.getByRole('button', { name: '退出登录' }).click();
  await login(page, 'p03_reader');
  await expect(page.locator('body')).not.toContainText('P03-revoked-draft');
  expect(await sql(request, 'SELECT * FROM "DeviceID_Main" WHERE "DeviceID" = 2003')).not.toContain('P03-revoked-draft');
});

test('injected loss after real committed insert reports unknown and explicit reload does not replay', async ({ page, request }) => {
  await login(page); await workbench(page, 'table');
  let writes = 0;
  await page.route(`**/v1/db/${db}/sql`, async (route) => {
    if (!/INSERT/u.test(route.request().postData() ?? '')) { await route.continue(); return; }
    writes++;
    const response = await route.fetch();
    expect(await response.text()).toContain('"recordsAffected":1');
    await route.abort('connectionfailed');
  });
  await stage(page, 2004, 'P03-unknown-committed');
  await page.getByRole('button', { name: /确认执行 1 项操作/u }).click();
  await expect(page.getByRole('status')).toContainText('写入状态未知');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(await sql(request, 'SELECT * FROM "DeviceID_Main" WHERE "DeviceID" = 2004')).toContain('P03-unknown-committed');
  await page.getByRole('button', { name: '清空并重新读取' }).click();
  await expect(page.getByTestId('workbench-table')).toBeVisible();
  expect(writes).toBe(1);
});

test('cancel discards delayed real read and never restores old response after explicit reload', async ({ page }) => {
  await login(page); await workbench(page);
  let release!: () => void;
  let arrived!: () => void;
  const held = new Promise<void>((resolve) => { release = resolve; });
  const received = new Promise<void>((resolve) => { arrived = resolve; });
  await page.route(`**/v1/db/${db}/sql`, async (route) => {
    const response = await route.fetch(); arrived();
    await held; await route.fulfill({ response }).catch(() => undefined);
  }, { times: 1 });
  await page.locator('#preview-sql').fill('SELECT * FROM "DeviceID_Main" LIMIT 1');
  await page.getByRole('button', { name: '执行读取' }).click();
  await received;
  await page.getByRole('button', { name: '取消当前操作' }).click();
  release();
  await expect(page.locator('.preview-sql pre')).toHaveCount(0);
  await page.getByRole('button', { name: '清空并重新读取' }).click();
  await expect(page.locator('#preview-sql')).toHaveValue('SHOW TABLES');
  await expect(page.locator('.preview-sql pre')).toHaveCount(0);
});

test('scope R1 keeps nine entries, proves table and measurement reads and denies seven deferred model requests', async ({ page }) => {
  await login(page, 'p03_reader');
  for (const model of ['table', 'measurement']) {
    await workbench(page, model);
    await expect(page.locator('.workbench-section-tabs button:disabled').first()).toBeVisible();
    await expect(page.getByTestId(`workbench-${model}`)).toContainText(model === 'table' ? 'P03-private-1' : 'sensor-a');
  }
  const modelRequests: string[] = [];
  page.on('request', (entry) => { if (/\/(?:documents|kv|fulltext|vector|s3|mq|graphs)(?:\/|$)/u.test(new URL(entry.url()).pathname)) modelRequests.push(entry.url()); });
  for (const model of ['document', 'kv', 'fulltext', 'vector', 'bucket', 'mq', 'graph']) {
    await page.goto(`/admin/app/sql?db=${db}&tool=${model}`);
    await expect(page.getByTestId('preview-model-deferred')).toContainText('服务端资源预算');
  }
  expect(modelRequests).toEqual([]);
});

test('revoked real login token clears loaded data before export and requires fresh login', async ({ page, request }) => {
  await login(page); await workbench(page);
  await page.locator('#preview-sql').fill('SELECT * FROM "DeviceID_Main" LIMIT 1');
  await page.getByRole('button', { name: '执行读取' }).click();
  await expect(page.locator('.preview-sql pre')).toContainText('P03-private-1');
  const tokenId = await page.evaluate(() => JSON.parse(localStorage.getItem('sndb.auth')!).tokenId as string);
  await sql(request, `REVOKE TOKEN '${tokenId}'`, true);
  const downloads: string[] = []; page.on('download', (download) => downloads.push(download.suggestedFilename()));
  await page.getByRole('button', { name: '导出当前窗口' }).click();
  await expect(page.locator('.preview-sql pre')).toHaveCount(0);
  await expect(page.getByRole('status')).toContainText('凭据失效');
  expect(downloads).toEqual([]);
  await page.getByRole('button', { name: '退出登录' }).click(); await login(page);
});

test('write grant downgrade invalidates pending insert without a write request', async ({ page, request }) => {
  await login(page); await workbench(page, 'table'); await stage(page, 2005, 'P03-downgrade');
  await sql(request, `REVOKE ON DATABASE ${db} FROM p03_writer`, true);
  await sql(request, `GRANT READ ON DATABASE ${db} TO p03_writer`, true);
  const token = await page.evaluate(() => JSON.parse(localStorage.getItem('sndb.auth')!).token as string);
  const access = await request.get(`${origin}/v1/db/${db}/access`, { headers: { Authorization: `Bearer ${token}` } });
  expect(await access.json()).toMatchObject({ canRead: true, canWrite: false });
  await page.getByRole('button', { name: /确认执行 1 项操作/u }).click();
  await expect(page.getByRole('status')).toContainText('权限已变化');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(await sql(request, 'SELECT * FROM "DeviceID_Main" WHERE "DeviceID" = 2005')).not.toContain('P03-downgrade');
});

test('injected oversized response and offline state clear data and reconnect needs explicit read', async ({ page, context }) => {
  await login(page); await workbench(page);
  await page.route(`**/v1/db/${db}/sql`, (route) => route.fulfill({ status: 200, contentType: 'application/x-ndjson', body: 'x'.repeat(4 * 1024 * 1024 + 1) }), { times: 1 });
  await page.locator('#preview-sql').fill('SELECT 1'); await page.getByRole('button', { name: '执行读取' }).click();
  await expect(page.getByRole('status')).toContainText('响应超限');
  await expect(page.locator('.preview-sql pre')).toHaveCount(0);
  await page.getByRole('button', { name: '清空并重新读取' }).click();
  await expect(page.locator('#preview-sql')).toBeVisible();
  await context.setOffline(true);
  await expect(page.getByRole('status')).toContainText('连接已断开');
  await context.setOffline(false);
  await expect(page.locator('#preview-sql')).toHaveCount(0);
  await page.getByRole('button', { name: '清空并重新读取' }).click();
  await expect(page.locator('#preview-sql')).toHaveValue('SHOW TABLES');
});

test('database change discards a delayed real response without contaminating the new context', async ({ page }) => {
  await login(page); await workbench(page);
  let release!: () => void; let arrived!: () => void;
  const held = new Promise<void>((resolve) => { release = resolve; });
  const received = new Promise<void>((resolve) => { arrived = resolve; });
  await page.route(`**/v1/db/${db}/sql`, async (route) => { const response = await route.fetch(); arrived(); await held; await route.fulfill({ response }).catch(() => undefined); }, { times: 1 });
  await page.locator('#preview-sql').fill('SELECT * FROM "DeviceID_Main" LIMIT 1'); await page.getByRole('button', { name: '执行读取' }).click();
  await received; await page.getByLabel('数据库', { exact: true }).selectOption(''); release();
  await expect(page.locator('.preview-sql pre')).toHaveCount(0);
  await page.getByLabel('数据库', { exact: true }).selectOption(db);
  await expect(page.locator('#preview-sql')).toHaveValue('SHOW TABLES');
  await expect(page.locator('.preview-sql pre')).toHaveCount(0);
});
