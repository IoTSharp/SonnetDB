import { defineStore } from 'pinia';
import { computed, markRaw, shallowRef } from 'vue';
import type { AxiosInstance } from 'axios';
import { createApiClient, loadAuth, persistAuth, type AuthState } from '@/api/client';

export const useAuthStore = defineStore('auth', () => {
  // 注意：state 必须用 shallowRef，否则 Pinia 会把 ref 内容做 reactive 代理；
  // 同时 api 这种 axios 实例（可调用对象 + 方法属性）必须用 shallowRef + markRaw 包装，
  // 否则 Pinia setup store 的返回值检测会把它当作 action 包装为新函数，
  // 导致 `auth.api.get/post/put` 等方法属性全部丢失（typeof === 'function' → action）。
  const state = shallowRef<AuthState | null>(loadAuth());
  const api = shallowRef<AxiosInstance>(
    markRaw(createApiClient(() => state.value?.token ?? null)),
  );

  const isAuthenticated = computed(() => state.value !== null);
  const username = computed(() => state.value?.username ?? '');
  const isSuperuser = computed(() => state.value?.isSuperuser ?? false);

  function apply(nextState: AuthState | null): void {
    state.value = nextState;
    persistAuth(nextState);
  }

  async function login(username: string, password: string): Promise<void> {
    const resp = await api.value.post<AuthState>('/v1/auth/login', { username, password });
    apply(resp.data);
  }

  function logout(): void {
    apply(null);
  }

  function setApiBaseUrl(baseUrl: string): void {
    api.value.defaults.baseURL = baseUrl || '/';
  }

  return { state, api, isAuthenticated, username, isSuperuser, apply, login, logout, setApiBaseUrl };
});
