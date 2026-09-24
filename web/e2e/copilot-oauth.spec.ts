import { createHash } from 'node:crypto';
import { expect, test as base, type BrowserContext, type Page, type Request, type Route } from '@playwright/test';

const Studio = 'https://studio.oauth.test';
const Issuer = 'https://idp.oauth.test';
const PublicRuntime = 'https://ai.oauth.test';
const Redirect = `${Studio}/admin/copilot/oauth/callback`;
const DatabaseToken = 'database-secret-oauth-e2e';
const PublicToken = 'public-short-lived-oauth-e2e';
const CallbackMessage = 'sonnetdb:browser-direct-oauth-callback';
const ClientId = 'sonnetdb-browser-e2e';

interface OAuthFixture {
  authorizations: URL[];
  tokenRequests: Request[];
  publicRequests: Request[];
  issuerRequests: Request[];
  callbackResourceRequests: Request[];
  unapprovedRequests: Request[];
  requestHeaders: Map<Request, Record<string, string>>;
  tokenResponse: Record<string, unknown>;
  tokenRedirect: string | null;
  callbackOverride: ((url: URL) => void) | null;
}

// HTTPS origins, authorization endpoint and token responses are controlled
// fixtures, not a deployed identity provider. Only static app resources are
// proxied to the existing local Vite server. No certificates are installed.
const test = base.extend<{ oauth: OAuthFixture }>({
  oauth: async ({ context, page, baseURL }, use) => {
    if (!baseURL) throw new Error('OAuth browser fixture requires the local Vite baseURL.');
    const fixture: OAuthFixture = {
      authorizations: [], tokenRequests: [], publicRequests: [], issuerRequests: [], callbackResourceRequests: [], unapprovedRequests: [],
      requestHeaders: new Map(),
      tokenResponse: { access_token: PublicToken, token_type: 'Bearer', expires_in: 120 },
      tokenRedirect: null,
      callbackOverride: null,
    };
    await installFixture(context, baseURL, fixture);
    await page.goto(`${Studio}/oauth-harness`);
    await expect(page.locator('#ready')).toHaveText('ready', { timeout: 15_000 });
    await use(fixture);
  },
});
test.setTimeout(45_000);

test('real popup uses state and PKCE S256, exchanges the code, and keeps tokens out of storage', async ({ context, page, oauth }) => {
  await context.addCookies([{ name: 'idp-session', value: 'controlled-session', domain: 'idp.oauth.test', path: '/', secure: true, sameSite: 'None' }]);
  const popup = await startLogin(page);
  const authorization = oauth.authorizations[0];
  expect(authorization.origin).toBe(Issuer);
  expect(authorization.searchParams.get('response_type')).toBe('code');
  expect(authorization.searchParams.get('client_id')).toBe(ClientId);
  expect(authorization.searchParams.get('redirect_uri')).toBe(Redirect);
  expect(authorization.searchParams.get('code_challenge_method')).toBe('S256');
  expect(authorization.searchParams.get('state')).toMatch(/^[A-Za-z0-9_-]{43,}$/u);
  expect(authorization.searchParams.get('scope')).toBe('openid copilot');
  expect(authorization.searchParams.has('code_verifier')).toBe(false);
  await completeLogin(popup);
  await expect(page.locator('#result')).toHaveText('success');
  await expect(page.locator('#credential')).toHaveText('connected');
  expectCallbackResourceReferrers(oauth);

  expect(oauth.tokenRequests).toHaveLength(1);
  const tokenRequest = oauth.tokenRequests[0];
  const tokenHeaders = headersFor(oauth, tokenRequest);
  const form = new URLSearchParams(tokenRequest.postData() ?? '');
  expect(tokenRequest.method()).toBe('POST');
  expect(tokenHeaders['content-type']).toContain('application/x-www-form-urlencoded');
  expect(form.get('grant_type')).toBe('authorization_code');
  expect(form.get('client_id')).toBe(ClientId);
  expect(form.get('redirect_uri')).toBe(Redirect);
  expect(form.get('code')).toBe('controlled-authorization-code');
  expect(form.has('client_secret')).toBe(false);
  const verifier = form.get('code_verifier') ?? '';
  expect(verifier).toMatch(/^[A-Za-z0-9_-]{43,128}$/u);
  expect(createHash('sha256').update(verifier).digest('base64url'))
    .toBe(authorization.searchParams.get('code_challenge'));

  await page.getByRole('button', { name: 'Probe public runtime', exact: true }).click();
  await expect(page.locator('#readiness')).toHaveText('ready');
  expect(oauth.publicRequests).toHaveLength(1);
  expect(headersFor(oauth, oauth.publicRequests[0]).authorization).toBe(`Bearer ${PublicToken}`);
  expect(tokenHeaders.authorization).toBeUndefined();
  expect(tokenHeaders.cookie).toBeUndefined();
  const authorizationRequest = oauth.issuerRequests.find((request) => new URL(request.url()).pathname === '/authorize');
  expect(authorizationRequest).toBeDefined();
  expect(headersFor(oauth, authorizationRequest!).cookie).toContain('idp-session=controlled-session');
  expect(oauth.issuerRequests.every((request) => !`${request.url()} ${request.postData() ?? ''}`.includes(DatabaseToken))).toBe(true);
  await expectNoPublicSecretsInStorage(page, [PublicToken, verifier, authorization.searchParams.get('state')!]);

  await page.reload();
  await expect(page.locator('#ready')).toHaveText('ready');
  await expect(page.locator('#credential')).toHaveText('empty');
  await page.getByRole('button', { name: 'Probe public runtime', exact: true }).click();
  await expect(page.locator('#readiness')).toHaveText('unavailable');
  expect(oauth.publicRequests).toHaveLength(1);
});

test('production Copilot button completes the popup callback and disconnect clears the credential', async ({ page, oauth }) => {
  await page.evaluate((token) => localStorage.setItem('sndb.auth', JSON.stringify({
    username: 'oauth-browser-user', token, tokenId: 'oauth-e2e', isSuperuser: false,
  })), DatabaseToken);
  await page.goto(`${Studio}/admin/app/dashboard`);
  await page.locator('.copilot-fab').click();
  await expect(page.getByTestId('copilot-public-auth-status')).toHaveText('未连接');
  const popupPromise = page.waitForEvent('popup', { timeout: 10_000 });
  await page.getByTestId('copilot-public-connect').click();
  const popup = await popupPromise;
  await expect(popup.getByRole('heading', { name: 'Controlled OAuth provider' })).toBeVisible();
  await completeLogin(popup);
  await expect(page.getByTestId('copilot-public-auth-status')).toHaveText('已连接');
  expect(oauth.tokenRequests).toHaveLength(1);
  await test.info().attach('browser-direct-oauth-connected', {
    body: await page.screenshot(), contentType: 'image/png',
  });
  await expectNoPublicSecretsInStorage(page, [PublicToken]);
  await page.getByTestId('copilot-public-disconnect').click();
  await expect(page.getByTestId('copilot-public-auth-status')).toHaveText('未连接');
  await expect(page.getByTestId('copilot-public-connect')).toBeEnabled();
});

test('callback page is anonymous and removes authorization parameters before displaying an orphan callback', async ({ context, oauth }) => {
  const orphan = await context.newPage();
  try {
    await orphan.goto(`${Redirect}?code=orphan-code&state=orphan-state&iss=${encodeURIComponent(Issuer)}`);
    await expect(orphan.getByTestId('copilot-oauth-callback')).toBeVisible();
    await expect(orphan).toHaveURL(Redirect);
    expect(await orphan.locator('body').innerText()).not.toContain('orphan-code');
    expect(await orphan.locator('body').innerText()).not.toContain('orphan-state');
    expect(oauth.tokenRequests).toHaveLength(0);
  } finally {
    await orphan.close();
  }
});

test('same-origin messages from a different popup and wrong-origin messages cannot complete the transaction', async ({ page, oauth }) => {
  const legitimate = await startLogin(page);
  const callbackUrl = callbackUrlFor(oauth.authorizations[0]);
  const roguePromise = page.waitForEvent('popup', { timeout: 10_000 });
  await page.getByRole('button', { name: 'Open unrelated popup', exact: true }).click();
  const rogue = await roguePromise;
  try {
    await expect(rogue.getByRole('heading', { name: 'Unrelated window' })).toBeVisible();
    await postCallback(rogue, callbackUrl); // Correct origin, wrong WindowProxy.
    await postCallback(legitimate, callbackUrl); // Correct WindowProxy, IdP origin.
    await expect(page.locator('#result')).toHaveText('pending');
    expect(oauth.tokenRequests).toHaveLength(0);
    await completeLogin(legitimate);
    await expect(page.locator('#result')).toHaveText('success');
    expect(oauth.tokenRequests).toHaveLength(1);
  } finally {
    await rogue.close();
  }
});

test('wrong state and issuer fail without exchanging a code', async ({ page, oauth }) => {
  const attacks: Array<(callback: URL) => void> = [
    (callback) => callback.searchParams.set('state', 'wrong-state'),
    (callback) => callback.searchParams.set('iss', 'https://other-idp.oauth.test'),
  ];
  // Two fixed cases, each bounded by browser action/assertion timeouts and the
  // 45-second test deadline. Reuse verifies failed transactions are retired.
  for (const attack of attacks) {
    oauth.callbackOverride = attack;
    const popup = await startLogin(page);
    await completeLogin(popup);
    await expect(page.locator('#result')).toHaveText(/^error:/u);
    await expect(page.locator('#credential')).toHaveText('empty');
    expect(oauth.tokenRequests).toHaveLength(0);
  }
});

test('callback view clears and rejects duplicate response parameters without sending them to the opener', async ({ page, oauth }) => {
  for (const parameter of ['code', 'state']) {
    oauth.callbackOverride = (callback) => callback.searchParams.append(parameter, 'duplicate');
    const popup = await startLogin(page);
    await popup.getByRole('link', { name: 'Continue to SonnetDB', exact: true }).click();
    await expect(popup.getByTestId('copilot-oauth-callback')).toBeVisible();
    await expect(popup).toHaveURL(Redirect);
    await expect(popup.getByRole('status')).toContainText('无法完成');
    await expect(page.locator('#result')).toHaveText('pending');
    expect(oauth.tokenRequests).toHaveLength(0);
    await page.getByRole('button', { name: 'Cancel pending login', exact: true }).click();
    await expect(page.locator('#result')).toHaveText(/^error:/u);
    expect(popup.isClosed()).toBe(true);
  }
});

test('a matching popup cannot substitute a different redirect path', async ({ page, oauth }) => {
  const popup = await startLogin(page);
  const callbackUrl = callbackUrlFor(oauth.authorizations[0]);
  const substituted = new URL(callbackUrl);
  substituted.pathname = '/oauth-wrong-callback';
  await popup.goto(`${Studio}/oauth-rogue`);
  await postCallback(popup, substituted.href);
  await expect(page.locator('#result')).toHaveText(/^error:/u);
  await expect(page.locator('#credential')).toHaveText('empty');
  expect(oauth.tokenRequests).toHaveLength(0);
});

test('completed state cannot be replayed into a new login transaction', async ({ page, oauth }) => {
  const firstPopup = await startLogin(page);
  const previous = callbackUrlFor(oauth.authorizations[0]);
  await completeLogin(firstPopup);
  await expect(page.locator('#result')).toHaveText('success');
  await page.getByRole('button', { name: 'Logout public credential', exact: true }).click();
  await expect(page.locator('#credential')).toHaveText('empty');
  oauth.callbackOverride = (callback) => {
    callback.search = new URL(previous).search;
  };
  const secondPopup = await startLogin(page);
  expect(oauth.authorizations[1].searchParams.get('state')).not.toBe(oauth.authorizations[0].searchParams.get('state'));
  await completeLogin(secondPopup);
  await expect(page.locator('#result')).toHaveText(/^error:/u);
  await expect(page.locator('#credential')).toHaveText('empty');
  expect(oauth.tokenRequests).toHaveLength(1);
});

test('the five-minute pending transaction expires and closes its popup', async ({ page, oauth }) => {
  await page.clock.install();
  await page.reload();
  await expect(page.locator('#ready')).toHaveText('ready');
  const popup = await startLogin(page);
  const closed = popup.waitForEvent('close', { timeout: 10_000 });
  await page.clock.fastForward(300_001);
  await expect(page.locator('#result')).toHaveText(/^error:/u);
  await closed;
  await expect(page.locator('#credential')).toHaveText('empty');
  expect(oauth.tokenRequests).toHaveLength(0);
});

test('cancel, database logout and popup close retire pending transactions', async ({ page, oauth }) => {
  const cancelPopup = await startLogin(page);
  await page.getByRole('button', { name: 'Cancel pending login', exact: true }).click();
  await expect(page.locator('#result')).toHaveText(/^error:/u);
  expect(cancelPopup.isClosed()).toBe(true);

  const logoutPopup = await startLogin(page);
  await page.getByRole('button', { name: 'Logout database', exact: true }).click();
  await expect(page.locator('#result')).toHaveText(/^error:/u);
  expect(logoutPopup.isClosed()).toBe(true);

  const closedPopup = await startLogin(page);
  await closedPopup.close();
  await expect(page.locator('#result')).toHaveText(/^error:/u);
  await expect(page.locator('#credential')).toHaveText('empty');
  expect(oauth.tokenRequests).toHaveLength(0);
});

test('OAuth refuses database-token reuse, excessive TTL and redirected token responses', async ({ page, oauth }) => {
  oauth.tokenResponse = { access_token: DatabaseToken, token_type: 'Bearer', expires_in: 120 };
  await completeLogin(await startLogin(page));
  await expect(page.locator('#result')).toHaveText(/^error:/u);
  await expect(page.locator('#credential')).toHaveText('empty');
  oauth.tokenResponse = { access_token: PublicToken, token_type: 'Bearer', expires_in: 7201 };
  await completeLogin(await startLogin(page));
  await expect(page.locator('#result')).toHaveText(/^error:/u);
  await expect(page.locator('#credential')).toHaveText('empty');
  oauth.tokenRedirect = 'https://unapproved.oauth.test/token';
  await completeLogin(await startLogin(page));
  await expect(page.locator('#result')).toHaveText(/^error:/u);
  await expect(page.locator('#credential')).toHaveText('empty');
  expect(oauth.tokenRequests).toHaveLength(3);
  expect(oauth.publicRequests).toHaveLength(0);
  expect(oauth.unapprovedRequests.filter((request) => new URL(request.url()).origin === 'https://unapproved.oauth.test')).toHaveLength(0);
  await expectNoPublicSecretsInStorage(page, [PublicToken, DatabaseToken]);
});

test('expiring public credentials and database logout prevent further public authenticated requests', async ({ page, oauth }) => {
  await page.clock.install();
  await page.reload();
  await expect(page.locator('#ready')).toHaveText('ready');
  oauth.tokenResponse = { access_token: PublicToken, token_type: 'Bearer', expires_in: 2 };
  await completeLogin(await startLogin(page));
  await expect(page.locator('#result')).toHaveText('success');
  await page.getByRole('button', { name: 'Probe public runtime', exact: true }).click();
  await expect(page.locator('#readiness')).toHaveText('ready');
  expect(oauth.publicRequests).toHaveLength(1);
  await page.clock.fastForward(2_001);
  await page.getByRole('button', { name: 'Probe public runtime', exact: true }).click();
  await expect(page.locator('#readiness')).toHaveText('unavailable');
  await expect(page.locator('#credential')).toHaveText('empty');
  expect(oauth.publicRequests).toHaveLength(1);

  oauth.tokenResponse = { access_token: PublicToken, token_type: 'Bearer', expires_in: 120 };
  await completeLogin(await startLogin(page));
  await expect(page.locator('#result')).toHaveText('success');
  await page.getByRole('button', { name: 'Logout database', exact: true }).click();
  await expect(page.locator('#credential')).toHaveText('empty');
  await page.getByRole('button', { name: 'Probe public runtime', exact: true }).click();
  await expect(page.locator('#readiness')).toHaveText('unavailable');
  expect(oauth.publicRequests).toHaveLength(1);
});

async function startLogin(page: Page): Promise<Page> {
  const popupPromise = page.waitForEvent('popup', { timeout: 10_000 });
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const popup = await popupPromise;
  await expect(popup.getByRole('heading', { name: 'Controlled OAuth provider' })).toBeVisible();
  return popup;
}

async function completeLogin(popup: Page): Promise<void> {
  const closed = popup.waitForEvent('close', { timeout: 15_000 });
  await popup.getByRole('link', { name: 'Continue to SonnetDB', exact: true }).click({ noWaitAfter: true });
  await closed;
}

async function postCallback(popup: Page, callbackUrl: string): Promise<void> {
  await popup.evaluate(({ type, callbackUrl, targetOrigin }) => {
    window.opener.postMessage({ type, callbackUrl }, targetOrigin);
  }, { type: CallbackMessage, callbackUrl, targetOrigin: Studio });
}

async function expectNoPublicSecretsInStorage(page: Page, secrets: string[]): Promise<void> {
  const storage = await page.evaluate(() => JSON.stringify({
    local: { ...localStorage }, session: { ...sessionStorage }, cookies: document.cookie,
  }));
  expect(secrets.length).toBeLessThanOrEqual(4);
  for (const secret of secrets) expect(storage).not.toContain(secret);
}

function expectCallbackResourceReferrers(fixture: OAuthFixture): void {
  expect(fixture.callbackResourceRequests.length).toBeLessThan(500);
  expect(fixture.callbackResourceRequests.length).toBeGreaterThan(0);
  for (const request of fixture.callbackResourceRequests) {
    const referrer = headersFor(fixture, request).referer ?? '';
    expect(referrer, `callback resource ${new URL(request.url()).pathname}`).not.toContain('code=');
    expect(referrer).not.toContain('state=');
  }
}

function headersFor(fixture: OAuthFixture, request: Request): Record<string, string> {
  const headers = fixture.requestHeaders.get(request);
  expect(headers, 'complete headers captured while the originating page was alive').toBeDefined();
  return headers!;
}

function callbackUrlFor(authorization: URL): string {
  const callback = new URL(authorization.searchParams.get('redirect_uri')!);
  callback.searchParams.set('code', 'controlled-authorization-code');
  callback.searchParams.set('state', authorization.searchParams.get('state')!);
  callback.searchParams.set('iss', Issuer);
  return callback.href;
}

async function installFixture(context: BrowserContext, baseURL: string, fixture: OAuthFixture): Promise<void> {
  let requests = 0;
  const deadline = Date.now() + 45_000;
  await context.route('**/*', async (route) => {
    if (++requests > 2_000 || Date.now() > deadline) {
      await route.abort('timedout');
      return;
    }
    const request = route.request();
    // Capture complete security headers before fulfilling a request. Callback
    // popups close promptly; querying their Request channel afterwards fails.
    fixture.requestHeaders.set(request, await request.allHeaders());
    const url = new URL(request.url());
    if (url.origin === Issuer) {
      fixture.issuerRequests.push(request);
      if (request.method() === 'OPTIONS') return corsJson(route, {});
      if (url.pathname === '/authorize') {
        fixture.authorizations.push(url);
        const callback = new URL(callbackUrlFor(url));
        fixture.callbackOverride?.(callback);
        return route.fulfill({ contentType: 'text/html', body: `<!doctype html><h1>Controlled OAuth provider</h1>
          <a href="${escapeHtml(callback.href)}">Continue to SonnetDB</a>` });
      }
      if (url.pathname === '/token') {
        fixture.tokenRequests.push(request);
        if (fixture.tokenRedirect) {
          return route.fulfill({ status: 302, headers: { ...corsHeaders(), location: fixture.tokenRedirect } });
        }
        return corsJson(route, fixture.tokenResponse);
      }
      return route.abort('blockedbyclient');
    }
    if (url.origin === PublicRuntime) {
      if (request.method() === 'OPTIONS') return corsJson(route, {});
      fixture.publicRequests.push(request);
      return route.fulfill({ contentType: 'application/json', headers: {
        ...corsHeaders(), 'X-SonnetDB-Copilot-Contract': 'm27-browser-direct-v1',
        'Access-Control-Expose-Headers': 'X-SonnetDB-Copilot-Contract',
      }, body: JSON.stringify({ status: 'ready' }) });
    }
    if (url.origin !== Studio) {
      fixture.unapprovedRequests.push(request);
      return route.abort('blockedbyclient');
    }
    if (request.resourceType() !== 'document' && request.frame().url().startsWith(Redirect)) {
      fixture.callbackResourceRequests.push(request);
    }
    if (url.pathname === '/oauth-harness') return route.fulfill({ contentType: 'text/html', body: harnessHtml() });
    if (url.pathname === '/oauth-rogue') return route.fulfill({ contentType: 'text/html', body: '<!doctype html><h1>Unrelated window</h1>' });
    if (url.pathname === '/metrics') return route.fulfill({ contentType: 'text/plain', body: '' });
    if (url.pathname.startsWith('/v1/') || url.pathname.startsWith('/healthz')) return managementJson(route, url.pathname);
    const upstream = new URL(url.pathname + url.search, baseURL);
    const response = await context.request.get(upstream.href, { timeout: 10_000 });
    await route.fulfill({ response });
  });
}

function corsHeaders(): Record<string, string> {
  return {
    'Access-Control-Allow-Origin': Studio,
    'Access-Control-Allow-Methods': 'GET,POST,OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type,Authorization,X-SonnetDB-Copilot-Contract',
  };
}

async function corsJson(route: Route, payload: unknown): Promise<void> {
  await route.fulfill({ contentType: 'application/json', headers: corsHeaders(), body: JSON.stringify(payload) });
}

async function managementJson(route: Route, path: string): Promise<void> {
  let payload: unknown = {};
  if (path === '/v1/setup/status') payload = { needsSetup: false, suggestedServerId: 'oauth-fixture', serverId: 'oauth-fixture', userCount: 1, databaseCount: 0 };
  else if (path === '/v1/db') payload = { databases: [] };
  else if (path.includes('/conversations')) payload = { conversations: [] };
  else if (path.endsWith('/models')) payload = { models: [], defaultModel: '' };
  else if (path.endsWith('/metrics')) payload = { requestCount: 0, totalTokens: 0, toolCalls: 0 };
  else if (path.startsWith('/healthz')) payload = { status: 'ok', databaseCount: 0, uptimeSeconds: 1 };
  await route.fulfill({ contentType: 'application/json', body: JSON.stringify(payload) });
}

function escapeHtml(value: string): string {
  return value.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
}

function harnessHtml(): string {
  return `<!doctype html><html><head><meta charset="utf-8"><title>Controlled OAuth browser fixture</title></head><body>
    <button id="sign-in">Sign in</button><button id="cancel">Cancel pending login</button>
    <button id="logout">Logout public credential</button><button id="database-logout">Logout database</button>
    <button id="rogue-popup">Open unrelated popup</button><button id="probe">Probe public runtime</button>
    <output id="ready">loading</output><output id="result">idle</output>
    <output id="credential">empty</output><output id="readiness">idle</output>
    <script type="module" src="/e2e/fixtures/copilotOAuthHarness.ts"></script>
  </body></html>`;
}
