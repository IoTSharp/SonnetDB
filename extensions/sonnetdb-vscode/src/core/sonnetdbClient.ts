import {
  BulkIngestResponse,
  CopilotChatEvent,
  CopilotKnowledgeStatusResponse,
  CopilotModelsResponse,
  DatabaseListResponse,
  FullTextAnalyzeRequest,
  FullTextAnalyzeResponse,
  FullTextIndexStat,
  FullTextIndexStatResponse,
  FullTextSearchPreviewRequest,
  FullTextSearchPreviewResponse,
  HealthResponse,
  KvKeyspaceListResponse,
  KvScanCursorRequest,
  KvScanCursorResponse,
  MqBrowseRequest,
  MqBrowseResponse,
  MqMonitorResponse,
  MqTopicInfo,
  MqTopicListResponse,
  SchemaResponse,
  SetupStatusResponse,
  SqlResultSet,
  VectorEmbedPreviewResponse,
  VectorIndexStat,
  VectorIndexStatResponse,
  VectorSearchPreviewRequest,
  VectorSearchPreviewResponse,
} from './types';

export class SonnetDbClient {
  public constructor(
    private readonly baseUrl: string,
    private readonly token?: string,
  ) {}

  public async checkHealth(): Promise<HealthResponse> {
    return this.getJson<HealthResponse>('/healthz');
  }

  public async fetchSetupStatus(): Promise<SetupStatusResponse> {
    return this.getJson<SetupStatusResponse>('/v1/setup/status');
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

  public async fetchCopilotKnowledgeStatus(): Promise<CopilotKnowledgeStatusResponse> {
    return this.getJson<CopilotKnowledgeStatusResponse>('/v1/copilot/knowledge/status');
  }

  public async fetchKvKeyspaces(database: string): Promise<string[]> {
    const response = await this.postJson<KvKeyspaceListResponse>(
      `/v1/db/${encodeURIComponent(database)}/kv/keyspaces`,
    );
    return Array.isArray(response.keyspaces) ? response.keyspaces : [];
  }

  public async scanKvEntries(
    database: string,
    keyspace: string,
    request: KvScanCursorRequest = {},
  ): Promise<KvScanCursorResponse> {
    const response = await this.postJson<KvScanCursorResponse>(
      `/v1/db/${encodeURIComponent(database)}/kv/${encodeURIComponent(keyspace)}/scan`,
      request,
    );
    return {
      entries: Array.isArray(response.entries) ? response.entries : [],
      nextCursor: response.nextCursor ?? null,
      hasMore: Boolean(response.hasMore),
    };
  }

  public async fetchVectorIndexes(database: string): Promise<VectorIndexStat[]> {
    const response = await this.postJson<VectorIndexStatResponse>(
      `/v1/db/${encodeURIComponent(database)}/vector/indexes`,
    );
    return Array.isArray(response.indexes) ? response.indexes : [];
  }

  public async searchVectorPreview(
    database: string,
    request: VectorSearchPreviewRequest,
  ): Promise<VectorSearchPreviewResponse> {
    const response = await this.postJson<VectorSearchPreviewResponse>(
      `/v1/db/${encodeURIComponent(database)}/vector/search-preview`,
      request,
    );
    return {
      hits: Array.isArray(response.hits) ? response.hits : [],
    };
  }

  public async embedVectorText(database: string, text: string): Promise<VectorEmbedPreviewResponse> {
    return this.postJson<VectorEmbedPreviewResponse>(
      `/v1/db/${encodeURIComponent(database)}/vector/embed-preview`,
      { text },
    );
  }

  public async fetchFullTextIndexes(database: string): Promise<FullTextIndexStat[]> {
    const response = await this.postJson<FullTextIndexStatResponse>(
      `/v1/db/${encodeURIComponent(database)}/fulltext/indexes`,
    );
    return Array.isArray(response.indexes) ? response.indexes : [];
  }

  public async searchFullTextPreview(
    database: string,
    request: FullTextSearchPreviewRequest,
  ): Promise<FullTextSearchPreviewResponse> {
    const response = await this.postJson<FullTextSearchPreviewResponse>(
      `/v1/db/${encodeURIComponent(database)}/fulltext/search-preview`,
      request,
    );
    return {
      hits: Array.isArray(response.hits) ? response.hits : [],
    };
  }

  public async analyzeFullText(
    database: string,
    request: FullTextAnalyzeRequest,
  ): Promise<FullTextAnalyzeResponse> {
    const response = await this.postJson<FullTextAnalyzeResponse>(
      `/v1/db/${encodeURIComponent(database)}/fulltext/analyze`,
      request,
    );
    return {
      tokens: Array.isArray(response.tokens) ? response.tokens : [],
    };
  }

  public async fetchMqTopics(database: string): Promise<MqTopicInfo[]> {
    const response = await this.postJson<MqTopicListResponse>(
      `/v1/db/${encodeURIComponent(database)}/mq/topics`,
    );
    return Array.isArray(response.topics) ? response.topics : [];
  }

  public async browseMqMessages(
    database: string,
    topic: string,
    request: MqBrowseRequest = {},
  ): Promise<MqBrowseResponse> {
    const response = await this.postJson<MqBrowseResponse>(
      `/v1/db/${encodeURIComponent(database)}/mq/${encodeURIComponent(topic)}/browse`,
      request,
    );
    return {
      messages: Array.isArray(response.messages) ? response.messages : [],
    };
  }

  public async fetchMqMonitor(database: string, topic: string): Promise<MqMonitorResponse> {
    return this.postJson<MqMonitorResponse>(
      `/v1/db/${encodeURIComponent(database)}/mq/${encodeURIComponent(topic)}/monitor`,
    );
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

  public async ingestBulk(
    database: string,
    measurement: string,
    format: 'lp' | 'json' | 'bulk',
    payload: Uint8Array,
  ): Promise<BulkIngestResponse> {
    const response = await fetch(
      this.toUrl(`/v1/db/${encodeURIComponent(database)}/measurements/${encodeURIComponent(measurement)}/${format}`),
      {
        method: 'POST',
        headers: this.buildHeaders({
          'Content-Type': format === 'json' ? 'application/json' : 'text/plain; charset=utf-8',
        }),
        body: Buffer.from(payload),
      },
    );
    const body = await response.text();
    if (!response.ok) {
      throw new Error(body || `HTTP ${response.status}`);
    }
    return JSON.parse(body) as BulkIngestResponse;
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

        try {
          onEvent(JSON.parse(payload) as CopilotChatEvent);
        } catch {
          onEvent({ type: 'message', message: payload });
        }
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

  private async postJson<T>(path: string, body: unknown = {}): Promise<T> {
    const response = await fetch(this.toUrl(path), {
      method: 'POST',
      headers: this.buildHeaders({
        'Content-Type': 'application/json',
      }),
      body: JSON.stringify(body),
    });

    if (!response.ok) {
      const text = await response.text();
      throw new Error(text || `Request failed: ${response.status} ${response.statusText}`);
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
