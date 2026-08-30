import { expect, test } from '@playwright/test';
import type { AxiosInstance } from 'axios';
import { createPinia, setActivePinia } from 'pinia';
import {
  BrowserDirectContractVersion,
  BrowserDirectCopilotTransport,
  resolveApprovedPublicBaseUrl,
} from '../src/copilot/browserDirect';
import {
  BrowserDirectMcpProtocolVersion,
  BrowserDirectMcpToolLoop,
} from '../src/copilot/browserDirectMcp';
import {
  clearBrowserDirectAccessToken,
  setBrowserDirectAccessToken,
} from '../src/copilot/browserDirectEntry';
import { CopilotRuntime, type CopilotTransportEvent } from '../src/copilot/runtime';
import {
  streamCopilotChat,
  type CopilotChatEvent,
  type CopilotChatRequest,
} from '../src/api/copilot';
import { useAuthStore } from '../src/stores/auth';

const Request: CopilotChatRequest = {
  db: 'factory',
  messages: [{ role: 'user', content: '描述 cpu' }],
  mode: 'read-only',
};

test.afterEach(() => {
  clearBrowserDirectAccessToken();
});

test('Web Copilot entry registers BrowserDirect with only the in-memory public token', async () => {
  setBrowserDirectAccessToken('public-access-token', new Date(Date.now() + 60_000).toISOString());
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const url = String(input);
    requests.push({ url, init });
    if (url.startsWith('https://db.internal')) return Response.json({ status: 'ok' });
    if (url.endsWith('/readiness')) return publicJsonResponse({ status: 'ready' });

    const body = JSON.parse(String(init?.body)) as { runId: string };
    const events: Array<CopilotTransportEvent<CopilotChatEvent>> = [
      { runId: body.runId, sequence: 1, cursor: 'cursor-1', event: { type: 'final', answer: 'ready' } },
      { runId: body.runId, sequence: 2, cursor: 'cursor-2', event: { type: 'done' } },
    ];
    return fragmentedResponse(
      `${events.map((item) => JSON.stringify(item)).join('\n')}\n`,
      'application/x-ndjson',
      [13],
      true,
    );
  };

  const received = await collect(streamCopilotChat(
    fakeApi('https://db.internal'),
    'database-token',
    Request,
    undefined,
    'BrowserDirect',
    {
      publicBaseUrl: 'https://ai.example.com/runtime',
      approvedPublicOrigins: ['https://ai.example.com'],
      locationHref: 'https://studio.local/app',
      fetchImpl,
    },
  ));

  expect(received.map((item) => item.event.type)).toEqual(['final', 'done']);
  expect(requests.map((item) => item.url)).toEqual([
    'https://db.internal/healthz',
    'https://ai.example.com/runtime/v1/copilot/readiness',
    'https://ai.example.com/runtime/v1/copilot/chat/stream',
  ]);
  expect(new Headers(requests[0].init?.headers).has('Authorization')).toBe(false);
  for (const publicRequest of requests.slice(1)) {
    expect(new Headers(publicRequest.init?.headers).get('Authorization')).toBe('Bearer public-access-token');
    expect(JSON.stringify(publicRequest)).not.toContain('database-token');
  }
});

test('Web Copilot entry rejects database-token reuse before any public request', async () => {
  setBrowserDirectAccessToken('database-token', futureExpiry());
  const requests: string[] = [];

  await expect(collect(streamCopilotChat(
    fakeApi('https://db.internal'),
    'database-token',
    Request,
    undefined,
    'BrowserDirect',
    {
      publicBaseUrl: 'https://ai.example.com',
      approvedPublicOrigins: ['https://ai.example.com'],
      locationHref: 'https://studio.local/app',
      fetchImpl: async (input) => {
        requests.push(String(input));
        return Response.json({ status: 'ok' });
      },
    },
  ))).rejects.toMatchObject({ code: 'browser_direct_token_boundary_violation' });

  expect(requests).toEqual(['https://db.internal/healthz']);
});

test('Web Copilot entry fails closed for an expired in-memory public token', async () => {
  expect(() => setBrowserDirectAccessToken(
    'expired-public-token',
    new Date(Date.now() - 1_000).toISOString(),
  )).toThrow(/已过期/u);
  const requests: string[] = [];

  await expect(collect(streamCopilotChat(
    fakeApi('https://db.internal'),
    'database-token',
    Request,
    undefined,
    'BrowserDirect',
    {
      publicBaseUrl: 'https://ai.example.com',
      approvedPublicOrigins: ['https://ai.example.com'],
      locationHref: 'https://studio.local/app',
      fetchImpl: async (input) => {
        requests.push(String(input));
        return Response.json({ status: 'ok' });
      },
    },
  ))).rejects.toMatchObject({ code: 'runtime_public_unavailable' });

  expect(requests).toEqual(['https://db.internal/healthz']);
});

test('database logout clears the in-memory BrowserDirect public token', async () => {
  const previousLocalStorage = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: {
      getItem: () => null,
      setItem: () => undefined,
      removeItem: () => undefined,
    },
  });

  try {
    setBrowserDirectAccessToken('public-access-token', futureExpiry());
    setActivePinia(createPinia());
    useAuthStore().logout();
    const requests: string[] = [];

    await expect(collect(streamCopilotChat(
      fakeApi('https://db.internal'),
      'database-token',
      Request,
      undefined,
      'BrowserDirect',
      {
        publicBaseUrl: 'https://ai.example.com',
        approvedPublicOrigins: ['https://ai.example.com'],
        locationHref: 'https://studio.local/app',
        fetchImpl: async (input) => {
          requests.push(String(input));
          return Response.json({ status: 'ok' });
        },
      },
    ))).rejects.toMatchObject({ code: 'runtime_public_unavailable' });

    expect(requests).toEqual(['https://db.internal/healthz']);
  } finally {
    if (previousLocalStorage) {
      Object.defineProperty(globalThis, 'localStorage', previousLocalStorage);
    } else {
      Reflect.deleteProperty(globalThis, 'localStorage');
    }
  }
});

test('default BrowserDirect credential rejects missing or excessive expiry', () => {
  expect(() => Reflect.apply(setBrowserDirectAccessToken, undefined, ['public-access-token']))
    .toThrow(/过期时间/u);
  expect(() => setBrowserDirectAccessToken(
    'public-access-token',
    new Date(Date.now() + (3 * 60 * 60 * 1000)).toISOString(),
  )).toThrow(/不能超过 2 小时/u);
});

test('BrowserDirect keeps local and public readiness credentials isolated', async () => {
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  const transport = createTransport(async (input, init) => {
    const url = String(input);
    requests.push({ url, init });
    return url.startsWith('https://db.internal')
      ? Response.json({ status: 'ok' })
      : publicJsonResponse({ status: 'ready' });
  });

  await expect(transport.probeReadiness(new AbortController().signal)).resolves.toEqual({
    local: { status: 'ready' },
    public: { status: 'ready' },
  });

  expect(requests.map((item) => item.url)).toEqual([
    'https://db.internal/healthz',
    'https://ai.example.com/v1/copilot/readiness',
  ]);
  const localHeaders = new Headers(requests[0].init?.headers);
  const publicHeaders = new Headers(requests[1].init?.headers);
  expect(localHeaders.has('Authorization')).toBe(false);
  expect(publicHeaders.get('Authorization')).toBe('Bearer public-access-token');
  expect(publicHeaders.get('X-SonnetDB-Copilot-Contract')).toBe(BrowserDirectContractVersion);
  expect(requests.every((item) => item.init?.credentials === 'omit')).toBe(true);
  expect(requests.every((item) => item.init?.redirect === 'error')).toBe(true);
});

test('BrowserDirect streams versioned public envelopes through the common state machine', async () => {
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  const events: Array<CopilotTransportEvent<CopilotChatEvent>> = [
    envelope(1, { type: 'start' }),
    envelope(2, { type: 'final', answer: 'cpu usage is stable' }),
    envelope(3, { type: 'done' }),
  ];
  const ndjson = `${events.map((item) => JSON.stringify(item)).join('\n')}\n`;
  const transport = createTransport(async (input, init) => {
    const url = String(input);
    requests.push({ url, init });
    if (url.startsWith('https://db.internal')) return Response.json({ status: 'ok' });
    if (url.endsWith('/readiness')) return publicJsonResponse({ status: 'ready' });
    return fragmentedResponse(ndjson, 'application/x-ndjson', [3, 19, 87], true);
  });
  const runtime = new CopilotRuntime('BrowserDirect', [transport]);

  const received = await collect(runtime.run(Request, { runId: 'run_browser_direct' }));

  expect(received.map((item) => item.event.type)).toEqual(['start', 'final', 'done']);
  const streamRequest = requests.at(-1);
  expect(streamRequest?.url).toBe('https://ai.example.com/v1/copilot/chat/stream');
  expect(new Headers(streamRequest?.init?.headers).get('Authorization')).toBe('Bearer public-access-token');
  expect(JSON.parse(String(streamRequest?.init?.body))).toEqual({
    contractVersion: BrowserDirectContractVersion,
    runId: 'run_browser_direct',
    request: Request,
  });
  expect(String(streamRequest?.init?.body)).not.toContain('database-token');
});

test('BrowserDirect executes a typed local MCP call then continues only after exact public echo', async () => {
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  let publicSegment = 0;
  const fetchImpl: typeof fetch = async (input, init) => {
    const url = String(input);
    requests.push({ url, init });
    if (url === 'https://db.internal/healthz') return Response.json({ status: 'ok' });
    if (url === 'https://ai.example.com/v1/copilot/readiness') return publicJsonResponse({ status: 'ready' });
    if (url === 'https://db.internal/mcp/factory') return mcpResponse(init);

    publicSegment += 1;
    if (publicSegment === 1) {
      return publicEvents([
        envelope(1, { type: 'start' }),
        envelope(
          2,
          { type: 'tool_call', toolName: 'list_measurements', toolArguments: '{"maxRows":10}' },
          'call-local-1',
        ),
      ]);
    }

    const body = JSON.parse(String(init?.body)) as {
      continuation: { toolResult: string; toolCallId: string; toolName: string };
    };
    return publicEvents([
      envelope(3, {
        type: 'tool_result',
        toolName: body.continuation.toolName,
        toolResult: body.continuation.toolResult,
      }, body.continuation.toolCallId),
      envelope(4, { type: 'final', answer: 'factory has cpu' }),
      envelope(5, { type: 'done' }),
    ]);
  };
  const transport = createToolLoopTransport(fetchImpl);

  const received = await collect(new CopilotRuntime('BrowserDirect', [transport]).run(
    Request,
    { runId: 'run_browser_direct' },
  ));

  expect(received.map((item) => item.event.type)).toEqual([
    'start', 'tool_call', 'tool_result', 'final', 'done',
  ]);
  const localRequests = requests.filter((item) => item.url === 'https://db.internal/mcp/factory');
  expect(localRequests).toHaveLength(4);
  expect(localRequests.map((item) => new Headers(item.init?.headers).get('Authorization')))
    .toEqual(Array(4).fill('Bearer database-token'));
  expect(localRequests.every((item) => JSON.stringify(item).includes('public-access-token') === false)).toBe(true);

  const publicRequests = requests.filter((item) => item.url.startsWith('https://ai.example.com'));
  expect(publicRequests.map((item) => new Headers(item.init?.headers).get('Authorization')))
    .toEqual(Array(3).fill('Bearer public-access-token'));
  expect(publicRequests.every((item) => JSON.stringify(item).includes('database-token') === false)).toBe(true);
  const continuation = JSON.parse(String(publicRequests[2].init?.body)).continuation as Record<string, unknown>;
  expect(continuation).toMatchObject({
    previousCursor: 'cursor-2',
    toolCallId: 'call-local-1',
    toolName: 'list_measurements',
  });
  expect(JSON.parse(String(continuation.toolResult))).toEqual({
    contractVersion: '1.0',
    isError: false,
    structuredContent: {
      contractVersion: '1.0',
      database: 'factory',
      measurements: ['cpu'],
      truncated: false,
    },
  });
});

test('BrowserDirect does not continue publicly after a local MCP error', async () => {
  const publicRequests: RequestInit[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const url = String(input);
    if (url === 'https://db.internal/healthz') return Response.json({ status: 'ok' });
    if (url.endsWith('/readiness')) return publicJsonResponse({ status: 'ready' });
    if (url === 'https://db.internal/mcp/factory') {
      const request = JSON.parse(String(init?.body)) as { id?: number; method: string };
      if (request.method !== 'tools/call') return mcpResponse(init);
      return Response.json({
        jsonrpc: '2.0',
        id: request.id,
        result: {
          isError: true,
          content: [
            { type: 'text', text: 'failed' },
            {
              type: 'text',
              text: JSON.stringify({
                contractVersion: '1.0',
                code: 'operation_failed',
                message: 'failed',
                retryable: false,
              }),
            },
          ],
        },
      });
    }
    publicRequests.push(init ?? {});
    return publicEvents([
      envelope(
        1,
        { type: 'tool_call', toolName: 'list_measurements', toolArguments: '{}' },
        'call-local-error',
      ),
    ]);
  };

  await expect(collect(new CopilotRuntime('BrowserDirect', [createToolLoopTransport(fetchImpl)]).run(
    Request,
    { runId: 'run_browser_direct' },
  ))).rejects.toMatchObject({ code: 'browser_direct_mcp_tool_error' });
  expect(publicRequests).toHaveLength(1);
});

test('BrowserDirect rejects generic MCP errors and incompatible typed result majors', async () => {
  const cases = [
    {
      result: { isError: true, content: [{ type: 'text', text: 'generic failure' }] },
      code: 'browser_direct_mcp_error_contract_invalid',
    },
    {
      result: {
        isError: false,
        structuredContent: {
          contractVersion: '2.0',
          database: 'factory',
          measurements: ['cpu'],
          truncated: false,
        },
      },
      code: 'browser_direct_mcp_result_contract_mismatch',
    },
  ];

  for (const item of cases) {
    let publicRequests = 0;
    const fetchImpl: typeof fetch = async (input, init) => {
      const url = String(input);
      if (url === 'https://db.internal/healthz') return Response.json({ status: 'ok' });
      if (url.endsWith('/readiness')) return publicJsonResponse({ status: 'ready' });
      if (url === 'https://db.internal/mcp/factory') {
        const request = JSON.parse(String(init?.body)) as { id?: number; method: string };
        if (request.method !== 'tools/call') return mcpResponse(init);
        return Response.json({ jsonrpc: '2.0', id: request.id, result: item.result });
      }
      publicRequests += 1;
      return publicEvents([
        envelope(
          1,
          { type: 'tool_call', toolName: 'list_measurements', toolArguments: '{}' },
          'call-invalid-local-result',
        ),
      ]);
    };

    await expect(collect(new CopilotRuntime('BrowserDirect', [createToolLoopTransport(fetchImpl)]).run(
      Request,
      { runId: 'run_browser_direct' },
    ))).rejects.toMatchObject({ code: item.code });
    expect(publicRequests).toBe(1);
  }
});

test('BrowserDirect rejects a continuation that does not exactly echo the local tool result', async () => {
  let publicSegment = 0;
  const fetchImpl: typeof fetch = async (input, init) => {
    const url = String(input);
    if (url === 'https://db.internal/healthz') return Response.json({ status: 'ok' });
    if (url.endsWith('/readiness')) return publicJsonResponse({ status: 'ready' });
    if (url === 'https://db.internal/mcp/factory') return mcpResponse(init);
    publicSegment += 1;
    return publicSegment === 1
      ? publicEvents([
        envelope(
          1,
          { type: 'tool_call', toolName: 'list_measurements', toolArguments: '{}' },
          'call-local-echo',
        ),
      ])
      : publicEvents([
        envelope(2, {
          type: 'tool_result',
          toolName: 'list_measurements',
          toolResult: '{"tampered":true}',
        }, 'call-local-echo'),
      ]);
  };

  await expect(collect(new CopilotRuntime('BrowserDirect', [createToolLoopTransport(fetchImpl)]).run(
    Request,
    { runId: 'run_browser_direct' },
  ))).rejects.toMatchObject({ code: 'browser_direct_tool_result_mismatch' });
});

test('BrowserDirect local MCP rejects unknown and non-read-only tools', async () => {
  const cases = [
    { toolName: 'sample_rows', annotations: readOnlyAnnotations(), code: 'browser_direct_mcp_tool_unknown' },
    {
      toolName: 'list_measurements',
      annotations: { ...readOnlyAnnotations(), readOnlyHint: false },
      code: 'browser_direct_mcp_tool_not_read_only',
    },
  ];
  for (const item of cases) {
    const loop = new BrowserDirectMcpToolLoop(fakeApi('https://db.internal'), 'database-token', {
      policy: { allowDataEgress: true, allowedToolNames: ['list_measurements', 'sample_rows'] },
      locationHref: 'https://studio.local/app',
      fetchImpl: async (_input, init) => mcpResponse(init, item.annotations),
    });
    await expect(loop.callTool(Request, {
      cursor: 'cursor-tool',
      toolCallId: 'call-tool',
      toolName: item.toolName,
      toolArguments: '{}',
    }, new AbortController().signal)).rejects.toMatchObject({ code: item.code });
  }
});

test('BrowserDirect reuses a cached result for a normalized duplicate tool call', async () => {
  let publicSegment = 0;
  let localToolCalls = 0;
  const toolResult = '{"contractVersion":"1.0","isError":false,"structuredContent":{"rows":[]}}';
  const transport = new BrowserDirectCopilotTransport<CopilotChatRequest, CopilotChatEvent>(
    fakeApi('https://db.internal'),
    {
      accessTokenProvider: { async getAccessToken() { return 'public-access-token'; } },
      publicBaseUrl: 'https://ai.example.com',
      approvedPublicOrigins: ['https://ai.example.com'],
      locationHref: 'https://studio.local/app',
      localToolLoop: {
        async callTool() {
          localToolCalls += 1;
          return toolResult;
        },
      },
      fetchImpl: async (input, init) => {
        const url = String(input);
        if (url.startsWith('https://db.internal')) return Response.json({ status: 'ok' });
        if (url.endsWith('/readiness')) return publicJsonResponse({ status: 'ready' });
        publicSegment += 1;
        if (publicSegment === 1) {
          return publicEvents([
            envelope(
              1,
              { type: 'tool_call', toolName: 'list_measurements', toolArguments: '{"maxRows":10}' },
              'stable-call',
            ),
          ]);
        }
        const continuation = JSON.parse(String(init?.body)).continuation as {
          toolCallId: string;
          toolName: string;
          toolResult: string;
        };
        const echo = envelope(2 * publicSegment - 2, {
          type: 'tool_result',
          toolName: continuation.toolName,
          toolResult: continuation.toolResult,
        }, continuation.toolCallId);
        if (publicSegment === 2) {
          return publicEvents([
            echo,
            envelope(
              3,
              { type: 'tool_call', toolName: 'list_measurements', toolArguments: '{ "maxRows": 10 }' },
              'stable-call',
            ),
          ]);
        }
        return publicEvents([
          echo,
          envelope(5, { type: 'final', answer: 'cached result used' }),
          envelope(6, { type: 'done' }),
        ]);
      },
    },
  );

  const received = await collect(new CopilotRuntime('BrowserDirect', [transport]).run(
    Request,
    { runId: 'run_browser_direct' },
  ));

  expect(received.map((item) => item.event.type)).toEqual([
    'tool_call', 'tool_result', 'final', 'done',
  ]);
  expect(localToolCalls).toBe(1);
  expect(publicSegment).toBe(3);
});

test('BrowserDirect rejects a conflicting repeated tool call before executing it', async () => {
  let publicSegment = 0;
  let localToolCalls = 0;
  const transport = new BrowserDirectCopilotTransport<CopilotChatRequest, CopilotChatEvent>(
    fakeApi('https://db.internal'),
    {
      accessTokenProvider: { async getAccessToken() { return 'public-access-token'; } },
      publicBaseUrl: 'https://ai.example.com',
      approvedPublicOrigins: ['https://ai.example.com'],
      locationHref: 'https://studio.local/app',
      localToolLoop: {
        async callTool() {
          localToolCalls += 1;
          return '{"contractVersion":"1.0","isError":false,"structuredContent":{}}';
        },
      },
      fetchImpl: async (input, init) => {
        const url = String(input);
        if (url.startsWith('https://db.internal')) return Response.json({ status: 'ok' });
        if (url.endsWith('/readiness')) return publicJsonResponse({ status: 'ready' });
        publicSegment += 1;
        if (publicSegment === 1) {
          return publicEvents([
            envelope(
              1,
              { type: 'tool_call', toolName: 'list_measurements', toolArguments: '{"maxRows":10}' },
              'conflict',
            ),
          ]);
        }
        const continuation = JSON.parse(String(init?.body)).continuation as {
          toolCallId: string;
          toolName: string;
          toolResult: string;
        };
        return publicEvents([
          envelope(2, {
            type: 'tool_result',
            toolName: continuation.toolName,
            toolResult: continuation.toolResult,
          }, continuation.toolCallId),
            envelope(
              3,
              { type: 'tool_call', toolName: 'list_measurements', toolArguments: '{"maxRows":11}' },
              'conflict',
            ),
        ]);
      },
    },
  );
  await expect(collect(new CopilotRuntime('BrowserDirect', [transport]).run(
    Request,
    { runId: 'run_browser_direct' },
  ))).rejects.toMatchObject({ code: 'browser_direct_tool_call_conflict' });
  expect(localToolCalls).toBe(1);
});

test('BrowserDirect local MCP rejects unapproved egress and oversized typed results', async () => {
  const disabled = new BrowserDirectMcpToolLoop(fakeApi('https://db.internal'), 'database-token', {
    policy: { allowDataEgress: false, allowedToolNames: ['list_measurements'] },
    locationHref: 'https://studio.local/app',
    fetchImpl: async () => { throw new Error('network should not be reached'); },
  });
  await expect(disabled.callTool(Request, {
    cursor: 'cursor-egress',
    toolCallId: 'call-egress',
    toolName: 'list_measurements',
    toolArguments: '{}',
  }, new AbortController().signal)).rejects.toMatchObject({ code: 'browser_direct_mcp_egress_disabled' });

  const oversized = new BrowserDirectMcpToolLoop(fakeApi('https://db.internal'), 'database-token', {
    policy: {
      allowDataEgress: true,
      allowedToolNames: ['list_measurements'],
      maximumResultBytes: 32,
    },
    locationHref: 'https://studio.local/app',
    fetchImpl: async (_input, init) => mcpResponse(init),
  });
  await expect(oversized.callTool(Request, {
    cursor: 'cursor-budget',
    toolCallId: 'call-budget',
    toolName: 'list_measurements',
    toolArguments: '{}',
  }, new AbortController().signal)).rejects.toMatchObject({ code: 'browser_direct_mcp_result_too_large' });
});

test('BrowserDirect stops before public auth when the local endpoint is unavailable', async () => {
  const requests: string[] = [];
  let tokenRequests = 0;
  const transport = new BrowserDirectCopilotTransport<CopilotChatRequest, CopilotChatEvent>(
    fakeApi('https://db.internal'),
    {
      accessTokenProvider: {
        async getAccessToken() {
          tokenRequests += 1;
          return 'public-access-token';
        },
      },
      publicBaseUrl: 'https://ai.example.com',
      approvedPublicOrigins: ['https://ai.example.com'],
      locationHref: 'https://studio.local/app',
      fetchImpl: async (input) => {
        requests.push(String(input));
        return Response.json({ status: 'down' }, { status: 503 });
      },
    },
  );

  await expect(transport.probeReadiness(new AbortController().signal)).resolves.toMatchObject({
    local: { status: 'unavailable' },
    public: { status: 'unavailable' },
  });
  expect(requests).toEqual(['https://db.internal/healthz']);
  expect(tokenRequests).toBe(0);
});

test('BrowserDirect rejects unapproved or insecure public origins before network access', () => {
  expect(() => resolveApprovedPublicBaseUrl(
    'https://evil.example.net/v1',
    ['https://ai.example.com'],
    'https://studio.local/app',
  )).toThrow(/未获批准/u);

  expect(() => resolveApprovedPublicBaseUrl(
    'http://ai.example.com',
    ['https://ai.example.com'],
    'https://studio.local/app',
  )).toThrow(/HTTPS/u);

  expect(() => resolveApprovedPublicBaseUrl(
    'https://ai.example.com',
    ['https://ai.example.com/path'],
    'https://studio.local/app',
  )).toThrow(/纯 HTTPS origin/u);

  expect(resolveApprovedPublicBaseUrl(
    'https://ai.example.com/gateway',
    ['https://ai.example.com'],
    'https://studio.local/app',
  ).href).toBe('https://ai.example.com/gateway/');
});

test('BrowserDirect preserves an approved public base path', async () => {
  const requests: string[] = [];
  const transport = new BrowserDirectCopilotTransport<CopilotChatRequest, CopilotChatEvent>(
    fakeApi('https://db.internal'),
    {
      accessTokenProvider: { async getAccessToken() { return 'public-access-token'; } },
      publicBaseUrl: 'https://ai.example.com/gateway',
      approvedPublicOrigins: ['https://ai.example.com'],
      locationHref: 'https://studio.local/app',
      fetchImpl: async (input) => {
        const url = String(input);
        requests.push(url);
        return url.startsWith('https://db.internal')
          ? Response.json({ status: 'ok' })
          : publicJsonResponse({ status: 'ready' });
      },
    },
  );

  await transport.probeReadiness(new AbortController().signal);
  expect(requests).toEqual([
    'https://db.internal/healthz',
    'https://ai.example.com/gateway/v1/copilot/readiness',
  ]);
});

test('BrowserDirect rejects malformed public envelopes and unexpected content types', async () => {
  const invalidEnvelope = createTransport(async (input) => {
    const url = String(input);
    if (url.startsWith('https://db.internal')) return Response.json({ status: 'ok' });
    if (url.endsWith('/readiness')) return publicJsonResponse({ status: 'ready' });
    return fragmentedResponse('{"type":"final"}\n', 'application/x-ndjson', [], true);
  });
  await expect(collect(new CopilotRuntime('BrowserDirect', [invalidEnvelope]).run(
    Request,
    { runId: 'run_browser_direct' },
  ))).rejects.toMatchObject({ code: 'browser_direct_envelope_invalid' });

  const htmlResponse = createTransport(async (input) => {
    const url = String(input);
    if (url.startsWith('https://db.internal')) return Response.json({ status: 'ok' });
    if (url.endsWith('/readiness')) return publicJsonResponse({ status: 'ready' });
    return new Response('<html>login</html>', {
      headers: {
        'Content-Type': 'text/html',
        'X-SonnetDB-Copilot-Contract': BrowserDirectContractVersion,
      },
    });
  });
  await expect(collect(new CopilotRuntime('BrowserDirect', [htmlResponse]).run(
    Request,
    { runId: 'run_browser_direct' },
  ))).rejects.toMatchObject({ code: 'browser_direct_content_type_invalid' });
});

test('BrowserDirect fails closed when the public endpoint omits the versioned contract', async () => {
  const transport = createTransport(async (input) => {
    const url = String(input);
    return url.startsWith('https://db.internal')
      ? Response.json({ status: 'ok' })
      : Response.json({ status: 'ready' });
  });

  await expect(transport.probeReadiness(new AbortController().signal))
    .rejects.toMatchObject({ code: 'browser_direct_contract_mismatch' });
});

function createTransport(fetchImpl: typeof fetch) {
  return new BrowserDirectCopilotTransport<CopilotChatRequest, CopilotChatEvent>(
    fakeApi('https://db.internal'),
    {
      accessTokenProvider: { async getAccessToken() { return 'public-access-token'; } },
      publicBaseUrl: 'https://ai.example.com',
      approvedPublicOrigins: ['https://ai.example.com'],
      locationHref: 'https://studio.local/app',
      fetchImpl,
    },
  );
}

function createToolLoopTransport(fetchImpl: typeof fetch) {
  const localToolLoop = new BrowserDirectMcpToolLoop(
    fakeApi('https://db.internal'),
    'database-token',
    {
      policy: { allowDataEgress: true, allowedToolNames: ['list_measurements'] },
      locationHref: 'https://studio.local/app',
      fetchImpl,
    },
  );
  return new BrowserDirectCopilotTransport<CopilotChatRequest, CopilotChatEvent>(
    fakeApi('https://db.internal'),
    {
      accessTokenProvider: { async getAccessToken() { return 'public-access-token'; } },
      publicBaseUrl: 'https://ai.example.com',
      approvedPublicOrigins: ['https://ai.example.com'],
      locationHref: 'https://studio.local/app',
      fetchImpl,
      localToolLoop,
    },
  );
}

function futureExpiry(): string {
  return new Date(Date.now() + 60_000).toISOString();
}

function fakeApi(baseUrl: string): AxiosInstance {
  return {
    getUri: ({ url }: { url?: string }) => `${baseUrl.replace(/\/+$/u, '')}/${String(url ?? '').replace(/^\/+/, '')}`,
  } as unknown as AxiosInstance;
}

function envelope(
  sequence: number,
  event: CopilotChatEvent,
  toolCallId?: string,
): CopilotTransportEvent<CopilotChatEvent> {
  return {
    runId: 'run_browser_direct',
    sequence,
    cursor: `cursor-${sequence}`,
    ...(toolCallId ? { toolCallId } : {}),
    event,
  };
}

function publicEvents(events: Array<CopilotTransportEvent<CopilotChatEvent>>): Response {
  return fragmentedResponse(
    `${events.map((item) => JSON.stringify(item)).join('\n')}\n`,
    'application/x-ndjson',
    [],
    true,
  );
}

function mcpResponse(init: RequestInit | undefined, annotations = readOnlyAnnotations()): Response {
  const request = JSON.parse(String(init?.body)) as {
    id?: number;
    method: string;
  };
  let result: unknown;
  if (request.method === 'initialize') {
    result = {
      protocolVersion: BrowserDirectMcpProtocolVersion,
      capabilities: { tools: {} },
      serverInfo: { name: 'SonnetDB', version: 'test' },
    };
  } else if (request.method === 'tools/list') {
    result = {
      tools: [{
        name: 'list_measurements',
        inputSchema: {
          type: 'object',
          properties: { maxRows: { type: ['integer', 'null'], minimum: 1, maximum: 1000 } },
          additionalProperties: false,
        },
        outputSchema: {
          type: 'object',
          required: ['contractVersion', 'database', 'measurements', 'truncated'],
          properties: {
            contractVersion: { type: 'string' },
            database: { type: 'string' },
            measurements: { type: 'array', items: { type: 'string' } },
            truncated: { type: 'boolean' },
          },
          additionalProperties: false,
        },
        annotations,
      }],
    };
  } else if (request.method === 'tools/call') {
    result = {
      isError: false,
      structuredContent: {
        contractVersion: '1.0',
        database: 'factory',
        measurements: ['cpu'],
        truncated: false,
      },
    };
  } else if (request.method === 'notifications/initialized') {
    return new Response(null, { status: 202 });
  } else {
    return Response.json({ jsonrpc: '2.0', id: request.id, error: { code: -32601 } });
  }
  return Response.json({ jsonrpc: '2.0', id: request.id, result });
}

function readOnlyAnnotations(): Record<string, unknown> {
  return {
    readOnlyHint: true,
    destructiveHint: false,
    idempotentHint: true,
    openWorldHint: false,
  };
}

function publicJsonResponse(value: unknown): Response {
  return Response.json(value, {
    headers: { 'X-SonnetDB-Copilot-Contract': BrowserDirectContractVersion },
  });
}

function fragmentedResponse(
  body: string,
  contentType: string,
  cuts: number[],
  includeContractVersion: boolean,
): Response {
  const bytes = new TextEncoder().encode(body);
  const boundaries = [...cuts.filter((cut) => cut > 0 && cut < bytes.length), bytes.length];
  let offset = 0;
  return new Response(new ReadableStream<Uint8Array>({
    start(controller) {
      for (const boundary of boundaries) {
        controller.enqueue(bytes.slice(offset, boundary));
        offset = boundary;
      }
      controller.close();
    },
  }), {
    headers: {
      'Content-Type': contentType,
      ...(includeContractVersion
        ? { 'X-SonnetDB-Copilot-Contract': BrowserDirectContractVersion }
        : {}),
    },
  });
}

async function collect<T>(values: AsyncIterable<T>): Promise<T[]> {
  const result: T[] = [];
  for await (const value of values) result.push(value);
  return result;
}
