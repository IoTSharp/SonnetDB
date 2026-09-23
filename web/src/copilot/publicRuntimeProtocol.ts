import {
  CopilotRuntimeContractError,
  canonicalizeCopilotJson,
  type CopilotEventPayload,
  type CopilotTransportEvent,
} from './runtime';
import type { BrowserDirectLocalToolCall, BrowserDirectLocalToolLoop } from './browserDirectMcp';

export const BrowserDirectContractVersion = 'm27-browser-direct-v1' as const;

export interface PublicCopilotRequest<TRequest> {
  contractVersion: typeof BrowserDirectContractVersion;
  runId: string;
  request: TRequest;
  continuation?: BrowserDirectContinuation;
}

export interface BrowserDirectContinuation {
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

/** Shared bounded public protocol; the caller owns the network/credential boundary. */
export async function* streamPublicCopilotProtocol<TRequest, TEvent extends CopilotEventPayload>(
  runId: string,
  request: TRequest,
  callerSignal: AbortSignal,
  startSegment: (payload: PublicCopilotRequest<TRequest>, signal: AbortSignal) => Promise<Response>,
  localToolLoop: BrowserDirectLocalToolLoop<TRequest> | undefined,
  maximumToolCalls = 8,
): AsyncGenerator<CopilotTransportEvent<TEvent>, void, unknown> {
  if (!Number.isSafeInteger(maximumToolCalls) || maximumToolCalls <= 0 || maximumToolCalls > 64) {
    throw contractError('browser_direct_tool_call_budget_invalid', '工具调用预算必须介于 1 和 64。');
  }
  const deadline = new AbortController();
  const timeout = setTimeout(() => deadline.abort(new DOMException('Copilot run timed out.', 'TimeoutError')), 120_000);
  const signal = AbortSignal.any([callerSignal, deadline.signal]);
  try {
    let continuation: BrowserDirectContinuation | undefined;
    let expectedToolResult: ExpectedToolResult | undefined;
    let toolLoopCount = 0;
    const completedToolCalls = new Map<string, CompletedLocalToolCall>();

    for (let segment = 0; segment <= maximumToolCalls; segment += 1) {
      signal.throwIfAborted();
      const response = await startSegment({ contractVersion: BrowserDirectContractVersion, runId, request, ...(continuation ? { continuation } : {}) }, signal);
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
          // Validate before exposing untrusted arguments to the runtime/UI.
          const fingerprint = fingerprintToolCall(localToolCall);
          const completed = completedToolCalls.get(localToolCall.toolCallId);
          if (completed) {
            if (completed.fingerprint !== fingerprint) {
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
      if (!localToolLoop) {
        throw contractError(
          'browser_direct_local_tool_loop_unavailable',
          'BrowserDirect 本地 MCP tool-call loop 尚未配置。',
        );
      }

      // Replayed calls count toward the public loop budget even when their local
      // result is reused. This bounds a provider that keeps requesting replay.
      toolLoopCount += 1;
      if (toolLoopCount > maximumToolCalls) {
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
        toolResult = await localToolLoop.callTool(request, pendingToolCall, signal);
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

    throw contractError('browser_direct_tool_call_budget_exceeded', '公网工具循环超过单轮预算。');
  } finally {
    clearTimeout(timeout);
    deadline.abort();
  }
}
function requireLocalToolCall<TEvent extends CopilotEventPayload>(
  candidate: CopilotTransportEvent<TEvent>,
): BrowserDirectLocalToolCall & { cursor: string } {
  const toolCallId = typeof candidate.toolCallId === 'string' ? candidate.toolCallId.trim() : '';
  const toolName = typeof candidate.event.toolName === 'string' ? candidate.event.toolName.trim() : '';
  if (!toolCallId || !toolName) {
    throw contractError('browser_direct_tool_call_invalid', 'BrowserDirect tool_call 缺少 toolCallId 或 toolName。');
  }
  if (candidate.event.toolArguments !== undefined && typeof candidate.event.toolArguments !== 'string') {
    throw contractError('browser_direct_tool_arguments_invalid', '公网工具参数必须是 JSON 文本。');
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
  const rawArguments = call.toolArguments?.trim() || '{}';
  const limits = { maximumDepth: 32, maximumNodes: 4096, maximumCharacters: 256 * 1024 };
  try {
    const canonical = canonicalizeCopilotJson(rawArguments, limits);
    const argumentsValue: unknown = JSON.parse(rawArguments);
    if (!isRecord(argumentsValue)) {
      throw contractError('browser_direct_tool_arguments_invalid', '公网工具参数必须是 JSON object。');
    }
    // Local MCP uses JavaScript JSON values. Reject precision loss and duplicate
    // properties before a first execution, as well as before replay normalization.
    if (canonicalizeCopilotJson(JSON.stringify(argumentsValue), limits) !== canonical) {
      throw contractError('browser_direct_tool_arguments_lossy', '公网工具参数包含无法精确保留的数字或重复属性。');
    }
    return JSON.stringify({ toolName: call.toolName, arguments: canonical });
  } catch (error) {
    if (error instanceof CopilotRuntimeContractError) {
      if (error.code === 'runtime_tool_payload_budget_exceeded') {
        throw contractError('browser_direct_tool_arguments_budget_exceeded', '公网工具参数超过深度、节点、大小或处理时间预算。');
      }
      throw error;
    }
    throw contractError('browser_direct_tool_arguments_invalid', `BrowserDirect 本地工具 ${call.toolName} 的参数不是有效 JSON。`);
  }
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
  let bytes = 0;
  let linesRead = 0;
  let ended = false;
  const onAbort = () => { void reader.cancel().catch(() => undefined); };
  signal.addEventListener('abort', onAbort, { once: true });
  try {
    for (let chunks = 0; chunks < 8192; chunks += 1) {
      signal.throwIfAborted();
      const { done, value } = await reader.read();
      signal.throwIfAborted();
      if (done) { ended = true; break; }
      bytes += value.byteLength;
      if (bytes > 8 * 1024 * 1024) {
        throw contractError('browser_direct_stream_budget_exceeded', '公网响应流超过单段大小预算。');
      }
      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split(/\r\n|\n|\r/u);
      buffer = lines.pop() ?? '';
      if (buffer.length > 256 * 1024) {
        throw contractError('browser_direct_event_budget_exceeded', '公网事件超过大小预算。');
      }
      for (const line of lines) {
        linesRead += 1;
        if (linesRead > 8192 || line.length > 256 * 1024) {
          throw contractError('browser_direct_event_budget_exceeded', '公网事件超过数量或大小预算。');
        }
        const record = parseEventLine(line, sse);
        if (record !== null) yield record;
      }
    }
    if (!ended) {
      throw contractError('browser_direct_stream_budget_exceeded', '公网响应流超过读取次数预算。');
    }
    buffer += decoder.decode();
    if (buffer.length > 256 * 1024) {
      throw contractError('browser_direct_event_budget_exceeded', '公网事件超过大小预算。');
    }
    const record = parseEventLine(buffer, sse);
    if (record !== null) yield record;
  } catch (error) {
    rethrowIfAborted(signal, error);
    throw error;
  } finally {
    signal.removeEventListener('abort', onAbort);
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
    || !isRecord(value.event)
    || typeof value.event.type !== 'string') {
    throw contractError(
      'browser_direct_envelope_invalid',
      'BrowserDirect 公网事件缺少 runId、sequence、cursor 或 event。',
    );
  }
  return value as unknown as CopilotTransportEvent<TEvent>;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function rethrowIfAborted(signal: AbortSignal, error: unknown): void {
  if (!signal.aborted) return;
  if (signal.reason !== undefined) throw signal.reason;
  throw error;
}

function contractError(code: string, message: string): CopilotRuntimeContractError {
  return new CopilotRuntimeContractError(code, message);
}
