import { createRouter, createWebHistory } from 'vue-router';
import WelcomeView from '@/views/WelcomeView.vue';
import { useAuthStore } from '@/stores/auth';
import { useSetupStore } from '@/stores/setup';

const SetupView = () => import('@/views/SetupView.vue');
const LoginView = () => import('@/views/LoginView.vue');
const AutoLoginView = () => import('@/views/AutoLoginView.vue');
const AppShell = () => import('@/views/AppShell.vue');
const DashboardView = () => import('@/views/DashboardView.vue');
const SqlConsoleView = () => import('@/views/SqlConsoleView.vue');
const EventsView = () => import('@/views/EventsView.vue');
const MonitoringView = () => import('@/views/MonitoringView.vue');
const UsersView = () => import('@/views/UsersView.vue');
const GrantsView = () => import('@/views/GrantsView.vue');
const TokensView = () => import('@/views/TokensView.vue');
const AiSettingsView = () => import('@/views/AiSettingsView.vue');
const CopilotTestView = () => import('@/views/CopilotTestView.vue');

const router = createRouter({
  history: createWebHistory('/'),
  routes: [
    // 产品官网首页（匿名可访问，单 SPA 单 base）
    { path: '/', name: 'home', component: WelcomeView, meta: { anon: true, marketing: true } },

    // /admin 入口：调转到 dashboard，交由守卫根据 setup/auth 状态选路
    { path: '/admin', redirect: { name: 'dashboard' } },

    // 首次安装 / 登录页面（匿名，但纳入 /admin 命名空间）
    { path: '/admin/setup', name: 'setup', component: SetupView, meta: { anon: true } },
    { path: '/admin/login', name: 'login', component: LoginView, meta: { anon: true } },
    { path: '/admin/auto-login', name: 'auto-login', component: AutoLoginView, meta: { anon: true } },

    // 管理后台主壳
    {
      path: '/admin/app',
      component: AppShell,
      meta: { app: true },
      redirect: { name: 'dashboard' },
      children: [
        { path: 'dashboard', name: 'dashboard', component: DashboardView },
        { path: 'studio', redirect: { name: 'sql' } },
        { path: 'sql', name: 'sql', component: SqlConsoleView },
        { path: 'trajectory-map', name: 'trajectory-map', redirect: { name: 'sql', query: { tool: 'trajectory' } } },
        { path: 'databases', name: 'databases', redirect: { name: 'sql' } },
        { path: 'events', name: 'events', component: EventsView },
        { path: 'monitoring', name: 'monitoring', component: MonitoringView },
        { path: 'users', name: 'users', component: UsersView, meta: { admin: true } },
        { path: 'grants', name: 'grants', component: GrantsView, meta: { admin: true } },
        { path: 'tokens', name: 'tokens', component: TokensView, meta: { admin: true } },
        { path: 'ai-settings', name: 'ai-settings', component: AiSettingsView, meta: { admin: true } },
        { path: 'copilot-test', name: 'copilot-test', component: CopilotTestView, meta: { admin: true } },
      ],
    },
  ],
});

router.beforeEach(async (to) => {
  const auth = useAuthStore();
  const setup = useSetupStore();

  // 产品首页是完全公开页面，不依赖 setup/auth 状态
  if (to.meta.marketing) {
    return true;
  }

  try {
    await setup.ensureLoaded();
  } catch {
    if (to.meta.app) {
      return { name: 'login' };
    }
    return true;
  }

  if (setup.needsSetup) {
    auth.apply(null);
    if (to.name === 'setup') {
      return true;
    }
    return { name: 'setup' };
  }

  if (to.name === 'setup') {
    return auth.isAuthenticated ? { name: 'dashboard' } : { name: 'login' };
  }

  if (to.name === 'login' && auth.isAuthenticated) {
    return { name: 'dashboard' };
  }

  if (to.meta.app && !auth.isAuthenticated) {
    return { name: 'login', query: { redirect: to.fullPath } };
  }

  if (to.meta.admin && !auth.isSuperuser) {
    return { name: 'dashboard' };
  }

  return true;
});

export default router;
