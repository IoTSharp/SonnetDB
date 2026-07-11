import type { AxiosInstance } from 'axios';
import type { CopilotModelGroup } from './copilot';

export interface AiStatusResponse {
  enabled: boolean;
  isCloudBound: boolean;
}

export interface AiConfigResponse {
  enabled: boolean;
  isCloudBound: boolean;
  cloudAccessTokenExpiresAtUtc: string | null;
  cloudBoundAtUtc: string | null;
}

export interface AiCloudDeviceCodeResponse {
  deviceCode: string;
  userCode: string;
  verificationUri: string;
  verificationUriComplete: string;
  expiresIn: number;
  interval: number;
}

export interface AiCloudDeviceTokenResponse {
  authorized: boolean;
  error: string | null;
  message: string | null;
  accessTokenExpiresAtUtc: string | null;
}

export interface AiCloudModelsResponse {
  default: string;
  candidates: string[];
  groups: CopilotModelGroup[];
}

export interface AiMessage {
  role: string;
  content: string;
}

/** 获取 AI 助手启用状态（任何已认证用户）。 */
export async function fetchAiStatus(api: AxiosInstance): Promise<AiStatusResponse> {
  const resp = await api.get<AiStatusResponse>('/v1/ai/status');
  return resp.data;
}

/** 获取 AI 配置（admin only）。 */
export async function fetchAiConfig(api: AxiosInstance): Promise<AiConfigResponse> {
  const resp = await api.get<AiConfigResponse>('/v1/admin/ai-config');
  return resp.data;
}

/** 保存 AI 启用状态（admin only）。 */
export async function saveAiConfig(api: AxiosInstance, enabled: boolean): Promise<void> {
  await api.put('/v1/admin/ai-config', { enabled });
}

/** 发起 sonnetdb.com 设备码绑定（admin only）。 */
export async function createAiCloudDeviceCode(api: AxiosInstance): Promise<AiCloudDeviceCodeResponse> {
  const resp = await api.post<AiCloudDeviceCodeResponse>('/v1/admin/ai-cloud/device-code', {});
  return resp.data;
}

/** 轮询 sonnetdb.com 设备码绑定结果（admin only）。 */
export async function pollAiCloudDeviceToken(
  api: AxiosInstance,
  deviceCode: string,
): Promise<AiCloudDeviceTokenResponse> {
  const resp = await api.post<AiCloudDeviceTokenResponse>('/v1/admin/ai-cloud/device-token', { deviceCode });
  return resp.data;
}

/** 读取平台当前可用模型列表（admin only）。 */
export async function fetchAiCloudModels(api: AxiosInstance): Promise<AiCloudModelsResponse> {
  const resp = await api.get<AiCloudModelsResponse>('/v1/admin/ai-cloud/models');
  return resp.data;
}

/**
 * 流式 AI 聊天：以 AsyncGenerator 逐 token yield。
 * 使用 fetch（而非 axios）以支持 ReadableStream。
 */
export async function* streamAiChat(
  token: string,
  messages: AiMessage[],
  db?: string,
  mode = 'chat',
): AsyncGenerator<string, void, unknown> {
  const resp = await fetch('/v1/ai/chat', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${token}`,
    },
    body: JSON.stringify({ messages, db, mode }),
  });

  if (!resp.ok) {
    const text = await resp.text();
    throw new Error(`AI 请求失败 ${resp.status}: ${text}`);
  }

  const reader = resp.body?.getReader();
  if (!reader) throw new Error('无法读取 AI 响应流');

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
        if (data === '[DONE]') return;

        try {
          const obj = JSON.parse(data) as { token?: string; error?: string };
          if (obj.error) throw new Error(obj.error);
          if (obj.token) yield obj.token;
        } catch {
          // 忽略解析失败的行
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
}
