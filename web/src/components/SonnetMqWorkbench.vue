<template>
  <main class="mq-workbench" data-testid="workbench-mq">
    <section class="mq-toolbar">
      <div class="mq-toolbar__identity">
        <n-space size="small" align="center" :wrap="true">
          <MessageSquareMore :size="23" />
          <n-text class="mq-toolbar__title">{{ activeTopic || 'No topic selected' }}</n-text>
        </n-space>
        <n-text depth="3" class="mq-toolbar__meta">
          {{ targetDb || 'database' }} / MQ Topics
        </n-text>
      </div>

      <div class="mq-headline-stats">
        <span><small>消息</small><strong>{{ formatStat(stats?.messageCount ?? 0) }}</strong></span>
        <span class="is-warning"><small>消费滞后</small><strong>{{ formatStat(totalLag) }}</strong></span>
        <span class="is-success"><small>消费者</small><strong>{{ consumerRows.length }}</strong></span>
      </div>

      <div class="mq-toolbar__actions">
        <n-button type="primary" :disabled="!targetDb" @click="openPublisher">
          <template #icon><Send :size="16" /></template>
          发布测试消息
        </n-button>
        <n-button quaternary title="导入消息文件" :disabled="!targetDb" @click="messageFileInput?.click()">
          <template #icon><Upload :size="17" /></template>
        </n-button>
        <input ref="messageFileInput" type="file" accept=".json,.jsonl,.ndjson,application/json,application/x-ndjson" class="mq-file-input" @change="onMessageFileSelected">
        <n-button quaternary title="刷新主题" :loading="loading" @click="refreshAll">
          <template #icon><RefreshCw :size="17" /></template>
        </n-button>
        <n-button quaternary title="操作历史" @click="historyVisible = true">
          <template #icon><History :size="17" /></template>
        </n-button>
      </div>
    </section>

    <WorkbenchSectionTabs
      :model-value="activeSection"
      :items="mqSections"
      aria-label="MQ 工作区"
      @update:model-value="activeSection = $event as MqSection"
    />

    <WriteApprovalPanel
      v-if="previewPlan"
      :plan="previewPlan"
      :busy="confirmBusy"
      @cancel="clearPendingOperations"
      @confirm="confirmPendingOperations"
    />

    <n-alert
      v-if="errorMsg"
      type="error"
      :title="errorMsg"
      closable
      class="mq-alert"
      @close="errorMsg = ''"
    />

    <section v-if="activeSection !== 'messages'" class="mq-monitor" :class="`is-${activeSection}`">
      <section v-if="activeSection === 'overview' || activeSection === 'consumers'" class="mq-monitor-pane mq-consumer-pane">
        <div class="mq-panel-head mq-panel-head--compact">
          <div>
            <n-text class="mq-panel-head__title">Consumers & ack</n-text>
            <n-text depth="3" class="mq-panel-head__meta">{{ consumerSummary }}</n-text>
          </div>
          <n-tag size="tiny" :bordered="false" :type="maxLag > 0 ? 'warning' : 'success'">
            {{ maxLag > 0 ? 'lagging' : 'caught up' }}
          </n-tag>
        </div>

        <n-data-table
          :columns="consumerColumns"
          :data="consumerRows"
          :bordered="false"
          :pagination="false"
          :single-line="false"
          size="small"
          class="mq-consumer-grid"
        />

        <div class="mq-ack-editor">
          <n-select
            v-model:value="ackConsumerGroup"
            size="small"
            filterable
            tag
            clearable
            placeholder="Consumer group"
            :options="ackGroupOptions"
          />
          <n-input-number
            v-model:value="ackOffset"
            size="small"
            :min="0"
            :show-button="false"
            placeholder="Ack offset"
          />
          <n-button size="small" secondary :disabled="!canStageAck" @click="stageAckFromForm">
            Stage ack
          </n-button>
          <n-button size="small" quaternary :disabled="!selectedMessage || !ackConsumerGroup" @click="stageAckSelected">
            Selected
          </n-button>
          <n-button size="small" quaternary :disabled="highWaterOffset <= 0 || !ackConsumerGroup" @click="stageAckHighWater">
            High-water
          </n-button>
        </div>
      </section>

      <section v-if="activeSection === 'overview'" class="mq-monitor-pane mq-traffic-pane">
        <div class="mq-panel-head mq-panel-head--compact">
          <div>
            <n-text class="mq-panel-head__title">Throughput & backlog</n-text>
            <n-text depth="3" class="mq-panel-head__meta">{{ sampleSummary }}</n-text>
          </div>
          <n-button size="tiny" quaternary :loading="loadingMonitor" :disabled="!activeTopic" @click="refreshMonitorOnly">
            Sample
          </n-button>
        </div>

        <div class="mq-rate-strip">
          <span>
            <small>Publish</small>
            <strong>{{ formatRate(latestRates.publishRate) }}</strong>
          </span>
          <span>
            <small>Ack</small>
            <strong>{{ formatRate(latestRates.ackRate) }}</strong>
          </span>
          <span>
            <small>Backlog</small>
            <strong>{{ formatStat(totalLag) }}</strong>
          </span>
        </div>

        <div class="mq-sparkline" :class="{ 'is-empty': trendPaths.length === 0 }">
          <svg
            v-if="trendPaths.length > 0"
            viewBox="0 0 360 126"
            preserveAspectRatio="none"
            xmlns="http://www.w3.org/2000/svg"
            role="img"
            aria-label="SonnetMQ backlog trend"
          >
            <line
              v-for="tick in trendGridLines"
              :key="tick.key"
              x1="38"
              x2="350"
              :y1="tick.y"
              :y2="tick.y"
            />
            <text
              v-for="tick in trendGridLines"
              :key="`${tick.key}-label`"
              x="32"
              :y="tick.y + 4"
              text-anchor="end"
            >{{ tick.label }}</text>
            <path
              v-for="path in trendPaths"
              :key="path.name"
              :d="path.d"
              :stroke="path.color"
              stroke-width="1.7"
              fill="none"
            />
          </svg>
          <span v-else>Waiting for samples...</span>
        </div>

        <div class="mq-trend-legend">
          <span v-for="path in trendPaths" :key="`${path.name}-legend`">
            <i :style="{ background: path.color }" />
            {{ path.name }}
          </span>
        </div>
      </section>

      <section v-if="activeSection === 'overview' || activeSection === 'configuration'" class="mq-monitor-pane mq-retention-pane">
        <div class="mq-panel-head mq-panel-head--compact">
          <div>
            <n-text class="mq-panel-head__title">Retention & DLQ</n-text>
            <n-text depth="3" class="mq-panel-head__meta">{{ retentionSummary }}</n-text>
          </div>
          <n-tag size="tiny" :bordered="false" :type="dlqTopic ? 'warning' : 'default'">
            {{ dlqTopic ? 'dlq topic' : 'no dlq' }}
          </n-tag>
        </div>

        <div class="mq-retention-grid">
          <span>
            <small>Window</small>
            <strong>{{ retainedWindowText }}</strong>
          </span>
          <span>
            <small>Age</small>
            <strong>{{ formatDurationSeconds(retention?.retentionMaxAgeSeconds) }}</strong>
          </span>
          <span>
            <small>Size</small>
            <strong>{{ formatBytes(retention?.retentionMaxBytes) }}</strong>
          </span>
          <span>
            <small>Ack trim</small>
            <strong>{{ retention?.trimAcknowledgedMessages ? 'on' : 'off' }}</strong>
          </span>
          <span>
            <small>Hot tail</small>
            <strong>{{ formatBytes(retention?.hotTailMaxBytes) }}</strong>
          </span>
          <span>
            <small>Segment</small>
            <strong>{{ formatBytes(retention?.segmentMaxBytes) }}</strong>
          </span>
        </div>

        <div class="mq-dlq-state">
          <template v-if="dlqTopic">
            <span>{{ dlqTopic.topic }} · {{ formatStat(dlqTopic.messageCount) }} messages</span>
            <n-button size="tiny" secondary @click="selectTopic(dlqTopic.topic)">Open</n-button>
          </template>
          <template v-else>
            <span>No conventional DLQ topic found for this topic.</span>
          </template>
        </div>
      </section>
    </section>

    <section v-if="activeSection === 'messages'" class="mq-body" :class="{ 'is-inspector-collapsed': inspectorCollapsed }">
      <section class="mq-message-panel">
        <div class="mq-panel-head mq-panel-head--grid">
          <div>
            <n-text class="mq-panel-head__title">Messages</n-text>
            <n-text depth="3" class="mq-panel-head__meta">{{ messageWindowSummary }}</n-text>
          </div>
          <n-space size="small" align="center" :wrap="true">
            <n-button v-if="inspectorCollapsed" quaternary title="打开消息详情" @click="inspectorCollapsed = false">
              <template #icon><PanelRightOpen :size="17" /></template>
            </n-button>
            <n-button size="small" secondary :disabled="!canPageBack" @click="previousPage">Previous</n-button>
            <n-button size="small" secondary :disabled="!canPageForward" @click="nextPage">Next</n-button>
          </n-space>
        </div>

        <div class="mq-message-tools">
          <n-button secondary :disabled="!activeTopic" @click="autoRefresh = !autoRefresh">
            {{ autoRefresh ? '暂停消费' : '实时消费' }}
          </n-button>
          <n-input-number
            v-model:value="fromOffset"
            :min="0"
            :show-button="false"
            placeholder="偏移量"
            class="mq-toolbar__offset"
            @keydown.enter="browseFromInput"
          />
          <n-date-picker
            v-model:value="seekTimeMs"
            type="datetime"
            clearable
            placeholder="按时间定位"
            class="mq-toolbar__time"
          />
          <n-select v-model:value="browseLimit" :options="browseLimitOptions" class="mq-toolbar__limit" />
          <n-button secondary :disabled="!activeTopic" :loading="loadingBrowse" @click="browseFromInput">浏览</n-button>
          <n-button secondary :disabled="!activeTopic || !seekTimeMs" :loading="loadingBrowse" @click="seekByTime">定位时间</n-button>
        </div>

        <n-data-table
          :columns="messageColumns"
          :data="rows"
          :loading="loadingBrowse || props.loading"
          :bordered="false"
          :single-line="false"
          :pagination="false"
          :row-key="rowKey"
          size="small"
          remote
          flex-height
          class="mq-grid"
        />

        <footer class="mq-pager">
          <span>{{ pagerText }}</span>
          <n-space size="small" align="center">
            <n-button size="small" quaternary :disabled="rows.length === 0" @click="clearRows">Clear window</n-button>
          </n-space>
        </footer>
      </section>

      <aside v-if="!inspectorCollapsed" class="mq-inspector">
        <div class="mq-panel-head">
          <div>
            <n-text class="mq-panel-head__title">Message inspector</n-text>
            <n-text depth="3" class="mq-panel-head__meta">{{ selectedMessage ? formatTimestamp(selectedMessage.timestampUtc) : 'No message selected' }}</n-text>
          </div>
          <div class="mq-inspector__actions">
            <n-tag v-if="selectedMessage" size="tiny" :bordered="false">offset {{ selectedMessage.offset }}</n-tag>
            <n-button quaternary title="收起消息详情" @click="inspectorCollapsed = true">
              <template #icon><PanelRightClose :size="17" /></template>
            </n-button>
          </div>
        </div>

        <template v-if="selectedMessage">
          <div class="mq-detail-strip">
            <span>{{ selectedMessage.byteLength }} bytes</span>
            <span>{{ selectedMessage.headerCount }} headers</span>
            <span>{{ selectedMessage.payloadKind }}</span>
          </div>

          <section class="mq-headers">
            <div class="mq-section-title">
              <span>Headers</span>
              <n-button size="tiny" quaternary @click="copyHeaders">Copy</n-button>
            </div>
            <pre>{{ selectedHeadersText }}</pre>
          </section>

          <n-tabs v-model:value="payloadView" type="segment" size="small" class="mq-payload-tabs">
            <n-tab name="text" tab="Text" />
            <n-tab name="json" tab="JSON" />
            <n-tab name="hex" tab="Hex" />
            <n-tab name="base64" tab="Base64" />
          </n-tabs>
          <pre class="mq-payload-preview">{{ selectedPayloadText }}</pre>
        </template>
        <n-empty v-else description="Select a message from the browser." />

        <section v-if="publisherVisible" class="mq-publisher">
          <n-text class="mq-section-title mq-section-title--standalone">Publish test message</n-text>
          <n-input v-model:value="publishTopic" size="small" placeholder="Topic" />
          <n-select v-model:value="publishMode" size="small" :options="payloadModeOptions" />
          <n-input
            v-model:value="publishHeadersText"
            type="textarea"
            :autosize="{ minRows: 2, maxRows: 4 }"
            placeholder="Headers, one key=value per line"
          />
          <n-input
            v-model:value="publishPayload"
            type="textarea"
            :autosize="{ minRows: 5, maxRows: 9 }"
            placeholder="Payload"
          />
          <n-space size="small" align="center" :wrap="true">
            <n-button size="small" type="primary" :disabled="!targetDb" @click="stagePublish">
              Stage publish
            </n-button>
            <n-button size="small" secondary :disabled="!selectedMessage" @click="copyPayload">
              Copy payload
            </n-button>
          </n-space>
        </section>
      </aside>
    </section>

    <WorkbenchResultPanel
      class="mq-result"
      title="SonnetMQ operation result"
      :sql="latestCommand"
      :result="latestResult"
      :ran-once="ranOnce"
      :summary="resultSummary"
      :file-name="`${targetDb}_${activeTopic || 'mq'}`"
      empty-description="Browse a topic or publish a test message to see results."
      @clear-error="latestResult = null"
    />

    <WorkbenchHistoryDrawer
      v-model:show="historyVisible"
      :active-database="targetDb"
      @select="openHistoryEntry"
    />
  </main>
</template>

<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import {
  History,
  MessageSquareMore,
  PanelRightClose,
  PanelRightOpen,
  RefreshCw,
  Send,
  Upload,
} from 'lucide-vue-next';
import {
  NAlert,
  NButton,
  NDataTable,
  NDatePicker,
  NEmpty,
  NInput,
  NInputNumber,
  NSelect,
  NSpace,
  NTab,
  NTabs,
  NTag,
  NText,
  useMessage,
  type DataTableColumns,
  type SelectOption,
} from 'naive-ui';
import { fetchMqTopics, type MqTopicInfo } from '@/api/management';
import {
  ackMqConsumer,
  browseMqMessages,
  fetchMqRetention,
  fetchMqOffsets,
  fetchMqStats,
  publishMqMessage,
  type MqConsumerLag,
  type MqMessageResponse,
  type MqOffsetsResponse,
  type MqRetentionResponse,
  type MqStatsResponse,
} from '@/api/mq';
import type { SqlResultSet } from '@/api/sql';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import WorkbenchResultPanel from '@/components/WorkbenchResultPanel.vue';
import WorkbenchSectionTabs from '@/components/WorkbenchSectionTabs.vue';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import {
  useWorkbenchHistoryStore,
  type WorkbenchHistoryEntry,
} from '@/stores/workbenchHistory';
import {
  createWriteApprovalPlan,
  type WriteApprovalItem,
  type WriteApprovalPlan,
  type WriteApprovalSeverity,
} from '@/utils/writeApproval';

const props = withDefaults(defineProps<{
  targetDb: string;
  topic: string;
  topics?: MqTopicInfo[];
  loading?: boolean;
}>(), {
  topics: () => [],
  loading: false,
});

const emit = defineEmits<{
  selectTopic: [topic: string];
  refreshSchema: [];
}>();

type PayloadView = 'text' | 'json' | 'hex' | 'base64';
type PayloadKind = 'json' | 'text' | 'binary';
type MqSection = 'overview' | 'messages' | 'consumers' | 'configuration';

interface MqRow {
  topic: string;
  offset: number;
  timestampUtc: string;
  headers: Record<string, string>;
  payload: string;
  byteLength: number;
  headerCount: number;
  payloadKind: PayloadKind;
  payloadPreview: string;
}

interface PendingOperation {
  id: string;
  label: string;
  detail: string;
  severity: WriteApprovalSeverity;
  command: string;
  run: () => Promise<OperationOutcome>;
}

interface OperationOutcome {
  action: string;
  target: string;
  succeeded: boolean;
  affected: number;
  detail: string;
  offset?: number;
  nextOffset?: number;
}

interface ConsumerRow extends MqConsumerLag {
  progressRatio: number;
  status: 'caught_up' | 'lagging' | 'beyond_retention';
}

interface TrendSample {
  at: number;
  nextOffset: number;
  totalLag: number;
  ackedOffset: number;
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();

const localTopics = ref<MqTopicInfo[]>([]);
const offsets = ref<MqOffsetsResponse | null>(null);
const stats = ref<MqStatsResponse | null>(null);
const retention = ref<MqRetentionResponse | null>(null);
const rows = ref<MqRow[]>([]);
const selectedOffset = ref<number | null>(null);
const fromOffset = ref<number | null>(0);
const browseLimit = ref(100);
const seekTimeMs = ref<number | null>(null);
const loading = ref(false);
const loadingBrowse = ref(false);
const loadingMonitor = ref(false);
const errorMsg = ref('');
const payloadView = ref<PayloadView>('text');
const publishTopic = ref('');
const publishMode = ref<PayloadView>('text');
const publishPayload = ref('{"message":"hello SonnetMQ"}');
const publishHeadersText = ref('source=web-admin');
const ackConsumerGroup = ref<string | null>(null);
const ackOffset = ref<number | null>(null);
const autoRefresh = ref(false);
const trendSamples = ref<TrendSample[]>([]);
const pendingOperations = ref<PendingOperation[]>([]);
const confirmBusy = ref(false);
const latestResult = ref<SqlResultSet | null>(null);
const latestCommand = ref('');
const ranOnce = ref(false);
const historyVisible = ref(false);
const activeSection = ref<MqSection>('overview');
const inspectorCollapsed = ref(false);
const publisherVisible = ref(false);
const messageFileInput = ref<HTMLInputElement | null>(null);

const mqSections: Array<{ key: MqSection; label: string }> = [
  { key: 'overview', label: '概览' },
  { key: 'messages', label: '消息' },
  { key: 'consumers', label: '消费者组' },
  { key: 'configuration', label: '配置' },
];

const browseLimitOptions: SelectOption[] = [
  { label: '25 messages', value: 25 },
  { label: '50 messages', value: 50 },
  { label: '100 messages', value: 100 },
  { label: '250 messages', value: 250 },
  { label: '1000 messages', value: 1000 },
];

const payloadModeOptions: SelectOption[] = [
  { label: 'Text (UTF-8)', value: 'text' },
  { label: 'JSON', value: 'json' },
  { label: 'Hex', value: 'hex' },
  { label: 'Base64', value: 'base64' },
];

let autoRefreshTimer: ReturnType<typeof setInterval> | null = null;
let compactViewport = false;

const activeTopic = computed(() => props.topic || localTopics.value[0]?.topic || '');

const currentTopicInfo = computed(() =>
  localTopics.value.find((topic) => topic.topic === activeTopic.value) ?? null);

const retainedStartOffset = computed(() => {
  const next = stats.value?.nextOffset ?? currentTopicInfo.value?.nextOffset ?? 0;
  const count = stats.value?.messageCount ?? currentTopicInfo.value?.messageCount ?? 0;
  return Math.max(0, next - count);
});

const highWaterOffset = computed(() =>
  stats.value?.nextOffset ?? currentTopicInfo.value?.nextOffset ?? 0);

const selectedMessage = computed(() =>
  rows.value.find((row) => row.offset === selectedOffset.value) ?? null);

const selectedHeadersText = computed(() => {
  const row = selectedMessage.value;
  if (!row || Object.keys(row.headers).length === 0) return '{}';
  return JSON.stringify(row.headers, null, 2);
});

const selectedPayloadText = computed(() => {
  const row = selectedMessage.value;
  if (!row) return '';
  return formatPayload(row.payload, payloadView.value);
});

const consumers = computed<MqConsumerLag[]>(() => offsets.value?.consumers ?? []);

const maxLag = computed(() =>
  consumers.value.reduce((max, consumer) => Math.max(max, consumer.lag), 0));

const totalLag = computed(() =>
  consumers.value.reduce((sum, consumer) => sum + consumer.lag, 0));

const ackedOffsetTotal = computed(() =>
  consumers.value.reduce((sum, consumer) => sum + consumer.committedOffset, 0));

const consumerRows = computed<ConsumerRow[]>(() => {
  const highWater = highWaterOffset.value;
  const retainedStart = retainedStartOffset.value;
  return consumers.value.map((consumer) => {
    const progressRatio = highWater <= 0
      ? 1
      : Math.min(1, Math.max(0, consumer.committedOffset / highWater));
    const status = consumer.committedOffset < retainedStart
      ? 'beyond_retention'
      : consumer.lag > 0
        ? 'lagging'
        : 'caught_up';
    return { ...consumer, progressRatio, status };
  });
});

const consumerSummary = computed(() => {
  if (!activeTopic.value) return 'Select a topic to inspect consumer offsets.';
  if (consumerRows.value.length === 0) return 'No committed consumer groups yet.';
  return `${consumerRows.value.length} groups · total lag ${formatStat(totalLag.value)}`;
});

const ackGroupOptions = computed<SelectOption[]>(() => {
  const names = new Set(consumers.value.map((consumer) => consumer.consumerGroup));
  if (ackConsumerGroup.value) names.add(ackConsumerGroup.value);
  return [...names].sort().map((name) => ({ label: name, value: name }));
});

const canStageAck = computed(() =>
  Boolean(activeTopic.value && ackConsumerGroup.value?.trim() && typeof ackOffset.value === 'number' && ackOffset.value >= 0));

const sampleSummary = computed(() => {
  if (trendSamples.value.length < 2) return 'Collect at least two samples to compute rates.';
  const first = trendSamples.value[0];
  const last = trendSamples.value[trendSamples.value.length - 1];
  const seconds = Math.max(1, (last.at - first.at) / 1000);
  return `${trendSamples.value.length} samples · ${(seconds / 60).toFixed(1)} min window`;
});

const latestRates = computed(() => {
  const samples = trendSamples.value;
  if (samples.length < 2) return { publishRate: null as number | null, ackRate: null as number | null };
  const prev = samples[samples.length - 2];
  const curr = samples[samples.length - 1];
  const seconds = Math.max(0.001, (curr.at - prev.at) / 1000);
  return {
    publishRate: Math.max(0, curr.nextOffset - prev.nextOffset) / seconds,
    ackRate: Math.max(0, curr.ackedOffset - prev.ackedOffset) / seconds,
  };
});

const trendSeries = computed(() => [
  { name: 'Backlog', color: '#e85d75', values: trendSamples.value.map((item) => item.totalLag) },
  { name: 'High-water', color: '#2c7be5', values: trendSamples.value.map((item) => item.nextOffset) },
  { name: 'Acked', color: '#52b788', values: trendSamples.value.map((item) => item.ackedOffset) },
].filter((item) => item.values.length >= 2));

const trendMax = computed(() => {
  const values = trendSeries.value.flatMap((item) => item.values);
  return Math.max(1, ...values);
});

const trendGridLines = computed(() => [0, trendMax.value / 2, trendMax.value].map((value, index) => ({
  key: `grid-${index}`,
  y: trendY(value),
  label: formatCompactNumber(value),
})));

const trendPaths = computed(() => {
  const samples = trendSamples.value;
  if (samples.length < 2) return [];
  const xMin = samples[0].at;
  const xMax = samples[samples.length - 1].at;
  const sx = (at: number): number => 38 + ((at - xMin) / Math.max(1, xMax - xMin)) * 312;
  return trendSeries.value.map((series) => ({
    name: series.name,
    color: series.color,
    d: series.values
      .map((value, index) => `${index === 0 ? 'M' : 'L'}${sx(samples[index].at).toFixed(1)},${trendY(value).toFixed(1)}`)
      .join(' '),
  }));
});

const retentionSummary = computed(() => {
  if (!retention.value) return 'Retention policy not sampled yet.';
  const age = formatDurationSeconds(retention.value.retentionMaxAgeSeconds);
  const size = formatBytes(retention.value.retentionMaxBytes);
  return `age ${age} · size ${size} · ack trim ${retention.value.trimAcknowledgedMessages ? 'on' : 'off'}`;
});

const retainedWindowText = computed(() => {
  if (retention.value) {
    return retention.value.retainedMessages > 0
      ? `${retention.value.retainedStartOffset} - ${retention.value.retainedEndOffset}`
      : 'empty';
  }
  const next = highWaterOffset.value;
  const start = retainedStartOffset.value;
  return next > start ? `${start} - ${next - 1}` : 'empty';
});

const dlqTopic = computed(() =>
  findDlqTopic(activeTopic.value, localTopics.value));

const canPageBack = computed(() =>
  Boolean(activeTopic.value && (fromOffset.value ?? 0) > retainedStartOffset.value && !loadingBrowse.value));

const canPageForward = computed(() => {
  if (!activeTopic.value || loadingBrowse.value || rows.value.length === 0) return false;
  const last = rows.value[rows.value.length - 1];
  return last.offset + 1 < highWaterOffset.value;
});

const messageWindowSummary = computed(() => {
  if (!activeTopic.value) return 'Select a topic to browse messages.';
  if (rows.value.length === 0) return `Offset ${fromOffset.value ?? 0} · no messages in this window`;
  const first = rows.value[0];
  const last = rows.value[rows.value.length - 1];
  return `Offsets ${first.offset} - ${last.offset} · ${rows.value.length} messages`;
});

const pagerText = computed(() =>
  highWaterOffset.value > retainedStartOffset.value
    ? `Retained offset window ${retainedStartOffset.value} - ${highWaterOffset.value - 1}.`
    : 'Topic has no retained messages.');

const previewPlan = computed<WriteApprovalPlan | null>(() => {
  if (pendingOperations.value.length === 0) return null;
  const items: WriteApprovalItem[] = pendingOperations.value.map((operation) => ({
    id: operation.id,
    command: operation.command,
    severity: operation.severity,
    label: operation.label,
    detail: operation.detail,
  }));
  return createWriteApprovalPlan({
    id: `mq_${props.targetDb}_${activeTopic.value || publishTopic.value}_${pendingOperations.value.map((item) => item.id).join('_')}`,
    title: 'SonnetMQ staged operations',
    target: `${props.targetDb}.${activeTopic.value || publishTopic.value || 'topic'}`,
    items,
  });
});

const resultSummary = computed(() => {
  if (!latestResult.value) return messageWindowSummary.value;
  if (latestResult.value.error) return latestResult.value.error.message;
  if (latestResult.value.end) {
    const affected = latestResult.value.end.recordsAffected >= 0
      ? `affected ${latestResult.value.end.recordsAffected}`
      : `${latestResult.value.end.rowCount} rows`;
    return `${affected} · ${latestResult.value.end.elapsedMs.toFixed(2)} ms`;
  }
  return 'Ready';
});

const messageColumns = computed<DataTableColumns<MqRow>>(() => [
  {
    title: 'Offset',
    key: 'offset',
    width: 110,
    render: (row) => h('button', {
      type: 'button',
      class: ['mq-offset-button', row.offset === selectedOffset.value ? 'is-active' : ''],
      onClick: () => selectMessage(row.offset),
    }, row.offset.toString()),
  },
  {
    title: 'Timestamp',
    key: 'timestampUtc',
    minWidth: 190,
    render: (row) => h('span', { class: 'mq-time-cell' }, formatTimestamp(row.timestampUtc)),
  },
  {
    title: 'Headers',
    key: 'headers',
    width: 96,
    render: (row) => h(NTag, {
      size: 'tiny',
      bordered: false,
      type: row.headerCount > 0 ? 'info' : 'default',
    }, { default: () => row.headerCount.toString() }),
  },
  {
    title: 'Bytes',
    key: 'byteLength',
    width: 88,
    render: (row) => h('code', row.byteLength.toString()),
  },
  {
    title: 'Type',
    key: 'payloadKind',
    width: 92,
    render: (row) => h(NTag, {
      size: 'tiny',
      bordered: false,
      type: row.payloadKind === 'json' ? 'success' : row.payloadKind === 'text' ? 'info' : 'warning',
    }, { default: () => row.payloadKind }),
  },
  {
    title: 'Payload preview',
    key: 'payloadPreview',
    minWidth: 260,
    ellipsis: { tooltip: true },
    render: (row) => h('span', { class: 'mq-preview-cell' }, row.payloadPreview),
  },
]);

const consumerColumns = computed<DataTableColumns<ConsumerRow>>(() => [
  {
    title: 'Consumer group',
    key: 'consumerGroup',
    minWidth: 150,
    render: (row) => h('span', { class: 'mq-consumer-name' }, row.consumerGroup),
  },
  {
    title: 'Committed',
    key: 'committedOffset',
    width: 110,
    render: (row) => h('code', row.committedOffset.toString()),
  },
  {
    title: 'Lag',
    key: 'lag',
    width: 90,
    render: (row) => h(NTag, {
      size: 'tiny',
      bordered: false,
      type: row.lag > 0 ? 'warning' : 'success',
    }, { default: () => row.lag.toLocaleString() }),
  },
  {
    title: 'Progress',
    key: 'progressRatio',
    minWidth: 160,
    render: (row) => h('div', { class: 'mq-progress' }, [
      h('span', { style: { width: `${Math.round(row.progressRatio * 100)}%` } }),
      h('em', `${Math.round(row.progressRatio * 100)}%`),
    ]),
  },
  {
    title: 'State',
    key: 'status',
    width: 116,
    render: (row) => h(NTag, {
      size: 'tiny',
      bordered: false,
      type: row.status === 'caught_up' ? 'success' : row.status === 'beyond_retention' ? 'error' : 'warning',
    }, { default: () => row.status.replace(/_/g, ' ') }),
  },
]);

function openPublisher(): void {
  activeSection.value = 'messages';
  inspectorCollapsed.value = false;
  publisherVisible.value = true;
}

function syncInspectorForViewport(): void {
  const nextCompact = window.innerWidth <= 980;
  if (nextCompact !== compactViewport) {
    compactViewport = nextCompact;
    inspectorCollapsed.value = nextCompact;
  }
}

function rowKey(row: MqRow): number {
  return row.offset;
}

function syncLocalTopics(topics: MqTopicInfo[]): void {
  localTopics.value = [...topics].sort((a, b) => a.topic.localeCompare(b.topic));
}

async function refreshAll(): Promise<void> {
  loading.value = true;
  errorMsg.value = '';
  try {
    await refreshTopicList();
    if (activeTopic.value) {
      await loadTopicMetadata(activeTopic.value);
      await loadMessages(fromOffset.value ?? retainedStartOffset.value, true);
    }
  } catch (error) {
    errorMsg.value = errorToMessage(error, '刷新 SonnetMQ 工作台失败');
  } finally {
    loading.value = false;
  }
}

async function refreshTopicList(): Promise<void> {
  if (!props.targetDb) {
    localTopics.value = [];
    return;
  }
  const topics = await fetchMqTopics(auth.api, props.targetDb);
  syncLocalTopics(topics);
}

async function loadTopicMetadata(topic: string): Promise<void> {
  if (!props.targetDb || !topic) {
    stats.value = null;
    offsets.value = null;
    retention.value = null;
    return;
  }
  const [nextStats, nextOffsets, nextRetention] = await Promise.all([
    fetchMqStats(auth.api, props.targetDb, topic),
    fetchMqOffsets(auth.api, props.targetDb, topic),
    fetchMqRetention(auth.api, props.targetDb, topic),
  ]);
  stats.value = nextStats;
  offsets.value = nextOffsets;
  retention.value = nextRetention;
  if (!ackConsumerGroup.value && nextOffsets.consumers.length > 0) {
    ackConsumerGroup.value = nextOffsets.consumers[0].consumerGroup;
  }
  if (ackOffset.value === null) {
    ackOffset.value = Math.max(0, nextStats.nextOffset - 1);
  }
  pushTrendSample();
}

async function refreshMonitorOnly(): Promise<void> {
  if (!activeTopic.value) return;
  loadingMonitor.value = true;
  errorMsg.value = '';
  try {
    await loadTopicMetadata(activeTopic.value);
  } catch (error) {
    errorMsg.value = errorToMessage(error, '刷新 MQ 监控采样失败');
  } finally {
    loadingMonitor.value = false;
  }
}

function stageAckFromForm(): void {
  if (!canStageAck.value || ackOffset.value === null || !ackConsumerGroup.value) return;
  stageAck(ackConsumerGroup.value, ackOffset.value);
}

function stageAckSelected(): void {
  if (!selectedMessage.value || !ackConsumerGroup.value) return;
  stageAck(ackConsumerGroup.value, selectedMessage.value.offset);
}

function stageAckHighWater(): void {
  if (!ackConsumerGroup.value || highWaterOffset.value <= 0) return;
  stageAck(ackConsumerGroup.value, highWaterOffset.value - 1);
}

function stageAck(consumerGroup: string, offset: number): void {
  const db = props.targetDb;
  const topic = activeTopic.value;
  const normalizedGroup = consumerGroup.trim();
  const normalizedOffset = Math.max(0, Math.trunc(offset));
  if (!db || !topic || !normalizedGroup) {
    message.warning('Ack requires a database, topic and consumer group.');
    return;
  }
  pendingOperations.value.push({
    id: makeOperationId('ack'),
    label: 'Ack consumer offset',
    detail: `${normalizedGroup} · offset ${normalizedOffset}`,
    severity: 'write',
    command: `MQ ACK ${topic} GROUP ${normalizedGroup} OFFSET ${normalizedOffset}`,
    run: async () => {
      const response = await ackMqConsumer(auth.api, db, topic, {
        consumerGroup: normalizedGroup,
        offset: normalizedOffset,
      });
      return {
        action: 'ack',
        target: response.topic,
        succeeded: true,
        affected: 1,
        detail: `${response.consumerGroup} next offset ${response.nextOffset}`,
        nextOffset: response.nextOffset,
      };
    },
  });
}

async function browseFromInput(): Promise<void> {
  await loadMessages(fromOffset.value ?? retainedStartOffset.value, true);
}

async function loadMessages(offset: number, updateResult: boolean, topic = activeTopic.value): Promise<void> {
  if (!props.targetDb || !topic) return;
  loadingBrowse.value = true;
  errorMsg.value = '';
  const started = performance.now();
  const startOffset = Math.max(0, Math.trunc(offset));
  const command = `MQ BROWSE ${topic} FROM ${startOffset} LIMIT ${browseLimit.value}`;
  try {
    await loadTopicMetadata(topic);
    const response = await browseMqMessages(auth.api, props.targetDb, topic, {
      fromOffset: startOffset,
      maxCount: browseLimit.value,
    });
    rows.value = response.messages.map(mapMessage);
    fromOffset.value = startOffset;
    syncSelectedAfterRows();
    const elapsed = performanceElapsed(started);
    latestCommand.value = command;
    if (updateResult) {
      latestResult.value = resultFromMessages(rows.value, elapsed);
      ranOnce.value = true;
      recordHistory('success', 'SonnetMQ browse', 'browse', command, `${rows.value.length} messages`, rows.value.length, -1, elapsed);
    }
  } catch (error) {
    const elapsed = performanceElapsed(started);
    const msg = errorToMessage(error, '浏览 MQ 消息失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult(msg);
    ranOnce.value = true;
    recordHistory('error', 'SonnetMQ browse', 'browse', command, msg, 0, 0, elapsed);
  } finally {
    loadingBrowse.value = false;
  }
}

async function nextPage(): Promise<void> {
  if (rows.value.length === 0) return;
  await loadMessages(rows.value[rows.value.length - 1].offset + 1, true);
}

async function previousPage(): Promise<void> {
  const current = fromOffset.value ?? retainedStartOffset.value;
  await loadMessages(Math.max(retainedStartOffset.value, current - browseLimit.value), true);
}

async function seekByTime(): Promise<void> {
  if (!props.targetDb || !activeTopic.value || !seekTimeMs.value) return;
  loadingBrowse.value = true;
  errorMsg.value = '';
  const target = seekTimeMs.value;
  const pageSize = Math.min(1000, Math.max(100, browseLimit.value));
  let offset = retainedStartOffset.value;
  let scanned = 0;
  const maxWindows = 25;
  try {
    await loadTopicMetadata(activeTopic.value);
    for (let i = 0; i < maxWindows && offset < highWaterOffset.value; i += 1) {
      const response = await browseMqMessages(auth.api, props.targetDb, activeTopic.value, {
        fromOffset: offset,
        maxCount: pageSize,
      });
      const messages = response.messages;
      if (messages.length === 0) break;
      scanned += messages.length;
      const found = messages.find((item) => Date.parse(item.timestampUtc) >= target);
      if (found) {
        seekTimeMs.value = target;
        await loadMessages(found.offset, true);
        selectMessage(found.offset);
        return;
      }
      const last = messages[messages.length - 1];
      const lastTime = Date.parse(last.timestampUtc);
      if (Number.isFinite(lastTime) && lastTime >= target) break;
      offset = last.offset + 1;
    }
    message.warning(`No message found near that timestamp in the scanned ${scanned} message window.`);
  } catch (error) {
    errorMsg.value = errorToMessage(error, '按时间定位 MQ 消息失败');
  } finally {
    loadingBrowse.value = false;
  }
}

function selectTopic(topic: string): void {
  if (!topic) return;
  emit('selectTopic', topic);
  publishTopic.value = topic;
  fromOffset.value = Math.max(0, localTopics.value.find((item) => item.topic === topic)?.nextOffset ?? 0) > 0
    ? Math.max(0, (localTopics.value.find((item) => item.topic === topic)?.nextOffset ?? 0) - browseLimit.value)
    : 0;
}

function selectMessage(offset: number): void {
  selectedOffset.value = offset;
}

function clearRows(): void {
  rows.value = [];
  selectedOffset.value = null;
}

function stagePublish(): void {
  const db = props.targetDb;
  const topic = publishTopic.value.trim() || activeTopic.value;
  if (!db || !topic) {
    message.warning('Publish requires a database and topic.');
    return;
  }
  const encoded = encodeDraftPayload(publishPayload.value, publishMode.value);
  if (!encoded.ok) {
    message.error(encoded.message);
    return;
  }
  const headers = parseHeaders(publishHeadersText.value);
  if (!headers.ok) {
    message.error(headers.message);
    return;
  }
  stagePublishPayload(topic, encoded.base64, headers.headers, encoded.byteLength);
}

function stagePublishPayload(
  topic: string,
  payload: string,
  headers: Record<string, string>,
  byteLength: number,
): void {
  const db = props.targetDb;
  if (!db || !topic) return;
  const stagedHeaders = { ...headers };
  pendingOperations.value.push({
    id: makeOperationId('publish'),
    label: 'Publish',
    detail: `${topic} · ${byteLength} bytes · ${Object.keys(stagedHeaders).length} headers`,
    severity: 'write',
    command: `MQ PUBLISH ${topic} ${byteLength} bytes`,
    run: async () => {
      const response = await publishMqMessage(auth.api, db, topic, {
        payload,
        headers: Object.keys(stagedHeaders).length > 0 ? stagedHeaders : null,
      });
      return {
        action: 'publish',
        target: response.topic,
        succeeded: true,
        affected: 1,
        detail: `offset ${response.offset}`,
        offset: response.offset,
      };
    },
  });
}

async function onMessageFileSelected(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) return;
  try {
    const parsed = parseMessageImport(await file.text());
    if (!parsed.ok) {
      errorMsg.value = parsed.message;
      return;
    }
    pendingOperations.value = [];
    for (const item of parsed.messages) {
      stagePublishPayload(item.topic || activeTopic.value, item.payload, item.headers, base64ToBytes(item.payload).length);
    }
    activeSection.value = 'messages';
    errorMsg.value = '';
    message.success(`已解析 ${parsed.messages.length} 条消息，确认后按文件顺序发布。`);
  } finally {
    input.value = '';
  }
}

function parseMessageImport(text: string):
  | { ok: true; messages: Array<{ topic: string; payload: string; headers: Record<string, string> }> }
  | { ok: false; message: string } {
  const trimmed = text.trim();
  if (!trimmed) return { ok: false, message: '消息导入文件为空。' };
  try {
    const source: unknown = trimmed.startsWith('[')
      ? JSON.parse(trimmed) as unknown
      : trimmed.split(/\r?\n/u).filter(Boolean).map((line) => JSON.parse(line) as unknown);
    const items = Array.isArray(source) ? source : [source];
    if (items.length === 0) return { ok: false, message: '消息导入文件没有记录。' };
    const messages = items.map((item, index) => {
      if (!item || typeof item !== 'object' || Array.isArray(item)) {
        throw new Error(`第 ${index + 1} 条消息必须是 JSON 对象。`);
      }
      const record = item as Record<string, unknown>;
      const topic = typeof record.topic === 'string' ? record.topic.trim() : '';
      if (!topic && !activeTopic.value) throw new Error(`第 ${index + 1} 条消息缺少 topic。`);
      let payload = '';
      if (typeof record.payloadBase64 === 'string') {
        payload = bytesToBase64(base64ToBytes(record.payloadBase64));
      } else if (typeof record.payload === 'string') {
        payload = bytesToBase64(new TextEncoder().encode(record.payload));
      } else if (record.payload !== undefined) {
        payload = bytesToBase64(new TextEncoder().encode(JSON.stringify(record.payload)));
      } else {
        throw new Error(`第 ${index + 1} 条消息缺少 payloadBase64 或 payload。`);
      }
      const rawHeaders = record.headers;
      const headers: Record<string, string> = {};
      if (rawHeaders !== undefined && (!rawHeaders || typeof rawHeaders !== 'object' || Array.isArray(rawHeaders))) {
        throw new Error(`第 ${index + 1} 条消息的 headers 必须是对象。`);
      }
      for (const [key, value] of Object.entries((rawHeaders ?? {}) as Record<string, unknown>)) {
        headers[key] = String(value);
      }
      return { topic, payload, headers };
    });
    return { ok: true, messages };
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : '消息文件解析失败。' };
  }
}

async function confirmPendingOperations(): Promise<void> {
  if (pendingOperations.value.length === 0) return;
  confirmBusy.value = true;
  errorMsg.value = '';
  const operations = [...pendingOperations.value];
  const command = operations.map((operation) => operation.command).join('\n');
  const started = performance.now();
  try {
    const outcomes: OperationOutcome[] = [];
    for (const operation of operations) {
      outcomes.push(await operation.run());
    }
    const elapsed = performanceElapsed(started);
    const affected = outcomes.reduce((sum, item) => sum + item.affected, 0);
    latestCommand.value = command;
    latestResult.value = resultFromOutcomes(outcomes, elapsed);
    ranOnce.value = true;
    pendingOperations.value = [];
    const publishCount = outcomes.filter((item) => item.action === 'publish').length;
    const ackCount = outcomes.filter((item) => item.action === 'ack').length;
    const summary = [
      publishCount > 0 ? `${publishCount} publish` : '',
      ackCount > 0 ? `${ackCount} ack` : '',
      `affected ${affected}`,
    ].filter(Boolean).join(' · ');
    recordHistory('success', 'SonnetMQ operations', 'operation', command, summary, outcomes.length, affected, elapsed);
    message.success(`Applied ${outcomes.length} MQ operation${outcomes.length === 1 ? '' : 's'}.`);
    await refreshTopicList();
    const lastPublished = [...outcomes].reverse().find((outcome) => typeof outcome.offset === 'number');
    if (lastPublished) {
      emit('selectTopic', lastPublished.target);
      publishTopic.value = lastPublished.target;
      await loadTopicMetadata(lastPublished.target);
      await loadMessages(lastPublished.offset ?? 0, false, lastPublished.target);
      selectMessage(lastPublished.offset ?? 0);
    } else if (activeTopic.value) {
      await loadTopicMetadata(activeTopic.value);
    }
    emit('refreshSchema');
  } catch (error) {
    const elapsed = performanceElapsed(started);
    const msg = errorToMessage(error, '提交 MQ 操作失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult(msg);
    ranOnce.value = true;
    recordHistory('error', 'SonnetMQ operations', 'operation', command, msg, 0, 0, elapsed);
  } finally {
    confirmBusy.value = false;
  }
}

function clearPendingOperations(): void {
  pendingOperations.value = [];
}

function openHistoryEntry(entry: WorkbenchHistoryEntry): void {
  latestCommand.value = entry.command;
}

function syncSelectedAfterRows(): void {
  if (selectedOffset.value !== null && rows.value.some((row) => row.offset === selectedOffset.value)) return;
  selectedOffset.value = rows.value[0]?.offset ?? null;
}

function mapMessage(item: MqMessageResponse): MqRow {
  const payload = item.payload ?? '';
  const kind = classifyPayload(payload);
  return {
    topic: item.topic,
    offset: item.offset,
    timestampUtc: item.timestampUtc,
    headers: item.headers ?? {},
    payload,
    byteLength: base64ToBytes(payload).length,
    headerCount: Object.keys(item.headers ?? {}).length,
    payloadKind: kind,
    payloadPreview: previewPayload(payload, kind),
  };
}

function resultFromMessages(messages: MqRow[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['topic', 'offset', 'timestampUtc', 'headers', 'bytes', 'type', 'preview'],
    rows: messages.map((item) => [
      item.topic,
      item.offset,
      item.timestampUtc,
      item.headerCount,
      item.byteLength,
      item.payloadKind,
      item.payloadPreview,
    ]),
    end: {
      type: 'end',
      rowCount: messages.length,
      recordsAffected: -1,
      elapsedMs,
    },
    error: null,
    hasColumns: true,
  };
}

function resultFromOutcomes(outcomes: OperationOutcome[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['action', 'topic', 'succeeded', 'affected', 'detail'],
    rows: outcomes.map((item) => [item.action, item.target, item.succeeded, item.affected, item.detail]),
    end: {
      type: 'end',
      rowCount: outcomes.length,
      recordsAffected: outcomes.reduce((sum, item) => sum + item.affected, 0),
      elapsedMs,
    },
    error: null,
    hasColumns: true,
  };
}

function errorResult(messageText: string): SqlResultSet {
  return {
    columns: [],
    rows: [],
    end: null,
    error: { type: 'error', code: 'mq_error', message: messageText },
    hasColumns: false,
  };
}

function parseHeaders(text: string):
  | { ok: true; headers: Record<string, string> }
  | { ok: false; message: string } {
  const trimmed = text.trim();
  if (!trimmed) return { ok: true, headers: {} };
  if (trimmed.startsWith('{')) {
    try {
      const parsed = JSON.parse(trimmed) as unknown;
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
        return { ok: false, message: 'Headers JSON must be an object.' };
      }
      const headers: Record<string, string> = {};
      for (const [key, value] of Object.entries(parsed)) {
        headers[key] = String(value);
      }
      return { ok: true, headers };
    } catch (error) {
      return { ok: false, message: error instanceof Error ? error.message : 'Invalid headers JSON.' };
    }
  }
  const headers: Record<string, string> = {};
  for (const line of trimmed.split(/\r?\n/g).map((item) => item.trim()).filter(Boolean)) {
    const index = line.indexOf('=');
    if (index <= 0) return { ok: false, message: `Invalid header line: ${line}` };
    headers[line.slice(0, index).trim()] = line.slice(index + 1).trim();
  }
  return { ok: true, headers };
}

function encodeDraftPayload(text: string, mode: PayloadView):
  | { ok: true; base64: string; byteLength: number }
  | { ok: false; message: string } {
  try {
    if (mode === 'hex') {
      const bytes = hexToBytes(text);
      return { ok: true, base64: bytesToBase64(bytes), byteLength: bytes.length };
    }
    if (mode === 'base64') {
      const bytes = base64ToBytes(text.trim());
      return { ok: true, base64: bytesToBase64(bytes), byteLength: bytes.length };
    }
    if (mode === 'json') {
      JSON.parse(text);
    }
    const bytes = new TextEncoder().encode(text);
    return { ok: true, base64: bytesToBase64(bytes), byteLength: bytes.length };
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : 'Invalid payload.' };
  }
}

function classifyPayload(base64: string): PayloadKind {
  const text = tryDecodeUtf8(base64);
  if (!text.ok) return 'binary';
  const trimmed = text.text.trim();
  if ((trimmed.startsWith('{') && trimmed.endsWith('}')) || (trimmed.startsWith('[') && trimmed.endsWith(']'))) {
    try {
      JSON.parse(trimmed);
      return 'json';
    } catch {
      return 'text';
    }
  }
  return isMostlyPrintable(text.text) ? 'text' : 'binary';
}

function previewPayload(base64: string, kind: PayloadKind): string {
  if (kind === 'binary') {
    return toHex(base64ToBytes(base64)).slice(0, 120);
  }
  const decoded = tryDecodeUtf8(base64);
  if (!decoded.ok) return '';
  const text = decoded.text.replace(/\s+/g, ' ').trim();
  return text.length > 180 ? `${text.slice(0, 177)}...` : text;
}

function formatPayload(base64: string, mode: PayloadView): string {
  if (mode === 'base64') return base64;
  const bytes = base64ToBytes(base64);
  if (mode === 'hex') return toHex(bytes);
  const decoded = tryDecodeUtf8(base64);
  if (!decoded.ok) return `Binary payload (${bytes.length} bytes). Use Hex or Base64 view.`;
  if (mode === 'json') {
    try {
      return JSON.stringify(JSON.parse(decoded.text), null, 2);
    } catch {
      return decoded.text;
    }
  }
  return decoded.text;
}

function tryDecodeUtf8(base64: string): { ok: true; text: string } | { ok: false } {
  try {
    return { ok: true, text: new TextDecoder('utf-8', { fatal: true }).decode(base64ToBytes(base64)) };
  } catch {
    return { ok: false };
  }
}

function isMostlyPrintable(text: string): boolean {
  if (!text) return true;
  let printable = 0;
  for (const ch of text) {
    const code = ch.charCodeAt(0);
    if (code === 9 || code === 10 || code === 13 || code >= 32) printable += 1;
  }
  return printable / text.length > 0.9;
}

function base64ToBytes(base64: string): Uint8Array {
  if (!base64) return new Uint8Array();
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = '';
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    const chunk = bytes.subarray(i, i + chunkSize);
    binary += String.fromCharCode(...chunk);
  }
  return btoa(binary);
}

function hexToBytes(text: string): Uint8Array {
  const normalized = text.replace(/\s+/g, '');
  if (normalized.length % 2 !== 0 || /[^0-9a-f]/i.test(normalized)) {
    throw new Error('Hex payload must contain an even number of hexadecimal characters.');
  }
  const bytes = new Uint8Array(normalized.length / 2);
  for (let i = 0; i < normalized.length; i += 2) {
    bytes[i / 2] = Number.parseInt(normalized.slice(i, i + 2), 16);
  }
  return bytes;
}

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(' ');
}

function formatTimestamp(value?: string | null): string {
  if (!value) return '-';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function formatStat(value?: number | null): string {
  return typeof value === 'number' ? value.toLocaleString() : '-';
}

function formatCompactNumber(value?: number | null): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '-';
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`;
  if (value >= 1_000) return `${(value / 1_000).toFixed(1)}k`;
  return value >= 100 ? value.toFixed(0) : value.toFixed(1);
}

function formatRate(value?: number | null): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '- /s';
  return `${formatCompactNumber(value)} /s`;
}

function formatBytes(value?: number | null): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '-';
  if (value >= 1024 ** 3) return `${(value / 1024 ** 3).toFixed(2)} GiB`;
  if (value >= 1024 ** 2) return `${(value / 1024 ** 2).toFixed(1)} MiB`;
  if (value >= 1024) return `${(value / 1024).toFixed(1)} KiB`;
  return `${value.toFixed(0)} B`;
}

function formatDurationSeconds(value?: number | null): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return 'off';
  if (value >= 86_400) return `${(value / 86_400).toFixed(1)} d`;
  if (value >= 3_600) return `${(value / 3_600).toFixed(1)} h`;
  if (value >= 60) return `${(value / 60).toFixed(1)} min`;
  return `${value.toFixed(0)} s`;
}

function trendY(value: number): number {
  const top = 10;
  const bottom = 112;
  return bottom - (Math.max(0, value) / trendMax.value) * (bottom - top);
}

function pushTrendSample(): void {
  if (!activeTopic.value) return;
  const next = [
    ...trendSamples.value,
    {
      at: Date.now(),
      nextOffset: highWaterOffset.value,
      totalLag: totalLag.value,
      ackedOffset: ackedOffsetTotal.value,
    },
  ];
  if (next.length > 80) next.splice(0, next.length - 80);
  trendSamples.value = next;
}

function findDlqTopic(topic: string, topics: MqTopicInfo[]): MqTopicInfo | null {
  if (!topic) return null;
  const exactNames = [
    `${topic}.dlq`,
    `${topic}-dlq`,
    `${topic}_dlq`,
    `${topic}.dead-letter`,
    `dlq.${topic}`,
    `dead-letter.${topic}`,
  ];
  for (const name of exactNames) {
    const exact = topics.find((item) => item.topic.toLowerCase() === name.toLowerCase());
    if (exact) return exact;
  }
  return topics.find((item) => item.topic.toLowerCase().startsWith(`${topic.toLowerCase()}.`)
    && (item.topic.toLowerCase().includes('.dlq') || item.topic.toLowerCase().includes('.dead'))) ?? null;
}

function startAutoRefresh(): void {
  stopAutoRefresh();
  if (!autoRefresh.value || !activeTopic.value) return;
  autoRefreshTimer = setInterval(() => {
    void refreshMonitorOnly();
  }, 5_000);
}

function stopAutoRefresh(): void {
  if (autoRefreshTimer) {
    clearInterval(autoRefreshTimer);
    autoRefreshTimer = null;
  }
}

function makeOperationId(prefix: string): string {
  return `${prefix}_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function performanceElapsed(started: number): number {
  return started > 0 ? performance.now() - started : 0;
}

function errorToMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const response = (error as { response?: { data?: unknown; status?: number } }).response;
    if (response?.data && typeof response.data === 'object') {
      const data = response.data as Record<string, unknown>;
      if (typeof data.message === 'string') return data.message;
      if (typeof data.error === 'string') return data.error;
    }
    if (typeof (error as { message?: unknown }).message === 'string') {
      return (error as { message: string }).message;
    }
  }
  return fallback;
}

async function copyPayload(): Promise<void> {
  const row = selectedMessage.value;
  if (!row) return;
  await copyText(formatPayload(row.payload, payloadView.value), 'Payload copied');
}

async function copyHeaders(): Promise<void> {
  await copyText(selectedHeadersText.value, 'Headers copied');
}

async function copyText(text: string, success: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    message.success(success);
  } catch {
    message.warning(text);
  }
}

function recordHistory(
  status: 'success' | 'error',
  title: string,
  action: string,
  command: string,
  summary: string,
  rowCount: number,
  recordsAffected: number,
  elapsedMs: number,
): void {
  history.record({
    kind: action === 'browse' ? 'query' : 'operation',
    status,
    title,
    target: activeTopic.value || publishTopic.value,
    database: props.targetDb,
    connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name,
    model: 'mq',
    action,
    command,
    summary,
    rowCount,
    recordsAffected,
    elapsedMs,
  });
}

watch(
  () => props.topics,
  (topics) => {
    syncLocalTopics(topics);
  },
  { immediate: true },
);

watch(
  () => [props.targetDb, props.topic] as const,
  () => {
    clearRows();
    stats.value = null;
    offsets.value = null;
    retention.value = null;
    trendSamples.value = [];
    ackConsumerGroup.value = null;
    ackOffset.value = null;
    pendingOperations.value = [];
    publishTopic.value = activeTopic.value;
    const topic = activeTopic.value;
    if (topic) {
      void loadTopicMetadata(topic).then(() => {
        fromOffset.value = retainedStartOffset.value;
        void loadMessages(retainedStartOffset.value, false);
      }).catch((error: unknown) => {
        errorMsg.value = errorToMessage(error, '加载 MQ topic 元数据失败');
      });
    }
  },
);

watch(
  () => [autoRefresh.value, props.targetDb, props.topic] as const,
  () => {
    startAutoRefresh();
  },
);

onMounted(() => {
  syncInspectorForViewport();
  window.addEventListener('resize', syncInspectorForViewport);
  publishTopic.value = activeTopic.value;
  void refreshAll();
  startAutoRefresh();
});

onBeforeUnmount(() => {
  window.removeEventListener('resize', syncInspectorForViewport);
  stopAutoRefresh();
});
</script>

<style scoped>
.mq-workbench {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  background: #fff;
}

.mq-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  min-height: 78px;
  padding: 12px 18px;
  border-bottom: 1px solid var(--sndb-border);
  background: #fff;
}

.mq-toolbar__identity {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.mq-toolbar__title {
  color: var(--sndb-ink-strong);
  font-size: 21px;
  font-weight: 650;
}

.mq-toolbar__meta,
.mq-panel-head__meta {
  font-size: 12px;
}

.mq-toolbar__actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: nowrap;
}

.mq-file-input {
  display: none;
}

.mq-headline-stats {
  display: grid;
  grid-template-columns: repeat(3, minmax(96px, 1fr));
  margin-left: auto;
  border: 1px solid var(--sndb-border);
  border-radius: 5px;
  overflow: hidden;
}

.mq-headline-stats span {
  display: flex;
  flex-direction: column;
  min-width: 96px;
  padding: 7px 12px;
  border-right: 1px solid var(--sndb-border);
}

.mq-headline-stats span:last-child {
  border-right: 0;
}

.mq-headline-stats small {
  color: var(--sndb-ink-muted);
  font-size: 12px;
}

.mq-headline-stats strong {
  font-size: 20px;
  font-weight: 500;
}

.mq-headline-stats .is-warning strong {
  color: var(--sndb-warning);
}

.mq-headline-stats .is-success strong {
  color: var(--sndb-success);
}

.mq-section-tabs {
  display: flex;
  flex: 0 0 46px;
  align-items: stretch;
  gap: 4px;
  padding: 0 14px;
  border-bottom: 1px solid var(--sndb-border);
  background: #fff;
}

.mq-section-tabs button {
  position: relative;
  min-width: 76px;
  padding: 0 12px;
  border: 0;
  background: transparent;
  color: var(--sndb-ink-muted);
  font: inherit;
  cursor: pointer;
}

.mq-section-tabs button:hover {
  color: var(--sndb-ink-strong);
}

.mq-section-tabs button.is-active {
  color: var(--sndb-interactive);
  font-weight: 600;
}

.mq-section-tabs button.is-active::after {
  position: absolute;
  right: 8px;
  bottom: 0;
  left: 8px;
  height: 2px;
  background: var(--sndb-interactive);
  content: '';
}

.mq-toolbar__topic {
  width: 170px;
}

.mq-toolbar__offset {
  width: 104px;
}

.mq-toolbar__time {
  width: 190px;
}

.mq-toolbar__limit {
  width: 128px;
}

.mq-alert {
  margin: 10px 12px 0;
}

.mq-stats {
  display: grid;
  grid-template-columns: repeat(6, minmax(110px, 1fr));
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.mq-stat {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
  padding: 9px 12px;
  border-right: 1px solid rgba(15, 23, 42, 0.08);
}

.mq-stat span {
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.mq-stat strong {
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 16px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mq-monitor {
  display: grid;
  grid-template-columns: minmax(340px, 1.2fr) minmax(320px, 1fr) minmax(320px, 1fr);
  gap: 0;
  flex: 1;
  min-height: 0;
  overflow: auto;
  border-bottom: 1px solid var(--sndb-border);
  background: #fff;
}

.mq-monitor.is-consumers,
.mq-monitor.is-configuration {
  grid-template-columns: minmax(0, 1fr);
}

.mq-monitor-pane {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 280px;
  border-right: 1px solid var(--sndb-border);
}

.mq-monitor-pane:last-child {
  border-right: 0;
}

.mq-panel-head--compact {
  padding: 9px 12px;
}

.mq-consumer-grid {
  flex: 1;
  min-height: 0;
}

.mq-consumer-name {
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mq-progress {
  position: relative;
  overflow: hidden;
  height: 18px;
  border-radius: 999px;
  background: rgba(13, 59, 102, 0.08);
}

.mq-progress span {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: linear-gradient(90deg, rgba(82, 183, 136, 0.85), rgba(44, 123, 229, 0.85));
}

.mq-progress em {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #17324d;
  font-size: 11px;
  font-style: normal;
  font-weight: 800;
}

.mq-ack-editor {
  display: grid;
  grid-template-columns: minmax(130px, 1fr) 100px auto auto auto;
  gap: 8px;
  padding: 9px 12px 10px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfcfe;
}

.mq-rate-strip,
.mq-retention-grid {
  display: grid;
  gap: 8px;
  padding: 10px 12px;
}

.mq-rate-strip {
  grid-template-columns: repeat(3, 1fr);
}

.mq-rate-strip span,
.mq-retention-grid span {
  min-width: 0;
  padding: 7px 8px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fbfcfe;
}

.mq-rate-strip small,
.mq-retention-grid small {
  display: block;
  color: var(--sndb-ink-soft);
  font-size: 10px;
  font-weight: 800;
  text-transform: uppercase;
}

.mq-rate-strip strong,
.mq-retention-grid strong {
  display: block;
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 14px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mq-sparkline {
  flex: 1;
  min-height: 104px;
  padding: 0 12px;
}

.mq-sparkline.is-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.mq-sparkline svg {
  width: 100%;
  height: 126px;
  display: block;
}

.mq-sparkline line {
  stroke: rgba(13, 59, 102, 0.1);
  stroke-width: 1;
}

.mq-sparkline text {
  fill: rgba(13, 59, 102, 0.55);
  font-size: 10px;
}

.mq-trend-legend {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  padding: 6px 12px 10px;
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
}

.mq-trend-legend span {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}

.mq-trend-legend i {
  width: 8px;
  height: 8px;
  border-radius: 999px;
}

.mq-retention-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.mq-dlq-state {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  margin: auto 12px 10px;
  padding: 9px 10px;
  border-radius: 6px;
  background: rgba(232, 93, 117, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.mq-body {
  position: relative;
  display: grid;
  flex: 1;
  min-height: 0;
  grid-template-columns: minmax(420px, 1fr) minmax(320px, 384px);
  min-width: 0;
  overflow: hidden;
}

.mq-body.is-inspector-collapsed {
  grid-template-columns: minmax(0, 1fr);
}

.mq-topics,
.mq-message-panel,
.mq-inspector {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.mq-topics {
  border-right: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfcfe;
}

.mq-message-panel {
  background: #fff;
}

.mq-inspector {
  border-left: 1px solid var(--sndb-border);
  background: #fff;
}

.mq-inspector__actions {
  display: flex;
  align-items: center;
  gap: 4px;
}

.mq-panel-head {
  display: flex;
  flex: 0 0 auto;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  min-height: 52px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--sndb-border);
}

.mq-message-tools {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--sndb-border);
  background: #fbfcfd;
}

.mq-panel-head--grid {
  align-items: center;
}

.mq-panel-head__title,
.mq-section-title {
  display: block;
  color: var(--sndb-ink-strong);
  font-weight: 800;
}

.mq-topic-filter {
  flex: 0 0 auto;
  margin: 8px;
  width: calc(100% - 16px);
}

.mq-topic-list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-height: 0;
  overflow: auto;
  padding: 0 8px 8px;
}

.mq-topic-card,
.mq-offset-button {
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.mq-topic-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  width: 100%;
  min-width: 0;
  padding: 8px;
  border-left: 2px solid rgba(32, 128, 240, 0.35);
  border-radius: 6px;
  text-align: left;
}

.mq-topic-card:hover,
.mq-topic-card.is-active,
.mq-offset-button:hover,
.mq-offset-button.is-active {
  background: rgba(32, 128, 240, 0.09);
}

.mq-topic-card.is-active {
  border-left-color: rgba(32, 128, 240, 0.9);
}

.mq-topic-card span {
  width: 100%;
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mq-topic-card small {
  color: var(--sndb-ink-soft);
  font-size: 11px;
}

.mq-grid {
  flex: 1;
  min-height: 0;
}

.mq-grid :deep(.n-data-table-base-table-body) {
  min-height: 260px;
}

.mq-offset-button {
  max-width: 100%;
  overflow: hidden;
  padding: 2px 5px;
  border-radius: 4px;
  color: var(--sndb-brand);
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  font-weight: 800;
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mq-time-cell {
  color: #31465d;
  font-size: 12px;
}

.mq-preview-cell {
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  color: #345;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mq-pager {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.mq-detail-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 9px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.mq-detail-strip span {
  padding: 2px 7px;
  border-radius: 4px;
  background: var(--sndb-hover);
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
}

.mq-headers {
  padding: 10px 12px 0;
}

.mq-section-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 6px;
}

.mq-section-title--standalone {
  margin-bottom: 0;
}

.mq-headers pre,
.mq-payload-preview {
  overflow: auto;
  padding: 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
  color: #24384f;
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.mq-headers pre {
  max-height: 110px;
  margin: 0;
}

.mq-payload-tabs {
  flex: 0 0 auto;
  padding: 8px 12px 0;
}

.mq-payload-preview {
  flex: 0 0 170px;
  min-height: 120px;
  max-height: 220px;
  margin: 8px 12px;
}

.mq-publisher {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

.mq-result {
  flex: 0 0 240px;
  min-height: 220px;
  border-top: 1px solid var(--sndb-border);
}

@media (max-width: 1360px) {
  .mq-monitor {
    grid-template-columns: 1fr;
  }

  .mq-monitor-pane {
    border-right: 0;
    border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  }

  .mq-monitor-pane:last-child {
    border-bottom: 0;
  }

  .mq-body {
    grid-template-columns: minmax(420px, 1fr) minmax(300px, 340px);
  }
}

@media (min-width: 981px) and (max-width: 1439px) {
  .mq-body {
    grid-template-columns: minmax(0, 1fr);
  }

  .mq-inspector {
    position: absolute;
    z-index: 8;
    top: 0;
    right: 0;
    bottom: 0;
    width: min(384px, 42vw);
    box-shadow: -12px 0 28px rgba(23, 33, 43, 0.12);
  }
}

@media (max-width: 980px) {
  .mq-toolbar,
  .mq-panel-head--grid,
  .mq-pager {
    flex-direction: column;
    align-items: stretch;
  }

  .mq-toolbar {
    min-height: auto;
  }

  .mq-headline-stats {
    margin-left: 0;
  }

  .mq-message-tools {
    flex-wrap: wrap;
  }

  .mq-body {
    grid-template-columns: 1fr;
    overflow: auto;
  }

  .mq-body:not(.is-inspector-collapsed) .mq-message-panel {
    display: none;
  }

  .mq-topics,
  .mq-inspector {
    border-right: 0;
    border-left: 0;
  }

  .mq-inspector {
    height: 100%;
    min-height: 0;
    overflow: auto;
    border-top: 1px solid var(--sndb-border);
  }

  .mq-stats {
    grid-template-columns: repeat(2, minmax(120px, 1fr));
  }

  .mq-ack-editor,
  .mq-rate-strip,
  .mq-retention-grid {
    grid-template-columns: 1fr;
  }

  .mq-toolbar__topic,
  .mq-toolbar__offset,
  .mq-toolbar__time,
  .mq-toolbar__limit {
    width: 100%;
  }
}
</style>
