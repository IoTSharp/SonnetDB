import type { AxiosInstance } from 'axios';

export interface CopilotMessage {
  role: string;
  content: string;
  citations?: CopilotCitation[];
}

interface ParsedErrorResponse {
  code: string;
  message: string;
}

const CopilotReadinessHints: Record<string, string> = {
  disabled: 'Copilot 当前未启用，请在「Copilot 设置」中启用后再试。',
  'chat.endpoint_invalid': 'Copilot Chat 服务地址未就绪。请在「Copilot 设置」绑定 sonnetdb.com 账号后再试。',
  'chat.api_key_missing': 'Copilot 还没有 Cloud Token。请在「Copilot 设置」绑定 sonnetdb.com 账号后再试。',
  'chat.model_missing': '平台默认模型暂不可用，请在「Copilot 设置」刷新平台模型后稍后再试。',
  'chat.provider_unsupported': '当前 Copilot Chat provider 暂不支持，请使用 sonnetdb.com 官方 AI Gateway。',
  'embedding.endpoint_invalid': '云端 Copilot 知识服务暂不可用，请稍后重试。',
  'embedding.api_key_missing': '云端 Copilot 知识服务暂不可用，请稍后重试。',
  'embedding.model_missing': '云端 Copilot 知识服务暂不可用，请稍后重试。',
  'embedding.local_model_path_missing': '云端 Copilot 知识服务暂不可用，请稍后重试。',
  'embedding.local_model_not_found': '云端 Copilot 知识服务暂不可用，请稍后重试。',
  'embedding.provider_unsupported': '云端 Copilot 知识服务暂不可用，请稍后重试。',
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function stringValue(value: unknown): string {
  return typeof value === 'string' ? value.trim() : '';
}

function parseErrorObject(value: unknown): ParsedErrorResponse | null {
  if (!isRecord(value)) return null;

  const nestedError = value.error;
  if (isRecord(nestedError)) {
    return {
      code: stringValue(nestedError.code) || stringValue(nestedError.type),
      message: stringValue(nestedError.message) || stringValue(value.message),
    };
  }

  return {
    code: stringValue(nestedError) || stringValue(value.code),
    message: stringValue(value.message),
  };
}

function parseErrorResponse(text: string): ParsedErrorResponse | null {
  const body = text.trim();
  if (!body) return null;

  try {
    return parseErrorObject(JSON.parse(body));
  } catch {
    const start = body.indexOf('{');
    const end = body.lastIndexOf('}');
    if (start < 0 || end <= start) return null;

    try {
      return parseErrorObject(JSON.parse(body.slice(start, end + 1)));
    } catch {
      return null;
    }
  }
}

function extractReadinessReason(message: string): string {
  return /(?:chat|embedding)\.[a-z0-9_.]+|disabled/i.exec(message)?.[0].toLowerCase() ?? '';
}

function formatReadinessError(reason: string): string {
  return CopilotReadinessHints[reason]
    ?? 'Copilot 还没准备好，请检查「Copilot 设置」中的 sonnetdb.com 账号绑定状态。';
}

function formatProviderStatusError(status: number, providerMessage: string): string {
  if (status === 401 || status === 403) {
    return 'Copilot 模型服务拒绝了请求，请检查「Copilot 设置」中的 sonnetdb.com 账号绑定和平台权限。';
  }

  if (status === 404) {
    return 'Copilot 模型服务找不到当前模型，请检查「Copilot 设置」中的模型名是否正确。';
  }

  if (status === 429) {
    return 'Copilot 模型服务额度或频率已达上限，请稍后重试，或切换到可用额度的模型。';
  }

  if (status >= 500) {
    return providerMessage
      ? `Copilot 模型服务暂时不可用：${providerMessage}`
      : 'Copilot 模型服务暂时不可用，请稍后重试。';
  }

  return providerMessage
    ? `Copilot 模型服务返回错误：${providerMessage}`
    : 'Copilot 模型服务返回错误，请检查配置后再试。';
}

function toUserFacingCopilotError(message: string): string {
  const raw = message.trim();
  if (!raw) return 'Copilot 请求失败，请稍后重试。';

  const readinessReason = extractReadinessReason(raw);
  if (readinessReason) return formatReadinessError(readinessReason);

  if (/endpoint is not configured correctly/i.test(raw)) {
    return formatReadinessError('chat.endpoint_invalid');
  }

  if (/api key is missing/i.test(raw)) {
    return formatReadinessError('chat.api_key_missing');
  }

  if (/model is missing/i.test(raw)) {
    return formatReadinessError('chat.model_missing');
  }

  if (/empty completion/i.test(raw)) {
    return 'Copilot 模型服务没有返回内容，请稍后重试，或切换到另一个模型。';
  }

  const providerMatch = /Copilot chat provider returned\s+(\d+):\s*([\s\S]*)$/i.exec(raw);
  if (providerMatch) {
    const status = Number(providerMatch[1]);
    const payload = parseErrorResponse(providerMatch[2] ?? '');
    return formatProviderStatusError(status, payload?.message ?? '');
  }

  const payload = parseErrorResponse(raw);
  if (payload?.message) return payload.message;

  return raw;
}

async function readCopilotHttpError(resp: Response, action: string): Promise<string> {
  const text = await resp.text();
  const payload = parseErrorResponse(text);
  const code = payload?.code ?? '';
  const serverMessage = payload?.message ?? '';

  if (code === 'copilot_not_ready') {
    return formatReadinessError(extractReadinessReason(serverMessage));
  }

  if (code === 'copilot_disabled') {
    return formatReadinessError('disabled');
  }

  if (serverMessage) {
    return toUserFacingCopilotError(serverMessage);
  }

  if (resp.status === 401) return '登录状态已失效，请重新登录后再试。';
  if (resp.status === 403) return '当前账号没有执行此 Copilot 操作的权限。';
  if (resp.status === 404) return 'Copilot 请求的资源不存在，请刷新页面后再试。';
  if (resp.status === 409) return 'Copilot 当前不可用，请在「Copilot 设置」中确认已启用。';
  if (resp.status === 503) return 'Copilot 暂时不可用，请稍后重试，或检查「Copilot 设置」中的服务配置。';

  return `${action}失败（HTTP ${resp.status}）。请稍后重试。`;
}

export interface CopilotCitation {
  id: string;
  kind: string;
  title: string;
  source: string;
  snippet: string;
}

export interface CopilotChatEvent {
  type: string;
  message?: string;
  answer?: string;
  toolName?: string;
  toolArguments?: string;
  toolResult?: string;
  skillNames?: string[];
  toolNames?: string[];
  citations?: CopilotCitation[];
  attempt?: number;
}

export interface CopilotChatRequest {
  db?: string;
  messages: CopilotMessage[];
  conversationId?: string;
  cloudMode?: 'sql_assist' | 'sql_analyze' | 'db_maintenance' | 'knowledge_qa';
  /**
   * M7：权限模式。
   * - `read-only`（默认）：服务端不会自动执行需要写入确认的本地工具。
   * - `read-write`：在凭据本身具备写权限的前提下允许 execute_sql 写入。
   */
  mode?: 'read-only' | 'read-write';
}

export interface CopilotConversationSummary {
  id: string;
  title: string;
  database?: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  messageCount: number;
}

export interface CopilotMetricsSummary {
  fromUtc: string;
  toUtc: string;
  requestCount: number;
  succeededCount: number;
  failedCount: number;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  toolCalls: number;
  averageDurationMilliseconds: number;
  includesEstimatedTokens: boolean;
  models: Array<{ model: string; requestCount: number; totalTokens: number }>;
}

export async function listCopilotConversations(api: AxiosInstance): Promise<CopilotConversationSummary[]> {
  const response = await api.get<{ conversations: CopilotConversationSummary[] }>('/v1/copilot/conversations');
  return response.data.conversations ?? [];
}

export async function upsertCopilotConversation(
  api: AxiosInstance,
  request: { id?: string; title?: string; database?: string },
): Promise<CopilotConversationSummary> {
  const response = await api.post<CopilotConversationSummary>('/v1/copilot/conversations', request);
  return response.data;
}

export async function listCopilotMessages(api: AxiosInstance, conversationId: string): Promise<CopilotMessage[]> {
  const response = await api.get<{ messages: Array<CopilotMessage & { createdAtUtc: string }> }>(
    `/v1/copilot/conversations/${encodeURIComponent(conversationId)}/messages`,
  );
  return (response.data.messages ?? []).map((item) => ({
    role: item.role,
    content: item.content,
    ...(item.citations && item.citations.length > 0 ? { citations: item.citations } : {}),
  }));
}

export async function deleteCopilotConversation(api: AxiosInstance, conversationId: string): Promise<void> {
  await api.delete(`/v1/copilot/conversations/${encodeURIComponent(conversationId)}`);
}

export async function clearCopilotConversations(api: AxiosInstance): Promise<void> {
  await api.delete('/v1/copilot/conversations');
}

export async function fetchCopilotMetrics(api: AxiosInstance, windowMinutes = 60): Promise<CopilotMetricsSummary> {
  const response = await api.get<CopilotMetricsSummary>('/v1/copilot/metrics', { params: { windowMinutes } });
  return response.data;
}

export async function* streamCopilotChat(
  token: string,
  request: CopilotChatRequest,
  signal?: AbortSignal,
): AsyncGenerator<CopilotChatEvent, void, unknown> {
  let resp: Response;
  try {
    resp = await fetch('/v1/copilot/chat/stream', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify(request),
      signal,
    });
  } catch (e) {
    if (signal?.aborted) throw e;
    throw new Error('无法连接 Copilot 服务，请确认 SonnetDB 服务仍在运行并稍后重试。');
  }

  if (!resp.ok) {
    throw new Error(await readCopilotHttpError(resp, 'Copilot 请求'));
  }

  const reader = resp.body?.getReader();
  if (!reader) throw new Error('无法读取 Copilot 响应流');

  const decoder = new TextDecoder();
  let buffer = '';

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = lines.pop() ?? '';

      for (const line of lines) {
        if (!line.startsWith('data: ')) continue;
        const data = line.slice('data: '.length);
        if (!data || data === '[DONE]') {
          if (data === '[DONE]') return;
          continue;
        }

        try {
          const event = JSON.parse(data) as CopilotChatEvent;
          if (event.type === 'error' && event.message) {
            yield { ...event, message: toUserFacingCopilotError(event.message) };
          } else {
            yield event;
          }
        } catch {
          // 忽略无法解析的中间行
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
}
