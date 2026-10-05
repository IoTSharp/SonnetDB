import axios, { type AxiosInstance } from 'axios';

const STORAGE_KEY = 'sndb.auth';

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

export function createApiClient(getToken: () => string | null): AxiosInstance {
  const client = axios.create({
    baseURL: import.meta.env.BASE_URL,
    timeout: 30_000,
  });
  client.interceptors.request.use((cfg) => {
    const t = getToken();
    if (t) cfg.headers.Authorization = `Bearer ${t}`;
    return cfg;
  });
  return client;
}
