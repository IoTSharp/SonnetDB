<template>
  <n-popover trigger="click" placement="bottom-end" :width="360">
    <template #trigger>
      <button
        type="button"
        class="readiness-trigger"
        :class="`is-${overallStatus}`"
        :aria-label="`服务就绪状态：${statusLabel(overallStatus)}`"
        title="查看服务就绪检查"
      >
        <span class="readiness-trigger__label">运行状态</span>
        <span
          v-for="check in checks"
          :key="check.key"
          class="readiness-chip"
          :title="`${check.label}：${statusLabel(check.status)}`"
        >
          <span class="status-dot" :class="`is-${check.status}`" />
          <span>{{ check.shortLabel }}</span>
        </span>
      </button>
    </template>

    <section class="readiness-popover" aria-label="服务就绪检查详情">
      <header>
        <div>
          <strong>服务就绪检查</strong>
          <span>{{ checkedAtLabel }}</span>
        </div>
        <button type="button" class="refresh-button" title="刷新就绪检查" :disabled="loading" @click="refresh">
          <RefreshCw :size="16" :class="{ 'is-spinning': loading }" />
        </button>
      </header>

      <div class="readiness-list">
        <div v-for="check in checks" :key="check.key" class="readiness-row">
          <span class="status-dot" :class="`is-${check.status}`" />
          <div>
            <strong>{{ check.label }}</strong>
            <span>{{ check.description || statusLabel(check.status) }}</span>
          </div>
          <span class="readiness-row__status">{{ statusLabel(check.status) }}</span>
        </div>
      </div>

      <p v-if="errorMessage" class="readiness-error">{{ errorMessage }}</p>
    </section>
  </n-popover>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { NPopover } from 'naive-ui';
import { RefreshCw } from 'lucide-vue-next';
import { loadReadiness, type HealthCheckStatus, type ReadinessEntry } from '@/api/health';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';

interface CheckDefinition {
  key: string;
  label: string;
  shortLabel: string;
}

interface DisplayCheck extends CheckDefinition {
  status: HealthCheckStatus;
  description: string;
}

const definitions: CheckDefinition[] = [
  { key: 'segment_store_writable', label: 'Segment 存储', shortLabel: '存储' },
  { key: 'wal_writable', label: 'WAL', shortLabel: 'WAL' },
  { key: 'copilot_provider_reachable', label: 'Chat provider', shortLabel: 'Chat' },
  { key: 'copilot_embedding_provider_reachable', label: 'Embedding provider', shortLabel: 'Embed' },
];

const auth = useAuthStore();
const connections = useConnectionsStore();
const entries = ref<Record<string, ReadinessEntry>>({});
const reportStatus = ref<HealthCheckStatus>('unknown');
const checkedAt = ref<Date | null>(null);
const loading = ref(false);
const errorMessage = ref('');
let pollTimer: number | null = null;

const checks = computed<DisplayCheck[]>(() => definitions.map((definition) => ({
  ...definition,
  status: entries.value[definition.key]?.status ?? 'unknown',
  description: formatDescription(entries.value[definition.key]?.description ?? ''),
})));

const overallStatus = computed<HealthCheckStatus>(() => {
  if (checks.value.some((check) => check.status === 'unhealthy')) return 'unhealthy';
  if (checks.value.some((check) => check.status === 'degraded')) return 'degraded';
  if (checks.value.every((check) => check.status === 'healthy')) return 'healthy';
  return reportStatus.value;
});

const checkedAtLabel = computed(() => checkedAt.value
  ? `最近检查 ${checkedAt.value.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`
  : '尚未检查');

function statusLabel(status: HealthCheckStatus): string {
  if (status === 'healthy') return '正常';
  if (status === 'degraded') return '降级';
  if (status === 'unhealthy') return '异常';
  return '未知';
}

function formatDescription(description: string): string {
  const labels: Record<string, string> = {
    'chat.not_ready': 'Chat provider 尚未就绪',
    'chat.provider_unsupported': 'Chat provider 类型不受支持',
    'chat.endpoint_invalid': 'Chat endpoint 未配置或无效',
    'chat.api_key_missing': 'Chat API Key 未配置',
    'embedding.not_ready': 'Embedding provider 尚未就绪',
    'embedding.provider_unsupported': 'Embedding provider 类型不受支持',
    'embedding.endpoint_invalid': 'Embedding endpoint 未配置或无效',
    'embedding.api_key_missing': 'Embedding API Key 未配置',
    'embedding.model_missing': 'Embedding 模型未配置',
    'embedding.local_model_path_missing': '本地 Embedding 模型路径未配置',
    'embedding.local_model_not_found': '本地 Embedding 模型文件不存在',
  };
  const label = labels[description];
  return label ? `${label}（${description}）` : description;
}

async function refresh(): Promise<void> {
  if (loading.value) return;
  loading.value = true;
  errorMessage.value = '';
  try {
    const report = await loadReadiness(auth.api);
    entries.value = report.entries;
    reportStatus.value = report.status;
    checkedAt.value = new Date();
  } catch (error) {
    entries.value = {};
    reportStatus.value = 'unknown';
    errorMessage.value = error instanceof Error ? error.message : '无法读取服务就绪状态。';
    checkedAt.value = new Date();
  } finally {
    loading.value = false;
  }
}

watch(() => connections.activeBaseUrl, () => void refresh());

onMounted(() => {
  void refresh();
  pollTimer = window.setInterval(() => void refresh(), 30_000);
});

onBeforeUnmount(() => {
  if (pollTimer !== null) window.clearInterval(pollTimer);
});
</script>

<style scoped>
.readiness-trigger {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  height: 34px;
  padding: 0 10px;
  border: 1px solid var(--sndb-border);
  border-radius: 5px;
  background: #fff;
  color: var(--sndb-ink-soft);
  font: inherit;
  font-size: 12px;
  white-space: nowrap;
  cursor: pointer;
}

.readiness-trigger:hover {
  border-color: var(--sndb-border-strong);
  background: var(--sndb-hover);
}

.readiness-trigger__label {
  padding-right: 2px;
  color: var(--sndb-ink-muted);
}

.readiness-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.status-dot {
  display: inline-block;
  flex: 0 0 8px;
  width: 8px;
  height: 8px;
  border: 1px solid rgba(0, 0, 0, 0.12);
  border-radius: 50%;
  background: #aab2ba;
}

.status-dot.is-healthy {
  background: var(--sndb-success);
}

.status-dot.is-degraded {
  background: var(--sndb-warning);
}

.status-dot.is-unhealthy {
  background: var(--sndb-danger);
}

.readiness-popover {
  color: var(--sndb-ink-strong);
}

.readiness-popover header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding-bottom: 10px;
  border-bottom: 1px solid var(--sndb-border);
}

.readiness-popover header > div {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.readiness-popover header strong {
  font-size: 14px;
}

.readiness-popover header span {
  color: var(--sndb-ink-muted);
  font-size: 12px;
}

.refresh-button {
  display: inline-grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--sndb-ink-soft);
  cursor: pointer;
}

.refresh-button:hover {
  background: var(--sndb-hover);
}

.refresh-button:disabled {
  cursor: wait;
  opacity: 0.6;
}

.readiness-list {
  display: grid;
  gap: 0;
  padding-top: 4px;
}

.readiness-row {
  display: grid;
  grid-template-columns: 10px minmax(0, 1fr) auto;
  align-items: center;
  gap: 9px;
  min-height: 48px;
  border-bottom: 1px solid var(--sndb-border);
}

.readiness-row:last-child {
  border-bottom: 0;
}

.readiness-row > div {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 2px;
}

.readiness-row strong {
  font-size: 13px;
  font-weight: 600;
}

.readiness-row span {
  overflow: hidden;
  color: var(--sndb-ink-muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.readiness-row__status {
  color: var(--sndb-ink-soft) !important;
}

.readiness-error {
  margin: 8px 0 0;
  color: var(--sndb-danger);
  font-size: 12px;
}

.is-spinning {
  animation: readiness-spin 0.8s linear infinite;
}

@keyframes readiness-spin {
  to { transform: rotate(360deg); }
}

@media (max-width: 1380px) {
  .readiness-trigger__label,
  .readiness-chip > span:last-child {
    display: none;
  }

  .readiness-trigger {
    gap: 6px;
  }
}
</style>
