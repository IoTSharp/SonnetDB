<template>
  <div class="setup-page">
    <section class="setup-aside">
      <BrandLogo light />
      <div class="setup-copy">
        <span class="setup-kicker">First Install</span>
        <h1>初始化这台 SonnetDB Server。</h1>
        <p>
          首次安装会把服务器 ID、组织名称、管理员账号和第一枚 Bearer Token 一次性写入
          <code>.system</code>。完成后，你就可以直接进入管理后台。
        </p>
      </div>

      <div class="setup-points">
        <article class="point-card">
          <strong>服务器身份</strong>
          <p>服务器 ID 会作为当前实例的稳定标识；组织名称用于首页和后续控制面展示。</p>
        </article>
        <article class="point-card">
          <strong>统一凭据</strong>
          <p>向导会同时创建管理员用户名密码和初始 Bearer Token，后续都走同一套认证体系。</p>
        </article>
        <article class="point-card">
          <strong>一次性明文展示</strong>
          <p>Bearer Token 明文仅在你提交和保存的这一刻可见，服务端只持久化它的哈希。</p>
        </article>
      </div>
    </section>

    <section class="setup-panel">
      <header class="setup-panel-header">
        <button type="button" class="back-link" @click="goHome">返回首页</button>
        <span class="setup-state">{{ setup.needsSetup ? '等待初始化' : '已安装' }}</span>
      </header>

      <div class="setup-card">
        <h2>首次安装向导</h2>
        <p class="setup-intro">设置完成后会自动登录为管理员并进入后台。</p>

        <n-alert v-if="error" type="error" class="setup-alert">{{ error }}</n-alert>

        <n-form class="setup-form" @submit.prevent="onSubmit">
          <n-form-item label="服务器 ID">
            <n-input v-model:value="serverId" placeholder="sonnetdb-dev-01" />
          </n-form-item>
          <n-form-item label="组织">
            <n-input v-model:value="organization" placeholder="Acme Observability" />
          </n-form-item>
          <n-form-item label="管理员用户名">
            <n-input v-model:value="username" placeholder="admin" />
          </n-form-item>
          <n-form-item label="管理员密码">
            <n-input
              v-model:value="password"
              type="password"
              show-password-on="click"
              autocomplete="new-password"
              placeholder="至少一组可记忆的强密码"
            />
          </n-form-item>
          <n-form-item label="初始 Bearer Token">
            <n-input-group>
              <n-input v-model:value="bearerToken" placeholder="tsl_..." />
              <n-button attr-type="button" @click="regenerateToken">重新生成</n-button>
            </n-input-group>
          </n-form-item>

          <div class="setup-hint">
            推荐先把 Bearer Token 保存在密码管理器。这个字段支持手动修改，但不能包含空白字符。
          </div>

          <div class="setup-actions">
            <n-button tertiary attr-type="button" @click="goHome">稍后再看</n-button>
            <n-button
              type="primary"
              attr-type="submit"
              :disabled="!setupReady"
              :loading="submitting"
            >
              完成初始化
            </n-button>
          </div>
        </n-form>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { NAlert, NButton, NForm, NFormItem, NInput, NInputGroup } from 'naive-ui';
import BrandLogo from '@/components/BrandLogo.vue';
import { useAuthStore } from '@/stores/auth';
import { useSetupStore } from '@/stores/setup';

const router = useRouter();
const auth = useAuthStore();
const setup = useSetupStore();

const localSuggestedServerId = setup.suggestedServerId;
const serverId = ref(localSuggestedServerId);
const organization = ref('Default Organization');
const username = ref('admin');
const password = ref('');
const bearerToken = ref(generateToken());
const error = ref<string | null>(null);
const submitting = ref(false);
const setupReady = ref(false);

function generateToken(): string {
  const bytes = new Uint8Array(18);
  crypto.getRandomValues(bytes);
  const body = Array.from(bytes, (value) => value.toString(16).padStart(2, '0')).join('');
  return `tsl_${body}`;
}

function regenerateToken(): void {
  bearerToken.value = generateToken();
}

function goHome(): void {
  router.push({ name: 'home' });
}

async function onSubmit(): Promise<void> {
  if (!setupReady.value) {
    error.value = '无法连接 SonnetDB Server，请确认服务端已启动后重试。';
    return;
  }

  const normalizedServerId = serverId.value.trim();
  const normalizedOrganization = organization.value.trim();
  const normalizedUsername = username.value.trim();
  const normalizedBearerToken = bearerToken.value.trim();

  if (!password.value) {
    error.value = '请输入管理员密码。';
    return;
  }

  if (!normalizedServerId || !normalizedOrganization || !normalizedUsername || !normalizedBearerToken) {
    error.value = '请完整填写所有初始化字段。';
    return;
  }

  submitting.value = true;
  error.value = null;
  try {
    const response = await setup.bootstrap({
      serverId: normalizedServerId,
      organization: normalizedOrganization,
      username: normalizedUsername,
      password: password.value,
      bearerToken: normalizedBearerToken,
    });

    auth.apply({
      username: response.username,
      token: response.token,
      tokenId: response.tokenId,
      isSuperuser: response.isSuperuser,
    });

    await router.replace({ name: 'dashboard' });
  } catch (cause: unknown) {
    error.value = (cause as { response?: { data?: { message?: string } } })?.response?.data?.message
      ?? '初始化失败，请检查输入后重试。';
  } finally {
    submitting.value = false;
  }
}

onMounted(async () => {
  try {
    const status = await setup.ensureLoaded();
    if (!status.needsSetup) {
      await router.replace(auth.isAuthenticated ? { name: 'dashboard' } : { name: 'login' });
      return;
    }

    if (!serverId.value || serverId.value === localSuggestedServerId) {
      serverId.value = status.suggestedServerId;
    }
    setupReady.value = true;
  } catch {
    error.value = '无法连接 SonnetDB Server，请确认服务端已启动后重试。';
  }
});
</script>

<style scoped>
.setup-page {
  display: grid;
  grid-template-columns: minmax(320px, 0.95fr) minmax(420px, 1.05fr);
  min-height: 100%;
  background:
    radial-gradient(circle at top left, rgba(24, 160, 88, 0.18), transparent 26%),
    linear-gradient(135deg, #0d3b66 0%, #103a5c 46%, #eef5f9 46%, #f8fbff 100%);
}

.setup-aside {
  display: flex;
  flex-direction: column;
  gap: 28px;
  padding: clamp(28px, 5vw, 48px);
  color: #f8fbff;
}

.setup-copy h1 {
  margin: 14px 0 0;
  font-size: clamp(2.2rem, 4vw, 3.4rem);
  line-height: 1.02;
  letter-spacing: -0.04em;
}

.setup-copy p {
  margin-top: 16px;
  max-width: 44ch;
  color: rgba(248, 251, 255, 0.78);
  line-height: 1.75;
}

.setup-kicker {
  font-size: 0.78rem;
  font-weight: 700;
  letter-spacing: 0.14em;
  text-transform: uppercase;
  color: rgba(248, 251, 255, 0.72);
}

.setup-points {
  display: grid;
  gap: 14px;
}

.point-card {
  border: 1px solid rgba(248, 251, 255, 0.12);
  border-radius: 22px;
  padding: 18px;
  background: rgba(248, 251, 255, 0.06);
  backdrop-filter: blur(12px);
}

.point-card strong {
  display: block;
  font-size: 1rem;
}

.point-card p {
  margin: 10px 0 0;
  color: rgba(248, 251, 255, 0.76);
  line-height: 1.65;
}

.setup-panel {
  display: flex;
  flex-direction: column;
  gap: 18px;
  padding: clamp(28px, 5vw, 48px);
}

.setup-panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.back-link {
  border: 0;
  background: transparent;
  color: var(--sndb-ink-soft);
  font: inherit;
  cursor: pointer;
}

.setup-state {
  display: inline-flex;
  align-items: center;
  padding: 8px 12px;
  border-radius: 999px;
  background: rgba(13, 59, 102, 0.06);
  color: var(--sndb-ink-strong);
  font-size: 0.86rem;
  font-weight: 600;
}

.setup-card {
  max-width: 620px;
  border: 1px solid rgba(13, 59, 102, 0.08);
  border-radius: 30px;
  padding: clamp(24px, 4vw, 36px);
  background: rgba(255, 255, 255, 0.88);
  box-shadow: 0 22px 54px rgba(13, 59, 102, 0.12);
  backdrop-filter: blur(12px);
}

.setup-card h2 {
  margin: 0;
  font-size: 1.65rem;
}

.setup-intro {
  margin: 10px 0 0;
  color: var(--sndb-ink-soft);
}

.setup-alert {
  margin-top: 18px;
}

.setup-form {
  margin-top: 20px;
}

.setup-hint {
  margin-top: 6px;
  color: var(--sndb-ink-soft);
  font-size: 0.92rem;
  line-height: 1.65;
}

.setup-actions {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
  margin-top: 24px;
}

@media (max-width: 980px) {
  .setup-page {
    grid-template-columns: 1fr;
    background:
      radial-gradient(circle at top left, rgba(24, 160, 88, 0.18), transparent 26%),
      linear-gradient(180deg, #0d3b66 0%, #103a5c 34%, #f8fbff 34%, #f8fbff 100%);
  }
}
</style>
