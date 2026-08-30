import { expect, test } from '@playwright/test';
import type { AxiosInstance } from 'axios';
import {
  ServerRelayCopilotTransport,
  resolveCopilotServerRelayEndpoint,
  streamCopilotChat,
  type CopilotChatEvent,
  type CopilotChatRequest,
} from '../src/api/copilot';
import {
  CopilotRuntime,
  resolveCopilotRuntimeMode,
  type CopilotRuntimeReadiness,
  type CopilotTransport,
  type CopilotTransportEvent,
} from '../src/copilot/runtime';

const ReadyRelay: CopilotRuntimeReadiness = {
  local: { status: 'ready' },
  public: { status: 'not-required' },
};

const Request: CopilotChatRequest = {
  db: 'factory',
  messages: [{ role: 'user', content: '描述 cpu' }],
  mode: 'read-only',
};

test('ServerRelay runs through the common state machine and preserves current events', async () => {
  const api = fakeApi('https://db.internal/sonnetdb');
  const runId = 'run_server_relay';
  const request: CopilotChatRequest = { ...Request, cursor: 'relay-cursor-before' };
  const sse = [
    relayEvent(runId, 1, 'relay-cursor-start', { type: 'start', message: 'started' }),
    relayEvent(runId, 2, 'relay-cursor-risk', { type: 'risk_review', message: 'read only' }),
    relayEvent(runId, 3, 'relay-cursor-call', {
      type: 'tool_call',
      toolName: 'describe_measurement',
      toolArguments: '{"measurement":"cpu"}',
    }, 'relay-call-stable-42'),
    relayEvent(runId, 4, 'relay-cursor-result', {
      type: 'tool_result',
      toolName: 'describe_measurement',
      toolResult: '{"fields":["usage"]}',
    }, 'relay-call-stable-42'),
    relayEvent(runId, 5, 'relay-cursor-final', { type: 'final', answer: 'cpu has usage' }),
    relayEvent(runId, 6, 'relay-cursor-done', { type: 'done', message: 'completed' }),
  ].join('');
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  const transport = new ServerRelayCopilotTransport(api, 'database-token', {
    locationHref: 'https://studio.local/app',
    fetchImpl: async (input, init) => {
      const url = String(input);
      requests.push({ url, init });
      if (url.endsWith('/healthz')) {
        return Response.json({ status: 'ok' });
      }
      return fragmentedSseResponse(sse, [7, 31, 79, 121]);
    },
  });
  const runtime = new CopilotRuntime('ServerRelay', [transport]);

  const received = await collect(runtime.run(request, { runId }));

  expect(received.map((item) => item.event.type)).toEqual([
    'start',
    'risk_review',
    'tool_call',
    'tool_result',
    'final',
    'done',
  ]);
  expect(received.map((item) => item.sequence)).toEqual([1, 2, 3, 4, 5, 6]);
  expect(received.map((item) => item.cursor)).toEqual([
    'relay-cursor-start',
    'relay-cursor-risk',
    'relay-cursor-call',
    'relay-cursor-result',
    'relay-cursor-final',
    'relay-cursor-done',
  ]);
  expect(received[2].toolCallId).toBe('relay-call-stable-42');
  expect(received[3].toolCallId).toBe('relay-call-stable-42');
  expect(requests.map((item) => item.url)).toEqual([
    'https://db.internal/sonnetdb/healthz',
    'https://db.internal/sonnetdb/v1/copilot/chat/stream',
  ]);
  expect(new Headers(requests[0].init?.headers).has('Authorization')).toBe(false);
  expect(new Headers(requests[1].init?.headers).get('Authorization')).toBe('Bearer database-token');
  expect(requests[0].init?.credentials).toBe('omit');
  expect(requests[1].init?.credentials).toBe('omit');
  expect(requests[0].init?.redirect).toBe('error');
  expect(requests[1].init?.redirect).toBe('error');
  expect(JSON.parse(String(requests[1].init?.body))).toEqual({ ...request, runId });
});

test('ServerRelay resolves only the fixed endpoint on the active SonnetDB connection', () => {
  const api = fakeApi('http://10.0.0.8:5080');
  expect(resolveCopilotServerRelayEndpoint(api, 'https://studio.local/app'))
    .toBe('http://10.0.0.8:5080/v1/copilot/chat/stream');

  const invalid = fakeApi('ftp://files.example.test');
  expect(() => resolveCopilotServerRelayEndpoint(invalid, 'https://studio.local/app'))
    .toThrow(/HTTP\(S\)/u);
});

test('legacy ServerRelay without a stable server envelope fails closed', async () => {
  const sse = [
    event({ type: 'final', answer: 'complete' }),
    event({ type: 'done' }),
  ].join('');
  const transport = new ServerRelayCopilotTransport(fakeApi('https://db.internal'), 'token', {
    locationHref: 'https://studio.local/app',
    fetchImpl: async (input) => String(input).endsWith('/healthz')
      ? Response.json({ status: 'ok' })
      : fragmentedSseResponse(sse, []),
  });
  const runtime = new CopilotRuntime('ServerRelay', [transport]);

  await expect(collect(runtime.run(Request, { runId: 'run_legacy_fifo' })))
    .rejects.toMatchObject({ code: 'server_relay_envelope_invalid' });
});

test('ServerRelay preserves a conflicting server runId for runtime validation', async () => {
  const transport = new ServerRelayCopilotTransport(fakeApi('https://db.internal'), 'token', {
    locationHref: 'https://studio.local/app',
    fetchImpl: async (input) => String(input).endsWith('/healthz')
      ? Response.json({ status: 'ok' })
      : fragmentedSseResponse(relayEvent(
        'run_from_other_request',
        1,
        'relay-cursor-other',
        { type: 'final', answer: 'wrong run' },
      ), []),
  });
  const runtime = new CopilotRuntime('ServerRelay', [transport]);

  await expect(collect(runtime.run(Request, { runId: 'run_expected' })))
    .rejects.toMatchObject({ code: 'runtime_run_id_mismatch' });
});

test('ServerRelay decodes bare CR and CRLF split across chunks', async () => {
  const cases = [
    {
      runId: 'run_bare_cr',
      body: [
        `data: ${JSON.stringify(relayPayload(
          'run_bare_cr',
          1,
          'relay-cursor-bare-final',
          { type: 'final', answer: 'bare-cr' },
        ))}\r\r`,
        `data: ${JSON.stringify(relayPayload(
          'run_bare_cr',
          2,
          'relay-cursor-bare-done',
          { type: 'done' },
        ))}\r\r`,
      ].join(''),
      cuts: [7, 19],
    },
    {
      runId: 'run_split_crlf',
      body: [
        relayEvent(
          'run_split_crlf',
          1,
          'relay-cursor-split-final',
          { type: 'final', answer: 'split-crlf' },
        ),
        relayEvent('run_split_crlf', 2, 'relay-cursor-split-done', { type: 'done' }),
      ].join(''),
      cuts: [] as number[],
    },
  ];
  cases[1].cuts = [...cases[1].body.matchAll(/\r\n/gu)].map((match) => match.index + 1);

  for (const item of cases) {
    const transport = new ServerRelayCopilotTransport(fakeApi('https://db.internal'), 'token', {
      locationHref: 'https://studio.local/app',
      fetchImpl: async (input) => String(input).endsWith('/healthz')
        ? Response.json({ status: 'ok' })
        : fragmentedSseResponse(item.body, item.cuts),
    });
    const runtime = new CopilotRuntime('ServerRelay', [transport]);
    const received = await collect(runtime.run(Request, { runId: item.runId }));
    expect(received.map((entry) => entry.event.type)).toEqual(['final', 'done']);
  }
});

test('runtime fails closed for unknown and unavailable configured modes', async () => {
  expect(() => resolveCopilotRuntimeMode('browser-direct')).toThrow(/未知/u);

  const relay = scriptedTransport(ReadyRelay, []);
  const directRuntime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('BrowserDirect', [relay]);
  await expect(collect(directRuntime.run(Request, { runId: 'run_no_direct' })))
    .rejects.toMatchObject({ code: 'runtime_transport_unavailable' });
  expect(relay.streamCalls).toBe(0);

  await expect(collect(streamCopilotChat(
    fakeApi('ftp://unused.example.test'),
    'token',
    Request,
    undefined,
    'BrowserDirect',
  )))
    .rejects.toMatchObject({ code: 'browser_direct_configuration_missing' });
  await expect(collect(streamCopilotChat(
    fakeApi('https://unused.example.test'),
    'token',
    Request,
    undefined,
    'Disabled',
  )))
    .rejects.toMatchObject({ code: 'runtime_disabled' });
});

test('runtime requires independent readiness before starting a transport', async () => {
  const relay = scriptedTransport(
    { local: { status: 'ready' } },
    [envelope('run_missing_readiness', 1, { type: 'done' })],
  );
  const runtime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [relay]);

  await expect(collect(runtime.run(Request, { runId: 'run_missing_readiness' })))
    .rejects.toMatchObject({ code: 'runtime_readiness_missing' });
  expect(relay.streamCalls).toBe(0);
});

test('state machine uses stable toolCallIds for idempotency and rejects conflicting reuse', async () => {
  const runId = 'run_idempotent';
  const events: Array<CopilotTransportEvent<CopilotChatEvent>> = [
    envelope(runId, 1, { type: 'start' }),
    envelope(runId, 2, { type: 'tool_call', toolName: 'sample_rows', toolArguments: '{"maxRows":1}' }, 'call-1'),
    envelope(runId, 3, { type: 'tool_call', toolName: 'sample_rows', toolArguments: '{"maxRows":1}' }, 'call-1'),
    envelope(runId, 4, { type: 'tool_result', toolName: 'sample_rows', toolResult: '{"rows":[]}' }, 'call-1'),
    envelope(runId, 5, { type: 'tool_result', toolName: 'sample_rows', toolResult: '{"rows":[]}' }, 'call-1'),
    envelope(runId, 6, { type: 'final', answer: 'empty' }),
    envelope(runId, 7, { type: 'done' }),
  ];
  const relay = scriptedTransport(ReadyRelay, events);
  const runtime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [relay]);

  const received = await collect(runtime.run(Request, { runId }));
  expect(received.map((item) => item.sequence)).toEqual([1, 2, 4, 6, 7]);

  const conflictRunId = 'run_conflict';
  const conflict = scriptedTransport(ReadyRelay, [
    envelope(conflictRunId, 1, { type: 'tool_call', toolName: 'sample_rows', toolArguments: '{"maxRows":1}' }, 'call-1'),
    envelope(conflictRunId, 2, { type: 'tool_call', toolName: 'sample_rows', toolArguments: '{"maxRows":2}' }, 'call-1'),
  ]);
  const conflictRuntime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [conflict]);
  await expect(collect(conflictRuntime.run(Request, { runId: conflictRunId })))
    .rejects.toMatchObject({ code: 'runtime_tool_call_conflict' });
});

test('state machine accepts semantically equivalent JSON tool replay payloads', async () => {
  const runId = 'run_equivalent_json';
  const relay = scriptedTransport(ReadyRelay, [
    envelope(runId, 1, {
      type: 'tool_call',
      toolName: 'sample_rows',
      toolArguments: '{"maxRows":1,"filter":{"site":"A","active":true},"ratio":0.01}',
    }, 'call-equivalent'),
    envelope(runId, 2, {
      type: 'tool_result',
      toolName: 'sample_rows',
      toolResult: '{"rows":[],"count":0}',
    }, 'call-equivalent'),
    envelope(runId, 3, {
      type: 'tool_call',
      toolName: 'sample_rows',
      toolArguments: ' { "ratio": 10e-3, "filter": { "active": true, "site": "A" }, "maxRows": 1.0 } ',
    }, 'call-equivalent'),
    envelope(runId, 4, {
      type: 'tool_result',
      toolName: 'sample_rows',
      toolResult: '{ "count": -0.0, "rows" : [ ] }',
    }, 'call-equivalent'),
    envelope(runId, 5, { type: 'final', answer: 'equivalent replay accepted' }),
    envelope(runId, 6, { type: 'done' }),
  ]);
  const runtime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [relay]);

  const received = await collect(runtime.run(Request, { runId }));
  expect(received.map((item) => item.sequence)).toEqual([1, 2, 5, 6]);
});

test('state machine compares non-JSON replay payloads exactly and fails closed on conflicts', async () => {
  const exactRunId = 'run_exact_non_json';
  const exactRelay = scriptedTransport(ReadyRelay, [
    envelope(exactRunId, 1, {
      type: 'tool_call', toolName: 'sample_rows', toolArguments: '{"maxRows":',
    }, 'call-non-json'),
    envelope(exactRunId, 2, {
      type: 'tool_result', toolName: 'sample_rows', toolResult: 'not-json',
    }, 'call-non-json'),
    envelope(exactRunId, 3, {
      type: 'tool_call', toolName: 'sample_rows', toolArguments: '{"maxRows":',
    }, 'call-non-json'),
    envelope(exactRunId, 4, {
      type: 'tool_result', toolName: 'sample_rows', toolResult: 'not-json',
    }, 'call-non-json'),
    envelope(exactRunId, 5, { type: 'final', answer: 'exact replay accepted' }),
    envelope(exactRunId, 6, { type: 'done' }),
  ]);
  const exactRuntime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [exactRelay]);
  const exactReceived = await collect(exactRuntime.run(Request, { runId: exactRunId }));
  expect(exactReceived.map((item) => item.sequence)).toEqual([1, 2, 5, 6]);

  const argumentsRunId = 'run_conflicting_non_json_arguments';
  const argumentsRelay = scriptedTransport(ReadyRelay, [
    envelope(argumentsRunId, 1, {
      type: 'tool_call', toolName: 'sample_rows', toolArguments: '{"maxRows":',
    }, 'call-invalid-arguments'),
    envelope(argumentsRunId, 2, {
      type: 'tool_result', toolName: 'sample_rows', toolResult: '{}',
    }, 'call-invalid-arguments'),
    envelope(argumentsRunId, 3, {
      type: 'tool_call', toolName: 'sample_rows', toolArguments: '{"maxRows": ',
    }, 'call-invalid-arguments'),
  ]);
  const argumentsRuntime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>(
    'ServerRelay',
    [argumentsRelay],
  );
  await expect(collect(argumentsRuntime.run(Request, { runId: argumentsRunId })))
    .rejects.toMatchObject({ code: 'runtime_tool_call_conflict' });

  const resultRunId = 'run_conflicting_non_json_result';
  const resultRelay = scriptedTransport(ReadyRelay, [
    envelope(resultRunId, 1, {
      type: 'tool_call', toolName: 'sample_rows', toolArguments: '{}',
    }, 'call-invalid-result'),
    envelope(resultRunId, 2, {
      type: 'tool_result', toolName: 'sample_rows', toolResult: 'not-json',
    }, 'call-invalid-result'),
    envelope(resultRunId, 3, {
      type: 'tool_call', toolName: 'sample_rows', toolArguments: '{}',
    }, 'call-invalid-result'),
    envelope(resultRunId, 4, {
      type: 'tool_result', toolName: 'sample_rows', toolResult: 'not-json ',
    }, 'call-invalid-result'),
  ]);
  const resultRuntime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>(
    'ServerRelay',
    [resultRelay],
  );
  await expect(collect(resultRuntime.run(Request, { runId: resultRunId })))
    .rejects.toMatchObject({ code: 'runtime_tool_result_conflict' });
});

test('state machine binds tool results to the original toolName', async () => {
  const runId = 'run_tool_name_mismatch';
  const relay = scriptedTransport(ReadyRelay, [
    envelope(runId, 1, { type: 'tool_call', toolName: 'sample_rows', toolArguments: '{}' }, 'call-1'),
    envelope(runId, 2, { type: 'tool_result', toolName: 'execute_sql', toolResult: '{}' }, 'call-1'),
  ]);
  const runtime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [relay]);

  await expect(collect(runtime.run(Request, { runId })))
    .rejects.toMatchObject({ code: 'runtime_tool_name_mismatch' });
});

test('state machine rejects retry after a tool result has completed the call', async () => {
  const runId = 'run_retry_after_result';
  const relay = scriptedTransport(ReadyRelay, [
    envelope(runId, 1, { type: 'tool_call', toolName: 'sample_rows', toolArguments: '{}' }, 'call-1'),
    envelope(runId, 2, { type: 'tool_result', toolName: 'sample_rows', toolResult: '{}' }, 'call-1'),
    envelope(runId, 3, { type: 'tool_retry', toolName: 'sample_rows' }, 'call-1'),
  ]);
  const runtime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [relay]);

  await expect(collect(runtime.run(Request, { runId })))
    .rejects.toMatchObject({ code: 'runtime_tool_already_completed' });
});

test('state machine rejects out-of-order sequences and conflicting cursors', async () => {
  const sequenceRunId = 'run_bad_sequence';
  const outOfOrder = scriptedTransport(ReadyRelay, [
    envelope(sequenceRunId, 2, { type: 'start' }),
  ]);
  const sequenceRuntime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [outOfOrder]);
  await expect(collect(sequenceRuntime.run(Request, { runId: sequenceRunId })))
    .rejects.toMatchObject({ code: 'runtime_sequence_invalid' });

  const cursorRunId = 'run_cursor_conflict';
  const cursorConflict = scriptedTransport(ReadyRelay, [
    { ...envelope(cursorRunId, 1, { type: 'start' }), cursor: 'cursor-shared' },
    { ...envelope(cursorRunId, 2, { type: 'risk_review' }), cursor: 'cursor-shared' },
  ]);
  const cursorRuntime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [cursorConflict]);
  await expect(collect(cursorRuntime.run(Request, { runId: cursorRunId })))
    .rejects.toMatchObject({ code: 'runtime_cursor_conflict' });
});

test('state machine rejects done without a final or error outcome', async () => {
  const runId = 'run_done_without_outcome';
  const relay = scriptedTransport(ReadyRelay, [
    envelope(runId, 1, { type: 'start' }),
    envelope(runId, 2, { type: 'done' }),
  ]);
  const runtime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [relay]);

  await expect(collect(runtime.run(Request, { runId })))
    .rejects.toMatchObject({ code: 'runtime_done_without_outcome' });
});

test('state machine rejects an empty final answer', async () => {
  const runId = 'run_empty_final';
  const relay = scriptedTransport(ReadyRelay, [
    envelope(runId, 1, { type: 'final', answer: '   ' }),
    envelope(runId, 2, { type: 'done' }),
  ]);
  const runtime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [relay]);

  await expect(collect(runtime.run(Request, { runId })))
    .rejects.toMatchObject({ code: 'runtime_final_invalid' });
});

test('state machine rejects tool events and conflicting outcomes after an outcome', async () => {
  const toolRunId = 'run_tool_after_outcome';
  const toolAfterOutcome = scriptedTransport(ReadyRelay, [
    envelope(toolRunId, 1, { type: 'final', answer: 'complete' }),
    envelope(
      toolRunId,
      2,
      { type: 'tool_call', toolName: 'execute_sql', toolArguments: '{"sql":"DROP DATABASE factory"}' },
      'late-tool',
    ),
  ]);
  const toolRuntime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>(
    'ServerRelay',
    [toolAfterOutcome],
  );
  await expect(collect(toolRuntime.run(Request, { runId: toolRunId })))
    .rejects.toMatchObject({ code: 'runtime_event_after_outcome' });

  const conflictRunId = 'run_outcome_conflict';
  const conflictingOutcomes = scriptedTransport(ReadyRelay, [
    envelope(conflictRunId, 1, { type: 'final', answer: 'complete' }),
    envelope(conflictRunId, 2, { type: 'error', message: 'late error' }),
  ]);
  const conflictRuntime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>(
    'ServerRelay',
    [conflictingOutcomes],
  );
  await expect(collect(conflictRuntime.run(Request, { runId: conflictRunId })))
    .rejects.toMatchObject({ code: 'runtime_outcome_conflict' });
});

test('runtime rejects a final outcome when the stream closes before done', async () => {
  const runId = 'run_missing_done';
  const relay = scriptedTransport(ReadyRelay, [
    envelope(runId, 1, { type: 'final', answer: 'truncated' }),
  ]);
  const runtime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [relay]);

  await expect(collect(runtime.run(Request, { runId })))
    .rejects.toMatchObject({ code: 'runtime_stream_incomplete' });
});

test('runtime cancellation stops delivery without changing transport mode', async () => {
  const runId = 'run_cancelled';
  const relay = scriptedTransport(ReadyRelay, [
    envelope(runId, 1, { type: 'start' }),
    envelope(runId, 2, { type: 'final', answer: 'late' }),
    envelope(runId, 3, { type: 'done' }),
  ]);
  const runtime = new CopilotRuntime<CopilotChatRequest, CopilotChatEvent>('ServerRelay', [relay]);
  const controller = new AbortController();
  const iterator = runtime.run(Request, { runId, signal: controller.signal });

  await expect(iterator.next()).resolves.toMatchObject({ value: { event: { type: 'start' } } });
  controller.abort(new DOMException('cancelled', 'AbortError'));
  await expect(iterator.next()).rejects.toMatchObject({ name: 'AbortError' });
  expect(relay.streamCalls).toBe(1);
});

function fakeApi(baseUrl: string): AxiosInstance {
  return {
    getUri: ({ url }: { url?: string }) => `${baseUrl.replace(/\/+$/u, '')}/${String(url ?? '').replace(/^\/+/, '')}`,
  } as unknown as AxiosInstance;
}

function event(value: CopilotChatEvent): string {
  return `data: ${JSON.stringify(value)}\r\n\r\n`;
}

function relayEvent(
  runId: string,
  sequence: number,
  cursor: string,
  value: CopilotChatEvent,
  toolCallId?: string,
): string {
  return event(relayPayload(runId, sequence, cursor, value, toolCallId));
}

function relayPayload(
  runId: string,
  sequence: number,
  cursor: string,
  value: CopilotChatEvent,
  toolCallId?: string,
): CopilotChatEvent {
  return {
    ...value,
    runId,
    sequence,
    cursor,
    ...(toolCallId ? { toolCallId } : {}),
  };
}

function fragmentedSseResponse(body: string, cuts: number[]): Response {
  const bytes = new TextEncoder().encode(body);
  const boundaries = [...cuts.filter((cut) => cut > 0 && cut < bytes.length), bytes.length];
  let offset = 0;
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const boundary of boundaries) {
        controller.enqueue(bytes.slice(offset, boundary));
        offset = boundary;
      }
      controller.close();
    },
  });
  return new Response(stream, { status: 200, headers: { 'Content-Type': 'text/event-stream; charset=utf-8' } });
}

function envelope(
  runId: string,
  sequence: number,
  value: CopilotChatEvent,
  toolCallId?: string,
): CopilotTransportEvent<CopilotChatEvent> {
  return {
    runId,
    sequence,
    cursor: `${runId}:${sequence}`,
    ...(toolCallId ? { toolCallId } : {}),
    event: value,
  };
}

function scriptedTransport(
  readiness: CopilotRuntimeReadiness,
  events: Array<CopilotTransportEvent<CopilotChatEvent>>,
): CopilotTransport<CopilotChatRequest, CopilotChatEvent> & { streamCalls: number } {
  return {
    mode: 'ServerRelay',
    streamCalls: 0,
    async probeReadiness() {
      return readiness;
    },
    async *stream() {
      this.streamCalls += 1;
      for (const item of events) yield item;
    },
  };
}

async function collect<T>(values: AsyncIterable<T>): Promise<T[]> {
  const result: T[] = [];
  for await (const value of values) result.push(value);
  return result;
}
