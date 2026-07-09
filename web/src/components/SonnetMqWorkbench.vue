<template>
  <main class="mq-workbench">
    <section class="mq-toolbar">
      <div class="mq-toolbar__identity">
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="info" :bordered="false">MQ</n-tag>
          <n-text class="mq-toolbar__title">{{ activeTopic || 'No topic selected' }}</n-text>
          <n-tag v-if="activeTopic" size="tiny" :bordered="false">SonnetMQ</n-tag>
        </n-space>
        <n-text depth="3" class="mq-toolbar__meta">
          {{ targetDb || 'database' }} · {{ rows.length }} loaded messages · {{ selectedMessage ? `offset ${selectedMessage.offset}` : 'no message selected' }}
        </n-text>
      </div>

      <div class="mq-toolbar__actions">
        <n-select
          v-model:value="selectedTopic"
          size="small"
          :options="topicOptions"
          :disabled="topicOptions.length === 0"
          class="mq-toolbar__topic"
        />
        <n-input-number
          v-model:value="fromOffset"
          size="small"
          :min="0"
          :show-button="false"
          placeholder="Offset"
          class="mq-toolbar__offset"
          @keydown.enter="browseFromInput"
        />
        <n-date-picker
          v-model:value="seekTimeMs"
          type="datetime"
          size="small"
          clearable
          placeholder="Seek time"
          class="mq-toolbar__time"
        />
        <n-select v-model:value="browseLimit" size="small" :options="browseLimitOptions" class="mq-toolbar__limit" />
        <n-button size="small" secondary :disabled="!activeTopic" :loading="loadingBrowse" @click="browseFromInput">
          Browse
        </n-button>
        <n-button size="small" secondary :disabled="!activeTopic || !seekTimeMs" :loading="loadingBrowse" @click="seekByTime">
          Seek time
        </n-button>
        <n-button size="small" secondary :loading="loading" @click="refreshAll">
          Refresh
        </n-button>
        <n-button size="small" quaternary @click="historyVisible = true">History</n-button>
      </div>
    </section>

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

    <section class="mq-stats">
      <article v-for="item in statItems" :key="item.label" class="mq-stat">
        <span>{{ item.label }}</span>
        <strong>{{ item.value }}</strong>
      </article>
    </section>

    <section class="mq-body">
      <aside class="mq-topics">
        <div class="mq-panel-head">
          <div>
            <n-text class="mq-panel-head__title">Topics</n-text>
            <n-text depth="3" class="mq-panel-head__meta">{{ filteredTopics.length }} visible · {{ localTopics.length }} total</n-text>
          </div>
        </div>
        <n-input
          v-model:value="topicFilter"
          size="small"
          clearable
          placeholder="Filter topics"
          class="mq-topic-filter"
        />
        <div class="mq-topic-list">
          <button
            v-for="topic in filteredTopics"
            :key="topic.topic"
            type="button"
            class="mq-topic-card"
            :class="{ 'is-active': topic.topic === activeTopic }"
            @click="selectTopic(topic.topic)"
          >
            <span>{{ topic.topic }}</span>
            <small>{{ formatStat(topic.messageCount) }} msg · next {{ topic.nextOffset }}</small>
          </button>
          <n-empty v-if="filteredTopics.length === 0" description="No MQ topics found." />
        </div>
      </aside>

      <section class="mq-message-panel">
        <div class="mq-panel-head mq-panel-head--grid">
          <div>
            <n-text class="mq-panel-head__title">Messages</n-text>
            <n-text depth="3" class="mq-panel-head__meta">{{ messageWindowSummary }}</n-text>
          </div>
          <n-space size="small" align="center" :wrap="true">
            <n-button size="small" secondary :disabled="!canPageBack" @click="previousPage">Previous</n-button>
            <n-button size="small" secondary :disabled="!canPageForward" @click="nextPage">Next</n-button>
          </n-space>
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

      <aside class="mq-inspector">
        <div class="mq-panel-head">
          <div>
            <n-text class="mq-panel-head__title">Message inspector</n-text>
            <n-text depth="3" class="mq-panel-head__meta">{{ selectedMessage ? formatTimestamp(selectedMessage.timestampUtc) : 'No message selected' }}</n-text>
          </div>
          <n-tag v-if="selectedMessage" size="tiny" :bordered="false">offset {{ selectedMessage.offset }}</n-tag>
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

        <section class="mq-publisher">
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
import { computed, h, onMounted, ref, watch } from 'vue';
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
  browseMqMessages,
  fetchMqOffsets,
  fetchMqStats,
  publishMqMessage,
  type MqConsumerLag,
  type MqMessageResponse,
  type MqOffsetsResponse,
  type MqStatsResponse,
} from '@/api/mq';
import type { SqlResultSet } from '@/api/sql';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import WorkbenchResultPanel from '@/components/WorkbenchResultPanel.vue';
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
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();

const localTopics = ref<MqTopicInfo[]>([]);
const offsets = ref<MqOffsetsResponse | null>(null);
const stats = ref<MqStatsResponse | null>(null);
const rows = ref<MqRow[]>([]);
const selectedOffset = ref<number | null>(null);
const topicFilter = ref('');
const fromOffset = ref<number | null>(0);
const browseLimit = ref(100);
const seekTimeMs = ref<number | null>(null);
const loading = ref(false);
const loadingBrowse = ref(false);
const errorMsg = ref('');
const payloadView = ref<PayloadView>('text');
const publishTopic = ref('');
const publishMode = ref<PayloadView>('text');
const publishPayload = ref('{"message":"hello SonnetMQ"}');
const publishHeadersText = ref('source=web-admin');
const pendingOperations = ref<PendingOperation[]>([]);
const confirmBusy = ref(false);
const latestResult = ref<SqlResultSet | null>(null);
const latestCommand = ref('');
const ranOnce = ref(false);
const historyVisible = ref(false);

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

const activeTopic = computed(() => props.topic || localTopics.value[0]?.topic || '');

const selectedTopic = computed({
  get: () => activeTopic.value,
  set: (value: string) => selectTopic(value),
});

const topicOptions = computed<SelectOption[]>(() => {
  const names = new Set(localTopics.value.map((topic) => topic.topic));
  if (props.topic) names.add(props.topic);
  return [...names].sort().map((name) => ({ label: name, value: name }));
});

const filteredTopics = computed(() => {
  const keyword = topicFilter.value.trim().toLowerCase();
  const topics = [...localTopics.value].sort((a, b) => a.topic.localeCompare(b.topic));
  if (!keyword) return topics;
  return topics.filter((topic) => topic.topic.toLowerCase().includes(keyword));
});

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

const statItems = computed(() => {
  const next = highWaterOffset.value;
  const start = retainedStartOffset.value;
  const last = next > start ? next - 1 : start;
  return [
    { label: 'Messages', value: formatStat(stats.value?.messageCount ?? currentTopicInfo.value?.messageCount) },
    { label: 'Next Offset', value: formatStat(next) },
    { label: 'Retained', value: next > start ? `${start} - ${last}` : 'empty' },
    { label: 'Partitions', value: '1' },
    { label: 'Consumers', value: formatStat(consumers.value.length) },
    { label: 'Max Lag', value: formatStat(maxLag.value) },
  ];
});

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
    title: 'SonnetMQ publish batch',
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
    return;
  }
  const [nextStats, nextOffsets] = await Promise.all([
    fetchMqStats(auth.api, props.targetDb, topic),
    fetchMqOffsets(auth.api, props.targetDb, topic),
  ]);
  stats.value = nextStats;
  offsets.value = nextOffsets;
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
  const payload = encoded.base64;
  const stagedHeaders = headers.headers;
  pendingOperations.value.push({
    id: makeOperationId('publish'),
    label: 'Publish',
    detail: `${topic} · ${encoded.byteLength} bytes · ${Object.keys(stagedHeaders).length} headers`,
    severity: 'write',
    command: `MQ PUBLISH ${topic} ${encoded.byteLength} bytes`,
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
    recordHistory('success', 'SonnetMQ publish', 'publish', command, `${outcomes.length} messages · affected ${affected}`, outcomes.length, affected, elapsed);
    message.success(`Published ${outcomes.length} message${outcomes.length === 1 ? '' : 's'}.`);
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
    const msg = errorToMessage(error, '提交 MQ 发布失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult(msg);
    ranOnce.value = true;
    recordHistory('error', 'SonnetMQ publish', 'publish', command, msg, 0, 0, elapsed);
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

onMounted(() => {
  publishTopic.value = activeTopic.value;
  void refreshAll();
});
</script>

<style scoped>
.mq-workbench {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.mq-toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #f7fbff;
}

.mq-toolbar__identity {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.mq-toolbar__title {
  color: var(--sndb-ink-strong);
  font-size: 15px;
  font-weight: 800;
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
  flex-wrap: wrap;
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

.mq-body {
  display: grid;
  flex: 1;
  min-height: 360px;
  grid-template-columns: 230px minmax(420px, 1fr) 360px;
  min-width: 0;
  overflow: hidden;
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
  border-left: 1px solid rgba(15, 23, 42, 0.08);
  background: #fcfdf8;
}

.mq-panel-head {
  display: flex;
  flex: 0 0 auto;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
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
  border-radius: 999px;
  background: rgba(13, 59, 102, 0.07);
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
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

@media (max-width: 1360px) {
  .mq-body {
    grid-template-columns: 220px minmax(420px, 1fr);
  }

  .mq-inspector {
    grid-column: 1 / -1;
    border-top: 1px solid rgba(15, 23, 42, 0.08);
    border-left: 0;
  }
}

@media (max-width: 980px) {
  .mq-toolbar,
  .mq-panel-head--grid,
  .mq-pager {
    flex-direction: column;
    align-items: stretch;
  }

  .mq-body {
    grid-template-columns: 1fr;
    overflow: visible;
  }

  .mq-topics,
  .mq-inspector {
    border-right: 0;
    border-left: 0;
  }

  .mq-stats {
    grid-template-columns: repeat(2, minmax(120px, 1fr));
  }

  .mq-toolbar__topic,
  .mq-toolbar__offset,
  .mq-toolbar__time,
  .mq-toolbar__limit {
    width: 100%;
  }
}
</style>
