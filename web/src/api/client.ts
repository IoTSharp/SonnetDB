import axios, { type AxiosInstance, type AxiosResponse } from 'axios';

const STORAGE_KEY = 'sndb.auth';
export const AuthExpiredEvent = 'sonnetdb:auth-expired';

export interface AuthState {
  username: string;
  token: string;
  tokenId: string;
  isSuperuser: boolean;
}

function readStored(): AuthState | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (typeof parsed?.token === 'string' && typeof parsed?.username === 'string') {
      return parsed as AuthState;
    }
  } catch {
    // 解析失败 → 视为未登录
  }
  return null;
}

export function persistAuth(state: AuthState | null): void {
  if (state) localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  else localStorage.removeItem(STORAGE_KEY);
}

export function loadAuth(): AuthState | null {
  return readStored();
}

export function createApiClient(getToken: () => string | null, onUnauthorized?: () => void): AxiosInstance {
  const profile = import.meta.env?.VITE_WORKBENCH_PROFILE;
  const client = axios.create({
    baseURL: import.meta.env.BASE_URL,
    timeout: 30_000,
    ...(profile && profile !== 'full' ? { adapter: async (config: Parameters<import('axios').AxiosAdapter>[0]) =>
      (await import('../preview/transport')).previewAdapter(config) } : {}),
  });
  client.interceptors.request.use((cfg) => {
    const t = getToken();
    if (t) cfg.headers.Authorization = `Bearer ${t}`;
    return cfg;
  });
  const handleUnauthorized = (response: AxiosResponse | undefined): void => {
    if (response?.status !== 401) return;
    const authorization = response.config.headers.Authorization;
    if (typeof authorization === 'string' && authorization !== `Bearer ${getToken()}`) return;
    onUnauthorized?.();
  };
  client.interceptors.response.use(
    (response) => {
      handleUnauthorized(response);
      return response;
    },
    (error: unknown) => {
      handleUnauthorized(axios.isAxiosError(error) ? error.response : undefined);
      return Promise.reject(error);
    },
  );
  return client;
}
