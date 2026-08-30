import type { AxiosInstance } from 'axios';
import {
  CopilotRuntimeContractError,
  type CopilotEventPayload,
  type CopilotRuntimeReadiness,
  type CopilotTransport,
  type CopilotTransportEvent,
} from './runtime';
import type { BrowserDirectLocalToolCall, BrowserDirectLocalToolLoop } from './browserDirectMcp';

export const BrowserDirectContractVersion = 'm27-browser-direct-v1' as const;

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

interface BrowserDirectRequest<TRequest> {
  contractVersion: typeof BrowserDirectContractVersion;
  runId: string;
  request: TRequest;
  continuation?: BrowserDirectContinuation;
}

interface BrowserDirectContinuation {
  previousCursor: string;
  toolCallId: string;
  toolName: string;
  toolResult: string;
}

interface ExpectedToolResult {
  toolCallId: string;
  toolName: string;
  toolResult: string;
}

interface CompletedLocalToolCall {
  fingerprint: string;
  toolArguments?: string;
  toolResult: string;
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
    let continuation: BrowserDirectContinuation | undefined;
    let expectedToolResult: ExpectedToolResult | undefined;
    let toolLoopCount = 0;
    const completedToolCalls = new Map<string, CompletedLocalToolCall>();

    while (true) {
      const response = await this.startPublicSegment(token, runId, request, continuation, signal);
      const contentType = response.headers.get('content-type')?.toLowerCase() ?? '';
      const isNdjson = contentType.startsWith('application/x-ndjson');
      const isSse = contentType.startsWith('text/event-stream');
      if (!isNdjson && !isSse) {
        throw contractError(
          'browser_direct_content_type_invalid',
          `BrowserDirect 公网端点返回了无效 Content-Type：${contentType || '(missing)'}。`,
        );
      }

      let pendingToolCall: BrowserDirectLocalToolCall | undefined;
      for await (const value of readEventRecords(response, isSse, signal)) {
        if (pendingToolCall) {
          throw contractError(
            'browser_direct_event_after_tool_call',
            'BrowserDirect 公网段在 tool_call 后仍返回事件，已拒绝执行本地工具。',
          );
        }
        const candidate = parseTransportEvent<TEvent>(value);
        if (expectedToolResult) {
          requireExpectedToolResult(candidate, expectedToolResult);
          expectedToolResult = undefined;
        }

        if (candidate.event.type === 'tool_call') {
          const localToolCall = requireLocalToolCall(candidate);
          const completed = completedToolCalls.get(localToolCall.toolCallId);
          if (completed) {
            if (completed.fingerprint !== fingerprintToolCall(localToolCall)) {
              throw contractError(
                'browser_direct_tool_call_conflict',
                `BrowserDirect toolCallId ${localToolCall.toolCallId} 被用于不同工具或参数。`,
              );
            }
            // The public replay remains bounded and must receive another exact
            // echo. Normalize arguments back to the first envelope so the common
            // state machine can advance sequence/cursor and suppress the replay.
            pendingToolCall = localToolCall;
            yield normalizeReplayEvent(candidate, completed.toolArguments);
            continue;
          }
          pendingToolCall = localToolCall;
        }
        yield candidate;
      }

      if (expectedToolResult) {
        throw contractError(
          'browser_direct_tool_result_missing',
          `BrowserDirect 公网 continuation 未回显 toolCallId ${expectedToolResult.toolCallId} 的结果。`,
        );
      }
      if (!pendingToolCall) return;
      if (!this.localToolLoop) {
        throw contractError(
          'browser_direct_local_tool_loop_unavailable',
          'BrowserDirect 本地 MCP tool-call loop 尚未配置。',
        );
      }

      // Replayed calls count toward the public loop budget even when their local
      // result is reused. This bounds a provider that keeps requesting replay.
      toolLoopCount += 1;
      if (toolLoopCount > this.maximumToolCalls) {
        throw contractError(
          'browser_direct_tool_call_budget_exceeded',
          'BrowserDirect 本地工具循环次数超过单轮预算（重复回放也计数）。',
        );
      }

      const fingerprint = fingerprintToolCall(pendingToolCall);
      const completed = completedToolCalls.get(pendingToolCall.toolCallId);
      let toolResult: string;
      if (completed) {
        if (completed.fingerprint !== fingerprint) {
          throw contractError(
            'browser_direct_tool_call_conflict',
            `BrowserDirect toolCallId ${pendingToolCall.toolCallId} 被用于不同工具或参数。`,
          );
        }
        toolResult = completed.toolResult;
      } else {
        toolResult = await this.localToolLoop.callTool(request, pendingToolCall, signal);
        completedToolCalls.set(pendingToolCall.toolCallId, {
          fingerprint,
          ...(pendingToolCall.toolArguments !== undefined
            ? { toolArguments: pendingToolCall.toolArguments }
            : {}),
          toolResult,
        });
      }
      continuation = {
        previousCursor: pendingToolCall.cursor,
        toolCallId: pendingToolCall.toolCallId,
        toolName: pendingToolCall.toolName,
        toolResult,
      };
      // The local result is untrusted opaque data. The public runtime must echo
      // the exact payload as a tool_result before it may continue the run.
      expectedToolResult = {
        toolCallId: pendingToolCall.toolCallId,
        toolName: pendingToolCall.toolName,
        toolResult,
      };
    }
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
        } satisfies BrowserDirectRequest<TRequest>),
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

function requireLocalToolCall<TEvent extends CopilotEventPayload>(
  candidate: CopilotTransportEvent<TEvent>,
): BrowserDirectLocalToolCall & { cursor: string } {
  const toolCallId = candidate.toolCallId?.trim();
  const toolName = candidate.event.toolName?.trim();
  if (!toolCallId || !toolName) {
    throw contractError('browser_direct_tool_call_invalid', 'BrowserDirect tool_call 缺少 toolCallId 或 toolName。');
  }
  return {
    cursor: candidate.cursor,
    toolCallId,
    toolName,
    ...(candidate.event.toolArguments !== undefined
      ? { toolArguments: candidate.event.toolArguments }
      : {}),
  };
}

function requireExpectedToolResult<TEvent extends CopilotEventPayload>(
  candidate: CopilotTransportEvent<TEvent>,
  expected: ExpectedToolResult,
): void {
  if (candidate.event.type === 'tool_result'
    && candidate.toolCallId === expected.toolCallId
    && candidate.event.toolName === expected.toolName
    && candidate.event.toolResult === expected.toolResult) {
    return;
  }
  throw contractError(
    'browser_direct_tool_result_mismatch',
    `BrowserDirect 公网 continuation 未逐字回显 toolCallId ${expected.toolCallId} 的本地结果。`,
  );
}

function normalizeReplayEvent<TEvent extends CopilotEventPayload>(
  candidate: CopilotTransportEvent<TEvent>,
  toolArguments: string | undefined,
): CopilotTransportEvent<TEvent> {
  return {
    ...candidate,
    event: {
      ...candidate.event,
      toolArguments,
    },
  };
}

function fingerprintToolCall(call: BrowserDirectLocalToolCall): string {
  let argumentsValue: unknown;
  try {
    argumentsValue = JSON.parse(call.toolArguments?.trim() || '{}');
  } catch {
    throw contractError(
      'browser_direct_tool_arguments_invalid',
      `BrowserDirect 本地工具 ${call.toolName} 的参数不是有效 JSON。`,
    );
  }
  if (!isRecord(argumentsValue)) {
    throw contractError(
      'browser_direct_tool_arguments_invalid',
      `BrowserDirect 本地工具 ${call.toolName} 的参数必须是 JSON object。`,
    );
  }
  return stableStringify({ toolName: call.toolName, arguments: argumentsValue });
}

function stableStringify(value: unknown): string {
  return JSON.stringify(sortJsonValue(value));
}

function sortJsonValue(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortJsonValue);
  if (!isRecord(value)) return value;
  return Object.fromEntries(
    Object.keys(value)
      .sort()
      .map((key) => [key, sortJsonValue(value[key])]),
  );
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

async function* readEventRecords(
  response: Response,
  sse: boolean,
  signal: AbortSignal,
): AsyncGenerator<unknown, void, unknown> {
  const reader = response.body?.getReader();
  if (!reader) {
    throw contractError('browser_direct_stream_missing', 'BrowserDirect 公网响应缺少可读流。');
  }

  const decoder = new TextDecoder();
  let buffer = '';
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split(/\r\n|\n|\r/u);
      buffer = lines.pop() ?? '';
      for (const line of lines) {
        const record = parseEventLine(line, sse);
        if (record !== null) yield record;
      }
    }
    buffer += decoder.decode();
    const record = parseEventLine(buffer, sse);
    if (record !== null) yield record;
  } catch (error) {
    rethrowIfAborted(signal, error);
    throw error;
  } finally {
    try {
      await reader.cancel();
    } catch {
      // The abort signal may already have closed the response stream.
    }
    reader.releaseLock();
  }
}

function parseEventLine(line: string, sse: boolean): unknown | null {
  let payload = line.trim();
  if (!payload || (sse && payload.startsWith(':'))) return null;
  if (sse) {
    if (!payload.startsWith('data:')) return null;
    payload = payload.slice('data:'.length).trimStart();
    if (!payload || payload === '[DONE]') return null;
  }
  try {
    return JSON.parse(payload);
  } catch {
    throw contractError('browser_direct_event_invalid', 'BrowserDirect 公网端点返回了无效 JSON 事件。');
  }
}

function parseTransportEvent<TEvent extends CopilotEventPayload>(value: unknown): CopilotTransportEvent<TEvent> {
  if (!isRecord(value)
    || typeof value.runId !== 'string'
    || typeof value.sequence !== 'number'
    || typeof value.cursor !== 'string'
    || !isRecord(value.event)) {
    throw contractError(
      'browser_direct_envelope_invalid',
      'BrowserDirect 公网事件缺少 runId、sequence、cursor 或 event。',
    );
  }
  return value as unknown as CopilotTransportEvent<TEvent>;
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
