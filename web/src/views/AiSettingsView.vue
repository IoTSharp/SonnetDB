<template>
  <n-card title="Copilot 设置" :bordered="false">
    <n-space vertical :size="20">
      <section class="copilot-usage-summary">
        <div>
          <n-text depth="3">最近 1 小时调用</n-text>
          <strong>{{ usage?.requestCount ?? 0 }}</strong>
        </div>
        <div>
          <n-text depth="3">输入 tokens</n-text>
          <strong>{{ formatNumber(usage?.inputTokens ?? 0) }}</strong>
        </div>
        <div>
          <n-text depth="3">输出 tokens</n-text>
          <strong>{{ formatNumber(usage?.outputTokens ?? 0) }}</strong>
        </div>
        <div>
          <n-text depth="3">总 tokens</n-text>
          <strong>{{ formatNumber(usage?.totalTokens ?? 0) }}{{ usage?.includesEstimatedTokens ? ' *' : '' }}</strong>
        </div>
      </section>
      <n-text v-if="usage?.includesEstimatedTokens" depth="3" style="font-size: 12px">
        * 云端未返回完整 usage 的调用使用服务端估算值。
      </n-text>

      <n-card size="small" embedded :bordered="false" title="sonnetdb.com 账号绑定">
        <template #header-extra>
          <n-button size="small" quaternary :loading="loadingConfig" @click="load">刷新</n-button>
        </template>

        <n-space vertical :size="12">
          <n-space :size="8" align="center">
            <n-tag size="small" :type="isCloudBound ? 'success' : 'warning'">
              {{ isCloudBound ? '已绑定' : '未绑定' }}
            </n-tag>
            <n-tag size="small" type="info">ai.sonnetdb.com</n-tag>
            <n-text v-if="config?.cloudAccessTokenExpiresAtUtc" depth="3" style="font-size: 12px">
              Access Token 到期：{{ formatTime(config.cloudAccessTokenExpiresAtUtc) }}
            </n-text>
          </n-space>

          <n-space>
            <n-button type="primary" :loading="binding" @click="startBinding">
              {{ isCloudBound ? '重新绑定 sonnetdb.com 账号' : '绑定 sonnetdb.com 账号' }}
            </n-button>
            <n-button :loading="testing" :disabled="!isCloudBound" @click="testConnection">
              测试连接
            </n-button>
            <n-button :loading="modelsLoading" :disabled="!isCloudBound" @click="loadModels">
              查看平台模型
            </n-button>
          </n-space>

          <n-alert v-if="deviceCode" type="info" :show-icon="false">
            <n-space vertical :size="6">
              <n-text>
                请在 sonnetdb.com 输入设备码：
                <strong style="letter-spacing: 1px">{{ deviceCode.userCode }}</strong>
              </n-text>
              <n-button
                text
                type="primary"
                tag="a"
                :href="deviceCode.verificationUriComplete || deviceCode.verificationUri"
                target="_blank"
                rel="noopener noreferrer"
              >
                打开授权页面
              </n-button>
              <n-text depth="3" style="font-size: 12px">
                绑定窗口会自动轮询，设备码约 {{ Math.ceil(deviceCode.expiresIn / 60) }} 分钟后过期。
              </n-text>
            </n-space>
          </n-alert>

          <n-alert v-if="bindMsg" :type="bindOk ? 'success' : 'error'" closable @close="bindMsg = ''">
            {{ bindMsg }}
          </n-alert>
          <n-alert v-if="testMsg" :type="testOk ? 'success' : 'error'" closable @close="testMsg = ''">
            {{ testMsg }}
          </n-alert>
        </n-space>
      </n-card>

      <n-card size="small" embedded :bordered="false" title="平台模型">
        <template #header-extra>
          <n-button size="small" quaternary :loading="modelsLoading" :disabled="!isCloudBound" @click="loadModels">
            刷新
          </n-button>
        </template>
        <n-space v-if="models.length > 0" :size="8" align="center">
          <n-tag
            v-for="model in models"
            :key="model"
            size="small"
            :type="model === defaultModel ? 'success' : 'default'"
          >
            {{ model }}{{ model === defaultModel ? ' · 默认' : '' }}
          </n-tag>
        </n-space>
        <n-text v-else depth="3" style="font-size: 12px">
          {{ isCloudBound ? (modelsErr || '点击「刷新」从平台读取模型列表') : '绑定 sonnetdb.com 账号后可查看平台可用模型。' }}
        </n-text>
      </n-card>
    </n-space>
  </n-card>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { NAlert, NButton, NCard, NSpace, NTag, NText } from 'naive-ui';
import { useAuthStore } from '@/stores/auth';
import {
  createAiCloudDeviceCode,
  fetchAiCloudModels,
  fetchAiConfig,
  pollAiCloudDeviceToken,
  streamAiChat,
  type AiCloudDeviceCodeResponse,
  type AiConfigResponse,
} from '@/api/ai';
import { fetchCopilotMetrics, type CopilotMetricsSummary } from '@/api/copilot';

const auth = useAuthStore();

const config = ref<AiConfigResponse | null>(null);
const loadingConfig = ref(false);
const binding = ref(false);
const deviceCode = ref<AiCloudDeviceCodeResponse | null>(null);
const pollTimer = ref<ReturnType<typeof setTimeout> | null>(null);
const bindMsg = ref('');
const bindOk = ref(false);
const testing = ref(false);
const testMsg = ref('');
const testOk = ref(false);

const modelsLoading = ref(false);
const models = ref<string[]>([]);
const defaultModel = ref('');
const modelsErr = ref('');
const usage = ref<CopilotMetricsSummary | null>(null);

const isCloudBound = computed(() => config.value?.isCloudBound === true);

function clearPollTimer(): void {
  if (pollTimer.value) {
    clearTimeout(pollTimer.value);
    pollTimer.value = null;
  }
}

function formatTime(iso: string): string {
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

function formatNumber(value: number): string {
  return new Intl.NumberFormat('zh-CN').format(value);
}

async function load(): Promise<void> {
  loadingConfig.value = true;
  try {
    const [nextConfig, nextUsage] = await Promise.all([
      fetchAiConfig(auth.api),
      fetchCopilotMetrics(auth.api),
    ]);
    config.value = nextConfig;
    usage.value = nextUsage;
    if (config.value.isCloudBound) {
      void loadModels();
    }
  } catch (e: unknown) {
    bindOk.value = false;
    bindMsg.value = `读取绑定状态失败：${e instanceof Error ? e.message : String(e)}`;
  } finally {
    loadingConfig.value = false;
  }
}

async function startBinding(): Promise<void> {
  clearPollTimer();
  binding.value = true;
  bindMsg.value = '';
  bindOk.value = false;
  deviceCode.value = null;
  try {
    const code = await createAiCloudDeviceCode(auth.api);
    deviceCode.value = code;
    bindMsg.value = '已创建绑定请求，请在 sonnetdb.com 授权页面确认。';
    bindOk.value = true;
    const url = code.verificationUriComplete || code.verificationUri;
    if (url) window.open(url, '_blank', 'noopener,noreferrer');
    schedulePoll(code.deviceCode, Math.max(code.interval, 2));
  } catch (e: unknown) {
    binding.value = false;
    bindOk.value = false;
    bindMsg.value = `创建绑定请求失败：${e instanceof Error ? e.message : String(e)}`;
  }
}

function schedulePoll(code: string, intervalSeconds: number): void {
  clearPollTimer();
  pollTimer.value = setTimeout(() => {
    void pollBinding(code, intervalSeconds);
  }, intervalSeconds * 1000);
}

async function pollBinding(code: string, intervalSeconds: number): Promise<void> {
  try {
    const result = await pollAiCloudDeviceToken(auth.api, code);
    if (result.authorized) {
      clearPollTimer();
      binding.value = false;
      deviceCode.value = null;
      bindOk.value = true;
      bindMsg.value = '绑定成功，SonnetDB 将通过 ai.sonnetdb.com 调用平台 AI 服务。';
      await load();
      await loadModels();
      return;
    }

    const error = result.error ?? 'authorization_pending';
    if (error === 'authorization_pending' || error === 'slow_down') {
      bindMsg.value = result.message || '等待 sonnetdb.com 授权确认...';
      bindOk.value = true;
      schedulePoll(code, error === 'slow_down' ? intervalSeconds + 5 : intervalSeconds);
      return;
    }

    clearPollTimer();
    binding.value = false;
    bindOk.value = false;
    bindMsg.value = result.message || `绑定未完成：${error}`;
  } catch (e: unknown) {
    clearPollTimer();
    binding.value = false;
    bindOk.value = false;
    bindMsg.value = `轮询绑定状态失败：${e instanceof Error ? e.message : String(e)}`;
  }
}

async function loadModels(): Promise<void> {
  if (!isCloudBound.value) return;
  modelsLoading.value = true;
  modelsErr.value = '';
  try {
    const result = await fetchAiCloudModels(auth.api);
    defaultModel.value = result.default ?? '';
    models.value = result.candidates ?? [];
  } catch (e: unknown) {
    models.value = [];
    defaultModel.value = '';
    modelsErr.value = e instanceof Error ? e.message : String(e);
  } finally {
    modelsLoading.value = false;
  }
}

async function testConnection(): Promise<void> {
  testing.value = true;
  testMsg.value = '';
  testOk.value = false;
  try {
    const token = auth.state?.token ?? '';
    let reply = '';
    for await (const chunk of streamAiChat(token, [{ role: 'user', content: '请用一句话介绍你自己' }])) {
      reply += chunk;
      if (reply.length > 120) break;
    }
    testOk.value = true;
    testMsg.value = `连接成功，平台回复：${reply.slice(0, 100)}${reply.length > 100 ? '...' : ''}`;
  } catch (e: unknown) {
    const raw = e instanceof Error ? e.message : String(e);
    if (raw.includes('cloud_not_bound')) {
      testMsg.value = '连接失败：请先绑定 sonnetdb.com 账号。';
    } else if (raw.includes('cloud_token_expired')) {
      testMsg.value = '连接失败：Cloud Access Token 已过期，请重新绑定 sonnetdb.com 账号。';
    } else {
      testMsg.value = `连接失败：${raw}`;
    }
  } finally {
    testing.value = false;
  }
}

onMounted(() => {
  void load();
});

onBeforeUnmount(() => {
  clearPollTimer();
});
</script>

<style scoped>
.copilot-usage-summary {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 16px;
  padding: 14px 0;
  border-bottom: 1px solid var(--n-border-color);
}

.copilot-usage-summary > div {
  min-width: 0;
  display: grid;
  gap: 4px;
}

.copilot-usage-summary strong {
  font-size: 20px;
  font-weight: 650;
}

@media (max-width: 720px) {
  .copilot-usage-summary {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
</style>
