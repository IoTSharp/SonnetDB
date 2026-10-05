<template>
  <div class="login-page">
    <section class="login-hero">
      <BrandLogo light />
      <div class="login-copy">
        <span class="login-kicker">Control Console</span>
        <h1>登录管理后台，接管数据库、用户、权限与实时事件。</h1>
        <p>
          当前实例
          <code>{{ setup.serverId ?? '未命名服务器' }}</code>
          属于
          <code>{{ setup.organization ?? '未命名组织' }}</code>。
          登录成功后会自动进入管理控制台。
        </p>
      </div>
    </section>

    <section class="login-panel">
      <div class="login-card">
        <button type="button" class="home-link" @click="goHome">返回首页</button>
        <h2>管理登录</h2>
        <p class="login-intro">使用首次安装时创建的管理员用户名和密码登录。</p>

        <n-form @submit.prevent="onSubmit">
          <n-form-item label="用户名">
            <n-input v-model:value="username" placeholder="admin" autofocus />
          </n-form-item>
          <n-form-item label="密码">
            <n-input
              v-model:value="password"
              type="password"
              show-password-on="click"
              placeholder="输入管理员密码"
            />
          </n-form-item>
          <n-button type="primary" block :loading="loading" attr-type="submit">登录后台</n-button>
          <n-text v-if="error" type="error" class="login-error">{{ error }}</n-text>
        </n-form>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { NButton, NForm, NFormItem, NInput, NText } from 'naive-ui';
import BrandLogo from '@/components/BrandLogo.vue';
import { useAuthStore } from '@/stores/auth';
import { useSetupStore } from '@/stores/setup';
import { validatedLoginRedirect } from '@/utils/workbenchNavigation';

const username = ref('');
const password = ref('');
const loading = ref(false);
const error = ref<string | null>(null);

const auth = useAuthStore();
const setup = useSetupStore();
const router = useRouter();
const route = useRoute();

function goHome(): void {
  router.push({ name: 'home' });
}

async function onSubmit(): Promise<void> {
  if (!username.value || !password.value) {
    error.value = '请输入用户名和密码。';
    return;
  }
  loading.value = true;
  error.value = null;
  try {
    await auth.login(username.value, password.value);
    const redirect = validatedLoginRedirect(route.query.redirect, router) ?? '/admin/app/dashboard';
    await router.replace(redirect);
  } catch (cause: unknown) {
    error.value = (cause as { response?: { data?: { message?: string } } })?.response?.data?.message
      ?? '登录失败。';
  } finally {
    loading.value = false;
  }
}

onMounted(async () => {
  const status = await setup.ensureLoaded();
  if (status.needsSetup) {
    await router.replace({ name: 'setup' });
  }
});
</script>

<style scoped>
.login-page {
  display: grid;
  grid-template-columns: minmax(320px, 0.96fr) minmax(420px, 1.04fr);
  min-height: 100%;
  background:
    radial-gradient(circle at left top, rgba(24, 160, 88, 0.18), transparent 22%),
    linear-gradient(135deg, #0d3b66 0%, #124f7a 48%, #f6fbff 48%, #eef5f9 100%);
}

.login-hero {
  display: flex;
  flex-direction: column;
  gap: 28px;
  padding: clamp(28px, 5vw, 48px);
  color: #f8fbff;
}

.login-kicker {
  font-size: 0.78rem;
  font-weight: 700;
  letter-spacing: 0.14em;
  text-transform: uppercase;
  color: rgba(248, 251, 255, 0.74);
}

.login-copy h1 {
  margin: 14px 0 0;
  font-size: clamp(2.1rem, 4vw, 3.2rem);
  line-height: 1.04;
  letter-spacing: -0.04em;
}

.login-copy p {
  margin-top: 16px;
  max-width: 42ch;
  color: rgba(248, 251, 255, 0.8);
  line-height: 1.75;
}

.login-panel {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: clamp(28px, 5vw, 48px);
}

.login-card {
  width: min(100%, 480px);
  border: 1px solid rgba(13, 59, 102, 0.08);
  border-radius: 30px;
  padding: clamp(24px, 4vw, 34px);
  background: rgba(255, 255, 255, 0.9);
  box-shadow: 0 22px 54px rgba(13, 59, 102, 0.12);
  backdrop-filter: blur(12px);
}

.home-link {
  border: 0;
  padding: 0;
  background: transparent;
  color: var(--sndb-ink-soft);
  font: inherit;
  cursor: pointer;
}

.login-card h2 {
  margin: 18px 0 0;
  font-size: 1.72rem;
}

.login-intro {
  margin: 10px 0 0;
  color: var(--sndb-ink-soft);
}

.login-error {
  display: block;
  margin-top: 12px;
}

@media (max-width: 980px) {
  .login-page {
    grid-template-columns: 1fr;
    background:
      radial-gradient(circle at left top, rgba(24, 160, 88, 0.18), transparent 22%),
      linear-gradient(180deg, #0d3b66 0%, #124f7a 36%, #f8fbff 36%, #eef5f9 100%);
  }
}
</style>
