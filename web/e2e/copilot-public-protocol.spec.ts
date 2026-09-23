import { expect, test } from '@playwright/test';
import { streamPublicCopilotProtocol } from '../src/copilot/publicRuntimeProtocol';
import { canonicalizeCopilotJson, CopilotRuntime, type CopilotEventPayload } from '../src/copilot/runtime';

test.setTimeout(10_000);

test('public protocol rejects deeply nested tool arguments before executing a local tool', async () => {
  // Fixed 64-level input is within the event-byte budget, but exceeds the
  // explicit 32-level argument budget and must not overflow the JS stack.
  const toolArguments = '{"nested":'.repeat(64) + '0' + '}'.repeat(64);
  let calls = 0;
  const iterator = streamPublicCopilotProtocol('run-depth', {}, new AbortController().signal,
    async () => ndjson({ runId: 'run-depth', sequence: 1, cursor: 'depth-1', toolCallId: 'call-depth',
      event: { type: 'tool_call', toolName: 'list_measurements', toolArguments } }),
    { async callTool() { calls += 1; return '{}'; } });
  await expect(iterator.next()).rejects.toMatchObject({ code: 'browser_direct_tool_arguments_budget_exceeded' });
  expect(calls).toBe(0);
});

test('public protocol rejects too many argument nodes before executing a local tool', async () => {
  let calls = 0;
  const iterator = streamPublicCopilotProtocol('run-nodes', {}, new AbortController().signal,
    async () => ndjson({ runId: 'run-nodes', sequence: 1, cursor: 'nodes-1', toolCallId: 'call-nodes',
      event: { type: 'tool_call', toolName: 'list_measurements', toolArguments: JSON.stringify({ items: Array(4097).fill(0) }) } }),
    { async callTool() { calls += 1; return '{}'; } });
  await expect(iterator.next()).rejects.toMatchObject({ code: 'browser_direct_tool_arguments_budget_exceeded' });
  expect(calls).toBe(0);
});

test('public protocol rejects lossy integer and duplicate-key arguments before first execution', async () => {
  for (const [index, toolArguments] of ['{"value":9007199254740993}', '{"value":1,"value":2}'].entries()) {
    let calls = 0;
    const runId = `lossy-${index}`;
    const iterator = streamPublicCopilotProtocol(runId, {}, new AbortController().signal,
      async () => ndjson(toolEvent(runId, 1, toolArguments)),
      { async callTool() { calls += 1; return '{}'; } });
    await expect(iterator.next()).rejects.toMatchObject({ code: 'browser_direct_tool_arguments_lossy' });
    expect(calls).toBe(0);
  }
});

test('public replay cannot normalize a changed large integer or duplicate key to a cached call', async () => {
  for (const [index, changedArguments] of ['{"value":9007199254740993}', '{"value":1,"value":9007199254740992}'].entries()) {
    const runId = `replay-lossy-${index}`;
    let calls = 0;
    const iterator = streamPublicCopilotProtocol(runId, {}, new AbortController().signal,
      async (payload) => payload.continuation
        ? new Response([
          JSON.stringify({ runId, sequence: 2, cursor: 'echo', toolCallId: 'bounded-call',
            event: { type: 'tool_result', toolName: 'list_measurements', toolResult: '{}' } }),
          JSON.stringify(toolEvent(runId, 3, changedArguments)),
        ].join('\n'), { headers: { 'Content-Type': 'application/x-ndjson' } })
        : ndjson(toolEvent(runId, 1, '{"value":9007199254740992}')),
      { async callTool() { calls += 1; return '{}'; } });
    await iterator.next();
    expect((await iterator.next()).value).toMatchObject({ event: { type: 'tool_result' } });
    await expect(iterator.next()).rejects.toMatchObject({ code: 'browser_direct_tool_arguments_lossy' });
    expect(calls).toBe(1);
  }
});

test('public protocol rejects malformed tool metadata with contract errors', async () => {
  const valid = toolEvent('invalid-metadata', 1, '{}');
  const cases = [
    { ...valid, toolCallId: 1 },
    { ...valid, event: { ...valid.event, toolName: 1 } },
    { ...valid, event: { ...valid.event, toolArguments: {} } },
    { ...valid, event: { ...valid.event, type: {} } },
  ];
  for (const event of cases) {
    const iterator = streamPublicCopilotProtocol('invalid-metadata', {}, new AbortController().signal,
      async () => ndjson(event), undefined);
    await expect(iterator.next()).rejects.toMatchObject({ name: 'CopilotRuntimeContractError' });
  }
});

test('public protocol rejects an oversized unterminated event and releases the stream', async () => {
  let canceled = false;
  const response = new Response(new ReadableStream<Uint8Array>({
    start(controller) { controller.enqueue(new TextEncoder().encode('x'.repeat(256 * 1024 + 1))); },
    cancel() { canceled = true; },
  }), { headers: { 'Content-Type': 'application/x-ndjson' } });
  const iterator = streamPublicCopilotProtocol('run-size', {}, new AbortController().signal, async () => response, undefined);
  await expect(iterator.next()).rejects.toMatchObject({ code: 'browser_direct_event_budget_exceeded' });
  expect(canceled).toBe(true);
});

test('public protocol caller cancellation releases a blocked stream read', async () => {
  const controller = new AbortController();
  let canceled = false;
  let started!: () => void;
  const reading = new Promise<void>((resolve) => { started = resolve; });
  const response = new Response(new ReadableStream<Uint8Array>({
    pull() { started(); },
    cancel() { canceled = true; },
  }), { headers: { 'Content-Type': 'application/x-ndjson' } });
  const iterator = streamPublicCopilotProtocol('run-cancel', {}, controller.signal, async () => response, undefined);
  const next = iterator.next();
  await reading;
  controller.abort(new DOMException('Canceled by caller.', 'AbortError'));
  await expect(next).rejects.toMatchObject({ name: 'AbortError' });
  expect(canceled).toBe(true);
});

test('runtime canonicalizes nested JSON once and accepts an equivalent replay', async () => {
  // The former per-level escaped-string representation grows exponentially;
  // this bounded 40-level case now retains a linear-size structured tree.
  const prefix = '{"nested":'.repeat(40);
  const suffix = '}'.repeat(40);
  await expect(runToolPayloads('nested-runtime', [`${prefix}1${suffix}`, `${prefix}1.0${suffix}`]))
    .resolves.toEqual([1, 3, 4, 5]);
});

test('runtime bounds JSON replay depth, nodes, text and exponent length', async () => {
  const cases = [
    '{"nested":'.repeat(66) + '0' + '}'.repeat(66),
    JSON.stringify({ items: Array(16_385).fill(0) }),
    JSON.stringify({ value: 'x'.repeat(1024 * 1024) }),
    '{"value":1e' + '9'.repeat(513) + '}',
  ];
  // Four fixed inputs, each at most 1 MiB, inside the 10-second test deadline.
  for (const [index, payload] of cases.entries()) {
    const runId = `budget-${index}`;
    await expect(runToolPayloads(runId, [payload]))
      .rejects.toMatchObject({ code: 'runtime_tool_payload_budget_exceeded' });
  }
});

test('runtime JSON replay keeps precise numbers and duplicate-key order', async () => {
  await expect(runToolPayloads('precise-replay', [
    '{"value":9007199254740993,"duplicate":1,"duplicate":2}',
    '{"duplicate":1.0,"value":9007199254740993.0,"duplicate":2e0}',
  ])).resolves.toEqual([1, 3, 4, 5]);
  await expect(runToolPayloads('precise-conflict', [
    '{"value":9007199254740993,"duplicate":1,"duplicate":2}',
    '{"duplicate":1,"value":9007199254740994,"duplicate":2}',
  ])).rejects.toMatchObject({ code: 'runtime_tool_call_conflict' });
  await expect(runToolPayloads('duplicate-order', [
    '{"duplicate":1,"duplicate":2}', '{"duplicate":2,"duplicate":1}',
  ])).rejects.toMatchObject({ code: 'runtime_tool_call_conflict' });
});

test('canonical JSON deadline rejects work beyond five seconds', () => {
  const descriptor = Object.getOwnPropertyDescriptor(performance, 'now');
  let calls = 0;
  let captured: unknown;
  Object.defineProperty(performance, 'now', { configurable: true, value: () => calls++ * 5_001 });
  try {
    // Keep Playwright's own timing/assertion machinery outside the clock mock.
    canonicalizeCopilotJson('{}');
  } catch (error) {
    captured = error;
  } finally {
    if (descriptor) Object.defineProperty(performance, 'now', descriptor);
    else Reflect.deleteProperty(performance, 'now');
  }
  expect(captured).toMatchObject({ code: 'runtime_tool_payload_budget_exceeded' });
});

test('runtime handles bounded long middle-zero and trailing-zero numbers without regex backtracking', async () => {
  const zeros = '0'.repeat(64 * 1024);
  const started = performance.now();
  await expect(runToolPayloads('middle-zeros', [
    `{"value":1${zeros}1}`, `{"value":1${zeros}1e0}`,
  ])).resolves.toEqual([1, 3, 4, 5]);
  await expect(runToolPayloads('trailing-zeros', [
    `{"value":1${zeros}}`, '{"value":1e65536}',
  ])).resolves.toEqual([1, 3, 4, 5]);
  // A generous two-second bound for four <= 64 KiB scans; the removed suffix
  // regex retried every position in the middle-zero case and was quadratic.
  expect(performance.now() - started).toBeLessThan(2_000);
});

test('runtime rejects deeply nested extra event fields before fingerprinting', async () => {
  const runId = 'event-depth';
  const nested: unknown = JSON.parse('{"nested":'.repeat(66) + '0' + '}'.repeat(66));
  const runtime = new CopilotRuntime<object, CopilotEventPayload>('ServerRelay', [{
    mode: 'ServerRelay',
    async probeReadiness() { return { local: { status: 'ready' }, public: { status: 'not-required' } }; },
    async *stream() { yield { runId, sequence: 1, cursor: 'deep', event: { type: 'status', extra: nested } }; },
  }]);
  await expect(runtime.run({}, { runId }).next()).rejects.toMatchObject({ code: 'runtime_event_budget_exceeded' });
});

async function runToolPayloads(runId: string, payloads: readonly string[]): Promise<number[]> {
  if (payloads.length > 2) throw new Error('This fixture accepts at most two tool payloads.');
  const runtime = new CopilotRuntime<object, CopilotEventPayload>('ServerRelay', [{
    mode: 'ServerRelay',
    async probeReadiness() { return { local: { status: 'ready' }, public: { status: 'not-required' } }; },
    async *stream() {
      for (let index = 0; index < payloads.length; index += 1) yield toolEvent(runId, index + 1, payloads[index]);
      yield { runId, sequence: payloads.length + 1, cursor: 'result', toolCallId: 'bounded-call',
        event: { type: 'tool_result', toolName: 'list_measurements', toolResult: '{}' } };
      yield { runId, sequence: payloads.length + 2, cursor: 'final', event: { type: 'final', answer: 'complete' } };
      yield { runId, sequence: payloads.length + 3, cursor: 'done', event: { type: 'done' } };
    },
  }]);
  const sequences: number[] = [];
  for await (const event of runtime.run({}, { runId })) sequences.push(event.sequence);
  return sequences;
}

function toolEvent(runId: string, sequence: number, toolArguments: string) {
  return { runId, sequence, cursor: `${runId}-${sequence}`, toolCallId: 'bounded-call',
    event: { type: 'tool_call', toolName: 'list_measurements', toolArguments } };
}

function ndjson(value: unknown): Response {
  return new Response(JSON.stringify(value) + '\n', { headers: { 'Content-Type': 'application/x-ndjson' } });
}
