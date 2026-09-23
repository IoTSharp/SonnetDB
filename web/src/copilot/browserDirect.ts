import type { AxiosInstance } from 'axios';
import {
  CopilotRuntimeContractError,
  type CopilotEventPayload,
  type CopilotRuntimeReadiness,
  type CopilotTransport,
  type CopilotTransportEvent,
} from './runtime';
import type { BrowserDirectLocalToolLoop } from './browserDirectMcp';
import { BrowserDirectContractVersion, streamPublicCopilotProtocol, type PublicCopilotRequest, type BrowserDirectContinuation } from './publicRuntimeProtocol';

export { BrowserDirectContractVersion } from './publicRuntimeProtocol';

export interface BrowserDirectAccessTokenProvider {
  /** Return a short-lived public-client token from memory. */
  getAccessToken(signal: AbortSignal): Promise<string | null>;
}

export interface BrowserDirectCopilotTransportOptions<TRequest = unknown> {
  publicBaseUrl: string;
  approvedPublicOrigins: readonly string[];
  accessTokenProvider: BrowserDirectAccessTokenProvider;
  fetchImpl?: typeof fetch;
  locationHref?: string;
  localToolLoop?: BrowserDirectLocalToolLoop<TRequest>;
  maximumToolCalls?: number;
}

/**
 * Browser-to-public-runtime network transport for M27 #340.
 *
 * The database credential is deliberately absent from this API. Local MCP
 * execution remains a separate, same-origin boundary and is not proxied here.
 */
export class BrowserDirectCopilotTransport<TRequest, TEvent extends CopilotEventPayload>
implements CopilotTransport<TRequest, TEvent> {
  readonly mode = 'BrowserDirect' as const;

  private readonly fetchImpl: typeof fetch;
  private readonly accessTokenProvider: BrowserDirectAccessTokenProvider;
  private readonly localReadinessEndpoint: string;
  private readonly publicReadinessEndpoint: string;
  private readonly publicStreamEndpoint: string;
  private readonly localToolLoop: BrowserDirectLocalToolLoop<TRequest> | undefined;
  private readonly maximumToolCalls: number;

  constructor(
    api: AxiosInstance,
    options: BrowserDirectCopilotTransportOptions<TRequest>,
  ) {
    this.fetchImpl = options.fetchImpl ?? globalThis.fetch.bind(globalThis);
    this.accessTokenProvider = options.accessTokenProvider;
    this.localToolLoop = options.localToolLoop;
    this.maximumToolCalls = options.maximumToolCalls ?? 8;
    if (!Number.isSafeInteger(this.maximumToolCalls) || this.maximumToolCalls <= 0) {
      throw contractError(
        'browser_direct_tool_call_budget_invalid',
        'BrowserDirect 工具调用预算必须是正安全整数。',
      );
    }
    const locationHref = options.locationHref ?? currentLocationHref();
    this.localReadinessEndpoint = resolveLocalEndpoint(api, '/healthz', locationHref);
    const publicBaseUrl = resolveApprovedPublicBaseUrl(
      options.publicBaseUrl,
      options.approvedPublicOrigins,
      locationHref,
    );
    this.publicReadinessEndpoint = new URL('v1/copilot/readiness', publicBaseUrl).href;
    this.publicStreamEndpoint = new URL('v1/copilot/chat/stream', publicBaseUrl).href;
  }

  async probeReadiness(signal: AbortSignal): Promise<CopilotRuntimeReadiness> {
    const local = await this.probeLocal(signal);
    if (local.status !== 'ready') {
      return {
        local,
        public: { status: 'unavailable', reason: '本地端点未就绪，未向公网发送认证请求。' },
      };
    }

    const token = await this.requireAccessToken(signal, false);
    if (!token) {
      return {
        local,
        public: { status: 'unavailable', reason: 'BrowserDirect 公网登录尚未完成。' },
      };
    }

    try {
      const response = await this.fetchImpl(this.publicReadinessEndpoint, {
        method: 'GET',
        headers: {
          Accept: 'application/json',
          Authorization: `Bearer ${token}`,
          'X-SonnetDB-Copilot-Contract': BrowserDirectContractVersion,
        },
        signal,
        credentials: 'omit',
        redirect: 'error',
      });
      rejectRedirectedResponse(response, 'browser_direct_public_redirect');
      requireContractVersion(response);
      const payload = response.ok ? await readJsonRecord(response) : null;
      if (response.ok && (payload?.status === 'ok' || payload?.status === 'ready')) {
        return { local, public: { status: 'ready' } };
      }
      return {
        local,
        public: {
          status: 'unavailable',
          reason: `BrowserDirect 公网端点 readiness 失败（HTTP ${response.status}）。`,
        },
      };
    } catch (error) {
      rethrowIfAborted(signal, error);
      if (error instanceof CopilotRuntimeContractError) throw error;
      return {
        local,
        public: { status: 'unavailable', reason: '无法连接 BrowserDirect 公网端点。' },
      };
    }
  }

  async *stream(
    runId: string,
    request: TRequest,
    signal: AbortSignal,
  ): AsyncGenerator<CopilotTransportEvent<TEvent>, void, unknown> {
    const token = await this.requireAccessToken(signal, true);
    if (!token) {
      throw contractError('browser_direct_token_missing', 'BrowserDirect 公网登录尚未完成。');
    }
    yield* streamPublicCopilotProtocol<TRequest, TEvent>(
      runId, request, signal,
      (payload, segmentSignal) => this.startPublicSegment(token, payload.runId, payload.request, payload.continuation, segmentSignal),
      this.localToolLoop,
      this.maximumToolCalls,
    );
  }
  private async startPublicSegment(
    token: string,
    runId: string,
    request: TRequest,
    continuation: BrowserDirectContinuation | undefined,
    signal: AbortSignal,
  ): Promise<Response> {
    let response: Response;
    try {
      response = await this.fetchImpl(this.publicStreamEndpoint, {
        method: 'POST',
        headers: {
          Accept: 'application/x-ndjson, text/event-stream',
          'Content-Type': 'application/json',
          Authorization: `Bearer ${token}`,
          'X-SonnetDB-Copilot-Contract': BrowserDirectContractVersion,
        },
        body: JSON.stringify({
          contractVersion: BrowserDirectContractVersion,
          runId,
          request,
          ...(continuation ? { continuation } : {}),
        } satisfies PublicCopilotRequest<TRequest>),
        signal,
        credentials: 'omit',
        redirect: 'error',
      });
    } catch (error) {
      rethrowIfAborted(signal, error);
      throw contractError('browser_direct_public_unavailable', '无法连接 BrowserDirect 公网端点。');
    }

    rejectRedirectedResponse(response, 'browser_direct_public_redirect');
    requireContractVersion(response);
    if (!response.ok) {
      throw contractError(
        'browser_direct_public_error',
        `BrowserDirect 公网请求失败（HTTP ${response.status}）。`,
      );
    }
    return response;
  }

  private async probeLocal(signal: AbortSignal): Promise<{ status: 'ready' | 'unavailable'; reason?: string }> {
    try {
      const response = await this.fetchImpl(this.localReadinessEndpoint, {
        method: 'GET',
        headers: { Accept: 'application/json' },
        signal,
        credentials: 'omit',
        redirect: 'error',
      });
      rejectRedirectedResponse(response, 'browser_direct_local_redirect');
      const payload = response.ok ? await readJsonRecord(response) : null;
      return response.ok && payload?.status === 'ok'
        ? { status: 'ready' }
        : {
          status: 'unavailable',
          reason: `SonnetDB 本地端点 readiness 失败（HTTP ${response.status}）。`,
        };
    } catch (error) {
      rethrowIfAborted(signal, error);
      if (error instanceof CopilotRuntimeContractError) throw error;
      return { status: 'unavailable', reason: '无法连接当前 SonnetDB 本地端点。' };
    }
  }

  private async requireAccessToken(signal: AbortSignal, required: boolean): Promise<string | null> {
    const token = (await this.accessTokenProvider.getAccessToken(signal))?.trim() ?? '';
    if (token) return token;
    if (!required) return null;
    throw contractError('browser_direct_token_missing', 'BrowserDirect 公网登录尚未完成。');
  }
}

export function resolveApprovedPublicBaseUrl(
  value: string,
  approvedOrigins: readonly string[],
  locationHref: string,
): URL {
  let url: URL;
  try {
    url = new URL(value, locationHref);
  } catch {
    throw contractError('browser_direct_public_url_invalid', 'BrowserDirect 公网地址无效。');
  }

  if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash) {
    throw contractError(
      'browser_direct_public_url_invalid',
      'BrowserDirect 公网地址必须是无用户信息、查询或片段的 HTTPS 地址。',
    );
  }

  const approved = new Set(approvedOrigins.map((origin) => normalizeApprovedOrigin(origin)));
  if (!approved.has(url.origin)) {
    throw contractError(
      'browser_direct_public_origin_unapproved',
      `BrowserDirect 公网 origin 未获批准：${url.origin}。`,
    );
  }

  url.pathname = `${url.pathname.replace(/\/+$/u, '')}/`;
  return url;
}

function normalizeApprovedOrigin(value: string): string {
  let origin: URL;
  try {
    origin = new URL(value);
  } catch {
    throw contractError('browser_direct_approved_origin_invalid', 'BrowserDirect approved origin 无效。');
  }
  if (origin.protocol !== 'https:' || origin.href !== `${origin.origin}/`) {
    throw contractError(
      'browser_direct_approved_origin_invalid',
      'BrowserDirect approved origin 必须是纯 HTTPS origin。',
    );
  }
  return origin.origin;
}

function resolveLocalEndpoint(api: AxiosInstance, path: string, locationHref: string): string {
  let endpoint: URL;
  try {
    endpoint = new URL(api.getUri({ url: path }), locationHref);
  } catch {
    throw contractError('browser_direct_local_url_invalid', '当前 SonnetDB 连接地址无效。');
  }
  if ((endpoint.protocol !== 'http:' && endpoint.protocol !== 'https:')
    || endpoint.username
    || endpoint.password
    || endpoint.search
    || endpoint.hash) {
    throw contractError(
      'browser_direct_local_url_invalid',
      'BrowserDirect 本地端点只允许当前 SonnetDB 连接上的 HTTP(S) 固定地址。',
    );
  }
  return endpoint.href;
}

async function readJsonRecord(response: Response): Promise<Record<string, unknown> | null> {
  try {
    const value: unknown = await response.json();
    return isRecord(value) ? value : null;
  } catch {
    return null;
  }
}

function rejectRedirectedResponse(response: Response, code: string): void {
  if (!response.redirected) return;
  throw contractError(code, 'BrowserDirect 拒绝跟随端点重定向。');
}

function requireContractVersion(response: Response): void {
  const version = response.headers.get('X-SonnetDB-Copilot-Contract')?.trim();
  if (version === BrowserDirectContractVersion) return;
  throw contractError(
    'browser_direct_contract_mismatch',
    `BrowserDirect 公网合同版本不匹配：${version || '(missing)'}。`,
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function currentLocationHref(): string {
  if (typeof window !== 'undefined') return window.location.href;
  return 'http://127.0.0.1/';
}

function rethrowIfAborted(signal: AbortSignal, error: unknown): void {
  if (!signal.aborted) return;
  if (signal.reason !== undefined) throw signal.reason;
  throw error;
}

function contractError(code: string, message: string): CopilotRuntimeContractError {
  return new CopilotRuntimeContractError(code, message);
}
