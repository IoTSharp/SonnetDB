import { expect, test } from '@playwright/test';
import type { AxiosInstance } from 'axios';
import { createPinia, setActivePinia } from 'pinia';
import {
  BrowserDirectContractVersion,
  BrowserDirectCopilotTransport,
  resolveApprovedPublicBaseUrl,
} from '../src/copilot/browserDirect';
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

function futureExpiry(): string {
  return new Date(Date.now() + 60_000).toISOString();
}

function fakeApi(baseUrl: string): AxiosInstance {
  return {
    getUri: ({ url }: { url?: string }) => `${baseUrl.replace(/\/+$/u, '')}/${String(url ?? '').replace(/^\/+/, '')}`,
  } as unknown as AxiosInstance;
}

function envelope(sequence: number, event: CopilotChatEvent): CopilotTransportEvent<CopilotChatEvent> {
  return {
    runId: 'run_browser_direct',
    sequence,
    cursor: `cursor-${sequence}`,
    event,
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
