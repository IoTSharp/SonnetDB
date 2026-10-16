<template>
  <main class="settings-page">
    <h1>设置</h1>
    <p class="muted">数据库参数、实例配置，以及当前浏览器的连接与观测偏好。</p>
    <section aria-labelledby="database-config-title">
      <h2 id="database-config-title">数据库参数</h2>
      <ConfigurationPanel scope="database" />
    </section>
    <section aria-labelledby="instance-config-title">
      <h2 id="instance-config-title">整个实例的配置</h2>
      <ConfigurationPanel v-if="auth.isSuperuser" scope="instance" />
      <p v-else class="muted">实例配置包含所有数据库共用的 SQL 预算、协议端口、观测和 AI 配置。请使用实例管理员账号登录查看；当前账号可以查看上方已授权数据库的参数。</p>
    </section>
    <section aria-labelledby="connection-title">
      <h2 id="connection-title">当前连接</h2>
      <dl>
        <div><dt>账号</dt><dd>{{ auth.username }}</dd></div>
        <div><dt>权限</dt><dd>{{ auth.isSuperuser ? '实例管理员' : '按数据库授权' }}</dd></div>
        <div><dt>服务地址</dt><dd>{{ connections.activeDisplayUrl }}</dd></div>
      </dl>
      <n-button :loading="checking" @click="checkConnection">检测连接</n-button>
      <p v-if="connectionResult" role="status">{{ connectionResult }}</p>
    </section>
    <section aria-labelledby="preferences-title">
      <h2 id="preferences-title">观测偏好</h2>
      <label id="diagnostics-refresh">诊断自动刷新</label>
      <n-select :value="preferences.refreshSeconds" :options="intervalOptions"
        aria-labelledby="diagnostics-refresh" @update:value="preferences.refreshSeconds = $event" />
      <p class="muted">页面在后台时暂停采样；仅保存到当前浏览器。</p>
      <n-alert v-if="preferences.storageError" type="warning">{{ preferences.storageError }}</n-alert>
      <p v-else role="status">偏好自动保存。</p>
    </section>
    <section aria-labelledby="administration-title">
      <h2 id="administration-title">服务与账号</h2>
      <p>服务器版本和运行环境可在“关于”查看；数据库权限由实例管理员分配。</p>
      <n-space>
        <n-button @click="router.push({ name: 'about' })">查看版本</n-button>
        <n-button @click="router.push({ name: 'monitoring' })">打开诊断</n-button>
        <n-button v-if="auth.isSuperuser" @click="router.push({ name: 'users' })">管理用户</n-button>
      </n-space>
    </section>
  </main>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { useRouter } from 'vue-router';
import { NAlert, NButton, NSelect, NSpace } from 'naive-ui';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import { refreshIntervals, usePreferencesStore } from '@/stores/preferences';
import ConfigurationPanel from '@/components/ConfigurationPanel.vue';

const auth = useAuthStore();
const connections = useConnectionsStore();
const preferences = usePreferencesStore();
const router = useRouter();
const checking = ref(false);
const connectionResult = ref('');
const intervalOptions = refreshIntervals.map(value => ({ value, label: value ? `每 ${value} 秒` : '仅手动刷新' }));
async function checkConnection(): Promise<void> {
  checking.value = true;
  try {
    const result = await connections.checkProfileHealth(connections.activeProfileId);
    connectionResult.value = result.state === 'healthy' ? `连接正常 · ${result.latencyMs ?? 0} ms` : result.message;
  } catch { connectionResult.value = '连接检测失败，请检查网络后重试。'; }
  finally { checking.value = false; }
}
</script>

<style scoped>
.settings-page { max-width: 880px; margin: 0 auto; padding: 8px 8px 36px; }
h1 { margin: 0; font-size: 24px; }
h2 { margin: 0 0 16px; font-size: 18px; }
section { padding: 24px 0; border-top: 1px solid var(--sndb-line, #dce3eb); margin-top: 24px; }
.muted, dt { color: var(--sndb-ink-soft, #566578); }
dl { margin-bottom: 20px; }
dl > div { display: grid; grid-template-columns: 110px minmax(0, 1fr); gap: 16px; padding: 7px 0; }
dd { margin: 0; overflow-wrap: anywhere; }
label { display: block; margin-bottom: 8px; }
.n-select { max-width: 320px; }
</style>
