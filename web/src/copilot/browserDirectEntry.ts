import type { AxiosInstance } from 'axios';
import {
  BrowserDirectCopilotTransport,
  type BrowserDirectAccessTokenProvider,
} from './browserDirect';
import { CopilotRuntimeContractError, type CopilotEventPayload } from './runtime';

export interface BrowserDirectRuntimeRegistrationOptions {
  publicBaseUrl?: string;
  approvedPublicOrigins?: readonly string[];
  accessTokenProvider?: BrowserDirectAccessTokenProvider;
  fetchImpl?: typeof fetch;
  locationHref?: string;
}

const MaximumBrowserDirectCredentialTtlMilliseconds = 2 * 60 * 60 * 1000;

class InMemoryBrowserDirectCredential implements BrowserDirectAccessTokenProvider {
  private accessToken: string | null = null;
  private expiresAtMilliseconds: number | null = null;

  setAccessToken(value: string, expiresAtUtc: string): void {
    const token = value.trim();
    if (!token) {
      this.clear();
      throw contractError('browser_direct_token_invalid', 'BrowserDirect 公网 access token 不能为空。');
    }

    const expiresAtMilliseconds = Date.parse(expiresAtUtc);
    if (!Number.isFinite(expiresAtMilliseconds)) {
      this.clear();
      throw contractError(
        'browser_direct_token_expiry_invalid',
        'BrowserDirect 公网 access token 必须提供有效的过期时间。',
      );
    }
    const remainingMilliseconds = expiresAtMilliseconds - Date.now();
    if (remainingMilliseconds <= 0) {
      this.clear();
      throw contractError(
        'browser_direct_token_expired',
        'BrowserDirect 公网 access token 已过期。',
      );
    }
    if (remainingMilliseconds > MaximumBrowserDirectCredentialTtlMilliseconds) {
      this.clear();
      throw contractError(
        'browser_direct_token_ttl_invalid',
        'BrowserDirect 公网 access token 的有效期不能超过 2 小时。',
      );
    }

    this.accessToken = token;
    this.expiresAtMilliseconds = expiresAtMilliseconds;
  }

  clear(): void {
    this.accessToken = null;
    this.expiresAtMilliseconds = null;
  }

  async getAccessToken(signal: AbortSignal): Promise<string | null> {
    throwIfAborted(signal);
    if (this.expiresAtMilliseconds !== null && Date.now() >= this.expiresAtMilliseconds) {
      this.clear();
      return null;
    }
    return this.accessToken;
  }
}

const browserDirectCredential = new InMemoryBrowserDirectCredential();

/** Inject a short-lived BrowserDirect public token into process memory only. */
export function setBrowserDirectAccessToken(accessToken: string, expiresAtUtc: string): void {
  browserDirectCredential.setAccessToken(accessToken, expiresAtUtc);
}

/** Clear the in-memory BrowserDirect public token, including on database logout. */
export function clearBrowserDirectAccessToken(): void {
  browserDirectCredential.clear();
}

/**
 * Register BrowserDirect from non-secret build configuration and an in-memory
 * public credential. The database token is used only as a deny-list value so it
 * cannot be reused as the public credential.
 */
export function createConfiguredBrowserDirectTransport<
  TRequest,
  TEvent extends CopilotEventPayload,
>(
  api: AxiosInstance,
  databaseToken: string,
  options: BrowserDirectRuntimeRegistrationOptions = {},
): BrowserDirectCopilotTransport<TRequest, TEvent> {
  const environment = import.meta.env ?? {};
  const publicBaseUrl = options.publicBaseUrl?.trim()
    || environment.VITE_COPILOT_BROWSER_DIRECT_PUBLIC_BASE_URL?.trim()
    || '';
  const approvedPublicOrigins = options.approvedPublicOrigins
    ?? parseApprovedOrigins(environment.VITE_COPILOT_BROWSER_DIRECT_APPROVED_ORIGINS);
  if (!publicBaseUrl || approvedPublicOrigins.length === 0) {
    throw contractError(
      'browser_direct_configuration_missing',
      'BrowserDirect 缺少公网地址或 approved origins，已拒绝启动。',
    );
  }

  const publicCredential = options.accessTokenProvider ?? browserDirectCredential;
  const databaseCredential = databaseToken.trim();
  const isolatedCredential: BrowserDirectAccessTokenProvider = {
    async getAccessToken(signal) {
      const accessToken = (await publicCredential.getAccessToken(signal))?.trim() ?? '';
      if (accessToken && databaseCredential && accessToken === databaseCredential) {
        throw contractError(
          'browser_direct_token_boundary_violation',
          'BrowserDirect 公网凭据不能复用 SonnetDB 数据库 token。',
        );
      }
      return accessToken || null;
    },
  };

  return new BrowserDirectCopilotTransport<TRequest, TEvent>(api, {
    publicBaseUrl,
    approvedPublicOrigins,
    accessTokenProvider: isolatedCredential,
    ...(options.fetchImpl ? { fetchImpl: options.fetchImpl } : {}),
    ...(options.locationHref ? { locationHref: options.locationHref } : {}),
  });
}

function parseApprovedOrigins(value: string | undefined): string[] {
  return (value ?? '')
    .split(',')
    .map((origin) => origin.trim())
    .filter(Boolean);
}

function throwIfAborted(signal: AbortSignal): void {
  if (!signal.aborted) return;
  if (signal.reason !== undefined) throw signal.reason;
  throw new DOMException('The operation was aborted.', 'AbortError');
}

function contractError(code: string, message: string): CopilotRuntimeContractError {
  return new CopilotRuntimeContractError(code, message);
}
