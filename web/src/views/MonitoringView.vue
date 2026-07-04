<template>
  <div>
    <n-alert
      v-if="!otelAvailable && !loading"
      type="info"
      title="Prometheus 指标端点未启用"
      style="margin-bottom:16px;"
    >
      当前 /metrics 为最小指标集。要启用完整监控面板，请在 appsettings.json 中设置
      <code>SonnetDBServer:Observability:Prometheus:Enabled = true</code> 并重启服务。
    </n-alert>

    <n-grid :cols="gridCols" :x-gap="16" :y-gap="16">
      <n-gi v-for="card in statCards" :key="card.label">
        <n-card size="small">
          <n-statistic :label="card.label" :value="card.value">
            <template #suffix>
              <span class="stat-unit">{{ card.unit }}</span>
            </template>
          </n-statistic>
        </n-card>
      </n-gi>
    </n-grid>

    <n-grid :cols="chartCols" :x-gap="16" :y-gap="16" style="margin-top:16px;">
      <n-gi v-for="chart in charts" :key="chart.title">
        <n-card :title="chart.title" size="small">
          <MetricSparkline :points="chart.points" :unit="chart.unit" :color="chart.color" />
        </n-card>
      </n-gi>
    </n-grid>

    <n-card v-if="otelAvailable" title="MemTable / Segment（按数据库）" size="small" style="margin-top:16px;">
      <n-data-table :columns="dbCols" :data="dbRows" :bordered="false" size="small" />
    </n-card>

    <n-alert v-if="lastError" type="error" :title="lastError" style="margin-top:16px;" closable />
  </div>
</template>

<script setup lang="ts">
import { computed, defineComponent, h, onBeforeUnmount, onMounted, ref } from 'vue';
import {
  NAlert, NCard, NDataTable, NGi, NGrid, NStatistic, type DataTableColumns,
} from 'naive-ui';
import { useAuthStore } from '@/stores/auth';
import {
  collectHistogram, fetchPromSnapshot, histogramQuantile, sumByLabel, sumSamples,
  type PromSnapshot,
} from '@/api/metrics';

const POLL_MS = 5_000;
const MAX_POINTS = 120; // 约 10 分钟窗口

interface SeriesPoint { at: number; value: number }
interface DbRow { db: string; memtableBytes: number; memtablePoints: number; segments: number; [k: string]: unknown }

const auth = useAuthStore();

const loading = ref(true);
const otelAvailable = ref(false);
const lastError = ref('');

const writeRate = ref<SeriesPoint[]>([]);
const queryP95 = ref<SeriesPoint[]>([]);
const walFsyncP95 = ref<SeriesPoint[]>([]);
const memtableBytes = ref<SeriesPoint[]>([]);
const copilotRequests = ref<SeriesPoint[]>([]);

const latestWriteRate = ref<number | null>(null);
const latestQueryP95 = ref<number | null>(null);
const latestWalP95 = ref<number | null>(null);
const latestMemtableBytes = ref<number | null>(null);
const latestSegments = ref<number | null>(null);
const latestFlushPending = ref<number | null>(null);
const latestCopilotTokens = ref<number | null>(null);
const dbRows = ref<DbRow[]>([]);

let prevSnapshot: PromSnapshot | null = null;
let prevWritePoints: number | null = null;
let prevCopilotRequests: number | null = null;
let timer: ReturnType<typeof setInterval> | null = null;

/** 内联 SVG 折线（无第三方图表库，与既有 SqlResultChart 同风格）。 */
const MetricSparkline = defineComponent({
  props: {
    points: { type: Array as () => SeriesPoint[], required: true },
    unit: { type: String, default: '' },
    color: { type: String, default: '#2c7be5' },
  },
  setup(props) {
    const width = 640;
    const height = 140;
    const padLeft = 52;
    const padRight = 10;
    const padTop = 10;
    const padBottom = 20;

    return () => {
      const pts = props.points;
      if (pts.length < 2) {
        return h('div', { class: 'spark-empty' }, '等待采样…');
      }
      const xs = pts.map((p) => p.at);
      const ys = pts.map((p) => p.value);
      const xMin = Math.min(...xs);
      const xMax = Math.max(...xs);
      const yMax = Math.max(...ys, 1e-9);
      const yMin = 0;
      const sx = (x: number): number => padLeft + ((x - xMin) / Math.max(1, xMax - xMin)) * (width - padLeft - padRight);
      const sy = (y: number): number => height - padBottom - ((y - yMin) / (yMax - yMin || 1)) * (height - padTop - padBottom);

      const path = pts.map((p, i) => `${i === 0 ? 'M' : 'L'}${sx(p.at).toFixed(1)},${sy(p.value).toFixed(1)}`).join(' ');
      const yTicks = [0, yMax / 2, yMax];
      const fmt = (v: number): string => {
        if (v >= 1_000_000) return `${(v / 1_000_000).toFixed(1)}M`;
        if (v >= 1_000) return `${(v / 1_000).toFixed(1)}k`;
        return v >= 100 ? v.toFixed(0) : v.toFixed(1);
      };
      const timeFmt = (ms: number): string => new Date(ms).toLocaleTimeString('zh-CN', { hour12: false });

      return h('svg', {
        viewBox: `0 0 ${width} ${height}`,
        preserveAspectRatio: 'none',
        xmlns: 'http://www.w3.org/2000/svg',
        style: 'width:100%;height:auto;display:block;',
        role: 'img',
      }, [
        ...yTicks.map((v) => h('line', {
          x1: padLeft, x2: width - padRight, y1: sy(v), y2: sy(v),
          stroke: 'rgba(13,59,102,0.10)', 'stroke-width': 1,
        })),
        ...yTicks.map((v) => h('text', {
          x: padLeft - 6, y: sy(v) + 4, 'text-anchor': 'end',
          style: 'font-size:10px;fill:rgba(13,59,102,0.55);',
        }, `${fmt(v)}${props.unit}`)),
        h('text', {
          x: padLeft, y: height - 4, 'text-anchor': 'start',
          style: 'font-size:10px;fill:rgba(13,59,102,0.45);',
        }, timeFmt(xMin)),
        h('text', {
          x: width - padRight, y: height - 4, 'text-anchor': 'end',
          style: 'font-size:10px;fill:rgba(13,59,102,0.45);',
        }, timeFmt(xMax)),
        h('path', { d: path, stroke: props.color, 'stroke-width': 1.6, fill: 'none' }),
      ]);
    };
  },
});

const gridCols = computed(() => (otelAvailable.value ? 6 : 3));
const chartCols = 2;

function fmtNumber(v: number | null): string {
  if (v === null) return '—';
  if (v >= 1_000_000) return `${(v / 1_000_000).toFixed(1)}M`;
  if (v >= 10_000) return `${(v / 1_000).toFixed(1)}k`;
  return v >= 100 ? v.toFixed(0) : v.toFixed(1);
}

function fmtBytes(v: number | null): string {
  if (v === null) return '—';
  if (v >= 1024 ** 3) return `${(v / 1024 ** 3).toFixed(2)} GiB`;
  if (v >= 1024 ** 2) return `${(v / 1024 ** 2).toFixed(1)} MiB`;
  if (v >= 1024) return `${(v / 1024).toFixed(1)} KiB`;
  return `${v.toFixed(0)} B`;
}

const statCards = computed(() => {
  const cards = [
    { label: '写入吞吐', value: fmtNumber(latestWriteRate.value), unit: 'pt/s' },
    { label: '查询 P95', value: fmtNumber(latestQueryP95.value), unit: 'ms' },
    { label: 'WAL 落盘 P95', value: fmtNumber(latestWalP95.value), unit: 'ms' },
  ];
  if (otelAvailable.value) {
    cards.push(
      { label: 'MemTable', value: fmtBytes(latestMemtableBytes.value), unit: '' },
      { label: 'Segment 数', value: fmtNumber(latestSegments.value), unit: '' },
      { label: 'Flush 排队', value: fmtNumber(latestFlushPending.value), unit: '' },
    );
  }
  return cards;
});

const charts = computed(() => {
  const list = [
    { title: '写入吞吐（points/s）', points: writeRate.value, unit: '', color: '#2c7be5' },
    { title: '查询 P95（ms）', points: queryP95.value, unit: '', color: '#e85d75' },
    { title: 'WAL fsync P95（ms）', points: walFsyncP95.value, unit: '', color: '#f4a261' },
    { title: 'MemTable 占用（bytes）', points: memtableBytes.value, unit: '', color: '#52b788' },
  ];
  if (latestCopilotTokens.value !== null || copilotRequests.value.length > 1) {
    list.push({ title: 'Copilot 调用（次/s）', points: copilotRequests.value, unit: '', color: '#7b2cbf' });
  }
  return list;
});

const dbCols: DataTableColumns<DbRow> = [
  { title: '数据库', key: 'db' },
  { title: 'MemTable 字节', key: 'memtableBytes', render: (r) => h('span', fmtBytes(r.memtableBytes)) },
  { title: 'MemTable 点数', key: 'memtablePoints' },
  { title: 'Segment 数', key: 'segments' },
];

function pushPoint(series: { value: SeriesPoint[] }, at: number, value: number | null): void {
  if (value === null || !Number.isFinite(value)) return;
  const next = [...series.value, { at, value }];
  if (next.length > MAX_POINTS) next.splice(0, next.length - MAX_POINTS);
  series.value = next;
}

async function poll(): Promise<void> {
  const snapshot = await fetchPromSnapshot(auth.api);
  loading.value = false;
  if (snapshot.error) {
    lastError.value = snapshot.error;
    return;
  }
  lastError.value = '';
  otelAvailable.value = snapshot.otel;
  if (!snapshot.otel) return;

  const at = snapshot.at;
  const elapsedSec = prevSnapshot ? Math.max(0.001, (at - prevSnapshot.at) / 1000) : null;

  // 写入吞吐：counter 差分 / 周期。
  const writePoints = sumSamples(snapshot, 'sonnetdb_write_points_total');
  if (writePoints !== null && prevWritePoints !== null && elapsedSec !== null) {
    const rate = Math.max(0, writePoints - prevWritePoints) / elapsedSec;
    latestWriteRate.value = rate;
    pushPoint(writeRate, at, rate);
  }
  prevWritePoints = writePoints;

  // 查询 P95：histogram bucket 差分还原。
  const queryHist = collectHistogram(snapshot, 'sonnetdb_query_duration_milliseconds');
  const prevQueryHist = prevSnapshot ? collectHistogram(prevSnapshot, 'sonnetdb_query_duration_milliseconds') : null;
  if (queryHist) {
    const p95 = histogramQuantile(0.95, queryHist, prevQueryHist);
    if (p95 !== null) {
      latestQueryP95.value = p95;
      pushPoint(queryP95, at, p95);
    }
  }

  // WAL fsync P95。
  const walHist = collectHistogram(snapshot, 'sonnetdb_wal_fsync_duration_milliseconds');
  const prevWalHist = prevSnapshot ? collectHistogram(prevSnapshot, 'sonnetdb_wal_fsync_duration_milliseconds') : null;
  if (walHist) {
    const p95 = histogramQuantile(0.95, walHist, prevWalHist);
    if (p95 !== null) {
      latestWalP95.value = p95;
      pushPoint(walFsyncP95, at, p95);
    }
  }

  // MemTable / Segment / Flush 排队（gauge，直接读）。
  const memBytes = sumSamples(snapshot, 'sonnetdb_memtable_bytes');
  latestMemtableBytes.value = memBytes;
  pushPoint(memtableBytes, at, memBytes);
  latestSegments.value = sumSamples(snapshot, 'sonnetdb_segments_count');
  latestFlushPending.value = sumSamples(snapshot, 'sonnetdb_flush_pending');

  // Copilot（#92 落地后出现；此处兼容读取，缺失即隐藏）。
  const copilotReq = sumSamples(snapshot, 'sonnetdb_copilot_chat_requests_total')
    ?? sumSamples(snapshot, 'copilot_chat_requests_total');
  if (copilotReq !== null && prevCopilotRequests !== null && elapsedSec !== null) {
    pushPoint(copilotRequests, at, Math.max(0, copilotReq - prevCopilotRequests) / elapsedSec);
  }
  prevCopilotRequests = copilotReq;
  latestCopilotTokens.value = sumSamples(snapshot, 'sonnetdb_copilot_chat_tokens_total')
    ?? sumSamples(snapshot, 'copilot_chat_tokens_total');

  // 按数据库聚合表。
  const bytesByDb = sumByLabel(snapshot, 'sonnetdb_memtable_bytes', 'sonnetdb_database');
  const pointsByDb = sumByLabel(snapshot, 'sonnetdb_memtable_points', 'sonnetdb_database');
  const segsByDb = sumByLabel(snapshot, 'sonnetdb_segments_count', 'sonnetdb_database');
  const names = new Set([...Object.keys(bytesByDb), ...Object.keys(pointsByDb), ...Object.keys(segsByDb)]);
  dbRows.value = [...names].sort().map((db) => ({
    db,
    memtableBytes: bytesByDb[db] ?? 0,
    memtablePoints: pointsByDb[db] ?? 0,
    segments: segsByDb[db] ?? 0,
  }));

  prevSnapshot = snapshot;
}

onMounted(() => {
  void poll();
  timer = setInterval(() => { void poll(); }, POLL_MS);
});

onBeforeUnmount(() => {
  if (timer) clearInterval(timer);
});
</script>

<style scoped>
.stat-unit {
  font-size: 0.75rem;
  color: var(--sndb-ink-soft);
}

.spark-empty {
  height: 140px;
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--sndb-ink-soft);
  font-size: 0.85rem;
}
</style>
