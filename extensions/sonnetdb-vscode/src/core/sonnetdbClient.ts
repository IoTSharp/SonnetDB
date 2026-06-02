import {
  CopilotChatEvent,
  CopilotModelsResponse,
  DatabaseListResponse,
  HealthResponse,
  SchemaResponse,
  SqlResultSet,
} from './types';

export class SonnetDbClient {
  public constructor(
    private readonly baseUrl: string,
    private readonly token?: string,
  ) {}

  public async checkHealth(): Promise<HealthResponse> {
    return this.getJson<HealthResponse>('/healthz');
  }

  public async listDatabases(): Promise<DatabaseListResponse> {
    return this.getJson<DatabaseListResponse>('/v1/db');
  }

  public async fetchSchema(database: string): Promise<SchemaResponse> {
    return this.getJson<SchemaResponse>(`/v1/db/${encodeURIComponent(database)}/schema`);
  }

  public async fetchCopilotModels(): Promise<CopilotModelsResponse> {
    return this.getJson<CopilotModelsResponse>('/v1/copilot/models');
  }

  public async executeSql(database: string, sql: string): Promise<SqlResultSet> {
    const response = await fetch(
      this.toUrl(`/v1/db/${encodeURIComponent(database)}/sql`),
      {
        method: 'POST',
        headers: this.buildHeaders({
          'Content-Type': 'application/json',
        }),
        body: JSON.stringify({ sql }),
      },
    );

    const contentType = response.headers.get('content-type') ?? '';
    const body = await response.text();

    if (contentType.includes('ndjson')) {
      return parseNdjson(body);
    }

    return {
      columns: [],
      rows: [],
      end: null,
      error: {
        code: `http_${response.status}`,
        message: body || `HTTP ${response.status}`,
      },
      hasColumns: false,
    };
  }

  public async streamCopilot(
    database: string,
    messages: Array<{ role: string; content: string }>,
    onEvent: (event: CopilotChatEvent) => void,
    mode: 'read-only' | 'read-write' = 'read-only',
    model?: string,
  ): Promise<void> {
    const response = await fetch(
      this.toUrl('/v1/copilot/chat/stream'),
      {
        method: 'POST',
        headers: this.buildHeaders({
          'Content-Type': 'application/json',
        }),
        body: JSON.stringify({
          db: database,
          messages,
          mode,
          model,
        }),
      },
    );

    if (!response.body) {
      throw new Error(`Copilot stream is unavailable: HTTP ${response.status}`);
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let pending = '';

    while (true) {
      const chunk = await reader.read();
      if (chunk.done) {
        break;
      }

      pending += decoder.decode(chunk.value, { stream: true });
      const blocks = pending.split('\n\n');
      pending = blocks.pop() ?? '';

      for (const block of blocks) {
        const line = block
          .split('\n')
          .find((value) => value.startsWith('data: '));

        if (!line) {
          continue;
        }

        const payload = line.slice('data: '.length).trim();
        if (payload === '[DONE]') {
          continue;
        }

        onEvent(JSON.parse(payload) as CopilotChatEvent);
      }
    }
  }

  private async getJson<T>(path: string): Promise<T> {
    const response = await fetch(this.toUrl(path), {
      headers: this.buildHeaders(),
    });

    if (!response.ok) {
      throw new Error(`Request failed: ${response.status} ${response.statusText}`);
    }

    return (await response.json()) as T;
  }

  private toUrl(path: string): string {
    return `${this.baseUrl.replace(/\/+$/u, '')}${path}`;
  }

  private buildHeaders(extraHeaders?: Record<string, string>): HeadersInit {
    const headers: Record<string, string> = {
      Accept: 'application/json',
      ...extraHeaders,
    };

    if (this.token) {
      headers.Authorization = `Bearer ${this.token}`;
    }

    return headers;
  }
}

export function parseNdjson(body: string): SqlResultSet {
  const result: SqlResultSet = {
    columns: [],
    rows: [],
    end: null,
    error: null,
    hasColumns: false,
  };

  const lines = body.split(/\r?\n/u).filter((line) => line.length > 0);
  for (const line of lines) {
    let parsed: unknown;
    try {
      parsed = JSON.parse(line);
    } catch {
      continue;
    }

    if (Array.isArray(parsed)) {
      result.rows.push(parsed);
      continue;
    }

    if (!parsed || typeof parsed !== 'object') {
      continue;
    }

    const record = parsed as Record<string, unknown>;
    if (record.type === 'meta' && Array.isArray(record.columns)) {
      result.columns = record.columns as string[];
      result.hasColumns = true;
      continue;
    }

    if (record.type === 'end') {
      result.end = record as unknown as SqlResultSet['end'];
      continue;
    }

    if (typeof record.message === 'string') {
      result.error = {
        code: typeof record.code === 'string' ? record.code : undefined,
        message: record.message,
      };
    }
  }

  return result;
}
