// Installed-package acceptance; uses only the existing browsers and synthetic local data.
import { chromium, expect } from '../../web/node_modules/@playwright/test/index.mjs';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';

const [root, phase, version, source] = process.argv.slice(2);
if (!path.isAbsolute(root) || !['first', 'reopen', 'rollback', 'micro09', 'legacy'].includes(phase)) throw new Error('Explicit owned evidence root/phase required.');
const origin = 'http://127.0.0.1:5080';
const password = process.env.P05_TEST_PASSWORD;
if (!password) throw new Error('Ephemeral fixture password missing.');
const db = 'P05Db', hidden = 'P05Hidden';
const deadline = Date.now() + 720_000;
const records = [], traffic = [];
let token = '', count = 0, context, page;
const save = (name, data) => fs.writeFileSync(path.join(root, name), JSON.stringify(data, null, 2), { flag: 'wx' });
const scrub = (text) => String(text).replaceAll(password, '[REDACTED]').replaceAll(token || '<no-token>', '[REDACTED]');
const watchdog = setTimeout(() => { process.stderr.write('Journey deadline exceeded\n'); process.exit(124); }, 730_000);
async function step(name, action, evidenceKind = 'REAL_INSTALLED_UI_AND_SERVER') {
  if (++count > 80 || Date.now() > deadline || fs.existsSync(path.join(root, 'cancel'))) throw new Error('Step/time/cancellation budget.');
  const started = Date.now();
  try {
    const detail = await action();
    records.push({ name, status: 'PASS', evidenceKind, elapsedMs: Date.now() - started, detail: detail ?? null });
    process.stdout.write(`${name}: PASS\n`);
  } catch (error) {
    records.push({ name, status: 'FAIL', evidenceKind, elapsedMs: Date.now() - started, error: scrub(error.message).slice(0, 1000) });
    if (page) fs.writeFileSync(path.join(root, `${phase}-${count}-failure.txt`), scrub(await page.locator('body').innerText({ timeout: 2000 }).catch(() => 'DOM unavailable')));
    throw error;
  }
}
async function api(method, route, data, authorization = token) {
  const response = await fetch(origin + route, { method, headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${authorization}` },
    body: data === undefined ? undefined : JSON.stringify(data), signal: AbortSignal.timeout(15_000), redirect: 'error' });
  const text = await response.text();
  assert.ok(response.ok, `${method} ${route}: ${response.status}`);
  return text;
}
async function sql(value, control = false, authorization = token) {
  const text = await api('POST', control ? '/v1/sql' : `/v1/db/${db}/sql`, { sql: value }, authorization);
  assert.ok(!text.includes('"type":"error"'), scrub(text.slice(0, 400)));
  return text;
}
async function login(username) {
  await page.goto(origin + '/admin/login');
  await page.getByPlaceholder('admin', { exact: true }).fill(username);
  await page.getByPlaceholder('输入密码', { exact: true }).fill(password);
  await page.getByRole('button', { name: '登录后台', exact: true }).click();
  await expect(page).toHaveURL(/\/admin\/app\/dashboard$/u, { timeout: 15_000 });
}
async function logout() { await page.getByRole('button', { name: '退出登录', exact: true }).click(); await expect(page).toHaveURL(/\/admin\/login/u); }
async function workbench(tool = 'sql') {
  await page.goto(`${origin}/admin/app/sql?db=${db}&tool=${tool}`);
  await expect(page.getByTestId('preview-workbench')).toBeVisible();
  await expect(page.getByRole('button', { name: '清空并重新读取', exact: true })).toBeEnabled();
  if (tool !== 'sql') await expect(page.getByTestId(`workbench-${tool}`)).toBeVisible();
}
async function read(query) {
  await page.locator('#preview-sql').fill(query);
  await page.getByRole('button', { name: '执行读取', exact: true }).click();
  await expect(page.locator('.preview-sql pre')).toBeVisible();
  return page.locator('.preview-sql pre').innerText();
}
async function stage(id, value) {
  const panel = page.getByTestId('workbench-table');
  await panel.getByRole('button', { name: 'Insert row', exact: true }).click();
  const fields = panel.locator('.relation-insert__grid .relation-field');
  await fields.filter({ hasText: 'DeviceID' }).locator('input').fill(String(id));
  await fields.filter({ hasText: 'MixedCaseName' }).locator('input,textarea').fill(value);
  await panel.getByRole('button', { name: 'Stage insert', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText(value);
}
async function downloadWindow() {
  const waiting = page.waitForEvent('download', { timeout: 10_000 });
  await page.getByRole('button', { name: '导出当前窗口', exact: true }).click();
  const download = await waiting;
  const target = path.join(root, `${phase}-${count}-window.json`);
  await download.saveAs(target);
  const bytes = fs.readFileSync(target);
  assert.ok(bytes.length > 0 && bytes.length <= 4194304);
  return { bytes: bytes.length, sha256: crypto.createHash('sha256').update(bytes).digest('hex'), text: bytes.toString('utf8') };
}

try {
  for (const browser of ['chrome', 'edge']) {
    const executablePath = browser === 'chrome' ? 'C:/Users/mysti/AppData/Local/Google/Chrome/Application/chrome.exe' : 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
    context = await chromium.launchPersistentContext(path.join(root, `profile-${phase}-${browser}`), {
      executablePath, headless: true, viewport: { width: 1440, height: 1000 }, acceptDownloads: true, serviceWorkers: 'block',
    });
    page = context.pages()[0]; page.setDefaultTimeout(10_000); page.setDefaultNavigationTimeout(15_000);
    page.on('request', request => {
      if (traffic.length >= 3000) throw new Error('Network event budget exceeded.');
      const url = new URL(request.url());
      if (url.origin !== origin && url.protocol !== 'data:' && url.protocol !== 'blob:') throw new Error('Unexpected network egress.');
      traffic.push({ browser, method: request.method(), path: url.pathname });
    });
    if (phase === 'legacy') {
      await step(`${browser}-actual-legacy-server-refused-before-login`, async () => {
        await page.goto(origin + '/admin'); await expect(page.getByRole('heading', { name: 'Preview 版本不匹配' })).toBeVisible();
        assert.ok(!traffic.some(item => item.path.startsWith('/v1/')));
        return { actualOlderServer: 'candidate-09', freshIsolatedData: true, newerWeb: version, authenticationRequests: 0 };
      });
      await context.close(); context = null; continue;
    }
    if ((phase === 'first' || phase === 'micro09') && browser === 'chrome') {
      await step('chrome-first-setup-ui', async () => {
        await page.goto(origin + '/admin');
        await expect(page).toHaveURL(/\/admin\/setup$/u);
        await page.getByPlaceholder('sonnetdb-dev-01').fill('p05-isolated-instance');
        await page.getByPlaceholder('Acme Observability').fill('P05 synthetic acceptance');
        await page.getByPlaceholder('至少一组可记忆的强密码').fill(password);
        await page.getByRole('button', { name: '完成初始化', exact: true }).click();
        await expect(page).toHaveURL(/\/admin\/app\/dashboard$/u, { timeout: 15_000 });
        await logout(); await login('admin');
        token = await page.evaluate(() => JSON.parse(localStorage.getItem('sndb.auth')).token);
      });
      if (phase === 'first') await step('operator-synthetic-fixtures', async () => {
        await api('POST', '/v1/db', { name: db }); await api('POST', '/v1/db', { name: hidden });
        for (const user of ['p05_writer', 'p05_reader']) await sql(`CREATE USER ${user} WITH PASSWORD '${password}'`, true);
        await sql(`GRANT WRITE ON DATABASE ${db} TO p05_writer`, true);
        await sql(`GRANT READ ON DATABASE ${db} TO p05_reader`, true);
        await sql('CREATE TABLE "DeviceID_Main" ("DeviceID" INT, "MixedCaseName" STRING, PRIMARY KEY ("DeviceID"))');
        await sql('INSERT INTO "DeviceID_Main" ("DeviceID", "MixedCaseName") VALUES ' + Array.from({ length: 1001 }, (_, i) => `(${i + 1}, 'P05-window-${i + 1}')`).join(','));
        await sql('CREATE MEASUREMENT Samples (DeviceID TAG, Reading FIELD FLOAT)');
        await sql("INSERT INTO Samples(time, DeviceID, Reading) VALUES (1780000000000, 'p05-sensor', 1.5)");
        return { database: db, tableRows: 1001, measurementRows: 1, userGrantLevels: ['Write', 'Read'], realUiLogin: true };
      }, 'OPERATOR_API_FIXTURE_PREPARATION');
      await logout();
    }
    if (['reopen', 'rollback'].includes(phase) && browser === 'chrome') {
      await login('admin'); token = await page.evaluate(() => JSON.parse(localStorage.getItem('sndb.auth')).token); await logout();
    }
    await step(`${browser}-password-login-and-about`, async () => {
      await login(phase === 'micro09' ? 'admin' : 'p05_writer');
      await expect(page.locator('nav[aria-label="全局模块"] button')).toHaveCount(7);
      await expect(page.locator('nav[aria-label="全局模块"] button:disabled')).toHaveCount(4);
      await page.getByRole('button', { name: '设置 · About', exact: true }).click();
      await expect(page.getByTestId('preview-about')).toContainText(version);
      await expect(page.getByTestId('preview-about')).toContainText(source);
      const dom = await page.getByTestId('preview-about').innerText();
      fs.writeFileSync(path.join(root, `${phase}-${browser}-about.txt`), dom);
      await page.screenshot({ path: path.join(root, `${phase}-${browser}-about.png`) });
      return { browserVersion: context.browser().version(), version, source, profile: 'preview-1', scope: 'R1' };
    });
    if (phase === 'micro09') { await context.close(); context = null; break; }
    if (phase === 'reopen' || phase === 'rollback') {
      await step(`${browser}-${phase}-confirmed-row-readback`, async () => {
        await workbench(); assert.ok((await read('SELECT * FROM "DeviceID_Main" WHERE "DeviceID" = 2001')).includes('P05-approved-chrome'));
        assert.ok(!(await sql('SELECT * FROM "DeviceID_Main" WHERE "DeviceID" = 2010')).includes('P05-after-snapshot'));
        return { normalStopReopen: phase === 'reopen', sameCandidateSnapshotRestore: phase === 'rollback', olderServerUsed: false };
      });
      await logout(); await context.close(); context = null; continue;
    }
    if (!token) throw new Error('Fixture admin missing.');
    const suffix = browser === 'chrome' ? 0 : 10;
    await step(`${browser}-authorized-select-query-truncation-export`, async () => {
      await workbench(); await expect(page.getByLabel('数据库', { exact: true })).not.toContainText(hidden);
      const text = await read('SELECT * FROM "DeviceID_Main"');
      assert.ok(text.includes('P05-window-1')); assert.ok(!text.includes('P05-window-1001'));
      assert.ok(/截断/u.test(await page.locator('body').innerText()));
      const exported = await downloadWindow(); assert.ok(exported.text.includes('P05-window-1')); assert.ok(!exported.text.includes('P05-window-1001'));
      return { outputLimit: 1000, exportBytes: exported.bytes, exportSha256: exported.sha256, unloadedRowExcluded: true };
    });
    await step(`${browser}-relation-and-measurement-reads`, async () => {
      await workbench('table'); await expect(page.getByTestId('workbench-table')).toContainText('P05-window-1');
      await expect(page.getByTestId('workbench-table')).toContainText('DeviceID');
      await page.screenshot({ path: path.join(root, `${phase}-${browser}-relation.png`) });
      await workbench('measurement'); await expect(page.getByTestId('workbench-measurement')).toContainText('p05-sensor');
      return { originalTableAndColumnSpelling: true, measurementRowsVisible: 1 };
    });
    await step(`${browser}-deferred-models-and-direct-routes`, async () => {
      const baseline = traffic.length;
      for (const model of ['document', 'kv', 'fulltext', 'vector', 'bucket', 'mq', 'graph']) {
        await page.goto(`${origin}/admin/app/sql?db=${db}&tool=${model}`);
        await expect(page.getByTestId('preview-model-deferred')).toContainText('服务端资源预算');
      }
      assert.ok(!traffic.slice(baseline).some(item => /\/(?:documents|kv|fulltext|vector|s3|mq|graphs)(?:\/|$)/u.test(item.path)));
      for (const route of ['monitoring', 'govern', 'events']) {
        await page.goto(`${origin}/admin/app/${route}`); await expect(page.getByRole('heading', { name: '本预览未开放' })).toBeVisible();
      }
      await page.goto(`${origin}/admin/app/sql?restore=old-tab`); await expect(page.getByRole('heading', { name: '本预览未开放' })).toBeVisible();
      return { deferredModels: 7, dedicatedRequests: 0, directRoutesRefused: 4 };
    });
    await step(`${browser}-approval-cancel-then-once-confirm`, async () => {
      await workbench('table'); await stage(2002 + suffix, `P05-cancelled-${browser}`);
      await page.getByRole('dialog').getByRole('contentinfo').getByRole('button', { name: '返回编辑', exact: true }).click();
      await page.getByTestId('workbench-table').getByRole('button', { name: 'Discard staged edits', exact: true }).click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
      assert.ok(!(await sql(`SELECT * FROM "DeviceID_Main" WHERE "DeviceID" = ${2002 + suffix}`)).includes(`P05-cancelled-${browser}`));
      const baseline = traffic.length;
      await stage(2001 + suffix, `P05-approved-${browser}`);
      await page.getByRole('button', { name: /确认执行 1 项操作/u }).click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
      assert.ok((await sql(`SELECT * FROM "DeviceID_Main" WHERE "DeviceID" = ${2001 + suffix}`)).includes(`P05-approved-${browser}`));
      return { cancelledRowAbsent: true, confirmedRowsAffected: 1, requestEvents: traffic.length - baseline };
    });
    await step(`${browser}-readonly-database-grant`, async () => {
      await logout(); await login('p05_reader'); await workbench('table');
      await expect(page.getByTestId('workbench-table').getByRole('button', { name: 'Insert row', exact: true })).toBeDisabled();
      const reader = await page.evaluate(() => JSON.parse(localStorage.getItem('sndb.auth')).token);
      const denied = await fetch(`${origin}/v1/db/${db}/sql`, { method: 'POST', headers: { Authorization: `Bearer ${reader}`, 'Content-Type': 'application/json' }, body: JSON.stringify({ sql: 'INSERT INTO "DeviceID_Main" ("DeviceID", "MixedCaseName") VALUES (9999, \'denied\')' }), signal: AbortSignal.timeout(10_000) });
      assert.equal(denied.status, 403); await logout(); await login('p05_writer');
      return { httpRole: 'readwrite', databaseGrant: 'Read', writeStatus: 403 };
    });
    await step(`${browser}-revoked-grant-clears-approval`, async () => {
      await workbench('table'); await stage(2003 + suffix, `P05-revoked-${browser}`);
      await sql(`REVOKE ON DATABASE ${db} FROM p05_writer`, true);
      await page.evaluate(() => window.dispatchEvent(new Event('focus')));
      await expect(page.getByRole('status')).toContainText('权限'); await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(page.locator('body')).not.toContainText(`P05-revoked-${browser}`);
      assert.ok(!(await sql(`SELECT * FROM "DeviceID_Main" WHERE "DeviceID" = ${2003 + suffix}`)).includes(`P05-revoked-${browser}`));
      await sql(`GRANT WRITE ON DATABASE ${db} TO p05_writer`, true);
      await page.getByRole('button', { name: '清空并重新读取', exact: true }).click();
    });
    await step(`${browser}-unknown-write-does-not-replay`, async () => {
      await workbench('table'); let writes = 0;
      await page.route(`**/v1/db/${db}/sql`, async route => {
        if (!/INSERT/u.test(route.request().postData() ?? '')) return route.continue();
        writes++; const response = await route.fetch(); assert.ok((await response.text()).includes('"recordsAffected":1')); await route.abort('connectionfailed');
      });
      try {
        await stage(2004 + suffix, `P05-unknown-${browser}`); await page.getByRole('button', { name: /确认执行 1 项操作/u }).click();
        await expect(page.getByRole('status')).toContainText('写入状态未知'); await expect(page.getByRole('dialog')).toHaveCount(0);
        assert.ok((await sql(`SELECT * FROM "DeviceID_Main" WHERE "DeviceID" = ${2004 + suffix}`)).includes(`P05-unknown-${browser}`));
        await page.getByRole('button', { name: '清空并重新读取', exact: true }).click();
        await expect(page.getByTestId('workbench-table')).toBeVisible(); assert.equal(writes, 1);
      } finally { await page.unrouteAll({ behavior: 'wait' }); }
      return { lostResponseAfterRealCommit: true, writes: 1, rollbackClaim: false };
    }, 'BROWSER_LOSS_INJECTION_AFTER_REAL_SERVER_COMMIT');
    await step(`${browser}-cancel-and-database-isolation`, async () => {
      await workbench(); let release, arrived;
      const held = new Promise(resolve => { release = resolve; }); const received = new Promise(resolve => { arrived = resolve; });
      await page.route(`**/v1/db/${db}/sql`, async route => { const response = await route.fetch(); arrived(); await held; await route.fulfill({ response }).catch(() => {}); }, { times: 1 });
      try {
        await page.locator('#preview-sql').fill('SELECT * FROM "DeviceID_Main" LIMIT 1'); await page.getByRole('button', { name: '执行读取', exact: true }).click();
        await Promise.race([received, new Promise((_, reject) => setTimeout(() => reject(new Error('Held request timeout')), 10_000))]);
        await page.getByRole('button', { name: '取消当前操作', exact: true }).click(); release();
        await expect(page.locator('.preview-sql pre')).toHaveCount(0);
        await page.getByLabel('数据库', { exact: true }).selectOption(''); await expect(page.locator('.preview-sql pre')).toHaveCount(0);
        await page.getByLabel('数据库', { exact: true }).selectOption(db); await expect(page.locator('#preview-sql')).toHaveValue('SHOW TABLES');
      } finally { release(); await page.unrouteAll({ behavior: 'wait' }); }
      return { discardedLateRead: true, serverScanStopProven: false };
    }, 'BROWSER_DELAY_INJECTION_WITH_REAL_RESPONSE');
    await step(`${browser}-offline-and-revoked-token-clearing`, async () => {
      await workbench(); await read('SELECT * FROM "DeviceID_Main" LIMIT 1');
      await context.setOffline(true); await expect(page.getByRole('status')).toContainText('连接已断开');
      await context.setOffline(false); await expect(page.locator('#preview-sql')).toHaveCount(0);
      await page.getByRole('button', { name: '清空并重新读取', exact: true }).click(); await read('SELECT * FROM "DeviceID_Main" LIMIT 1');
      const id = await page.evaluate(() => JSON.parse(localStorage.getItem('sndb.auth')).tokenId);
      await sql(`REVOKE TOKEN '${id}'`, true);
      let downloads = 0; const listener = () => { downloads++; }; page.on('download', listener);
      try { await page.getByRole('button', { name: '导出当前窗口', exact: true }).click(); await expect(page.locator('.preview-sql pre')).toHaveCount(0); await expect(page.getByRole('status')).toContainText('凭据失效'); assert.equal(downloads, 0); }
      finally { page.off('download', listener); }
      await logout(); await login('p05_writer'); return { revokedTokenExportDownloads: 0, noAutomaticReconnectWrite: true };
    });
    await step(`${browser}-actual-server-rejects-stale-preview-headers`, async () => {
      const response = await fetch(origin + '/v1/setup/status', { headers: { 'X-SonnetDB-Workbench-Profile': 'preview-1', 'X-SonnetDB-Workbench-Version': '4.5.0-preview.1.9', 'X-SonnetDB-Workbench-Source': 'a44058adbe640d9ae04b93af388dccff6a17a961' }, signal: AbortSignal.timeout(5000) });
      assert.equal(response.status, 428); assert.ok((await response.text()).includes('preview_identity_mismatch'));
      return { httpStatus: 428, actualServerEnforcement: true };
    });
    await step(`${browser}-cached-web-identity-mismatch-clears-state`, async () => {
      const baseline = traffic.length;
      await page.route('**/workbench-preview/identity', async route => {
        const response = await route.fetch(); const identity = await response.json();
        identity.version = '4.5.0-preview.1.9'; await route.fulfill({ json: identity, headers: { 'Cache-Control': 'no-store' } });
      });
      try {
        await page.goto(origin + '/admin'); await expect(page.getByRole('heading', { name: 'Preview 版本不匹配' })).toBeVisible();
        assert.equal(await page.evaluate(() => localStorage.getItem('sndb.auth')), null);
        assert.ok(!traffic.slice(baseline).some(item => item.path.startsWith('/v1/')));
      } finally { await page.unrouteAll({ behavior: 'wait' }); }
      await login('p05_writer'); return { authenticationRequestsWhileMismatched: 0, authCleared: true, writeReplay: false };
    }, 'BROWSER_IDENTITY_RESPONSE_FAULT_INJECTION');
    await logout(); await context.close(); context = null;
  }
  if (phase === 'reopen') await step('operator-post-snapshot-mutation', async () => {
    await sql('INSERT INTO "DeviceID_Main" ("DeviceID", "MixedCaseName") VALUES (2010, \'P05-after-snapshot\')');
    return { separateMutationAfterSnapshot: true };
  }, 'OPERATOR_API_FIXTURE_MUTATION');
} catch (error) { process.exitCode = 1; process.stderr.write(scrub(error.stack).slice(0, 1800) + '\n'); }
finally {
  if (context) await context.close().catch(() => {});
  clearTimeout(watchdog);
  save(`${phase}-journey.json`, { phase, version, source, status: process.exitCode ? 'FAIL' : 'PASS', evidenceKind: 'INSTALLED_NATIVEAOT_SAME_ORIGIN', records, traffic, fixtureOnly: true, historicalFailuresPreserved: true });
}
