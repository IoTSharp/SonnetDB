import {
  clearBrowserDirectAccessToken,
  registerBrowserDirectCredentialClearHandler,
  setBrowserDirectAccessToken,
} from './browserDirectEntry';
import { CopilotRuntimeContractError } from './runtime';

export const BrowserDirectOAuthCallbackMessage = 'sonnetdb:browser-direct-oauth-callback';
export const BrowserDirectOAuthCallbackPath = '/admin/copilot/oauth/callback';
const TransactionLifetimeMilliseconds = 5 * 60 * 1000;
const MaximumTokenLifetimeSeconds = 2 * 60 * 60;
const MaximumTokenResponseBytes = 64 * 1024;

/** 显式批准的 OAuth 公共客户端配置；不支持 client secret 或 implicit grant。 */
export interface BrowserDirectOAuthConfiguration {
  issuer: string;
  authorizationEndpoint: string;
  tokenEndpoint: string;
  clientId: string;
  redirectUri: string;
  approvedOrigins: readonly string[];
  scopes?: readonly string[];
}

/** 授权窗口的最小接口，生产使用浏览器 WindowProxy。 */
export interface BrowserDirectOAuthPopup {
  readonly closed: boolean;
  readonly location: { replace(url: string): void };
  close(): void;
}

/** 浏览器依赖边界；不提供任何持久化凭据接口。 */
export interface BrowserDirectOAuthWindow {
  readonly location: { readonly href: string };
  open(url: string, target: string, features: string): BrowserDirectOAuthPopup | null;
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
}

/** 可注入的测试依赖；所有配置仍经过同一 HTTPS 与 origin 校验。 */
export interface BrowserDirectOAuthRuntime {
  configuration?: BrowserDirectOAuthConfiguration;
  environment?: Record<string, string | undefined>;
  windowImpl?: BrowserDirectOAuthWindow;
  fetchImpl?: typeof fetch;
  cryptoImpl?: Pick<Crypto, 'getRandomValues' | 'subtle'>;
  now?: () => number;
  setTimeoutImpl?: typeof window.setTimeout;
  clearTimeoutImpl?: typeof window.clearTimeout;
  setAccessToken?: typeof setBrowserDirectAccessToken;
}

export interface BrowserDirectOAuthSignInResult {
  expiresAtUtc: string;
}

export interface BrowserDirectOAuthReadiness {
  status: 'ready' | 'not-ready';
  reason?: string;
}

interface OAuthTransaction {
  readonly controller: AbortController;
  readonly popup: BrowserDirectOAuthPopup;
  readonly expiresAt: number;
  state: string;
  verifier: string;
  exchanging: boolean;
  exchangeTimer: number;
  finish(error?: unknown, result?: BrowserDirectOAuthSignInResult): void;
}

/** 读取构建配置并验证 readiness，不打开窗口或发出认证请求。 */
export function getConfiguredBrowserDirectOAuthReadiness(
  runtime: BrowserDirectOAuthRuntime = {},
): BrowserDirectOAuthReadiness {
  try {
    const browser = requireWindow(runtime);
    validateConfiguration(readConfiguration(runtime), browser.location.href);
    requireCrypto(runtime);
    return { status: 'ready' };
  } catch {
    return { status: 'not-ready', reason: '公网登录尚未配置可信的 HTTPS OAuth/PKCE 入口。' };
  }
}

/** 创建仅使用显式批准端点的 OAuth 客户端；缺少配置时 fail closed。 */
export function createConfiguredBrowserDirectOAuthClient(
  runtime: BrowserDirectOAuthRuntime = {},
): BrowserDirectOAuthClient {
  return new BrowserDirectOAuthClient(readConfiguration(runtime), runtime);
}

/** 在内存中完成一次性 Authorization Code + PKCE，数据库 token 只用于隔离校验。 */
export class BrowserDirectOAuthClient {
  private readonly configuration: BrowserDirectOAuthConfiguration;
  private readonly browser: BrowserDirectOAuthWindow;
  private readonly crypto: Pick<Crypto, 'getRandomValues' | 'subtle'>;
  private readonly fetchImpl: typeof fetch;
  private readonly now: () => number;
  private readonly setTimer: typeof window.setTimeout;
  private readonly clearTimer: typeof window.clearTimeout;
  private readonly publishToken: typeof setBrowserDirectAccessToken;
  private readonly unregisterClear: () => void;
  private transaction: OAuthTransaction | null = null;
  private disposed = false;

  constructor(configuration: BrowserDirectOAuthConfiguration, runtime: BrowserDirectOAuthRuntime = {}) {
    this.browser = requireWindow(runtime);
    this.configuration = validateConfiguration(configuration, this.browser.location.href);
    this.crypto = requireCrypto(runtime);
    this.fetchImpl = runtime.fetchImpl ?? globalThis.fetch.bind(globalThis);
    this.now = runtime.now ?? Date.now;
    this.setTimer = runtime.setTimeoutImpl ?? globalThis.setTimeout.bind(globalThis);
    this.clearTimer = runtime.clearTimeoutImpl ?? globalThis.clearTimeout.bind(globalThis);
    this.publishToken = runtime.setAccessToken ?? setBrowserDirectAccessToken;
    this.unregisterClear = registerBrowserDirectCredentialClearHandler(() => this.cancel());
  }

  /** 从用户点击同步打开 popup，再生成 S256 挑战；仅返回非敏感到期时间。 */
  signIn(databaseToken: string, signal?: AbortSignal): Promise<BrowserDirectOAuthSignInResult> {
    if (this.disposed) return Promise.reject(failure('disposed', '公网登录客户端已关闭。'));
    clearBrowserDirectAccessToken();
    if (signal?.aborted) return Promise.reject(failure('cancelled', '公网登录已取消。'));

    let popup: BrowserDirectOAuthPopup | null;
    let state: string;
    let verifier: string;
    try {
      state = randomSecret(this.crypto);
      verifier = randomSecret(this.crypto);
      // 保留 opener 供精确 source 校验；命名为 _blank，避免复用不属于本交易的窗口。
      popup = this.browser.open('about:blank', '_blank', 'popup=yes,width=520,height=720');
    } catch {
      return Promise.reject(failure('popup_unavailable', '无法打开公网登录窗口。'));
    }
    if (!popup) return Promise.reject(failure('popup_blocked', '请允许打开公网登录窗口。'));

    return new Promise<BrowserDirectOAuthSignInResult>((resolve, reject) => {
      let finished = false;
      let expiryTimer = 0;
      let closeTimer = 0;
      let checks = 0;
      const controller = new AbortController();
      const transaction: OAuthTransaction = {
        popup,
        controller,
        state,
        verifier,
        expiresAt: this.now() + TransactionLifetimeMilliseconds,
        exchanging: false,
        exchangeTimer: 0,
        finish: (error, result) => {
          if (finished) return;
          finished = true;
          this.clearTimer(expiryTimer);
          this.clearTimer(closeTimer);
          this.clearTimer(transaction.exchangeTimer);
          this.browser.removeEventListener('message', onMessage);
          signal?.removeEventListener('abort', onAbort);
          transaction.state = '';
          transaction.verifier = '';
          if (this.transaction === transaction) this.transaction = null;
          controller.abort();
          try { popup.close(); } catch { /* 已关闭或隔离的窗口不影响内存清理。 */ }
          if (error !== undefined) reject(error);
          else resolve(result!);
        },
      };
      const onAbort = () => transaction.finish(failure('cancelled', '公网登录已取消。'));
      const onMessage = (event: MessageEvent) => {
        if (finished || transaction.exchanging
          || event.source !== popup || event.origin !== new URL(this.configuration.redirectUri).origin) return;
        if (!isRecord(event.data) || event.data.type !== BrowserDirectOAuthCallbackMessage) return;
        // source + origin 正确后，非法响应也必须消费交易，不能留作第二次尝试。
        transaction.exchanging = true;
        this.browser.removeEventListener('message', onMessage);
        this.clearTimer(closeTimer);
        void this.acceptCallback(transaction, event.data.callbackUrl, databaseToken.trim())
          .then((result) => transaction.finish(undefined, result), (error: unknown) => transaction.finish(error));
      };
      this.transaction = transaction;
      this.browser.addEventListener('message', onMessage);
      signal?.addEventListener('abort', onAbort, { once: true });
      expiryTimer = this.setTimer(() => transaction.finish(failure('expired', '公网登录已超时，请重新连接。')),
        TransactionLifetimeMilliseconds);
      // 最多 600 次、每次相隔 500ms；同时受绝对 5 分钟期限与取消控制。
      const checkClosed = () => {
        if (finished || transaction.exchanging) return;
        if (popup.closed) { onAbort(); return; }
        if (++checks >= 600 || this.now() >= transaction.expiresAt) {
          transaction.finish(failure('expired', '公网登录已超时，请重新连接。'));
          return;
        }
        closeTimer = this.setTimer(checkClosed, 500);
      };
      closeTimer = this.setTimer(checkClosed, 500);
      if (signal?.aborted) { onAbort(); return; }
      void this.crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier)).then((digest) => {
        if (finished) return;
        if (this.now() >= transaction.expiresAt) {
          transaction.finish(failure('expired', '公网登录已超时，请重新连接。'));
          return;
        }
        const authorization = new URL(this.configuration.authorizationEndpoint);
        authorization.searchParams.set('response_type', 'code');
        authorization.searchParams.set('client_id', this.configuration.clientId);
        authorization.searchParams.set('redirect_uri', this.configuration.redirectUri);
        authorization.searchParams.set('state', transaction.state);
        authorization.searchParams.set('code_challenge', base64Url(new Uint8Array(digest)));
        authorization.searchParams.set('code_challenge_method', 'S256');
        if (this.configuration.scopes?.length) authorization.searchParams.set('scope', this.configuration.scopes.join(' '));
        popup.location.replace(authorization.href);
      }).catch(() => transaction.finish(failure('start_failed', '无法开始安全的公网登录。')));
    });
  }

  /** 取消在途交易；已完成的凭据由 logout 明确清理。 */
  cancel(): void {
    this.transaction?.finish(failure('cancelled', '公网登录已取消。'));
  }

  /** 注销在途交易和现有内存凭据。 */
  logout(): void {
    this.cancel();
    clearBrowserDirectAccessToken();
  }

  /** 释放交易、凭据和数据库登出订阅。 */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.logout();
    this.unregisterClear();
  }

  private async acceptCallback(
    transaction: OAuthTransaction,
    callbackUrl: unknown,
    databaseToken: string,
  ): Promise<BrowserDirectOAuthSignInResult> {
    this.requireActive(transaction);
    const responseUrl = validateCallbackUrl(callbackUrl, this.configuration.redirectUri);
    const state = singleParameter(responseUrl, 'state');
    const issuer = singleParameter(responseUrl, 'iss');
    if (state !== transaction.state || issuer !== this.configuration.issuer) {
      throw failure('callback_invalid', '公网登录响应与当前交易或受信发行方不匹配。');
    }
    if (responseUrl.searchParams.has('error')) {
      if (responseUrl.searchParams.has('code')) throw failure('callback_invalid', '公网登录响应格式无效。');
      singleParameter(responseUrl, 'error');
      throw failure('authorization_denied', '授权服务未完成公网登录。');
    }
    const code = singleParameter(responseUrl, 'code');
    const form = new URLSearchParams({
      grant_type: 'authorization_code',
      client_id: this.configuration.clientId,
      redirect_uri: this.configuration.redirectUri,
      code,
      code_verifier: transaction.verifier,
    });
    transaction.verifier = '';
    transaction.state = '';
    const requestStartedAt = this.now();
    let response: Response;
    transaction.exchangeTimer = this.setTimer(() => transaction.finish(failure('exchange_timeout', '公网凭据交换超时。')), 30_000);
    try {
      response = await this.fetchImpl(this.configuration.tokenEndpoint, {
        method: 'POST',
        headers: { Accept: 'application/json', 'Content-Type': 'application/x-www-form-urlencoded' },
        body: form,
        credentials: 'omit',
        redirect: 'error',
        referrerPolicy: 'no-referrer',
        cache: 'no-store',
        signal: transaction.controller.signal,
      });
      this.requireActive(transaction);
      if (!response.ok || response.redirected
        || !/^application\/(?:json|[a-z0-9.+-]+\+json)(?:\s*;|$)/iu.test(response.headers.get('content-type') ?? '')) {
        throw failure('exchange_failed', '授权服务未返回有效的公网凭据。');
      }
      const payload = await readTokenResponse(response, transaction.controller.signal);
      this.requireActive(transaction);
      if (!isRecord(payload) || typeof payload.access_token !== 'string'
        || !/^[\x21-\x7e]{1,8192}$/u.test(payload.access_token)
        || typeof payload.token_type !== 'string' || payload.token_type.toLowerCase() !== 'bearer'
        || typeof payload.expires_in !== 'number' || !Number.isSafeInteger(payload.expires_in)
        || payload.expires_in <= 0 || payload.expires_in > MaximumTokenLifetimeSeconds) {
        throw failure('token_invalid', '公网凭据必须是两小时以内有效的 Bearer access token。');
      }
      if (databaseToken && payload.access_token === databaseToken) {
        throw failure('token_boundary_violation', '公网凭据不能复用 SonnetDB 数据库 token。');
      }
      const expiresAt = requestStartedAt + payload.expires_in * 1000;
      if (expiresAt <= this.now()) throw failure('token_expired', '返回的公网凭据已过期。');
      const expiresAtUtc = new Date(expiresAt).toISOString();
      this.publishToken(payload.access_token, expiresAtUtc);
      return { expiresAtUtc };
    } catch (error) {
      if (error instanceof CopilotRuntimeContractError) throw error;
      throw failure('exchange_failed', '无法完成公网凭据交换。');
    } finally {
      this.clearTimer(transaction.exchangeTimer);
      transaction.exchangeTimer = 0;
    }
  }

  private requireActive(transaction: OAuthTransaction): void {
    if (this.transaction !== transaction || transaction.controller.signal.aborted) {
      throw failure('cancelled', '公网登录已取消。');
    }
    if (this.now() >= transaction.expiresAt) throw failure('expired', '公网登录已超时，请重新连接。');
  }
}

export interface BrowserDirectOAuthCallbackRuntime {
  configuration?: BrowserDirectOAuthConfiguration;
  environment?: Record<string, string | undefined>;
  windowImpl?: {
    readonly location: { readonly href: string };
    readonly history: { replaceState(data: unknown, unused: string, url?: string | URL | null): void };
    readonly opener: { postMessage(message: unknown, targetOrigin: string): void } | null;
    close(): void;
  };
}

/** callback 页面只转交一次响应；先从当前历史条目移除 code/state，再精确发给同源 opener。 */
export function completeBrowserDirectOAuthCallback(runtime: BrowserDirectOAuthCallbackRuntime = {}): boolean {
  const browser = runtime.windowImpl ?? (typeof window === 'undefined' ? null : window);
  if (!browser) return false;
  const callbackUrl = browser.location.href;
  // 即使配置无效或没有 opener，也不能把授权响应继续留在当前地址栏。
  try {
    const current = new URL(callbackUrl);
    browser.history.replaceState(null, '', current.pathname);
    const configuration = validateConfiguration(readConfiguration(runtime), callbackUrl);
    validateCallbackUrl(callbackUrl, configuration.redirectUri);
    if (!browser.opener) return false;
    browser.opener.postMessage({ type: BrowserDirectOAuthCallbackMessage, callbackUrl }, new URL(configuration.redirectUri).origin);
    browser.close();
    return true;
  } catch {
    return false;
  }
}

function readConfiguration(runtime: Pick<BrowserDirectOAuthRuntime, 'configuration' | 'environment'>): BrowserDirectOAuthConfiguration {
  if (runtime.configuration) return runtime.configuration;
  const environment: Record<string, string | undefined> = runtime.environment ?? import.meta.env ?? {};
  const read = (key: string): string => environment[`VITE_COPILOT_OAUTH_${key}`]?.trim() ?? '';
  return {
    issuer: read('ISSUER'),
    authorizationEndpoint: read('AUTHORIZATION_ENDPOINT'),
    tokenEndpoint: read('TOKEN_ENDPOINT'),
    clientId: read('CLIENT_ID'),
    redirectUri: read('REDIRECT_URI'),
    approvedOrigins: read('APPROVED_ORIGINS').split(',').map((origin) => origin.trim()).filter(Boolean),
    scopes: read('SCOPES').split(/\s+/u).filter(Boolean),
  };
}

function validateConfiguration(configuration: BrowserDirectOAuthConfiguration, locationHref: string): BrowserDirectOAuthConfiguration {
  const clientId = configuration.clientId?.trim();
  if (!clientId || clientId.length > 256 || /[\x00-\x20\x7f]/u.test(clientId)
    || !configuration.approvedOrigins?.length || configuration.approvedOrigins.length > 16) throw notReady();
  const approvedOrigins = configuration.approvedOrigins.map((origin) => {
    const url = requireHttpsUrl(origin);
    if (url.origin !== origin) throw notReady();
    return url.origin;
  });
  const issuer = requireHttpsUrl(configuration.issuer);
  const authorization = requireHttpsUrl(configuration.authorizationEndpoint);
  const token = requireHttpsUrl(configuration.tokenEndpoint);
  const redirect = requireHttpsUrl(configuration.redirectUri);
  const current = new URL(locationHref);
  if (current.protocol !== 'https:' || current.origin !== redirect.origin
    || redirect.pathname !== BrowserDirectOAuthCallbackPath
    || !approvedOrigins.includes(issuer.origin) || !approvedOrigins.includes(authorization.origin)
    || !approvedOrigins.includes(token.origin)) throw notReady();
  const scopes = [...(configuration.scopes ?? [])];
  if (scopes.length > 32 || scopes.some((scope) => !/^[\x21\x23-\x5b\x5d-\x7e]{1,128}$/u.test(scope)
    || scope === 'offline_access')) throw notReady();
  return {
    ...configuration,
    clientId,
    authorizationEndpoint: authorization.href,
    tokenEndpoint: token.href,
    redirectUri: redirect.href,
    approvedOrigins,
    scopes,
  };
}

function requireHttpsUrl(value: string): URL {
  let url: URL;
  try { url = new URL(value); } catch { throw notReady(); }
  if (value.length > 2048 || value !== value.trim() || url.protocol !== 'https:'
    || url.username || url.password || url.search || url.hash) throw notReady();
  return url;
}

function validateCallbackUrl(value: unknown, redirectUri: string): URL {
  if (typeof value !== 'string' || value.length > 16_384) throw failure('callback_invalid', '公网登录响应格式无效。');
  let response: URL;
  try { response = new URL(value); } catch { throw failure('callback_invalid', '公网登录响应地址无效。'); }
  const expected = new URL(redirectUri);
  if (response.origin !== expected.origin || response.pathname !== expected.pathname
    || response.username || response.password || response.hash) throw failure('callback_invalid', '公网登录返回地址不匹配。');
  const allowed = new Set(['code', 'state', 'iss', 'error', 'error_description', 'error_uri', 'scope', 'session_state']);
  if ([...response.searchParams.keys()].some((key) => !allowed.has(key) || response.searchParams.getAll(key).length !== 1)) {
    throw failure('callback_invalid', '公网登录响应包含无效或重复参数。');
  }
  return response;
}

function singleParameter(url: URL, name: string): string {
  const values = url.searchParams.getAll(name);
  if (values.length !== 1 || !values[0] || values[0].length > 4096 || /[\x00-\x20\x7f]/u.test(values[0])) {
    throw failure('callback_invalid', '公网登录响应缺少有效的一次性参数。');
  }
  return values[0];
}

async function readTokenResponse(response: Response, signal: AbortSignal): Promise<unknown> {
  if (!response.body) throw failure('token_invalid', '公网凭据响应为空。');
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let bytes = 0;
  const onAbort = () => { void reader.cancel().catch(() => undefined); };
  signal.addEventListener('abort', onAbort, { once: true });
  try {
    // 64KiB/1024块双上限，外层30秒exchange timer及AbortSignal限制墙钟等待。
    for (let index = 0; index < 1024; index++) {
      if (signal.aborted) throw failure('cancelled', '公网登录已取消。');
      const chunk = await reader.read();
      if (chunk.done) {
        const value = new Uint8Array(bytes);
        let offset = 0;
        for (const part of chunks) { value.set(part, offset); offset += part.length; }
        try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(value)) as unknown; }
        catch { throw failure('token_invalid', '公网凭据响应不是有效 JSON。'); }
      }
      bytes += chunk.value.byteLength;
      if (bytes > MaximumTokenResponseBytes) throw failure('token_invalid', '公网凭据响应超过大小上限。');
      chunks.push(chunk.value);
    }
    throw failure('token_invalid', '公网凭据响应分片过多。');
  } finally {
    signal.removeEventListener('abort', onAbort);
    void reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}

function randomSecret(crypto: Pick<Crypto, 'getRandomValues'>): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(32)));
}

function base64Url(value: Uint8Array): string {
  return btoa(String.fromCharCode(...value)).replace(/\+/gu, '-').replace(/\//gu, '_').replace(/=+$/u, '');
}

function requireWindow(runtime: BrowserDirectOAuthRuntime): BrowserDirectOAuthWindow {
  const browser = runtime.windowImpl ?? (typeof window === 'undefined' ? null : window);
  if (!browser) throw notReady();
  return browser;
}

function requireCrypto(runtime: BrowserDirectOAuthRuntime): Pick<Crypto, 'getRandomValues' | 'subtle'> {
  const crypto = runtime.cryptoImpl ?? globalThis.crypto;
  if (!crypto?.subtle || !crypto.getRandomValues) throw notReady();
  return crypto;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function failure(code: string, message: string): CopilotRuntimeContractError {
  return new CopilotRuntimeContractError(`browser_direct_oauth_${code}`, message);
}

function notReady(): CopilotRuntimeContractError {
  return failure('not_ready', '公网登录缺少有效的受信 HTTPS OAuth/PKCE 配置。');
}
