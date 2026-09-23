import type { AxiosInstance } from 'axios';
import { getStudioNativeBridge, type StudioNativeBridgeClient } from '../api/studioNativeBridge';
import { BrowserDirectMcpToolLoop, type BrowserDirectLocalToolLoop } from './browserDirectMcp';
import {
  BrowserDirectContractVersion,
  streamPublicCopilotProtocol,
  type PublicCopilotRequest,
} from './publicRuntimeProtocol';
import {
  CopilotRuntimeContractError,
  type CopilotEventPayload,
  type CopilotRuntimeReadiness,
  type CopilotTransport,
  type CopilotTransportEvent,
} from './runtime';

export const StudioNativeCopilotCapability = 'copilot.nativeBroker.v1';

export interface StudioNativeRuntimeRegistrationOptions {
  bridge?: StudioNativeBridgeClient;
  fetchImpl?: typeof fetch;
  locationHref?: string;
  allowLocalToolDataEgress?: boolean;
  allowedLocalToolNames?: readonly string[];
  maximumLocalToolResultBytes?: number;
  maximumLocalToolCalls?: number;
}

/** Native host holds the public credential; this client only invokes fixed bridge operations. */
export class StudioNativeCopilotTransport<TRequest, TEvent extends CopilotEventPayload>
implements CopilotTransport<TRequest, TEvent> {
  readonly mode = 'StudioNative' as const;

  constructor(
    private readonly bridge: StudioNativeBridgeClient,
    private readonly localReadinessEndpoint: string,
    private readonly fetchImpl: typeof fetch,
    private readonly localToolLoop: BrowserDirectLocalToolLoop<TRequest>,
    private readonly maximumToolCalls = 8,
  ) {
    if (!bridge.manifest.capabilities.includes(StudioNativeCopilotCapability)) {
      throw contractError('studio_native_bridge_unsupported', '当前 Studio 宿主不支持原生 AI 服务连接。');
    }
  }

  async probeReadiness(callerSignal: AbortSignal): Promise<CopilotRuntimeReadiness> {
    const signal = AbortSignal.any([callerSignal, AbortSignal.timeout(10_000)]);
    let local: { status: 'ready' | 'unavailable'; reason?: string };
    try {
      const response = await this.fetchImpl(this.localReadinessEndpoint, {
        method: 'GET', headers: { Accept: 'application/json' }, signal,
        credentials: 'omit', redirect: 'error',
      });
      const payload = response.ok && !response.redirected ? await response.json() as { status?: unknown } : null;
      local = payload?.status === 'ok'
        ? { status: 'ready' }
        : { status: 'unavailable', reason: '当前 SonnetDB 本地端点尚未就绪。' };
    } catch {
      callerSignal.throwIfAborted();
      local = { status: 'unavailable', reason: '无法连接当前 SonnetDB 本地端点。' };
    }
    if (local.status !== 'ready') {
      return { local, public: { status: 'unavailable', reason: '本地端点未就绪。' } };
    }

    try {
      const status = await this.bridge.getCopilotStatus(signal);
      if (!status.configured || !status.connected) {
        return { local, public: { status: 'unavailable', reason: '请先在 Studio 中连接 AI 服务。' } };
      }
      const response = await this.bridge.probeCopilotReadiness(signal);
      requirePublicContract(response);
      const payload = response.ok ? await response.json() as { status?: unknown } : null;
      return {
        local,
        public: payload?.status === 'ready' || payload?.status === 'ok'
          ? { status: 'ready' }
          : { status: 'unavailable', reason: 'Studio 公网 AI 服务尚未就绪。' },
      };
    } catch (error) {
      callerSignal.throwIfAborted();
      if (error instanceof CopilotRuntimeContractError) throw error;
      return { local, public: { status: 'unavailable', reason: '无法通过 Studio 宿主连接 AI 服务。' } };
    }
  }

  async *stream(
    runId: string,
    request: TRequest,
    signal: AbortSignal,
  ): AsyncGenerator<CopilotTransportEvent<TEvent>, void, unknown> {
    yield* streamPublicCopilotProtocol<TRequest, TEvent>(
      runId, request, signal,
      async (payload: PublicCopilotRequest<TRequest>, segmentSignal) => {
        const response = payload.continuation
          ? await this.bridge.continueCopilotChat(payload, segmentSignal)
          : await this.bridge.startCopilotChat(payload, segmentSignal);
        requirePublicContract(response);
        if (!response.ok) {
          throw contractError('studio_native_public_error', `Studio AI 服务请求失败（HTTP ${response.status}）。`);
        }
        return response;
      },
      this.localToolLoop,
      this.maximumToolCalls,
    );
  }
}

/** Explicit StudioNative registration; missing host support never changes runtime mode. */
export async function createConfiguredStudioNativeTransport<
  TRequest extends { db?: string }, TEvent extends CopilotEventPayload,
>(
  api: AxiosInstance,
  databaseToken: string,
  options: StudioNativeRuntimeRegistrationOptions = {},
): Promise<StudioNativeCopilotTransport<TRequest, TEvent>> {
  const bridge = options.bridge ?? await getStudioNativeBridge();
  if (!bridge) throw contractError('studio_native_bridge_missing', 'StudioNative 需要原生 Studio 宿主。');
  const environment = import.meta.env ?? {};
  const locationHref = options.locationHref ?? (typeof window === 'undefined' ? 'http://127.0.0.1/' : window.location.href);
  const endpoint = new URL(api.getUri({ url: '/healthz' }), locationHref);
  if (!['http:', 'https:'].includes(endpoint.protocol) || endpoint.username || endpoint.password || endpoint.search || endpoint.hash) {
    throw contractError('studio_native_local_url_invalid', '当前 SonnetDB 本地端点地址无效。');
  }
  const fetchImpl = options.fetchImpl ?? globalThis.fetch.bind(globalThis);
  const localToolLoop = new BrowserDirectMcpToolLoop<TRequest>(api, databaseToken, {
    policy: {
      allowDataEgress: options.allowLocalToolDataEgress
        ?? environment.VITE_COPILOT_STUDIO_NATIVE_ALLOW_DATA_EGRESS?.trim().toLowerCase() === 'true',
      allowedToolNames: options.allowedLocalToolNames
        ?? (environment.VITE_COPILOT_STUDIO_NATIVE_ALLOWED_TOOLS ?? '').split(',').map((value: string) => value.trim()).filter(Boolean),
      ...(options.maximumLocalToolResultBytes !== undefined ? { maximumResultBytes: options.maximumLocalToolResultBytes } : {}),
    },
    fetchImpl,
    locationHref,
  });
  return new StudioNativeCopilotTransport(bridge, endpoint.href, fetchImpl, localToolLoop, options.maximumLocalToolCalls);
}

function requirePublicContract(response: Response): void {
  if (response.redirected || response.headers.get('X-SonnetDB-Copilot-Contract')?.trim() !== BrowserDirectContractVersion) {
    throw contractError('studio_native_contract_mismatch', 'Studio 公网响应合同不匹配或发生重定向。');
  }
}

function contractError(code: string, message: string): CopilotRuntimeContractError {
  return new CopilotRuntimeContractError(code, message);
}
