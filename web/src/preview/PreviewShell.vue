<template>
  <div class="preview-shell">
    <header><strong>SonnetDB · Workbench Preview 1</strong><span>{{ auth.username }}</span><button @click="logout">退出登录</button></header>
    <nav aria-label="全局模块">
      <button v-for="item in modules" :key="item.label" :disabled="!item.route" :title="item.route ? item.label : '本预览未开放'" @click="item.route && router.push(item.route)">{{ item.label }}<small v-if="!item.route">本预览未开放</small></button>
    </nav>
    <main><p v-if="previewSession.message" role="status">{{ previewSession.message }}</p><router-view /></main>
  </div>
</template>
<script setup lang="ts">
import { onBeforeUnmount, watch } from 'vue';
import { useRouter } from 'vue-router';
import { useAuthStore } from '@/stores/auth';
import { useWorkbenchHistoryStore } from '@/stores/workbenchHistory';
import { clearPreviewSession, previewSession } from './session';
const auth = useAuthStore();
const router = useRouter();
const history = useWorkbenchHistoryStore();
const modules = [
  { label: '概览', route: '/admin/app/dashboard' }, { label: '工作台', route: '/admin/app/sql' },
  { label: '观测', route: '' }, { label: '数据流', route: '' }, { label: 'AI 与 MCP', route: '' },
  { label: '治理', route: '' }, { label: '设置 · About', route: '/admin/app/about' },
];
auth.setApiBaseUrl('/');
watch(() => previewSession.value.generation, () => history.clear(), { flush: 'sync', immediate: true });
function logout() { auth.logout(); void router.replace('/admin/login'); }
function storage(event: StorageEvent) { if (event.key === 'sndb.auth') logout(); }
function offline() { clearPreviewSession('连接已断开；载荷与审批已清除。重连后请显式重读，写入不会重放。', true); }
window.addEventListener('storage', storage);
window.addEventListener('offline', offline);
onBeforeUnmount(() => { window.removeEventListener('storage', storage); window.removeEventListener('offline', offline); clearPreviewSession(); });
</script>
<style scoped>
.preview-shell { height: 100vh; display: grid; grid-template: 52px minmax(0, 1fr) / 112px minmax(0, 1fr); background: var(--sndb-app-canvas); }
header { grid-column: 1 / 3; display: flex; gap: 20px; align-items: center; padding: 0 20px; border-bottom: 1px solid var(--sndb-border); }
header strong { flex: 1; } nav { display: flex; flex-direction: column; gap: 8px; padding: 12px 4px; border-right: 1px solid var(--sndb-border); }
button { font: inherit; border: 1px solid var(--sndb-border); border-radius: 4px; padding: 8px; background: var(--sndb-surface-strong); color: var(--sndb-ink); cursor: pointer; }
button:disabled { cursor: default; opacity: .55; } small { display: block; font-size: 10px; } main { min-width: 0; min-height: 0; overflow: auto; } p[role=status] { padding: 10px 20px; background: #fff2d6; color: #614700; }
</style>
