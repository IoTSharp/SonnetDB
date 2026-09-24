import { expect, test } from '@playwright/test';
import {
  BrowserDirectOAuthCallbackMessage,
  BrowserDirectOAuthClient,
  completeBrowserDirectOAuthCallback,
  createConfiguredBrowserDirectOAuthClient,
  getConfiguredBrowserDirectOAuthReadiness,
  type BrowserDirectOAuthConfiguration,
  type BrowserDirectOAuthPopup,
  type BrowserDirectOAuthRuntime,
  type BrowserDirectOAuthWindow,
} from '../src/copilot/browserDirectOAuth';
import { clearBrowserDirectAccessToken } from '../src/copilot/browserDirectEntry';

const Configuration: BrowserDirectOAuthConfiguration = {
  issuer: 'https://idp.example.test',
  authorizationEndpoint: 'https://idp.example.test/authorize',
  tokenEndpoint: 'https://idp.example.test/token',
  clientId: 'sonnetdb-public-client',
  redirectUri: 'https://studio.example.test/admin/copilot/oauth/callback',
  approvedOrigins: ['https://idp.example.test'],
  scopes: ['openid', 'copilot'],
};
const clients: BrowserDirectOAuthClient[] = [];
test.afterEach(() => {
  for (const client of clients.splice(0, 4)) client.dispose();
  clearBrowserDirectAccessToken();
});

test('trusted configuration is explicit, HTTPS only, and ready without a network request', () => {
  const h = harness();
  expect(getConfiguredBrowserDirectOAuthReadiness(h.runtime)).toEqual({ status: 'ready' });
  expect(h.requests).toEqual([]);
  expect(h.opened).toEqual([]);
  expect(getConfiguredBrowserDirectOAuthReadiness({ ...h.runtime, configuration: undefined, environment: {} }).status).toBe('not-ready');
  expect(() => createConfiguredBrowserDirectOAuthClient({ ...h.runtime, configuration: undefined, environment: {} }))
    .toThrow(/HTTPS OAuth\/PKCE/u);
});

for (const [name, changes] of [
  ['HTTP issuer', { issuer: 'http://idp.example.test' }],
  ['HTTP authorize', { authorizationEndpoint: 'http://idp.example.test/authorize' }],
  ['HTTP token', { tokenEndpoint: 'http://idp.example.test/token' }],
  ['HTTP redirect', { redirectUri: 'http://studio.example.test/admin/copilot/oauth/callback' }],
  ['foreign redirect origin', { redirectUri: 'https://foreign.example.test/admin/copilot/oauth/callback' }],
  ['unregistered callback path', { redirectUri: 'https://studio.example.test/admin/app/studio' }],
  ['unapproved endpoint', { tokenEndpoint: 'https://foreign.example.test/token' }],
  ['endpoint credentials', { authorizationEndpoint: 'https://secret@idp.example.test/authorize' }],
  ['endpoint prebuilt query', { authorizationEndpoint: 'https://idp.example.test/authorize?client_secret=bad' }],
  ['callback fragment', { redirectUri: `${Configuration.redirectUri}#fragment` }],
  ['non-origin allowlist', { approvedOrigins: ['https://idp.example.test/path'] }],
  ['refresh scope', { scopes: ['offline_access'] }],
] as const) {
  test(`configuration rejects ${name}`, () => {
    const h = harness({ ...Configuration, ...changes });
    expect(getConfiguredBrowserDirectOAuthReadiness(h.runtime).status).toBe('not-ready');
    expect(() => createConfiguredBrowserDirectOAuthClient(h.runtime)).toThrow();
    expect(h.requests).toEqual([]);
  });
}

test('PKCE uses independent random state/verifier, exact endpoints, and memory-only token publication', async () => {
  const h = harness();
  const client = h.create();
  const result = client.signIn('database-secret');
  expect(h.opened).toEqual(['about:blank']);
  const authorization = new URL(await h.navigation);
  expect(authorization.origin + authorization.pathname).toBe(Configuration.authorizationEndpoint);
  expect(authorization.searchParams.get('response_type')).toBe('code');
  expect(authorization.searchParams.get('client_id')).toBe(Configuration.clientId);
  expect(authorization.searchParams.get('redirect_uri')).toBe(Configuration.redirectUri);
  expect(authorization.searchParams.get('code_challenge_method')).toBe('S256');
  expect(authorization.searchParams.get('scope')).toBe('openid copilot');
  expect(authorization.searchParams.get('state')).toMatch(/^[A-Za-z0-9_-]{43}$/u);
  expect(authorization.searchParams.get('code_challenge')).toMatch(/^[A-Za-z0-9_-]{43}$/u);
  expect(authorization.href).not.toContain('database-secret');
  expect(authorization.searchParams.has('code_verifier')).toBe(false);
  h.deliver(callback(authorization));
  expect(await result).toEqual({ expiresAtUtc: new Date(h.now() + 3600_000).toISOString() });
  expect(h.published).toEqual([{ token: 'public-only-token', expiresAtUtc: new Date(h.now() + 3600_000).toISOString() }]);
  expect(h.requests).toHaveLength(1);
  const request = h.requests[0];
  expect(request.url).toBe(Configuration.tokenEndpoint);
  expect(request.init.credentials).toBe('omit');
  expect(request.init.redirect).toBe('error');
  expect(request.init.referrerPolicy).toBe('no-referrer');
  expect(new Headers(request.init.headers).has('Authorization')).toBe(false);
  expect(new Headers(request.init.headers).get('Content-Type')).toBe('application/x-www-form-urlencoded');
  const form = new URLSearchParams(String(request.init.body));
  expect([...form.keys()].sort()).toEqual(['client_id', 'code', 'code_verifier', 'grant_type', 'redirect_uri']);
  expect(form.get('grant_type')).toBe('authorization_code');
  expect(form.get('code')).toBe('one-time-code');
  const verifier = form.get('code_verifier')!;
  expect(verifier).toMatch(/^[A-Za-z0-9_-]{43}$/u);
  expect(verifier).not.toBe(authorization.searchParams.get('state'));
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  expect(Buffer.from(digest).toString('base64url')).toBe(authorization.searchParams.get('code_challenge'));
  expect(String(request.init.body)).not.toContain('database-secret');
  h.deliver(callback(authorization));
  expect(h.requests).toHaveLength(1);
  expect(h.listenerCount()).toBe(0);
  expect(h.timers.size).toBe(0);
  expect(h.popup.closed).toBe(true);
});

test('messages from another window or origin cannot consume the active transaction', async () => {
  const h = harness();
  const result = h.create().signIn('database-secret');
  const authorization = new URL(await h.navigation);
  h.deliver(callback(authorization), {}, 'https://studio.example.test');
  h.deliver(callback(authorization), h.popup, 'https://attacker.example.test');
  expect(h.requests).toHaveLength(0);
  expect(h.listenerCount()).toBe(1);
  h.deliver(callback(authorization));
  await expect(result).resolves.toHaveProperty('expiresAtUtc');
});

for (const invalid of ['state', 'issuer', 'missing-issuer', 'path', 'duplicate-code', 'implicit', 'access-token', 'denied'] as const) {
  test(`callback rejects ${invalid} without contacting the token endpoint`, async () => {
    const h = harness();
    const result = captureFailure(h.create().signIn('database-secret'));
    const authorization = new URL(await h.navigation);
    const response = new URL(callback(authorization));
    if (invalid === 'state') response.searchParams.set('state', 'wrong');
    if (invalid === 'issuer') response.searchParams.set('iss', Configuration.issuer + '/');
    if (invalid === 'missing-issuer') response.searchParams.delete('iss');
    if (invalid === 'path') response.pathname += '/other';
    if (invalid === 'duplicate-code') response.searchParams.append('code', 'second-code');
    if (invalid === 'implicit') response.hash = 'access_token=secret';
    if (invalid === 'access-token') response.searchParams.set('access_token', 'secret');
    if (invalid === 'denied') { response.searchParams.delete('code'); response.searchParams.set('error', 'denied-with-secret'); }
    h.deliver(response.href);
    expect((await result).code).toBe(`browser_direct_oauth_${invalid === 'denied' ? 'authorization_denied' : 'callback_invalid'}`);
    expect((await result).message).not.toContain('secret');
    expect(h.requests).toEqual([]);
    expect(h.published).toEqual([]);
    expect(h.timers.size).toBe(0);
    expect(h.listenerCount()).toBe(0);
  });
}

for (const cancellation of ['cancel', 'abort', 'global-logout', 'dispose', 'deadline', 'popup-closed'] as const) {
  test(`${cancellation} ends the transaction and removes all listeners and timers`, async () => {
    const h = harness();
    const client = h.create();
    const abort = new AbortController();
    const result = captureFailure(client.signIn('database-secret', abort.signal));
    await h.navigation;
    if (cancellation === 'cancel') client.cancel();
    if (cancellation === 'abort') abort.abort();
    if (cancellation === 'global-logout') clearBrowserDirectAccessToken();
    if (cancellation === 'dispose') client.dispose();
    if (cancellation === 'deadline') h.fireTimer(300_000);
    if (cancellation === 'popup-closed') { h.closePopup(); h.fireTimer(500); }
    expect((await result).code).toBe(`browser_direct_oauth_${cancellation === 'deadline' ? 'expired' : 'cancelled'}`);
    expect(h.timers.size).toBe(0);
    expect(h.listenerCount()).toBe(0);
    expect(h.published).toEqual([]);
  });
}

test('blocked popup fails before timers, listeners, or token exchange are created', async () => {
  const h = harness();
  h.browser.open = () => null;
  await expect(h.create().signIn('database-secret')).rejects.toHaveProperty('code', 'browser_direct_oauth_popup_blocked');
  expect(h.timers.size).toBe(0);
  expect(h.listenerCount()).toBe(0);
  expect(h.requests).toEqual([]);
});

for (const [name, token] of [
  ['zero lifetime', { expires_in: 0 }],
  ['excessive lifetime', { expires_in: 7201 }],
  ['infinite lifetime', { expires_in: Infinity }],
  ['string lifetime', { expires_in: '3600' }],
  ['fractional lifetime', { expires_in: 1.5 }],
  ['non bearer type', { token_type: 'MAC' }],
  ['CRLF token', { access_token: 'public\r\nsecret' }],
  ['empty token', { access_token: '' }],
  ['database credential', { access_token: 'database-secret' }],
] as const) {
  test(`token exchange rejects ${name}`, async () => {
    const h = harness(Configuration, async () => Response.json({ access_token: 'public-only-token', token_type: 'Bearer', expires_in: 3600, ...token }));
    const result = captureFailure(h.create().signIn('database-secret'));
    h.deliver(callback(new URL(await h.navigation)));
    expect((await result).code).toBe(`browser_direct_oauth_${name === 'database credential' ? 'token_boundary_violation' : 'token_invalid'}`);
    expect(h.published).toEqual([]);
  });
}

test('response byte limit and redirects fail closed without publishing credentials', async () => {
  const h = harness(Configuration, async () => Response.json({ access_token: 'x'.repeat(70_000) }));
  const result = captureFailure(h.create().signIn('database-secret'));
  h.deliver(callback(new URL(await h.navigation)));
  expect((await result).code).toBe('browser_direct_oauth_token_invalid');
  expect(h.published).toEqual([]);

  const redirected = harness(Configuration, async () => {
    const response = Response.json({ access_token: 'public-only-token', token_type: 'Bearer', expires_in: 3600 });
    Object.defineProperty(response, 'redirected', { value: true });
    return response;
  });
  const redirectedResult = captureFailure(redirected.create().signIn('database-secret'));
  redirected.deliver(callback(new URL(await redirected.navigation)));
  expect((await redirectedResult).code).toBe('browser_direct_oauth_exchange_failed');
  expect(redirected.published).toEqual([]);
});

test('logout during an exchange prevents a late response from restoring credentials', async () => {
  let completeFetch!: (value: Response) => void;
  const h = harness(Configuration, () => new Promise((resolve) => { completeFetch = resolve; }));
  const result = captureFailure(h.create().signIn('database-secret'));
  h.deliver(callback(new URL(await h.navigation)));
  expect(h.requests).toHaveLength(1);
  clearBrowserDirectAccessToken();
  completeFetch(Response.json({ access_token: 'public-only-token', token_type: 'Bearer', expires_in: 3600 }));
  expect((await result).code).toBe('browser_direct_oauth_cancelled');
  await Promise.resolve();
  expect(h.published).toEqual([]);
  expect(h.requests[0].init.signal?.aborted).toBe(true);
  expect(h.timers.size).toBe(0);
});

test('callback bridge erases history first, sends only to the exact opener origin, and closes', () => {
  const calls: unknown[] = [];
  const href = Configuration.redirectUri + '?code=one-time-code&state=state&iss=https%3A%2F%2Fidp.example.test';
  const success = completeBrowserDirectOAuthCallback({
    configuration: Configuration,
    windowImpl: {
      location: { href },
      history: { replaceState: (_state, _unused, url) => { calls.push(['history', url]); } },
      opener: { postMessage: (message, origin) => { calls.push(['message', message, origin]); } },
      close: () => { calls.push(['close']); },
    },
  });
  expect(success).toBe(true);
  expect(calls).toEqual([
    ['history', '/admin/copilot/oauth/callback'],
    ['message', { type: BrowserDirectOAuthCallbackMessage, callbackUrl: href }, 'https://studio.example.test'],
    ['close'],
  ]);
});

test('callback without opener or valid configuration still strips response credentials', () => {
  let replacement: string | URL | null | undefined;
  expect(completeBrowserDirectOAuthCallback({ environment: {}, windowImpl: {
    location: { href: Configuration.redirectUri + '?code=secret#token=secret' },
    history: { replaceState: (_state, _unused, value) => { replacement = value; } },
    opener: null,
    close: () => undefined,
  } })).toBe(false);
  expect(replacement).toBe('/admin/copilot/oauth/callback');
});

function captureFailure(promise: Promise<unknown>): Promise<{ code: string; message: string }> {
  return promise.then(() => { throw new Error('Expected OAuth operation to fail.'); }, (error: unknown) => {
    expect(error).toHaveProperty('code');
    expect(error).toBeInstanceOf(Error);
    return error as { code: string; message: string };
  });
}

function callback(authorization: URL): string {
  const response = new URL(Configuration.redirectUri);
  response.searchParams.set('code', 'one-time-code');
  response.searchParams.set('state', authorization.searchParams.get('state')!);
  response.searchParams.set('iss', Configuration.issuer);
  return response.href;
}

function harness(configuration = Configuration, responseFactory: () => Promise<Response> = async () => Response.json({
  access_token: 'public-only-token', token_type: 'Bearer', expires_in: 3600,
})) {
  const listeners = new Set<(event: MessageEvent) => void>();
  const timers = new Map<number, { handler: () => void; milliseconds: number }>();
  let timerId = 0;
  let closed = false;
  let navigate!: (url: string) => void;
  const navigation = new Promise<string>((resolve) => { navigate = resolve; });
  const opened: string[] = [];
  const requests: Array<{ url: string; init: RequestInit }> = [];
  const published: Array<{ token: string; expiresAtUtc: string }> = [];
  const popup: BrowserDirectOAuthPopup = {
    get closed() { return closed; },
    location: { replace: (url) => { navigate(url); } },
    close: () => { closed = true; },
  };
  const browser: BrowserDirectOAuthWindow = {
    location: { href: 'https://studio.example.test/admin/app/studio' },
    open: (url) => { opened.push(url); return popup; },
    addEventListener: (_type, listener) => { listeners.add(listener); },
    removeEventListener: (_type, listener) => { listeners.delete(listener); },
  };
  const now = () => 1_800_000_000_000;
  const runtime: BrowserDirectOAuthRuntime = {
    configuration,
    windowImpl: browser,
    now,
    fetchImpl: async (input, init) => {
      requests.push({ url: String(input), init: init ?? {} });
      return responseFactory();
    },
    setAccessToken: (token, expiresAtUtc) => { published.push({ token, expiresAtUtc }); },
    setTimeoutImpl: ((handler: () => void, milliseconds = 0) => {
      const id = ++timerId;
      timers.set(id, { handler, milliseconds });
      return id;
    }) as typeof window.setTimeout,
    clearTimeoutImpl: (id) => { timers.delete(id); },
  };
  return {
    runtime, browser, popup, opened, requests, published, navigation, timers, now,
    create: () => { const client = createConfiguredBrowserDirectOAuthClient(runtime); clients.push(client); return client; },
    listenerCount: () => listeners.size,
    closePopup: () => { closed = true; },
    deliver: (callbackUrl: string, source: unknown = popup, origin = 'https://studio.example.test') => {
      const event = { source, origin, data: { type: BrowserDirectOAuthCallbackMessage, callbackUrl } } as MessageEvent;
      for (const listener of [...listeners]) listener(event);
    },
    fireTimer: (milliseconds: number) => {
      const timer = [...timers].find(([, value]) => value.milliseconds === milliseconds);
      expect(timer).toBeTruthy();
      timers.delete(timer![0]);
      timer![1].handler();
    },
  };
}
