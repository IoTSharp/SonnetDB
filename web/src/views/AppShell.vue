<template>
  <div class="app-shell" :class="{ 'is-studio': activeKey === 'sql' }">
    <header class="app-topbar">
      <button type="button" class="app-brand" title="返回产品首页" @click="goHome">
        <BrandLogo compact />
        <span>SonnetDB Studio</span>
      </button>

      <nav class="app-breadcrumb" aria-label="当前位置">
        <span>SonnetDB</span>
        <ChevronRight :size="15" />
        <span v-if="activeKey === 'sql'">{{ activeDatabase }}</span>
        <span v-else>{{ activeTitle }}</span>
        <template v-if="activeKey === 'sql'">
          <ChevronRight :size="15" />
          <strong>{{ studioContext }}</strong>
        </template>
      </nav>

      <button type="button" class="command-search" title="命令搜索暂未开放" disabled>
        <Search :size="17" />
        <span>搜索命令</span>
        <kbd>Ctrl+K</kbd>
      </button>

      <div class="app-topbar__actions">
        <div class="readiness-slot">
          <ReadinessStatus />
        </div>
        <div class="connection-state" :class="`is-${events.status}`">
          <CircleCheck :size="16" />
          <span>连接：{{ connections.activeProfile.name }}</span>
        </div>
        <button type="button" class="topbar-icon" title="通知">
          <Bell :size="19" />
          <span class="notification-count">3</span>
        </button>
        <button type="button" class="topbar-icon" title="帮助" @click="openHelp">
          <CircleHelp :size="20" />
        </button>
        <n-dropdown trigger="click" :options="userOptions" @select="onUserAction">
          <button type="button" class="user-menu" :title="auth.username">
            <span>{{ userInitials }}</span>
            <ChevronDown :size="15" />
          </button>
        </n-dropdown>
      </div>
    </header>

    <div class="app-body">
      <nav class="module-rail" aria-label="全局模块">
        <div class="module-rail__main">
          <button
            v-for="item in primaryNavigation"
            :key="`${item.key}:${item.label}`"
            type="button"
            class="module-button"
            :class="{ 'is-active': activeKey === item.key && item.label === activeModuleLabel }"
            :title="item.label"
            @click="onMenu(item.key)"
          >
            <component :is="item.icon" :size="22" :stroke-width="1.75" />
            <span>{{ item.label }}</span>
          </button>
        </div>

        <div class="module-rail__footer">
          <button
            v-for="item in secondaryNavigation"
            :key="`${item.key}:${item.label}`"
            type="button"
            class="module-button"
            :class="{ 'is-active': activeKey === item.key }"
            :title="item.label"
            @click="onMenu(item.key)"
          >
            <component :is="item.icon" :size="22" :stroke-width="1.75" />
            <span>{{ item.label }}</span>
          </button>
        </div>
      </nav>

      <main class="app-content">
        <router-view v-slot="{ Component: RouteComponent }">
          <KeepAlive include="SqlConsoleView">
            <component :is="RouteComponent" />
          </KeepAlive>
        </router-view>
      </main>
    </div>

    <CopilotDock v-if="auth.isAuthenticated" />
  </div>
</template>

<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, watch, type Component } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { NDropdown, type DropdownOption } from 'naive-ui';
import {
  Activity,
  Bell,
  Bot,
  ChevronDown,
  ChevronRight,
  CircleCheck,
  CircleHelp,
  Database,
  FlaskConical,
  Gauge,
  KeyRound,
  LayoutDashboard,
  LogOut,
  RadioTower,
  Search,
  Settings,
  ShieldCheck,
  Users,
} from 'lucide-vue-next';
import BrandLogo from '@/components/BrandLogo.vue';
import CopilotDock from '@/components/CopilotDock.vue';
import ReadinessStatus from '@/components/ReadinessStatus.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import { useCopilotSessionsStore } from '@/stores/copilotSessions';
import { useEventsStore } from '@/stores/events';
import { useSetupStore } from '@/stores/setup';
import { CONTROL_PLANE_KEY, useSqlConsoleStore } from '@/stores/sqlConsole';

interface NavigationItem {
  label: string;
  key: string;
  icon: Component;
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const copilotSessions = useCopilotSessionsStore();
const events = useEventsStore();
const setup = useSetupStore();
const sqlConsole = useSqlConsoleStore();
const router = useRouter();
const route = useRoute();

const baseNavigation: NavigationItem[] = [
  { label: '概览', key: 'dashboard', icon: LayoutDashboard },
  { label: '查询', key: 'sql', icon: RadioTower },
  { label: '数据', key: 'sql', icon: Database },
  { label: 'Studio', key: 'sql', icon: FlaskConical },
  { label: '事件', key: 'events', icon: Activity },
  { label: '监控', key: 'monitoring', icon: Gauge },
];

const adminNavigation: NavigationItem[] = [
  { label: '用户', key: 'users', icon: Users },
  { label: '权限', key: 'grants', icon: ShieldCheck },
  { label: 'Token', key: 'tokens', icon: KeyRound },
  { label: 'Copilot', key: 'ai-settings', icon: Bot },
];

const primaryNavigation = computed(() => baseNavigation);
const secondaryNavigation = computed(() => [
  ...(auth.isSuperuser ? adminNavigation : []),
  { label: '设置', key: auth.isSuperuser ? 'ai-settings' : 'dashboard', icon: Settings },
]);

const titleByKey: Record<string, string> = {
  dashboard: '概览',
  sql: 'Studio',
  events: '事件流',
  monitoring: '监控',
  users: '用户',
  grants: '权限',
  tokens: 'Token',
  'ai-settings': 'Copilot',
  'copilot-test': 'Copilot 测试',
};

const toolLabels: Record<string, string> = {
  sql: 'SQL 查询',
  table: '关系表',
  document: '文档集合',
  kv: 'KV Keyspace',
  mq: 'MQ Topic',
  vector: '向量索引',
  fulltext: '全文索引',
  bucket: '对象桶',
  trajectory: '轨迹分析',
};

const activeKey = computed(() => (route.name as string | undefined) ?? 'dashboard');
const activeTitle = computed(() => titleByKey[activeKey.value] ?? 'SonnetDB');
const activeModuleLabel = computed(() => activeKey.value === 'sql' ? 'Studio' : activeTitle.value.replace('流', ''));
const activeDatabase = computed(() => {
  const db = sqlConsole.activeTab?.db;
  if (!db || db === CONTROL_PLANE_KEY) return 'system';
  return db;
});
const studioContext = computed(() => toolLabels[String(route.query.tool ?? 'sql')] ?? 'SQL 查询');
const userInitials = computed(() => auth.username.trim().slice(0, 2).toUpperCase() || 'AD');

const userOptions: DropdownOption[] = [
  { label: '产品首页', key: 'home' },
  { label: '帮助文档', key: 'help' },
  { type: 'divider', key: 'user-divider' },
  { label: '退出登录', key: 'logout', icon: () => h(LogOut, { size: 16 }) },
];

function onMenu(key: string): void {
  void router.push({ name: key });
}

function goHome(): void {
  void router.push({ name: 'home' });
}

function openHelp(): void {
  const popup = window.open('/help/', '_blank', 'noopener,noreferrer');
  if (!popup) window.location.assign('/help/');
}

function onUserAction(key: string | number): void {
  if (key === 'home') goHome();
  if (key === 'help') openHelp();
  if (key === 'logout') onLogout();
}

function onLogout(): void {
  events.disconnect();
  auth.logout();
  void router.replace({ name: 'login' });
}

function hideControlPlaneForRegularUser(): void {
  if (!auth.isAuthenticated || auth.isSuperuser) return;
  sqlConsole.hideControlPlaneForRegularUser();
  copilotSessions.hideControlPlaneForRegularUser();
}

watch(() => [auth.isAuthenticated, auth.isSuperuser] as const, hideControlPlaneForRegularUser, { immediate: true });
watch(() => connections.activeBaseUrl, (baseUrl) => auth.setApiBaseUrl(baseUrl), { immediate: true });

onMounted(async () => {
  await setup.ensureLoaded();
  if (auth.isAuthenticated) events.connect();
});

onBeforeUnmount(() => {
  // 由 logout 显式断开 SSE，避免 SPA 内部切换路由造成短暂闪断。
});
</script>

<style scoped>
.app-shell {
  display: grid;
  grid-template-rows: 56px minmax(0, 1fr);
  height: 100vh;
  overflow: hidden;
  background: var(--sndb-app-canvas);
}

.app-topbar {
  position: relative;
  z-index: 20;
  display: grid;
  grid-template-columns: 262px minmax(260px, 1fr) minmax(260px, 340px) auto;
  align-items: center;
  min-width: 0;
  border-bottom: 1px solid var(--sndb-border);
  background: rgba(252, 252, 253, 0.96);
}

.app-brand {
  display: flex;
  align-items: center;
  gap: 10px;
  height: 100%;
  padding: 0 18px;
  border: 0;
  background: transparent;
  color: var(--sndb-ink-strong);
  font-size: 16px;
  font-weight: 650;
  cursor: pointer;
}

.app-brand :deep(.brand-mark) {
  width: 30px;
  height: 30px;
  border-radius: 6px;
  box-shadow: none;
}

.app-breadcrumb {
  display: flex;
  align-items: center;
  gap: 7px;
  min-width: 0;
  padding: 0 18px;
  color: var(--sndb-ink-muted);
  font-size: 14px;
  white-space: nowrap;
}

.app-breadcrumb strong {
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-weight: 550;
  text-overflow: ellipsis;
}

.command-search {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  height: 34px;
  padding: 0 9px;
  border: 1px solid var(--sndb-border-strong);
  border-radius: 5px;
  background: #fff;
  color: var(--sndb-ink-muted);
  font: inherit;
  text-align: left;
}

.command-search span {
  flex: 1;
}

.command-search kbd {
  padding: 2px 6px;
  border: 1px solid var(--sndb-border);
  border-radius: 4px;
  background: var(--sndb-chrome);
  color: var(--sndb-ink-subtle);
  font: 12px inherit;
}

.app-topbar__actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 6px;
  height: 100%;
  padding: 0 14px;
}

.readiness-slot {
  display: flex;
  align-items: center;
}

.connection-state {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  padding: 0 12px;
  border-right: 1px solid var(--sndb-border);
  color: var(--sndb-ink-soft);
  font-size: 13px;
  white-space: nowrap;
}

.connection-state svg {
  color: var(--sndb-success);
}

.connection-state.is-error svg,
.connection-state.is-unauthorized svg {
  color: var(--sndb-warning);
}

.topbar-icon,
.user-menu {
  position: relative;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--sndb-ink-strong);
  cursor: pointer;
}

.topbar-icon:hover,
.user-menu:hover {
  background: var(--sndb-hover);
}

.notification-count {
  position: absolute;
  top: 1px;
  right: 0;
  min-width: 16px;
  height: 16px;
  padding: 0 4px;
  border: 2px solid #fff;
  border-radius: 8px;
  background: var(--sndb-danger);
  color: #fff;
  font-size: 10px;
  line-height: 12px;
}

.user-menu {
  width: auto;
  gap: 5px;
  padding: 0 7px;
}

.user-menu > span {
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border: 1px solid var(--sndb-border-strong);
  border-radius: 50%;
  background: #eef3f8;
  font-size: 12px;
  font-weight: 650;
}

.app-body {
  display: grid;
  grid-template-columns: 64px minmax(0, 1fr);
  min-width: 0;
  min-height: 0;
}

.module-rail {
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  min-height: 0;
  overflow-y: auto;
  border-right: 1px solid var(--sndb-border);
  background: rgba(249, 250, 252, 0.96);
}

.module-rail__main,
.module-rail__footer {
  display: flex;
  flex-direction: column;
  padding: 7px 0;
}

.module-button {
  position: relative;
  display: flex;
  flex: 0 0 64px;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 4px;
  width: 100%;
  min-height: 58px;
  padding: 4px;
  border: 0;
  background: transparent;
  color: #3f4a52;
  font: inherit;
  cursor: pointer;
}

.module-button span {
  font-size: 11px;
}

.module-button:hover {
  background: var(--sndb-hover);
}

.module-button.is-active {
  background: #edf4fb;
  color: var(--sndb-interactive);
}

.module-button.is-active::before {
  position: absolute;
  inset: 8px auto 8px 0;
  width: 3px;
  border-radius: 0 2px 2px 0;
  background: var(--sndb-interactive);
  content: '';
}

.app-content {
  min-width: 0;
  min-height: 0;
  overflow: auto;
  padding: 24px;
}

.is-studio .app-content {
  overflow: hidden;
  padding: 0;
}

@media (max-width: 1180px) {
  .app-topbar {
    grid-template-columns: 230px minmax(200px, 1fr) auto;
  }

  .command-search {
    display: none;
  }
}

@media (max-width: 760px) {
  .app-topbar {
    grid-template-columns: 56px minmax(0, 1fr) auto;
  }

  .app-brand {
    justify-content: center;
    padding: 0;
  }

  .app-brand > span,
  .readiness-slot,
  .connection-state,
  .app-topbar__actions .topbar-icon {
    display: none;
  }

  .app-breadcrumb {
    padding: 0 10px;
  }

  .app-body {
    grid-template-columns: 56px minmax(0, 1fr);
  }

  .module-button {
    flex-basis: 56px;
    min-height: 54px;
  }

  .module-button span {
    display: none;
  }

  .app-content {
    padding: 14px;
  }
}
</style>
