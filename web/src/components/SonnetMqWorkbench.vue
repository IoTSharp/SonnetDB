<template>
  <main class="mq-workbench" data-testid="workbench-mq" :data-page-state="mqState" :data-database="targetDb" :data-resource-key="`mq:${activeTopic}`">
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

      <div v-if="!permissionDenied" class="mq-headline-stats">
        <span><small>消息</small><strong>{{ formatStat(stats?.messageCount ?? 0) }}</strong></span>
        <span class="is-warning"><small>消费滞后</small><strong>{{ formatStat(totalLag) }}</strong></span>
        <span class="is-success"><small>消费者</small><strong>{{ consumerRows.length }}</strong></span>
      </div>

      <div class="mq-toolbar__actions">
        <n-button type="primary" :disabled="!canWrite" @click="openPublisher">
          <template #icon><Send :size="16" /></template>
          发布测试消息
        </n-button>
        <n-button quaternary title="导入消息文件" :disabled="!canWrite" @click="messageFileInput?.click()">
          <template #icon><Upload :size="17" /></template>
        </n-button>
        <input ref="messageFileInput" type="file" accept=".json,.jsonl,.ndjson,application/json,application/x-ndjson" class="mq-file-input" @change="onMessageFileSelected">
        <n-button quaternary title="刷新主题" :disabled="permissionDenied" :loading="loading" @click="refreshAll">
          <template #icon><RefreshCw :size="17" /></template>
        </n-button>
        <n-button quaternary title="操作历史" :disabled="permissionDenied" @click="historyVisible = true">
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

    <n-alert v-if="permissionDenied" type="warning" class="mq-alert" data-testid="mq-permission">当前身份没有 MQ 访问权限。</n-alert>
    <n-alert v-else-if="readOnly" type="info" class="mq-alert" data-testid="mq-readonly">当前资源为只读；发布、导入和 Ack 已禁用。</n-alert>
    <n-text v-if="!permissionDenied" depth="3" class="mq-alert" data-testid="mq-preview-budget">每窗最多 1000 条；{{ previewTruncated ? '超返已截断 truncated；' : '' }}这是有界消息预览。Topic 属于数据库逻辑作用域，持久化位于实例 .system/mq；单库备份不覆盖实例 MQ。</n-text>

    <WriteApprovalPanel
      v-if="previewPlan && canWrite"
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

    <section v-if="!permissionDenied && activeSection !== 'messages'" class="mq-monitor" :class="`is-${activeSection}`">
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
          <n-button size="small" quaternary :disabled="!canWrite || !selectedMessage || !isSafeOffset(selectedMessage.offset) || !ackConsumerGroup" @click="stageAckSelected">
            Selected
          </n-button>
          <n-button size="small" quaternary :disabled="!canWrite || !isSafeOffset(highWaterOffset) || highWaterOffset <= 0 || !ackConsumerGroup" @click="stageAckHighWater">
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

    <section v-if="!permissionDenied && activeSection === 'messages'" class="mq-body" :class="{ 'is-inspector-collapsed': inspectorCollapsed }">
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
            {{ autoRefresh ? '停止采样' : '自动采样' }}
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
          <n-button secondary :disabled="rows.length === 0" data-testid="mq-export-jsonl" @click="exportMessages">导出 JSONL</n-button>
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
            <n-text depth="3" data-testid="mq-header-budget">Headers 最多预览 32 项、4096 字符；超限截断 truncated。</n-text>
          </section>

          <n-tabs v-model:value="payloadView" type="segment" size="small" class="mq-payload-tabs">
            <n-tab name="text" tab="Text" />
            <n-tab name="json" tab="JSON" />
            <n-tab name="hex" tab="Hex" />
            <n-tab name="base64" tab="Base64" />
          </n-tabs>
          <pre class="mq-payload-preview">{{ selectedPayloadText }}</pre>
          <n-text depth="3" data-testid="mq-payload-budget">Payload 仅预览前 4096 原始字节；{{ selectedMessage.byteLength > 4096 ? '超限截断 truncated；' : '' }}JSONL 保留当前窗口完整消息。</n-text>
        </template>
        <n-empty v-else description="Select a message from the browser." />

        <section v-if="publisherVisible && canWrite" class="mq-publisher">
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
            <n-button size="small" type="primary" :disabled="!canWrite" @click="stagePublish">
              Stage publish
            </n-button>
            <n-button size="small" secondary :disabled="!selectedMessage" @click="copyPayload">
              Copy payload
            </n-button>
          </n-space>
        </section>
      </aside>
    </section>

    <n-button v-if="!permissionDenied" secondary data-testid="mq-open-result" @click="resultVisible = true">打开结果</n-button>
    <n-drawer v-if="!permissionDenied" v-model:show="resultVisible" :width="760" placement="right">
    <n-drawer-content title="SonnetMQ operation result" closable>
    <WorkbenchResultPanel
      inline
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
    </n-drawer-content>
    </n-drawer>

    <WorkbenchHistoryDrawer
      v-if="!permissionDenied"
      v-model:show="historyVisible"
      :active-database="targetDb"
      @select="openHistoryEntry"
    />
  </main>
</template>

<script setup lang="ts">
import { computed, h, markRaw, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
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
  NDrawer,
  NDrawerContent,
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
import { createApiClient } from '@/api/client';
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
  type WorkbenchHistoryCompleteness,
} from '@/stores/workbenchHistory';
import {
  createWriteApprovalPlan,
  type WriteApprovalItem,
  type WriteApprovalPlan,
  type WriteApprovalSeverity,
} from '@/utils/writeApproval';
import { downloadText, safeFileStem } from '@/utils/resultExport';

const props = withDefaults(defineProps<{
  targetDb: string;
  topic: string;
  topics?: MqTopicInfo[];
  loading?: boolean;
  readOnly?: boolean;
  permissionDenied?: boolean;
}>(), {
  topics: () => [],
  loading: false,
  readOnly: false,
  permissionDenied: false,
});

const emit = defineEmits<{
  selectTopic: [topic: string];
  refreshSchema: [];
}>();

type PayloadView = 'text' | 'json' | 'hex' | 'base64';
type PayloadKind = 'json' | 'text' | 'binary';
type MqSection = 'overview' | 'messages' | 'consumers' | 'configuration';

interface MqContext {
  revision: number;
  database: string;
  topic: string;
  connectionId: string;
  connectionName: string;
  baseUrl: string | undefined;
  profileBaseUrl: string | undefined;
  token: string | undefined;
  sourceApi: ReturnType<typeof useAuthStore>['api'];
  api: ReturnType<typeof useAuthStore>['api'];
}

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
  context: MqContext;
  targetTopic: string;
  run: (signal: AbortSignal) => Promise<OperationOutcome>;
}

interface OperationOutcome {
  action: string;
  target: string;
  succeeded: boolean;
  affected: number;
  detail: string;
  offset?: number;
  nextOffset?: number;
  state?: 'completed' | 'failed' | 'unknown';
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
const MqPreviewMessageBudget = 1000;
const MqInspectorByteBudget = 4096;
const permissionLocked = ref(false);
const permissionDenied = computed(() => Boolean(props.permissionDenied || permissionLocked.value));
const readOnly = computed(() => Boolean(props.readOnly));
const canWrite = computed(() => Boolean(!disposed && !permissionDenied.value && !readOnly.value && props.targetDb && !confirmBusy.value));

const localTopics = ref<MqTopicInfo[]>([]);
const offsets = ref<MqOffsetsResponse | null>(null);
const stats = ref<MqStatsResponse | null>(null);
const retention = ref<MqRetentionResponse | null>(null);
const rows = ref<MqRow[]>([]);
const previewTruncated = ref(false);
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
const runningOperations = ref<PendingOperation[]>([]);
const confirmBusy = ref(false);
const latestResult = ref<SqlResultSet | null>(null);
const latestCommand = ref('');
const ranOnce = ref(false);
const historyVisible = ref(false);
const resultVisible = ref(false);
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

let autoRefreshTimer: ReturnType<typeof setTimeout> | null = null;
let autoRefreshDeadlineTimer: ReturnType<typeof setTimeout> | null = null;
let autoMonitorController: AbortController | null = null;
let compactViewport = false;
let disposed = false;
let contextRevision = 0;
let changingContext = false;
let deniedContext: MqContext | null = null;
let topicsRequestId = 0;
let monitorRequestId = 0;
let browseRequestId = 0;
let writeRequestId = 0;
let fileRequestId = 0;
let topicsController: AbortController | null = null;
let monitorController: AbortController | null = null;
let browseController: AbortController | null = null;
let writeController: AbortController | null = null;
let autoRefreshGeneration = 0;

const mqState = computed(() => permissionDenied.value ? 'permission' : readOnly.value ? 'readonly'
  : errorMsg.value ? 'error' : !activeTopic.value || rows.value.length === 0 ? 'empty'
  : previewTruncated.value || rows.value.length > 100 || (selectedMessage.value?.byteLength ?? 0) > MqInspectorByteBudget ? 'longContent' : 'normal');

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
  if (!row) return '{}';
  return formatHeaders(row.headers);
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
  Boolean(canWrite.value && activeTopic.value && ackConsumerGroup.value?.trim() && isSafeOffset(ackOffset.value)));

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
  Boolean(!permissionDenied.value && activeTopic.value && isSafeOffset(fromOffset.value) && isSafeOffset(retainedStartOffset.value) && (fromOffset.value ?? 0) > retainedStartOffset.value && !loadingBrowse.value));

const canPageForward = computed(() => {
  if (!activeTopic.value || loadingBrowse.value || rows.value.length === 0) return false;
  const last = rows.value[rows.value.length - 1];
  return !permissionDenied.value && isSafeOffset(last.offset) && isSafeOffset(last.offset + 1) && isSafeOffset(highWaterOffset.value) && last.offset + 1 < highWaterOffset.value;
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
  const operations = confirmBusy.value ? runningOperations.value : pendingOperations.value;
  if (operations.length === 0) return null;
  const first = operations[0]!;
  const items: WriteApprovalItem[] = operations.map((operation) => ({
    id: operation.id,
    command: operation.command,
    severity: operation.severity,
    label: operation.label,
    detail: operation.detail,
  }));
  return createWriteApprovalPlan({
    id: `mq_${first.context.revision}_${operations.map((item) => item.id).join('_')}`,
    title: 'SonnetMQ staged operations',
    target: `${first.context.connectionName}: ${first.context.database} · ${[...new Set(operations.map((item) => item.targetTopic))].join(', ')}`,
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
  if (!canWrite.value) return;
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
  if (permissionDenied.value || disposed) return;
  localTopics.value = [...topics].sort((a, b) => a.topic.localeCompare(b.topic));
}

async function refreshAll(): Promise<void> {
  if (disposed || permissionDenied.value || !props.targetDb) return;
  let context = captureContext();
  loading.value = true;
  errorMsg.value = '';
  try {
    await refreshTopicList();
    if (!context.topic && context.revision === contextRevision && !permissionDenied.value) context = captureContext();
    if (!isCurrentContext(context)) return;
    if (context.topic) {
      await loadTopicMetadata(context.topic);
      if (!isCurrentContext(context)) return;
      await loadMessages(fromOffset.value ?? retainedStartOffset.value, true);
    }
  } catch (error) {
    if (isCurrentContext(context)) {
      if (isPermissionError(error)) lockPermission(context);
      else errorMsg.value = errorToMessage(error, '刷新 SonnetMQ 工作台失败');
    }
  } finally {
    if (context.revision === contextRevision) loading.value = false;
  }
}

async function refreshTopicList(): Promise<void> {
  if (disposed || permissionDenied.value || !props.targetDb) return;
  const context = captureContext();
  const requestId = ++topicsRequestId;
  topicsController?.abort();
  const controller = topicsController = new AbortController();
  try {
    const topics = await fetchMqTopics(context.api, context.database, controller.signal);
    if (isCurrentContext(context) && requestId === topicsRequestId && !controller.signal.aborted) syncLocalTopics(topics);
  } catch (error) {
    if (isCurrentContext(context) && requestId === topicsRequestId && !controller.signal.aborted) {
      if (isPermissionError(error)) lockPermission(context);
      else errorMsg.value = errorToMessage(error, '加载 MQ Topics 失败');
    }
  } finally {
    if (topicsController === controller) topicsController = null;
  }
}

async function loadTopicMetadata(topic: string): Promise<void> {
  if (disposed || permissionDenied.value || !props.targetDb || !topic || topic !== activeTopic.value) return;
  const context = captureContext();
  const requestId = ++monitorRequestId;
  monitorController?.abort();
  const controller = monitorController = new AbortController();
  try {
    const [nextStats, nextOffsets, nextRetention] = await Promise.all([
      guardMetadata(fetchMqStats(context.api, context.database, context.topic, controller.signal), context, controller),
      guardMetadata(fetchMqOffsets(context.api, context.database, context.topic, controller.signal), context, controller),
      guardMetadata(fetchMqRetention(context.api, context.database, context.topic, controller.signal), context, controller),
    ]);
    if (!isCurrentContext(context) || requestId !== monitorRequestId || controller.signal.aborted) return;
    if (nextStats.topic !== context.topic || nextOffsets.topic !== context.topic || nextRetention.topic !== context.topic) {
      errorMsg.value = 'MQ 元数据目标不匹配，请重新采样。';
      return;
    }
    stats.value = nextStats;
    offsets.value = nextOffsets;
    retention.value = nextRetention;
    if (!ackConsumerGroup.value && nextOffsets.consumers.length > 0) ackConsumerGroup.value = nextOffsets.consumers[0].consumerGroup;
    if (ackOffset.value === null && isSafeOffset(nextStats.nextOffset)) ackOffset.value = Math.max(0, nextStats.nextOffset - 1);
    pushTrendSample();
  } catch (error) {
    if (isCurrentContext(context) && requestId === monitorRequestId && !controller.signal.aborted) {
      if (isPermissionError(error)) lockPermission(context);
      else errorMsg.value = errorToMessage(error, '加载 MQ 元数据失败');
    }
  } finally {
    if (monitorController === controller) monitorController = null;
  }
}

async function refreshMonitorOnly(): Promise<void> {
  if (disposed || permissionDenied.value || !activeTopic.value || loadingMonitor.value) return;
  const context = captureContext();
  const requestId = monitorRequestId + 1;
  loadingMonitor.value = true;
  errorMsg.value = '';
  try {
    await loadTopicMetadata(activeTopic.value);
  } catch (error) {
    if (isCurrentContext(context)) errorMsg.value = errorToMessage(error, '刷新 MQ 监控采样失败');
  } finally {
    if (context.revision === contextRevision && requestId === monitorRequestId) loadingMonitor.value = false;
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
  if (!canWrite.value || !ackConsumerGroup.value || !isSafeOffset(highWaterOffset.value) || highWaterOffset.value <= 0) return;
  stageAck(ackConsumerGroup.value, highWaterOffset.value - 1);
}

function stageAck(consumerGroup: string, offset: number): void {
  if (!canWrite.value) return;
  const db = props.targetDb;
  const topic = activeTopic.value;
  if (!db || !topic || !consumerGroup.trim() || !isSafeOffset(offset)) {
    message.warning('Ack 需要数据库、Topic、消费者组及安全非负整数 offset。');
    return;
  }
  const context = captureContext();
  enqueueOperation({
    id: makeOperationId('ack'),
    label: 'Ack consumer offset',
    detail: `${topic} · ${consumerGroup} · offset ${offset}`,
    severity: 'write',
    command: `MQ ACK ${topic} GROUP ${consumerGroup} OFFSET ${offset}`,
    context,
    targetTopic: topic,
    run: async (signal) => {
      const response = await ackMqConsumer(context.api, db, topic, { consumerGroup, offset }, signal);
      if (response?.topic !== topic || response?.consumerGroup !== consumerGroup || !isSafeOffset(response?.nextOffset)) throw new Error('Missing MQ terminal');
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

async function loadMessages(offset: number, updateResult: boolean, topic = activeTopic.value, parentSignal?: AbortSignal): Promise<void> {
  if (disposed || permissionDenied.value || !props.targetDb || !topic || topic !== activeTopic.value) return;
  if (!isSafeOffset(offset)) { errorMsg.value = 'MQ offset 必须为安全非负整数。'; return; }
  const context = captureContext();
  const requestId = ++browseRequestId;
  browseController?.abort();
  const controller = browseController = new AbortController();
  const requestSignal = parentSignal ? AbortSignal.any([controller.signal, parentSignal]) : controller.signal;
  loadingBrowse.value = true;
  clearRows();
  errorMsg.value = '';
  const started = performance.now();
  const startOffset = offset;
  const limit = boundedBrowseLimit();
  const command = `MQ BROWSE ${topic} FROM ${startOffset} LIMIT ${limit}`;
  try {
    const response = await browseMqMessages(context.api, context.database, topic, {
      fromOffset: startOffset,
      maxCount: limit,
    }, requestSignal);
    if (!isCurrentContext(context) || requestId !== browseRequestId || requestSignal.aborted) return;
    const preview = response.messages.slice(0, limit);
    if (preview.some((item) => item.topic !== context.topic)) { errorMsg.value = 'MQ 消息目标不匹配，请重新浏览。'; return; }
    rows.value = preview.map(mapMessage);
    previewTruncated.value = response.messages.length > limit;
    fromOffset.value = startOffset;
    syncSelectedAfterRows();
    const elapsed = performanceElapsed(started);
    latestCommand.value = command;
    if (updateResult) {
      latestResult.value = resultFromMessages(rows.value, elapsed);
      ranOnce.value = true;
      recordHistory('success', 'SonnetMQ browse', 'browse', command, `${rows.value.length} preview messages · 当前 offset 窗口`, rows.value.length, -1, elapsed, context,
        response.messages.length > limit ? 'truncated' : 'partial');
    }
  } catch (error) {
    if (!isCurrentContext(context) || requestId !== browseRequestId || requestSignal.aborted) return;
    if (isPermissionError(error)) { lockPermission(context); return; }
    const elapsed = performanceElapsed(started);
    const msg = errorToMessage(error, '浏览 MQ 消息失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult(msg);
    ranOnce.value = true;
    recordHistory('error', 'SonnetMQ browse', 'browse', command, msg, 0, 0, elapsed, context);
  } finally {
    if (browseController === controller) browseController = null;
    if (requestId === browseRequestId && context.revision === contextRevision) loadingBrowse.value = false;
  }
}

async function nextPage(): Promise<void> {
  if (!canPageForward.value) return;
  await loadMessages(rows.value[rows.value.length - 1]!.offset + 1, true);
}

async function previousPage(): Promise<void> {
  if (!canPageBack.value) return;
  const current = fromOffset.value ?? retainedStartOffset.value;
  await loadMessages(Math.max(retainedStartOffset.value, current - boundedBrowseLimit()), true);
}

async function seekByTime(): Promise<void> {
  if (disposed || permissionDenied.value || !props.targetDb || !activeTopic.value || seekTimeMs.value === null) return;
  if (!isSafeOffset(retainedStartOffset.value) || !isSafeOffset(highWaterOffset.value)) { errorMsg.value = 'MQ offset 超出安全整数范围，无法按时间定位。'; return; }
  const context = captureContext();
  const requestId = ++browseRequestId;
  browseController?.abort();
  const controller = browseController = new AbortController();
  loadingBrowse.value = true;
  errorMsg.value = '';
  const target = seekTimeMs.value;
  const pageSize = boundedBrowseLimit();
  const highWater = highWaterOffset.value;
  let offset = retainedStartOffset.value;
  let scanned = 0;
  const maxWindows = 25;
  const deadline = Date.now() + 60_000;
  const timeout = setTimeout(() => controller.abort(), 60_000);
  try {
    for (let i = 0; i < maxWindows && offset < highWater && Date.now() < deadline; i += 1) {
      if (!isCurrentContext(context) || requestId !== browseRequestId || controller.signal.aborted) return;
      const response = await browseMqMessages(context.api, context.database, context.topic, {
        fromOffset: offset,
        maxCount: pageSize,
      }, controller.signal);
      if (!isCurrentContext(context) || requestId !== browseRequestId || controller.signal.aborted) return;
      const messages = response.messages.slice(0, pageSize);
      if (messages.length === 0) break;
      if (messages.some((item) => item.topic !== context.topic || !isSafeOffset(item.offset))) { errorMsg.value = 'MQ 消息目标或 offset 无效，无法定位。'; return; }
      scanned += messages.length;
      const found = messages.find((item) => Date.parse(item.timestampUtc) >= target);
      if (found) {
        seekTimeMs.value = target;
        // Keep the Seek deadline alive across its final Browse. Detach the old
        // controller so starting that Browse does not cancel the parent signal.
        if (browseController === controller) browseController = null;
        const finalRequestId = browseRequestId + 1;
        await loadMessages(found.offset, true, context.topic, controller.signal);
        if (isCurrentContext(context) && finalRequestId === browseRequestId && !controller.signal.aborted) selectMessage(found.offset);
        return;
      }
      const last = messages[messages.length - 1];
      const lastTime = Date.parse(last.timestampUtc);
      if (Number.isFinite(lastTime) && lastTime >= target) break;
      const next = last.offset + 1;
      if (!isSafeOffset(next) || next <= offset) { errorMsg.value = 'MQ offset 未安全前进，已停止定位。'; return; }
      offset = next;
    }
    message.warning(`No message found near that timestamp in the scanned ${scanned} message window.`);
  } catch (error) {
    if (isCurrentContext(context) && requestId === browseRequestId) {
      if (isPermissionError(error)) lockPermission(context);
      else errorMsg.value = errorToMessage(error, '按时间定位 MQ 消息失败');
    }
  } finally {
    clearTimeout(timeout);
    if (browseController === controller) browseController = null;
    if (context.revision === contextRevision && requestId === browseRequestId) loadingBrowse.value = false;
  }
}

function selectTopic(topic: string): void {
  if (disposed || permissionDenied.value || !topic) return;
  emit('selectTopic', topic);
  publishTopic.value = topic;
  fromOffset.value = Math.max(0, localTopics.value.find((item) => item.topic === topic)?.nextOffset ?? 0) > 0
    ? Math.max(0, (localTopics.value.find((item) => item.topic === topic)?.nextOffset ?? 0) - browseLimit.value)
    : 0;
}

function selectMessage(offset: number): void {
  if (disposed || permissionDenied.value) return;
  selectedOffset.value = offset;
}

function clearRows(): void {
  rows.value = [];
  selectedOffset.value = null;
  previewTruncated.value = false;
}

function stagePublish(): void {
  if (!canWrite.value) return;
  const db = props.targetDb;
  const topic = publishTopic.value || activeTopic.value;
  if (!db || !topic.trim()) {
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
  if (!canWrite.value || !db || !topic.trim()) return;
  const context = captureContext();
  const stagedHeaders = { ...headers };
  enqueueOperation({
    id: makeOperationId('publish'),
    label: 'Publish',
    detail: `${topic} · ${byteLength} bytes · ${Object.keys(stagedHeaders).length} headers`,
    severity: 'write',
    command: `MQ PUBLISH ${topic} ${byteLength} bytes`,
    context,
    targetTopic: topic,
    run: async (signal) => {
      const response = await publishMqMessage(context.api, db, topic, {
        payload,
        headers: Object.keys(stagedHeaders).length > 0 ? stagedHeaders : null,
      }, signal);
      if (response?.topic !== topic || !isSafeOffset(response?.offset)) throw new Error('Missing MQ terminal');
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
  if (!file || !canWrite.value) { input.value = ''; return; }
  const context = captureContext();
  const requestId = ++fileRequestId;
  try {
    const text = await file.text();
    if (!isCurrentContext(context) || requestId !== fileRequestId || !canWrite.value) return;
    const parsed = parseMessageImport(text);
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
  } catch {
    if (isCurrentContext(context) && requestId === fileRequestId) errorMsg.value = '消息文件读取失败。';
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
    if (items.length > MqPreviewMessageBudget) return { ok: false, message: '每次最多导入 1000 条消息。' };
    const messages = items.map((item, index) => {
      if (!item || typeof item !== 'object' || Array.isArray(item)) {
        throw new Error(`第 ${index + 1} 条消息必须是 JSON 对象。`);
      }
      const record = item as Record<string, unknown>;
      const topic = typeof record.topic === 'string' ? record.topic : '';
      if (topic && !topic.trim()) throw new Error('Topic 不能为空白。');
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
  } catch {
    return { ok: false, message: '消息文件格式无效；请检查 Topic、payload 和 headers。' };
  }
}

async function confirmPendingOperations(): Promise<void> {
  if (!canWrite.value || confirmBusy.value || pendingOperations.value.length === 0) return;
  const operations = [...pendingOperations.value];
  const context = operations[0]!.context;
  if (!operations.every((operation) => isCurrentContext(operation.context))) { pendingOperations.value = []; return; }
  const requestId = ++writeRequestId;
  const controller = writeController = new AbortController();
  topicsController?.abort();
  monitorController?.abort();
  browseController?.abort();
  loading.value = loadingBrowse.value = loadingMonitor.value = false;
  pendingOperations.value = [];
  runningOperations.value = operations;
  confirmBusy.value = true;
  errorMsg.value = '';
  const command = operations.map((operation) => operation.command).join('\n');
  const started = performance.now();
  const deadline = Date.now() + 60_000;
  const timeout = setTimeout(() => controller.abort(), 60_000);
  const outcomes: OperationOutcome[] = [];
  try {
    for (let index = 0; index < Math.min(operations.length, MqPreviewMessageBudget) && Date.now() < deadline; index += 1) {
      const operation = operations[index]!;
      if (!isCurrentContext(operation.context) || controller.signal.aborted) break;
      try {
        const outcome = await operation.run(controller.signal);
        const late = !isCurrentContext(operation.context) || controller.signal.aborted;
        outcomes.push(late ? { action: operation.label, target: operation.targetTopic, succeeded: false, affected: 0,
          state: 'unknown', detail: '写入结果未知；请核对服务端状态，未自动重试。' } : { ...outcome, state: 'completed' });
        if (late) break;
      } catch (error) {
        const status = (error as { response?: { status?: number } } | null)?.response?.status;
        const knownFailure = isCurrentContext(operation.context) && typeof status === 'number' && status >= 400 && status < 500 && status !== 408;
        if (isPermissionError(error) && isCurrentContext(operation.context)) lockPermission(operation.context);
        outcomes.push({ action: operation.label, target: operation.targetTopic, succeeded: false, affected: 0,
          state: knownFailure ? 'failed' : 'unknown', detail: knownFailure ? 'MQ 写入被拒绝。' : '写入结果未知；请核对服务端状态，未自动重试。' });
        break;
      }
    }
    const elapsed = performanceElapsed(started);
    const affected = outcomes.reduce((sum, item) => sum + item.affected, 0);
    const failure = outcomes.find((item) => item.state === 'failed' || item.state === 'unknown');
    const stopped = outcomes.length < operations.length || controller.signal.aborted;
    const status = failure?.state === 'failed' ? 'error' : failure || stopped || !isCurrentContext(context) ? 'unknown' : 'success';
    const publishCount = outcomes.filter((item) => item.action === 'publish').length;
    const ackCount = outcomes.filter((item) => item.action === 'ack').length;
    const summary = [
      publishCount > 0 ? `${publishCount} publish` : '',
      ackCount > 0 ? `${ackCount} ack` : '',
      `affected ${affected}`,
    ].filter(Boolean).join(' · ');
    recordHistory(status, 'SonnetMQ operations', 'operation', command, `${summary}${failure ? ` · ${failure.detail}` : stopped ? ' · 后续操作未执行' : ''}`,
      outcomes.length, affected, elapsed, context, status === 'unknown' ? 'unknown' : stopped ? 'partial' : 'complete');
    if (!isCurrentContext(context) || requestId !== writeRequestId) return;
    latestCommand.value = command;
    latestResult.value = resultFromOutcomes(outcomes, elapsed);
    ranOnce.value = true;
    errorMsg.value = failure?.detail ?? (stopped ? '后续操作未执行，未重试已派发写入。' : '');
    if (status === 'success') {
      publishPayload.value = '';
      publishHeadersText.value = '';
      message.success(`Applied ${outcomes.length} MQ operations.`);
      void refreshAll();
      emit('refreshSchema');
    }
  } finally {
    clearTimeout(timeout);
    if (writeController === controller) writeController = null;
    if (requestId === writeRequestId && !disposed) { confirmBusy.value = false; runningOperations.value = []; }
  }
}

function clearPendingOperations(): void {
  if (confirmBusy.value) return;
  pendingOperations.value = [];
}

function openHistoryEntry(entry: WorkbenchHistoryEntry): void {
  if (disposed || permissionDenied.value) return;
  latestCommand.value = entry.command;
}

function syncSelectedAfterRows(): void {
  if (selectedOffset.value !== null && rows.value.some((row) => row.offset === selectedOffset.value)) return;
  selectedOffset.value = rows.value[0]?.offset ?? null;
}

function mapMessage(item: MqMessageResponse): MqRow {
  const payload = item.payload ?? '';
  const bytes = base64ToBytes(payload);
  const preview = bytesToBase64(bytes.subarray(0, MqInspectorByteBudget));
  const kind = classifyPayload(preview);
  return {
    topic: item.topic,
    offset: item.offset,
    timestampUtc: item.timestampUtc,
    headers: item.headers ?? {},
    payload,
    byteLength: bytes.length,
    headerCount: Object.keys(item.headers ?? {}).length,
    payloadKind: kind,
    payloadPreview: previewPayload(preview, kind),
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
    columns: ['action', 'topic', 'state', 'succeeded', 'affected', 'detail'],
    rows: outcomes.map((item) => [item.action, item.target, item.state ?? 'completed', item.succeeded, item.affected, item.detail]),
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
      return { ok: false, message: 'Invalid headers JSON.' };
    }
  }
  const headers: Record<string, string> = {};
  for (const line of trimmed.split(/\r?\n/g).map((item) => item.trim()).filter(Boolean)) {
    const index = line.indexOf('=');
    if (index <= 0) return { ok: false, message: 'Invalid header line; use key=value.' };
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
  } catch {
    return { ok: false, message: 'Invalid payload for the selected format.' };
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
  const bytes = base64ToBytes(base64).subarray(0, MqInspectorByteBudget);
  const preview = bytesToBase64(bytes);
  if (mode === 'base64') return preview;
  if (mode === 'hex') return toHex(bytes);
  const decoded = tryDecodeUtf8(preview);
  if (!decoded.ok) return `Binary payload (${bytes.length} bytes). Use Hex or Base64 view.`;
  if (mode === 'json') {
    try {
      return JSON.stringify(JSON.parse(decoded.text), null, 2).slice(0, MqInspectorByteBudget);
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
  if (!autoRefresh.value || !activeTopic.value || disposed || permissionDenied.value) return;
  const generation = autoRefreshGeneration;
  const context = captureContext();
  const deadline = Date.now() + 60_000;
  autoRefreshDeadlineTimer = setTimeout(() => {
    if (generation !== autoRefreshGeneration) return;
    if (autoMonitorController && monitorController === autoMonitorController) autoMonitorController.abort();
    autoRefresh.value = false;
  }, 60_000);
  let rounds = 0;
  const tick = async (): Promise<void> => {
    if (generation !== autoRefreshGeneration || !autoRefresh.value || !isCurrentContext(context)) return;
    if (rounds >= 12 || Date.now() >= deadline) { autoRefresh.value = false; return; }
    rounds += 1;
    const previousRequestId = monitorRequestId;
    const previousController = monitorController;
    const sampling = refreshMonitorOnly();
    const ownedController = monitorRequestId !== previousRequestId && monitorController !== previousController
      ? monitorController : null;
    if (generation === autoRefreshGeneration && ownedController) autoMonitorController = ownedController;
    await sampling;
    if (generation === autoRefreshGeneration && autoMonitorController === ownedController) autoMonitorController = null;
    if (generation !== autoRefreshGeneration || !autoRefresh.value || !isCurrentContext(context)) return;
    if (rounds >= 12 || Date.now() >= deadline) { autoRefresh.value = false; return; }
    autoRefreshTimer = setTimeout(() => { void tick(); }, Math.min(5_000, Math.max(1, deadline - Date.now())));
  };
  autoRefreshTimer = setTimeout(() => { void tick(); }, 5_000);
}

function stopAutoRefresh(): void {
  autoRefreshGeneration += 1;
  if (autoMonitorController && monitorController === autoMonitorController) {
    autoMonitorController.abort();
    monitorController = null;
    monitorRequestId += 1;
    loadingMonitor.value = false;
  }
  autoMonitorController = null;
  if (autoRefreshDeadlineTimer) { clearTimeout(autoRefreshDeadlineTimer); autoRefreshDeadlineTimer = null; }
  if (autoRefreshTimer) {
    clearTimeout(autoRefreshTimer);
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
  if (isPermissionError(error)) return '当前身份没有 MQ 访问权限。';
  return fallback;
}

async function copyPayload(): Promise<void> {
  if (disposed || permissionDenied.value) return;
  const row = selectedMessage.value;
  if (!row) return;
  await copyText(formatPayload(row.payload, payloadView.value), 'Payload copied');
}

async function copyHeaders(): Promise<void> {
  if (disposed || permissionDenied.value) return;
  await copyText(selectedHeadersText.value, 'Headers copied');
}

async function copyText(text: string, success: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    message.success(success);
  } catch {
    message.warning('复制失败，请检查剪贴板权限。');
  }
}

function recordHistory(
  status: 'success' | 'error' | 'unknown',
  title: string,
  action: string,
  command: string,
  summary: string,
  rowCount: number,
  recordsAffected: number,
  elapsedMs: number,
  context: MqContext,
  completeness?: WorkbenchHistoryCompleteness,
): void {
  history.record({
    kind: action === 'browse' ? 'query' : 'operation',
    status,
    title,
    target: context.topic,
    database: context.database,
    connectionId: context.connectionId,
    connectionName: context.connectionName,
    model: 'mq',
    action,
    command,
    summary,
    rowCount,
    recordsAffected,
    elapsedMs,
    completeness,
  });
}

function captureContext(): MqContext {
  const token = auth.state?.token;
  const baseUrl = auth.api.defaults.baseURL;
  const api = markRaw(createApiClient(() => token ?? null));
  api.defaults.baseURL = baseUrl;
  return { revision: contextRevision, database: props.targetDb, topic: activeTopic.value,
    connectionId: connections.activeProfileId, connectionName: connections.activeProfile.name,
    baseUrl, profileBaseUrl: connections.activeBaseUrl, token, sourceApi: auth.api, api };
}

function isCurrentContext(context: MqContext): boolean {
  return !disposed && !permissionDenied.value && context.revision === contextRevision && context.database === props.targetDb
    && context.topic === activeTopic.value && context.connectionId === connections.activeProfileId
    && context.profileBaseUrl === connections.activeBaseUrl && context.sourceApi === auth.api
    && context.baseUrl === auth.api.defaults.baseURL && context.token === auth.state?.token;
}

function isPermissionError(error: unknown): boolean {
  const status = (error as { response?: { status?: unknown } } | null)?.response?.status;
  return status === 401 || status === 403;
}

async function guardMetadata<T>(request: Promise<T>, context: MqContext, controller: AbortController): Promise<T> {
  try { return await request; }
  catch (error) {
    if (isPermissionError(error) && isCurrentContext(context) && !controller.signal.aborted) lockPermission(context);
    throw error;
  }
}

function cancelRequests(): void {
  topicsController?.abort();
  monitorController?.abort();
  browseController?.abort();
  writeController?.abort();
}

function invalidateContext(preserveTopics = false): void {
  contextRevision += 1;
  topicsRequestId += 1;
  monitorRequestId += 1;
  browseRequestId += 1;
  fileRequestId += 1;
  cancelRequests();
  stopAutoRefresh();
  autoRefresh.value = false;
  clearRows();
  if (!preserveTopics) localTopics.value = [];
  stats.value = offsets.value = retention.value = null;
  trendSamples.value = [];
  latestResult.value = null;
  latestCommand.value = '';
  ranOnce.value = false;
  errorMsg.value = '';
  pendingOperations.value = [];
  runningOperations.value = [];
  confirmBusy.value = false;
  loading.value = loadingBrowse.value = loadingMonitor.value = false;
  ackConsumerGroup.value = null;
  ackOffset.value = null;
  publishTopic.value = '';
  publishPayload.value = '';
  publishHeadersText.value = '';
  publisherVisible.value = false;
  fromOffset.value = 0;
  seekTimeMs.value = null;
  historyVisible.value = resultVisible.value = false;
}

function lockPermission(context: MqContext): void {
  deniedContext = context;
  permissionLocked.value = true;
  invalidateContext();
  errorMsg.value = '当前身份没有 MQ 访问权限。';
}

function isSafeOffset(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
}

function boundedBrowseLimit(): number {
  return typeof browseLimit.value === 'number' && Number.isFinite(browseLimit.value)
    ? Math.max(1, Math.min(MqPreviewMessageBudget, Math.floor(browseLimit.value))) : 100;
}

function enqueueOperation(operation: PendingOperation): void {
  if (!canWrite.value || !isCurrentContext(operation.context)) return;
  if (pendingOperations.value.length >= MqPreviewMessageBudget) { message.warning('每次最多暂存 1000 项操作。'); return; }
  pendingOperations.value.push(operation);
}

function formatHeaders(headers: Record<string, string>): string {
  const preview: Record<string, string> = {};
  let remaining = 4096;
  const keys = Object.keys(headers).slice(0, 32);
  for (const key of keys) {
    if (remaining <= 0) break;
    const boundedKey = key.slice(0, remaining);
    remaining -= boundedKey.length;
    const value = String(headers[key]).slice(0, remaining);
    remaining -= value.length;
    preview[boundedKey] = value;
  }
  return JSON.stringify(preview, null, 2).slice(0, 4096);
}

function exportMessages(): void {
  if (disposed || permissionDenied.value || rows.value.length === 0) return;
  const text = rows.value.slice(0, MqPreviewMessageBudget).map((row) => JSON.stringify({
    topic: row.topic, offset: row.offset, timestampUtc: row.timestampUtc, headers: row.headers, payloadBase64: row.payload,
  })).join('\n');
  downloadText(`${safeFileStem(props.targetDb, 'database')}_${safeFileStem(activeTopic.value, 'mq')}.jsonl`, text, 'application/x-ndjson');
}

watch(
  () => props.topics,
  (topics) => {
    syncLocalTopics(topics);
  },
  { immediate: true, flush: 'sync' },
);

watch(
  () => [props.targetDb, props.topic, connections.activeProfileId, connections.activeBaseUrl, auth.api,
    auth.api.defaults.baseURL, auth.state, auth.state?.token, props.permissionDenied, readOnly.value] as const,
  () => {
    changingContext = true;
    try {
      invalidateContext();
      const previous = deniedContext;
      const changedIdentity = previous && ((props.targetDb && previous.database !== props.targetDb)
        || (connections.activeProfileId && previous.connectionId !== connections.activeProfileId)
        || (connections.activeBaseUrl && previous.profileBaseUrl !== connections.activeBaseUrl)
        || (auth.api.defaults.baseURL && previous.baseUrl !== auth.api.defaults.baseURL));
      const changedResource = previous && props.topic && previous.topic !== props.topic;
      if (changedIdentity || changedResource) { permissionLocked.value = false; deniedContext = null; }
      syncLocalTopics(props.topics);
      publishTopic.value = activeTopic.value;
    } finally { changingContext = false; }
    const revision = contextRevision;
    void nextTick().then(() => { if (!disposed && !permissionDenied.value && revision === contextRevision) return refreshAll(); });
  },
  { immediate: true, flush: 'sync' },
);

watch(activeTopic, (next, previous) => {
  if (changingContext || disposed || permissionDenied.value || next === previous) return;
  // The first metadata Topic is still a real read target. Its changes advance
  // the epoch, but they never count as an explicit resource permission reset.
  changingContext = true;
  try { invalidateContext(true); publishTopic.value = next; }
  finally { changingContext = false; }
  const revision = contextRevision;
  void nextTick().then(() => { if (!disposed && !permissionDenied.value && revision === contextRevision) return refreshAll(); });
}, { flush: 'sync' });

watch(
  () => autoRefresh.value,
  () => {
    startAutoRefresh();
  },
);

onMounted(() => {
  syncInspectorForViewport();
  window.addEventListener('resize', syncInspectorForViewport);
});

onBeforeUnmount(() => {
  disposed = true;
  contextRevision += 1;
  cancelRequests();
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
