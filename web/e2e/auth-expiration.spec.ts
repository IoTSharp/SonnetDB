import { expect, test, type Page, type Route } from '@playwright/test';

const targetPath = '/admin/app/settings?source=expiration';

async function json(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installSession(page: Page, eventSource: 'idle' | 'unauthorized'): Promise<void> {
  await page.addInitScript((mode) => {
    localStorage.setItem('sndb.auth', JSON.stringify({
      username: 'admin',
      token: 'expired-token',
      tokenId: 'expired-token-id',
      isSuperuser: true,
    }));
    class EventSourceFixture extends EventTarget {
      static readonly CLOSED = 2;
      readonly CONNECTING = 0;
      readonly OPEN = 1;
      readonly CLOSED = 2;
      readyState = 0;
      onerror: (() => void) | null = null;
      onopen: (() => void) | null = null;
      constructor(_url: string | URL) {
        super();
        if (mode === 'unauthorized') {
          queueMicrotask(() => {
            this.readyState = EventSourceFixture.CLOSED;
            this.onerror?.();
          });
        }
      }
      close(): void { this.readyState = EventSourceFixture.CLOSED; }
    }
    Object.defineProperty(window, 'EventSource', { configurable: true, value: EventSourceFixture });
  }, eventSource);
}

test.beforeEach(async ({ page }) => {
  await page.route('**/v1/setup/status', (route) => json(route, {
    needsSetup: false,
    suggestedServerId: 'server',
    serverId: 'server',
    organization: 'organization',
    userCount: 1,
    databaseCount: 1,
  }));
});

test('API 401 clears the session and returns through the login redirect', async ({ page }) => {
  await installSession(page, 'idle');
  await page.route('**/v1/db', (route) => json(route, { message: 'Token revoked' }, 401));

  await page.goto(targetPath);

  await expect(page).toHaveURL(/\/admin\/login\?redirect=/u);
  expect(new URL(page.url()).searchParams.get('redirect')).toBe(targetPath);
  expect(await page.evaluate(() => localStorage.getItem('sndb.auth'))).toBeNull();
  await expect(page.getByRole('heading', { name: '管理登录' })).toBeVisible();
});

test('API 403 keeps the session and shows the permission error', async ({ page }) => {
  await installSession(page, 'idle');
  await page.route('**/v1/db', (route) => json(route, { message: 'Forbidden' }, 403));
  await page.route('**/v1/system/configuration', (route) => json(route, { message: 'Forbidden' }, 403));

  await page.goto(targetPath);

  await expect(page.getByText('当前账号没有读取这些配置的权限。').first()).toBeVisible();
  await expect(page).toHaveURL(new RegExp(`${targetPath.replace('?', '\\?')}$`, 'u'));
  expect(await page.evaluate(() => localStorage.getItem('sndb.auth'))).not.toBeNull();
});

test('SSE authorization failure expires the session without an API 401', async ({ page }) => {
  await installSession(page, 'unauthorized');

  await page.goto(targetPath);

  await expect(page).toHaveURL(/\/admin\/login\?redirect=/u);
  expect(new URL(page.url()).searchParams.get('redirect')).toBe(targetPath);
  expect(await page.evaluate(() => localStorage.getItem('sndb.auth'))).toBeNull();
});
