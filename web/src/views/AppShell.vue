<template>
  <n-layout has-sider class="app-shell">
    <n-layout-sider
      bordered
      collapse-mode="width"
      :collapsed-width="74"
      :width="250"
      :native-scrollbar="false"
      class="app-sider"
    >
      <div class="brand-pane">
        <BrandLogo :compact="false" />
        <div class="brand-meta">
          <strong>{{ setup.organization ?? 'SonnetDB Organization' }}</strong>
          <span>{{ setup.serverId ?? '未命名服务器' }}</span>
        </div>
      </div>

      <n-menu :options="menuOptions" :value="activeKey" @update:value="onMenu" />
    </n-layout-sider>

    <n-layout>
      <n-layout-header bordered class="app-header">
        <div class="header-left">
          <button type="button" class="header-home" @click="goHome">产品首页</button>
          <button type="button" class="header-home" @click="openHelp">帮助</button>
          <span class="header-title">{{ activeTitle }}</span>
        </div>

        <n-space align="center">
          <n-tag :type="liveTagType" size="small">
            <template #icon>
              <span class="dot" :class="liveDotClass" />
            </template>
            {{ liveLabel }}
          </n-tag>
          <n-tag :type="auth.isSuperuser ? 'success' : 'info'" size="small">
            {{ auth.username }}{{ auth.isSuperuser ? ' / admin' : '' }}
          </n-tag>
          <n-button text type="error" @click="onLogout">退出</n-button>
        </n-space>
      </n-layout-header>

      <n-layout-content :content-style="contentStyle">
        <router-view v-slot="{ Component }">
          <KeepAlive include="SqlConsoleView">
            <component :is="Component" />
          </KeepAlive>
        </router-view>
      </n-layout-content>
    </n-layout>
    <CopilotDock v-if="auth.isAuthenticated" />
  </n-layout>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import {
  NButton,
  NLayout,
  NLayoutContent,
  NLayoutHeader,
  NLayoutSider,
  NMenu,
  NSpace,
  NTag,
  type MenuOption,
} from 'naive-ui';
import BrandLogo from '@/components/BrandLogo.vue';
import CopilotDock from '@/components/CopilotDock.vue';
import { useAuthStore } from '@/stores/auth';
import { useCopilotSessionsStore } from '@/stores/copilotSessions';
import { useEventsStore } from '@/stores/events';
import { useSetupStore } from '@/stores/setup';
import { useSqlConsoleStore } from '@/stores/sqlConsole';

const auth = useAuthStore();
const copilotSessions = useCopilotSessionsStore();
const events = useEventsStore();
const setup = useSetupStore();
const sqlConsole = useSqlConsoleStore();
const router = useRouter();
const route = useRoute();

const baseMenu: MenuOption[] = [
  { label: '概览', key: 'dashboard' },
  { label: 'Studio', key: 'sql' },
  { label: '事件流', key: 'events' },
  { label: '监控', key: 'monitoring' },
];

const adminMenu: MenuOption[] = [
  { label: '用户', key: 'users' },
  { label: '权限', key: 'grants' },
  { label: 'Token', key: 'tokens' },
  { label: 'Copilot', key: 'ai-settings' },
];

const menuOptions = computed<MenuOption[]>(() => (
  auth.isSuperuser ? [...baseMenu, ...adminMenu] : baseMenu
));

const titleByKey: Record<string, string> = {
  dashboard: '概览',
  sql: 'Studio',
  events: '事件流',
  monitoring: '监控',
  users: '用户',
  grants: '权限',
  tokens: 'Token',
  'ai-settings': 'Copilot',
};

const activeKey = computed(() => (route.name as string | undefined) ?? 'dashboard');
const activeTitle = computed(() => titleByKey[activeKey.value] ?? '');
const contentStyle = computed(() => (activeKey.value === 'sql'
  ? 'padding:16px 16px 18px;'
  : 'padding:24px;'));

const liveLabel = computed(() => {
  switch (events.status) {
    case 'open': return '实时在线';
    case 'connecting': return '连接中';
    case 'error': return '重连中';
    case 'unauthorized': return 'SSE 未授权';
    default: return '未连接';
  }
});

const liveTagType = computed(() => {
  switch (events.status) {
    case 'open': return 'success' as const;
    case 'connecting': return 'info' as const;
    case 'error':
    case 'unauthorized': return 'warning' as const;
    default: return 'default' as const;
  }
});

const liveDotClass = computed(() => `dot-${events.status}`);

function onMenu(key: string): void {
  router.push({ name: key });
}

function goHome(): void {
  router.push({ name: 'home' });
}

function openHelp(): void {
  const popup = window.open('/help/', '_blank', 'noopener,noreferrer');
  if (!popup) {
    window.location.assign('/help/');
  }
}

function onLogout(): void {
  events.disconnect();
  auth.logout();
  router.replace({ name: 'login' });
}

function hideControlPlaneForRegularUser(): void {
  if (!auth.isAuthenticated || auth.isSuperuser) return;
  sqlConsole.hideControlPlaneForRegularUser();
  copilotSessions.hideControlPlaneForRegularUser();
}

watch(() => [auth.isAuthenticated, auth.isSuperuser] as const, hideControlPlaneForRegularUser, { immediate: true });

onMounted(async () => {
  await setup.ensureLoaded();
  if (auth.isAuthenticated) {
    events.connect();
  }
});

onBeforeUnmount(() => {
  // 由 logout 显式断开 SSE，避免 SPA 内部切换路由造成短暂闪断。
});
</script>

<style scoped>
.app-shell {
  height: 100vh;
}

.app-sider {
  background:
    linear-gradient(180deg, rgba(248, 251, 255, 0.98), rgba(238, 245, 249, 0.98));
}

.brand-pane {
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 20px 18px 16px;
}

.brand-meta {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 14px;
  border-radius: 18px;
  background: rgba(13, 59, 102, 0.04);
  color: var(--sndb-ink-soft);
  font-size: 0.88rem;
}

.brand-meta strong {
  color: var(--sndb-ink-strong);
}

.app-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 24px;
  height: 62px;
  background: rgba(248, 251, 255, 0.82);
  backdrop-filter: blur(12px);
}

.header-left {
  display: flex;
  align-items: center;
  gap: 14px;
}

.header-home {
  border: 0;
  padding: 8px 12px;
  border-radius: 999px;
  background: rgba(13, 59, 102, 0.06);
  color: var(--sndb-ink-soft);
  font: inherit;
  cursor: pointer;
}

.header-title {
  font-size: 1.02rem;
  font-weight: 600;
}

.dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  margin-right: 4px;
  vertical-align: middle;
  background: #c0c0c0;
}

.dot-open {
  background: #18a058;
  box-shadow: 0 0 0 2px rgba(24, 160, 88, 0.18);
}

.dot-connecting {
  background: #2080f0;
}

.dot-error,
.dot-unauthorized {
  background: #f0a020;
}
</style>
