import { expect, test, type Route } from '@playwright/test';

const suggestedServerId = 'sndb-factory-pc-a1b2c3d4';

test('setup defaults every field except the administrator password', async ({ page }) => {
  let initializeRequest: Record<string, unknown> | null = null;

  await page.route('**/v1/setup/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/v1/setup/status') {
      return json(route, {
        needsSetup: true,
        suggestedServerId,
        serverId: null,
        organization: null,
        userCount: 0,
        databaseCount: 0,
      });
    }

    if (path === '/v1/setup/initialize') {
      initializeRequest = request.postDataJSON() as Record<string, unknown>;
      return json(route, {
        serverId: suggestedServerId,
        organization: 'Default Organization',
        username: 'admin',
        token: initializeRequest.bearerToken,
        tokenId: 'tok_setup_e2e',
        isSuperuser: true,
      }, 201);
    }

    return json(route, { message: `Unexpected setup request: ${request.method()} ${path}` }, 501);
  });

  await page.goto('/admin/setup');

  await expect(page.getByPlaceholder('sonnetdb-dev-01')).toHaveValue(suggestedServerId);
  await expect(page.getByPlaceholder('Acme Observability')).toHaveValue('Default Organization');
  await expect(page.getByPlaceholder('admin')).toHaveValue('admin');
  await expect(page.getByPlaceholder('至少一组可记忆的强密码')).toHaveValue('');
  await expect(page.getByPlaceholder('tsl_...')).toHaveValue(/^tsl_[0-9a-f]{36}$/u);
  await expect(page.getByRole('button', { name: '完成初始化' })).toBeEnabled();

  await page.getByRole('button', { name: '完成初始化' }).click();
  await expect(page.getByText('请输入管理员密码。')).toBeVisible();

  await page.getByPlaceholder('至少一组可记忆的强密码').fill('SetupSecret123!');
  const initializeResponse = page.waitForResponse((response) =>
    response.url().endsWith('/v1/setup/initialize') && response.status() === 201);
  await page.getByRole('button', { name: '完成初始化' }).click();
  await initializeResponse;

  expect(initializeRequest).toMatchObject({
    serverId: suggestedServerId,
    organization: 'Default Organization',
    username: 'admin',
    password: 'SetupSecret123!',
  });
  expect(initializeRequest?.bearerToken).toMatch(/^tsl_[0-9a-f]{36}$/u);
});

test('setup keeps local defaults and reports an unavailable server', async ({ page }) => {
  await page.route('**/v1/setup/status', (route) => json(route, {
    code: 'server_unavailable',
    message: 'Server unavailable',
  }, 503));

  await page.goto('/admin/setup');

  await expect(page.getByText('无法连接 SonnetDB Server，请确认服务端已启动后重试。')).toBeVisible();
  await expect(page.getByPlaceholder('sonnetdb-dev-01')).toHaveValue('sndb-local');
  await expect(page.getByPlaceholder('Acme Observability')).toHaveValue('Default Organization');
  await expect(page.getByPlaceholder('admin')).toHaveValue('admin');
  await expect(page.getByPlaceholder('至少一组可记忆的强密码')).toHaveValue('');
  await expect(page.getByPlaceholder('tsl_...')).toHaveValue(/^tsl_[0-9a-f]{36}$/u);
  await expect(page.getByRole('button', { name: '完成初始化' })).toBeDisabled();
});

async function json(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}
