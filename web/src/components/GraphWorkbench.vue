<template>
  <section class="graph-workbench" data-testid="workbench-graph">
    <WriteApprovalPanel
      v-if="approvalPlan"
      :plan="approvalPlan"
      :busy="writeBusy"
      @cancel="clearApproval"
      @confirm="confirmApproval"
    />

    <header class="graph-head">
      <div class="graph-head__identity">
        <div class="graph-head__title-row">
          <Network :size="18" />
          <strong>{{ graph || 'Graph workbench' }}</strong>
          <n-tag v-if="overview" size="tiny" :bordered="false" type="info">
            snapshot {{ overview.snapshotSequence }}
          </n-tag>
        </div>
        <span>{{ targetDb }} · 原生属性图运维</span>
      </div>
      <div class="graph-head__actions">
        <n-select
          class="graph-select"
          size="small"
          :value="graph"
          :options="graphOptions"
          placeholder="选择 Graph"
          @update:value="$emit('select-graph', String($event))"
        />
        <n-button size="small" secondary :loading="busy" title="刷新 Graph 运维数据" @click="refreshAll">
          <template #icon><RefreshCw :size="15" /></template>
          刷新
        </n-button>
      </div>
    </header>

    <WorkbenchSectionTabs v-model="activeSection" :items="sectionTabs" aria-label="Graph 运维视图" />

    <n-alert v-if="errorMsg" type="error" closable @close="errorMsg = ''">{{ errorMsg }}</n-alert>
    <div v-if="!graph" class="graph-empty">
      <Network :size="34" />
      <strong>当前数据库没有可打开的 Graph</strong>
    </div>

    <div v-else-if="activeSection === 'canvas'" class="graph-section graph-section--canvas">
      <div class="graph-toolbar">
        <div class="graph-stat-strip">
          <span><strong>{{ formatNumber(overview?.vertexCount ?? 0) }}</strong> vertices</span>
          <span><strong>{{ formatNumber(overview?.edgeCount ?? 0) }}</strong> edges</span>
          <span><strong>{{ overview?.labels.length ?? 0 }}</strong> labels</span>
          <span><strong>{{ overview?.indexes.length ?? 0 }}</strong> indexes</span>
        </div>
        <label class="limit-control">
          <span>元素上限</span>
          <n-input-number v-model:value="visualizationLimit" size="small" :min="10" :max="1000" :step="50" />
        </label>
        <n-button size="small" secondary :loading="visualizationBusy" @click="loadVisualization">重新采样</n-button>
      </div>

      <n-alert v-if="visualization?.truncated" type="warning" :show-icon="true">
        当前画布是有界快照；请缩小分析范围，不要把它视为全图导出。
      </n-alert>

      <div class="graph-canvas-layout">
        <div ref="chartElement" class="graph-canvas" role="img" :aria-label="`${graph} 属性图可视化`" />
        <aside class="graph-inspector">
          <template v-if="selectedElement">
            <header>
              <n-tag size="tiny" :bordered="false" :type="selectedElement.kind === 'vertex' ? 'success' : 'warning'">
                {{ selectedElement.kind }}
              </n-tag>
              <strong>#{{ selectedElement.data.id }}</strong>
            </header>
            <dl>
              <div><dt>version</dt><dd>{{ selectedElement.data.elementVersion }}</dd></div>
              <template v-if="selectedElement.kind === 'vertex'">
                <div><dt>labels</dt><dd>{{ selectedElement.data.labels.join(', ') || '-' }}</dd></div>
              </template>
              <template v-else>
                <div><dt>from → to</dt><dd>{{ selectedElement.data.sourceId }} → {{ selectedElement.data.targetId }}</dd></div>
                <div><dt>label</dt><dd>{{ selectedElement.data.labelId }}</dd></div>
              </template>
              <div><dt>properties</dt><dd>{{ selectedElement.data.properties.length }}</dd></div>
            </dl>
            <pre>{{ formatProperties(selectedElement.data.properties) }}</pre>
            <n-button size="small" secondary @click="editSelectedElement">在受限编辑器中打开</n-button>
          </template>
          <template v-else>
            <MousePointer2 :size="24" />
            <strong>选择一个元素</strong>
            <span>属性、标签和元素版本会显示在这里。</span>
          </template>
        </aside>
      </div>
      <footer class="graph-snapshot-note">
        <span>{{ visualization?.vertices.length ?? 0 }} vertices · {{ visualization?.edges.length ?? 0 }} edges</span>
        <span>snapshot {{ visualization?.snapshotSequence ?? '-' }}</span>
      </footer>
    </div>

    <div v-else-if="activeSection === 'schema'" class="graph-section graph-section--scroll">
      <div class="operations-grid">
        <article class="operations-panel">
          <header><Tags :size="17" /><strong>Label 基数</strong></header>
          <n-data-table :columns="labelColumns" :data="overview?.labels ?? []" size="small" :bordered="false" />
        </article>
        <article class="operations-panel operations-panel--wide">
          <header><ListTree :size="17" /><strong>属性索引</strong></header>
          <n-data-table :columns="indexColumns" :data="overview?.indexes ?? []" size="small" :bordered="false" />
        </article>
        <article class="operations-panel">
          <header><ChartNoAxesColumnIncreasing :size="17" /><strong>出度分布</strong></header>
          <n-data-table :columns="degreeColumns" :data="overview?.degreeHistogram ?? []" size="small" :bordered="false" />
        </article>
      </div>

      <article class="operations-panel slow-traversals">
        <header>
          <TimerReset :size="17" />
          <strong>慢遍历诊断</strong>
          <n-tag size="tiny" :bordered="false" :type="overview?.capabilities.slowTraversalDiagnostics ? 'success' : 'default'">
            {{ overview?.slowTraversalSource ?? 'unavailable' }}
          </n-tag>
        </header>
        <n-data-table :columns="slowColumns" :data="overview?.slowTraversals ?? []" size="small" :bordered="false" />
      </article>
    </div>

    <div v-else-if="activeSection === 'edit'" class="graph-section graph-section--scroll">
      <div class="editor-layout">
        <aside class="editor-rail">
          <n-radio-group v-model:value="editorKind" size="small">
            <n-radio-button value="vertex">Vertex</n-radio-button>
            <n-radio-button value="edge">Edge</n-radio-button>
          </n-radio-group>
          <label><span>元素 ID</span><n-input-number v-model:value="editorId" :min="1" :precision="0" /></label>
          <n-button secondary :disabled="!validEditorId" :loading="editorBusy" @click="loadElement">
            <template #icon><Search :size="15" /></template>
            读取当前版本
          </n-button>
          <dl class="editor-contract">
            <div><dt>expectedVersion</dt><dd>{{ editorVersion }}</dd></div>
            <div><dt>requestId</dt><dd>每次写入自动生成</dd></div>
          </dl>
        </aside>

        <div class="editor-form">
          <div v-if="editorKind === 'vertex'" class="editor-fields">
            <label><span>Labels（JSON 数组）</span><n-input v-model:value="labelsText" type="textarea" :autosize="{ minRows: 2, maxRows: 5 }" /></label>
          </div>
          <div v-else class="edge-fields">
            <label><span>Source ID</span><n-input-number v-model:value="sourceId" :min="1" :precision="0" /></label>
            <label><span>Target ID</span><n-input-number v-model:value="targetId" :min="1" :precision="0" /></label>
            <label><span>Label ID</span><n-input-number v-model:value="edgeLabelId" :min="1" :precision="0" /></label>
          </div>
          <label>
            <span>Properties（typed JSON 数组）</span>
            <n-input v-model:value="propertiesText" type="textarea" :autosize="{ minRows: 9, maxRows: 18 }" />
          </label>
          <label>
            <span>Unique property IDs（JSON 数组）</span>
            <n-input v-model:value="uniquePropertiesText" type="textarea" :autosize="{ minRows: 2, maxRows: 4 }" />
          </label>
          <div class="editor-actions">
            <n-button type="primary" :disabled="!validEditor" @click="stageElementSave">
              <template #icon><Save :size="15" /></template>
              暂存 Upsert
            </n-button>
            <n-button tertiary type="error" :disabled="!validEditorId || editorVersion <= 0" @click="stageElementDelete">
              <template #icon><Trash2 :size="15" /></template>
              暂存删除
            </n-button>
          </div>
        </div>
      </div>
    </div>

    <div v-else-if="activeSection === 'transfer'" class="graph-section graph-section--scroll">
      <div class="transfer-grid">
        <article class="transfer-panel">
          <header><Download :size="18" /><strong>JSON 导出</strong></header>
          <p>导出使用稳定 statement snapshot，可直接交给 SonnetDB Graph 导入器。</p>
          <label class="transfer-limit"><span>最大元素数</span><n-input-number v-model:value="exportLimit" :min="1" :max="1000000" :step="10000" /></label>
          <n-alert type="warning" :show-icon="true">
            只有导出文档中的 <code>truncated=false</code> 才代表完整 round-trip 数据集。
          </n-alert>
          <n-button type="primary" :loading="transferBusy" @click="exportGraph">
            <template #icon><Download :size="15" /></template>
            下载 .graph.json
          </n-button>
        </article>

        <article class="transfer-panel">
          <header><Upload :size="18" /><strong>JSON 导入</strong></header>
          <p>单批最多 10,000 个元素；写入前会展示目标、数量和幂等 request ID。</p>
          <input ref="fileInput" class="sr-only" type="file" accept="application/json,.json" @change="readImportFile" />
          <div class="import-actions">
            <n-button secondary @click="fileInput?.click()">
              <template #icon><FolderOpen :size="15" /></template>
              选择 JSON
            </n-button>
            <span>{{ importFileName || '尚未选择文件' }}</span>
          </div>
          <n-input v-model:value="importText" type="textarea" :autosize="{ minRows: 8, maxRows: 16 }" placeholder="{ &quot;vertices&quot;: [], &quot;edges&quot;: [] }" />
          <n-button type="primary" :disabled="!importText.trim()" @click="stageImport">
            <template #icon><ShieldCheck :size="15" /></template>
            校验并暂存导入
          </n-button>
        </article>
      </div>
    </div>

    <div v-else class="graph-section graph-section--scroll">
      <div class="maintenance-layout">
        <article class="maintenance-stage">
          <header><Wrench :size="18" /><strong>维护暂存</strong></header>
          <label><span>维护动作</span><n-select v-model:value="maintenanceAction" :options="maintenanceOptions" /></label>
          <label v-if="maintenanceAction === 'RepairRebuild'"><span>单次 work units</span><n-input-number v-model:value="maxWorkUnits" :min="1" :max="4096" /></label>
          <n-checkbox v-if="maintenanceAction === 'RepairRebuild'" v-model:checked="compactOnCompletion">修复完成后 compact</n-checkbox>
          <n-alert type="warning" :show-icon="true">
            Stage 不会修改数据。服务端返回十分钟有效的审批记录后，还需再次批准才会执行。
          </n-alert>
          <n-button type="warning" @click="stageMaintenanceRequest">
            <template #icon><ShieldAlert :size="15" /></template>
            预览并暂存
          </n-button>
        </article>

        <article class="maintenance-approval">
          <header><ClipboardCheck :size="18" /><strong>待决策审批</strong></header>
          <template v-if="stagedApproval && stagedApproval.state === 'staged'">
            <dl>
              <div><dt>approval</dt><dd>{{ stagedApproval.approvalId }}</dd></div>
              <div><dt>action</dt><dd>{{ stagedApproval.action }}</dd></div>
              <div><dt>principal</dt><dd>{{ stagedApproval.principal }}</dd></div>
              <div><dt>expires</dt><dd>{{ formatDate(stagedApproval.expiresAtUtc) }}</dd></div>
            </dl>
            <n-input v-model:value="rejectReason" placeholder="拒绝原因（可选）" />
            <div class="approval-actions">
              <n-button type="error" :loading="writeBusy" @click="stageApprovalDecision('approve')">批准并执行</n-button>
              <n-button secondary :loading="writeBusy" @click="rejectStagedApproval">拒绝</n-button>
            </div>
          </template>
          <div v-else class="maintenance-empty">
            <ClipboardCheck :size="28" />
            <span>没有当前会话待决策的审批。</span>
          </div>
        </article>
      </div>

      <article class="operations-panel audit-panel">
        <header>
          <ScrollText :size="17" />
          <strong>维护审计</strong>
          <n-button size="tiny" quaternary :loading="auditBusy" title="刷新审计" @click="loadAudit"><RefreshCw :size="14" /></n-button>
        </header>
        <n-data-table :columns="auditColumns" :data="audit" size="small" :bordered="false" />
      </article>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, h, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import {
  NAlert,
  NButton,
  NCheckbox,
  NDataTable,
  NInput,
  NInputNumber,
  NRadioButton,
  NRadioGroup,
  NSelect,
  NTag,
  useMessage,
  type DataTableColumns,
  type SelectOption,
} from 'naive-ui';
import * as echarts from 'echarts/core';
import { GraphChart, type GraphSeriesOption } from 'echarts/charts';
import { LegendComponent, TooltipComponent, type LegendComponentOption, type TooltipComponentOption } from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import {
  ChartNoAxesColumnIncreasing,
  ClipboardCheck,
  Download,
  FolderOpen,
  ListTree,
  MousePointer2,
  Network,
  RefreshCw,
  Save,
  ScrollText,
  Search,
  ShieldAlert,
  ShieldCheck,
  Tags,
  TimerReset,
  Trash2,
  Upload,
  Wrench,
} from 'lucide-vue-next';
import {
  approveGraphMaintenance,
  deleteGraphElement,
  downloadGraphExport,
  fetchGraphEdge,
  fetchGraphMaintenanceAudit,
  fetchGraphOperationsOverview,
  fetchGraphVertex,
  fetchGraphVisualization,
  importGraphJson,
  rejectGraphMaintenance,
  stageGraphMaintenance,
  upsertGraphEdge,
  upsertGraphVertex,
  type GraphDegreeBucket,
  type GraphEdge,
  type GraphExportDocument,
  type GraphImportRequest,
  type GraphIndexStatistic,
  type GraphInfo,
  type GraphLabelStatistic,
  type GraphMaintenanceAction,
  type GraphMaintenanceApproval,
  type GraphOperationsOverview,
  type GraphProperty,
  type GraphSlowTraversal,
  type GraphVertex,
  type GraphVisualization,
} from '@/api/graphs';
import WorkbenchSectionTabs, { type WorkbenchSectionTab } from '@/components/WorkbenchSectionTabs.vue';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import { useWorkbenchHistoryStore } from '@/stores/workbenchHistory';
import { createWriteApprovalPlan, type WriteApprovalPlan } from '@/utils/writeApproval';

echarts.use([GraphChart, LegendComponent, TooltipComponent, CanvasRenderer]);
type GraphChartOption = echarts.ComposeOption<GraphSeriesOption | LegendComponentOption | TooltipComponentOption>;
type GraphSection = 'canvas' | 'schema' | 'edit' | 'transfer' | 'maintenance';
type EditorKind = 'vertex' | 'edge';
type SelectedElement = { kind: 'vertex'; data: GraphVertex } | { kind: 'edge'; data: GraphEdge };
type PendingAction = { plan: WriteApprovalPlan; run: () => Promise<void> };

const props = defineProps<{
  targetDb: string;
  graph: string;
  graphs: GraphInfo[];
  loading?: boolean;
}>();

defineEmits<{ 'select-graph': [graph: string]; 'refresh-graphs': [] }>();

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();
const activeSection = ref<GraphSection>('canvas');
const busy = ref(false);
const visualizationBusy = ref(false);
const editorBusy = ref(false);
const transferBusy = ref(false);
const writeBusy = ref(false);
const auditBusy = ref(false);
const errorMsg = ref('');
const overview = ref<GraphOperationsOverview | null>(null);
const visualization = ref<GraphVisualization | null>(null);
const visualizationLimit = ref<number | null>(250);
const selectedElement = ref<SelectedElement | null>(null);
const chartElement = ref<HTMLElement | null>(null);
let chart: echarts.ECharts | null = null;
let resizeObserver: ResizeObserver | null = null;

const editorKind = ref<EditorKind>('vertex');
const editorId = ref<number | null>(null);
const editorVersion = ref(0);
const labelsText = ref('[1]');
const propertiesText = ref('[\n  { "propertyId": 1, "value": { "kind": 5, "string": "value" } }\n]');
const uniquePropertiesText = ref('[]');
const sourceId = ref<number | null>(null);
const targetId = ref<number | null>(null);
const edgeLabelId = ref<number | null>(null);

const exportLimit = ref<number | null>(100000);
const importText = ref('');
const importFileName = ref('');
const fileInput = ref<HTMLInputElement | null>(null);
const pendingAction = ref<PendingAction | null>(null);
const approvalPlan = computed(() => pendingAction.value?.plan ?? null);

const maintenanceAction = ref<GraphMaintenanceAction>('RepairRebuild');
const maxWorkUnits = ref<number | null>(64);
const compactOnCompletion = ref(false);
const stagedApproval = ref<GraphMaintenanceApproval | null>(null);
const rejectReason = ref('');
const audit = ref<GraphMaintenanceApproval[]>([]);

const sectionTabs = computed<WorkbenchSectionTab[]>(() => [
  { key: 'canvas', label: 'Canvas', icon: Network, count: visualization.value?.vertices.length },
  { key: 'schema', label: 'Schema & diagnostics', icon: ListTree, count: overview.value?.indexes.length },
  { key: 'edit', label: 'Restricted edit', icon: Save },
  { key: 'transfer', label: 'Import / export', icon: Upload },
  { key: 'maintenance', label: 'Maintenance', icon: Wrench, count: audit.value.length },
]);

const graphOptions = computed<SelectOption[]>(() => props.graphs.map((item) => ({ label: item.name, value: item.name })));
const maintenanceOptions: SelectOption[] = [
  { label: 'Repair / rebuild', value: 'RepairRebuild' },
  { label: 'Checkpoint', value: 'Checkpoint' },
  { label: 'Compact', value: 'Compact' },
];
const validEditorId = computed(() => Number.isInteger(editorId.value) && Number(editorId.value) > 0);
const validEditor = computed(() => validEditorId.value
  && (editorKind.value === 'vertex'
    || (Number(sourceId.value) > 0 && Number(targetId.value) > 0 && Number(edgeLabelId.value) > 0)));

const labelColumns: DataTableColumns<GraphLabelStatistic> = [
  { title: 'Label ID', key: 'labelId', width: 120 },
  { title: 'Elements', key: 'elementCount', render: (row) => formatNumber(row.elementCount) },
];
const indexColumns: DataTableColumns<GraphIndexStatistic> = [
  { title: 'Element', key: 'elementType', width: 100 },
  { title: 'Label', key: 'labelId', width: 90 },
  { title: 'Property', key: 'propertyId', width: 100 },
  { title: 'Type', key: 'valueKind', width: 110 },
  { title: 'Entries', key: 'entryCount', render: (row) => formatNumber(row.entryCount) },
];
const degreeColumns: DataTableColumns<GraphDegreeBucket> = [
  { title: 'Out degree', key: 'degree', width: 120 },
  { title: 'Vertices', key: 'vertexCount', render: (row) => formatNumber(row.vertexCount) },
];
const slowColumns: DataTableColumns<GraphSlowTraversal> = [
  { title: 'When', key: 'timestampMs', width: 170, render: (row) => formatDate(row.timestampMs) },
  { title: 'Elapsed', key: 'elapsedMs', width: 110, render: (row) => `${row.elapsedMs.toFixed(2)} ms` },
  { title: 'Rows', key: 'rowCount', width: 90 },
  { title: 'Path', key: 'accessPath', width: 150, render: (row) => row.accessPath || row.fallbackReason || '-' },
  { title: 'SQL', key: 'sql', ellipsis: { tooltip: true } },
];
const auditColumns: DataTableColumns<GraphMaintenanceApproval> = [
  { title: 'When', key: 'occurredAtUtc', width: 170, render: (row) => formatDate(row.occurredAtUtc) },
  { title: 'Action', key: 'action', width: 140 },
  { title: 'State', key: 'state', width: 110, render: (row) => h(NTag, { size: 'tiny', bordered: false, type: auditTagType(row.state) }, { default: () => row.state }) },
  { title: 'Principal', key: 'principal', width: 150, ellipsis: { tooltip: true } },
  { title: 'Result', key: 'result', render: (row) => auditSummary(row), ellipsis: { tooltip: true } },
];

async function refreshAll(): Promise<void> {
  if (!props.graph) return;
  busy.value = true;
  errorMsg.value = '';
  try {
    const [overviewResult, visualizationResult] = await Promise.all([
      fetchGraphOperationsOverview(auth.api, props.targetDb, props.graph),
      fetchGraphVisualization(auth.api, props.targetDb, props.graph, visualizationLimit.value ?? 250),
    ]);
    overview.value = overviewResult;
    visualization.value = visualizationResult;
    selectedElement.value = null;
    await renderChart();
  } catch (error) {
    handleError(error, '加载 Graph 运维数据失败');
  } finally {
    busy.value = false;
  }
}

async function loadVisualization(): Promise<void> {
  if (!props.graph) return;
  visualizationBusy.value = true;
  errorMsg.value = '';
  try {
    visualization.value = await fetchGraphVisualization(auth.api, props.targetDb, props.graph, visualizationLimit.value ?? 250);
    selectedElement.value = null;
    await renderChart();
  } catch (error) {
    handleError(error, '加载 Graph 可视化失败');
  } finally {
    visualizationBusy.value = false;
  }
}

async function renderChart(): Promise<void> {
  if (activeSection.value !== 'canvas') return;
  await nextTick();
  if (!chartElement.value || !visualization.value) return;
  chart ??= echarts.init(chartElement.value);
  const vertexById = new Map(visualization.value.vertices.map((vertex) => [vertex.id, vertex]));
  const categories = [...new Set(visualization.value.vertices.flatMap((vertex) => vertex.labels))]
    .map((label) => ({ name: `Label ${label}` }));
  const categoryByLabel = new Map(categories.map((category, index) => [Number(category.name.slice(6)), index]));
  const option: GraphChartOption = {
    animationDurationUpdate: 250,
    tooltip: {
      trigger: 'item',
      formatter: (params: unknown) => graphTooltip(params),
    },
    legend: categories.length <= 12 ? [{ data: categories.map((category) => category.name), bottom: 4 }] : undefined,
    series: [{
      type: 'graph',
      layout: 'force',
      roam: true,
      draggable: false,
      categories,
      force: { repulsion: 130, edgeLength: [55, 125], gravity: 0.08 },
      label: { show: visualization.value.vertices.length <= 80, position: 'right', formatter: '{b}' },
      emphasis: { focus: 'adjacency', lineStyle: { width: 3 } },
      data: visualization.value.vertices.map((vertex) => ({
        id: String(vertex.id),
        name: `v${vertex.id}`,
        value: vertex.properties.length,
        category: categoryByLabel.get(vertex.labels[0] ?? -1),
        symbolSize: Math.min(34, 13 + Math.sqrt(vertex.properties.length + 1) * 4),
        itemStyle: { color: vertex.labels.length > 0 ? undefined : '#587286' },
      })),
      edges: visualization.value.edges
        .filter((edge) => vertexById.has(edge.sourceId) && vertexById.has(edge.targetId))
        .map((edge) => ({
          id: String(edge.id),
          source: String(edge.sourceId),
          target: String(edge.targetId),
          name: `e${edge.id} · L${edge.labelId}`,
          value: edge.labelId,
        })),
      lineStyle: { color: '#81909d', opacity: 0.68, width: 1.2, curveness: 0.08 },
    }],
  };
  chart.setOption(option, true);
  chart.off('click');
  chart.on('click', (params) => {
    const id = Number(params.dataType === 'edge' ? (params.data as { id?: string }).id : (params.data as { id?: string }).id);
    selectedElement.value = params.dataType === 'edge'
      ? visualization.value?.edges.find((item) => item.id === id)
        ? { kind: 'edge', data: visualization.value.edges.find((item) => item.id === id)! }
        : null
      : visualization.value?.vertices.find((item) => item.id === id)
        ? { kind: 'vertex', data: visualization.value.vertices.find((item) => item.id === id)! }
        : null;
  });
  if (!resizeObserver) {
    resizeObserver = new ResizeObserver(() => chart?.resize());
    resizeObserver.observe(chartElement.value);
  }
}

function graphTooltip(params: unknown): string {
  const item = params as { dataType?: string; data?: { id?: string; source?: string; target?: string; value?: number }; name?: string };
  if (item.dataType === 'edge') return `${item.name ?? 'edge'}<br/>${item.data?.source ?? '?'} → ${item.data?.target ?? '?'}`;
  return `${item.name ?? 'vertex'}<br/>${item.data?.value ?? 0} properties`;
}

async function loadElement(): Promise<void> {
  if (!validEditorId.value || !props.graph) return;
  editorBusy.value = true;
  errorMsg.value = '';
  try {
    if (editorKind.value === 'vertex') {
      setVertexEditor(await fetchGraphVertex(auth.api, props.targetDb, props.graph, Number(editorId.value)));
    } else {
      setEdgeEditor(await fetchGraphEdge(auth.api, props.targetDb, props.graph, Number(editorId.value)));
    }
  } catch (error) {
    handleError(error, '读取 Graph 元素失败');
  } finally {
    editorBusy.value = false;
  }
}

function setVertexEditor(vertex: GraphVertex): void {
  editorKind.value = 'vertex';
  editorId.value = vertex.id;
  editorVersion.value = vertex.elementVersion;
  labelsText.value = JSON.stringify(vertex.labels, null, 2);
  propertiesText.value = JSON.stringify(vertex.properties, null, 2);
  uniquePropertiesText.value = '[]';
}

function setEdgeEditor(edge: GraphEdge): void {
  editorKind.value = 'edge';
  editorId.value = edge.id;
  editorVersion.value = edge.elementVersion;
  sourceId.value = edge.sourceId;
  targetId.value = edge.targetId;
  edgeLabelId.value = edge.labelId;
  propertiesText.value = JSON.stringify(edge.properties, null, 2);
  uniquePropertiesText.value = '[]';
}

function editSelectedElement(): void {
  if (!selectedElement.value) return;
  if (selectedElement.value.kind === 'vertex') setVertexEditor(selectedElement.value.data);
  else setEdgeEditor(selectedElement.value.data);
  activeSection.value = 'edit';
}

function stageElementSave(): void {
  if (!validEditor.value) return;
  try {
    const properties = parseJsonArray<GraphProperty>(propertiesText.value, 'Properties 必须是 JSON 数组。');
    const uniquePropertyIds = parseIntegerArray(uniquePropertiesText.value, 'Unique property IDs');
    const id = Number(editorId.value);
    const command = editorKind.value === 'vertex'
      ? `upsert vertex ${id} expectedVersion=${editorVersion.value}`
      : `upsert edge ${id} ${sourceId.value}->${targetId.value} label=${edgeLabelId.value} expectedVersion=${editorVersion.value}`;
    pendingAction.value = {
      plan: makePlan('Graph 元素 Upsert', command, 'write'),
      run: async () => {
        const result = editorKind.value === 'vertex'
          ? await upsertGraphVertex(auth.api, props.targetDb, props.graph, {
            id,
            expectedElementVersion: editorVersion.value,
            labels: parseIntegerArray(labelsText.value, 'Labels'),
            properties,
            uniquePropertyIds,
            requestId: crypto.randomUUID(),
          })
          : await upsertGraphEdge(auth.api, props.targetDb, props.graph, {
            id,
            expectedElementVersion: editorVersion.value,
            sourceId: Number(sourceId.value),
            targetId: Number(targetId.value),
            labelId: Number(edgeLabelId.value),
            properties,
            uniquePropertyIds,
            requestId: crypto.randomUUID(),
          });
        message.success(`Graph 元素已写入 sequence ${result.sequence}。`);
        await refreshAll();
        await loadElement();
      },
    };
  } catch (error) {
    handleError(error, 'Graph 元素 JSON 无效');
  }
}

function stageElementDelete(): void {
  if (!validEditorId.value || editorVersion.value <= 0) return;
  const id = Number(editorId.value);
  pendingAction.value = {
    plan: makePlan('删除 Graph 元素', `delete ${editorKind.value} ${id} expectedVersion=${editorVersion.value}`, 'danger'),
    run: async () => {
      const result = await deleteGraphElement(auth.api, props.targetDb, props.graph, editorKind.value, id, editorVersion.value);
      message.success(`Graph 元素已删除，sequence ${result.sequence}。`);
      resetEditor();
      await refreshAll();
    },
  };
}

async function readImportFile(event: Event): Promise<void> {
  const file = (event.target as HTMLInputElement).files?.[0];
  if (!file) return;
  importFileName.value = file.name;
  importText.value = await file.text();
}

function stageImport(): void {
  try {
    const document = JSON.parse(importText.value) as Partial<GraphExportDocument & GraphImportRequest>;
    if (document.truncated === true) throw new Error('截断的 Graph 导出不能作为完整导入源。');
    const vertices = (document.vertices ?? document.nodes ?? []).map((vertex) => ({
      id: vertex.id,
      expectedElementVersion: 'expectedElementVersion' in vertex ? vertex.expectedElementVersion : 0,
      labels: vertex.labels ?? [],
      properties: vertex.properties ?? [],
      uniquePropertyIds: vertex.uniquePropertyIds ?? [],
    }));
    const edges = (document.edges ?? document.relationships ?? []).map((edge) => ({
      id: edge.id,
      expectedElementVersion: 'expectedElementVersion' in edge ? edge.expectedElementVersion : 0,
      sourceId: edge.sourceId,
      targetId: edge.targetId,
      labelId: edge.labelId,
      properties: edge.properties ?? [],
      uniquePropertyIds: edge.uniquePropertyIds ?? [],
    }));
    const count = vertices.length + edges.length;
    if (count < 1 || count > 10000) throw new Error('Graph 导入批次必须包含 1 到 10,000 个元素。');
    const request: GraphImportRequest = { requestId: crypto.randomUUID(), vertices, edges };
    pendingAction.value = {
      plan: makePlan('导入 Graph JSON', `${vertices.length} vertices · ${edges.length} edges\nrequestId=${request.requestId}`, 'write'),
      run: async () => {
        const result = await importGraphJson(auth.api, props.targetDb, props.graph, request);
        message.success(`已导入 ${result.vertexCount} vertices / ${result.edgeCount} edges。`);
        await refreshAll();
      },
    };
  } catch (error) {
    handleError(error, 'Graph 导入 JSON 无效');
  }
}

async function exportGraph(): Promise<void> {
  transferBusy.value = true;
  errorMsg.value = '';
  try {
    const blob = await downloadGraphExport(auth.api, props.targetDb, props.graph, exportLimit.value ?? 100000);
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `${props.graph}.graph.json`;
    anchor.click();
    URL.revokeObjectURL(url);
    recordHistory('success', 'Graph JSON export', `maxElements=${exportLimit.value ?? 100000}`, 'export completed');
  } catch (error) {
    handleError(error, 'Graph 导出失败');
  } finally {
    transferBusy.value = false;
  }
}

function stageMaintenanceRequest(): void {
  const request = {
    action: maintenanceAction.value,
    compactOnCompletion: maintenanceAction.value === 'RepairRebuild' && compactOnCompletion.value,
    maxWorkUnits: maintenanceAction.value === 'RepairRebuild' ? maxWorkUnits.value ?? 64 : 64,
  };
  pendingAction.value = {
    plan: makePlan('暂存 Graph 维护', `${request.action} maxWorkUnits=${request.maxWorkUnits} compact=${request.compactOnCompletion}`, 'danger'),
    run: async () => {
      stagedApproval.value = await stageGraphMaintenance(auth.api, props.targetDb, props.graph, request);
      message.warning(`维护已暂存，审批 ${stagedApproval.value.approvalId} 尚未执行。`);
      await loadAudit();
    },
  };
}

function stageApprovalDecision(decision: 'approve'): void {
  if (!stagedApproval.value) return;
  const approval = stagedApproval.value;
  pendingAction.value = {
    plan: makePlan('批准 Graph 维护', `${decision} ${approval.action}\napproval=${approval.approvalId}`, 'danger'),
    run: async () => {
      stagedApproval.value = await approveGraphMaintenance(auth.api, props.targetDb, props.graph, approval.approvalId);
      message.success(`Graph 维护状态：${stagedApproval.value.state}。`);
      await refreshAll();
    },
  };
}

async function rejectStagedApproval(): Promise<void> {
  if (!stagedApproval.value) return;
  writeBusy.value = true;
  try {
    stagedApproval.value = await rejectGraphMaintenance(auth.api, props.targetDb, props.graph, stagedApproval.value.approvalId, rejectReason.value);
    message.info('Graph 维护审批已拒绝。');
    await loadAudit();
  } catch (error) {
    handleError(error, '拒绝 Graph 维护审批失败');
  } finally {
    writeBusy.value = false;
  }
}

async function loadAudit(): Promise<void> {
  if (!props.graph) return;
  auditBusy.value = true;
  try {
    audit.value = await fetchGraphMaintenanceAudit(auth.api, props.targetDb, props.graph);
  } catch (error) {
    handleError(error, '加载 Graph 维护审计失败');
  } finally {
    auditBusy.value = false;
  }
}

async function confirmApproval(): Promise<void> {
  if (!pendingAction.value) return;
  const action = pendingAction.value;
  writeBusy.value = true;
  errorMsg.value = '';
  const started = performance.now();
  try {
    await action.run();
    recordHistory('success', action.plan.title, action.plan.items[0]?.command ?? '', 'completed', performance.now() - started);
    pendingAction.value = null;
  } catch (error) {
    handleError(error, `${action.plan.title}失败`, action.plan.items[0]?.command ?? '', performance.now() - started);
  } finally {
    writeBusy.value = false;
  }
}

function makePlan(title: string, command: string, severity: 'write' | 'danger'): WriteApprovalPlan {
  return createWriteApprovalPlan({
    id: `graph_${Date.now()}_${Math.random().toString(36).slice(2, 7)}`,
    title,
    target: `${props.targetDb}.${props.graph}`,
    items: [{ id: 'graph-operation', command, severity, label: title }],
  });
}

function clearApproval(): void { if (!writeBusy.value) pendingAction.value = null; }
function resetEditor(): void {
  editorVersion.value = 0;
  labelsText.value = '[]';
  propertiesText.value = '[]';
  uniquePropertiesText.value = '[]';
  sourceId.value = null;
  targetId.value = null;
  edgeLabelId.value = null;
}
function parseJsonArray<T>(text: string, error: string): T[] {
  const value = JSON.parse(text) as unknown;
  if (!Array.isArray(value)) throw new Error(error);
  return value as T[];
}
function parseIntegerArray(text: string, label: string): number[] {
  const value = parseJsonArray<unknown>(text, `${label} 必须是 JSON 数组。`);
  if (value.some((item) => !Number.isInteger(item) || Number(item) <= 0)) throw new Error(`${label} 只能包含正整数。`);
  return value.map(Number);
}
function formatNumber(value: number): string { return new Intl.NumberFormat().format(value); }
function formatDate(value: string | number): string { return new Date(value).toLocaleString(); }
function formatProperties(properties: GraphProperty[]): string { return JSON.stringify(properties, null, 2); }
function auditTagType(state: string): 'success' | 'warning' | 'error' | 'info' | 'default' {
  if (state === 'completed') return 'success';
  if (state === 'staged' || state === 'paused' || state === 'applying') return 'warning';
  if (state === 'failed' || state === 'expired' || state === 'rejected' || state === 'interrupted') return 'error';
  return 'default';
}
function auditSummary(row: GraphMaintenanceApproval): string {
  if (row.result) return `seq ${row.result.sequence} · repaired ${row.result.repairedEntries} · removed ${row.result.removedEntries}`;
  return row.reason || row.errorCode || '-';
}
function errorToMessage(error: unknown, fallback: string): string {
  if (typeof error === 'object' && error && 'response' in error) {
    const data = (error as { response?: { data?: { message?: string } } }).response?.data;
    if (data?.message) return data.message;
  }
  return error instanceof Error ? error.message : fallback;
}
function handleError(error: unknown, fallback: string, command = '', elapsedMs = 0): void {
  const detail = errorToMessage(error, fallback);
  errorMsg.value = detail;
  recordHistory('error', fallback, command, detail, elapsedMs);
}
function recordHistory(status: 'success' | 'error', title: string, command: string, summary: string, elapsedMs = 0): void {
  history.record({
    kind: 'operation', status, title, target: props.graph, database: props.targetDb,
    connectionId: connections.activeProfileId, connectionName: connections.activeProfile.name,
    model: 'graph', action: title.toLowerCase().replaceAll(' ', '_'), command, summary, elapsedMs,
  });
}

watch(() => [props.targetDb, props.graph], () => {
  overview.value = null;
  visualization.value = null;
  selectedElement.value = null;
  stagedApproval.value = null;
  audit.value = [];
  resetEditor();
  void refreshAll();
}, { immediate: true });
watch(activeSection, (section) => {
  if (section === 'canvas') void renderChart();
  if (section === 'maintenance' && audit.value.length === 0) void loadAudit();
});
watch(editorKind, resetEditor);
onBeforeUnmount(() => {
  resizeObserver?.disconnect();
  chart?.dispose();
  chart = null;
});
</script>

<style scoped>
.graph-workbench { display: flex; flex: 1; min-width: 0; min-height: 0; flex-direction: column; overflow: hidden; background: #fff; }
.graph-head { display: flex; flex: 0 0 auto; align-items: center; justify-content: space-between; gap: 16px; min-height: 58px; padding: 10px 18px; border-bottom: 1px solid var(--sndb-border); background: var(--sndb-surface); }
.graph-head__identity, .graph-head__actions, .graph-head__title-row, .graph-stat-strip, .graph-toolbar, .operations-panel > header, .transfer-panel > header, .maintenance-stage > header, .maintenance-approval > header, .editor-actions, .import-actions, .approval-actions { display: flex; align-items: center; gap: 9px; }
.graph-head__identity { min-width: 0; flex-direction: column; align-items: flex-start; gap: 2px; }
.graph-head__identity > span { color: var(--sndb-ink-muted); font-size: 11px; }
.graph-head__title-row strong { overflow: hidden; color: var(--sndb-ink-strong); font-size: 15px; text-overflow: ellipsis; white-space: nowrap; }
.graph-head__actions { flex-wrap: wrap; justify-content: flex-end; }.graph-select { width: min(260px, 34vw); }
.graph-workbench > .n-alert { margin: 10px 16px 0; }
.graph-section { display: flex; flex: 1; min-width: 0; min-height: 0; flex-direction: column; gap: 12px; padding: 14px 16px 16px; }
.graph-section--scroll { overflow: auto; }.graph-section--canvas { overflow: hidden; }
.graph-empty { display: grid; flex: 1; place-content: center; gap: 10px; color: var(--sndb-ink-muted); text-align: center; }
.graph-toolbar { flex-wrap: wrap; min-height: 34px; }.graph-stat-strip { flex: 1; min-width: 320px; gap: 18px; color: var(--sndb-ink-muted); font-size: 11px; }
.graph-stat-strip strong { color: var(--sndb-ink-strong); font: 700 15px/1 "Cascadia Code", Consolas, monospace; }
.limit-control, .editor-rail label, .editor-form label, .maintenance-stage label, .transfer-limit { display: flex; flex-direction: column; gap: 5px; color: var(--sndb-ink-soft); font-size: 11px; font-weight: 700; }
.limit-control { width: 150px; flex-direction: row; align-items: center; }.limit-control > span { white-space: nowrap; }
.graph-canvas-layout { display: grid; flex: 1; min-height: 380px; grid-template-columns: minmax(0, 1fr) 260px; overflow: hidden; border: 1px solid var(--sndb-border); border-radius: var(--sndb-radius); }
.graph-canvas { min-width: 0; min-height: 380px; background: linear-gradient(#f8fafb 1px, transparent 1px), linear-gradient(90deg, #f8fafb 1px, transparent 1px), #fff; background-size: 24px 24px; }
.graph-inspector { display: flex; min-width: 0; flex-direction: column; gap: 12px; padding: 14px; overflow: auto; border-left: 1px solid var(--sndb-border); background: var(--sndb-surface); color: var(--sndb-ink-muted); }
.graph-inspector header { display: flex; align-items: center; gap: 8px; color: var(--sndb-ink-strong); }.graph-inspector dl, .maintenance-approval dl, .editor-contract { display: flex; flex-direction: column; margin: 0; }
.graph-inspector dl div, .maintenance-approval dl div, .editor-contract div { display: grid; grid-template-columns: 90px minmax(0, 1fr); gap: 8px; padding: 6px 0; border-bottom: 1px solid var(--sndb-border); }
.graph-inspector dt, .maintenance-approval dt, .editor-contract dt { color: var(--sndb-ink-muted); }.graph-inspector dd, .maintenance-approval dd, .editor-contract dd { min-width: 0; margin: 0; overflow-wrap: anywhere; color: var(--sndb-ink-strong); }
.graph-inspector pre { max-height: 310px; margin: 0; overflow: auto; padding: 9px; border: 1px solid var(--sndb-border); background: #fff; font: 11px/1.5 "Cascadia Code", Consolas, monospace; white-space: pre-wrap; }
.graph-snapshot-note { display: flex; justify-content: space-between; padding-right: 54px; color: var(--sndb-ink-muted); font-size: 11px; }
.operations-grid { display: grid; grid-template-columns: minmax(230px, .7fr) minmax(420px, 1.4fr) minmax(230px, .7fr); gap: 12px; }
.operations-panel { min-width: 0; overflow: hidden; border: 1px solid var(--sndb-border); border-radius: var(--sndb-radius); background: #fff; }
.operations-panel > header { min-height: 42px; padding: 8px 12px; border-bottom: 1px solid var(--sndb-border); background: var(--sndb-surface); color: var(--sndb-ink-strong); }.operations-panel > header .n-button { margin-left: auto; }
.slow-traversals { min-height: 260px; }.slow-traversals > header .n-tag { margin-left: auto; }
.editor-layout { display: grid; min-height: 510px; grid-template-columns: 250px minmax(0, 1fr); border: 1px solid var(--sndb-border); border-radius: var(--sndb-radius); overflow: hidden; }
.editor-rail { display: flex; flex-direction: column; gap: 14px; padding: 16px; border-right: 1px solid var(--sndb-border); background: var(--sndb-surface); }.editor-rail .n-radio-group { display: grid; grid-template-columns: 1fr 1fr; }
.editor-contract { margin-top: 4px; font: 11px/1.5 "Cascadia Code", Consolas, monospace; }.editor-form { display: flex; min-width: 0; flex-direction: column; gap: 14px; padding: 16px; overflow: auto; }
.edge-fields { display: grid; grid-template-columns: repeat(3, minmax(140px, 1fr)); gap: 10px; }.editor-actions { margin-top: auto; justify-content: flex-end; }
.transfer-grid, .maintenance-layout { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; }
.transfer-panel, .maintenance-stage, .maintenance-approval { display: flex; min-width: 0; flex-direction: column; gap: 14px; padding: 16px; border: 1px solid var(--sndb-border); border-radius: var(--sndb-radius); background: #fff; }
.transfer-panel p { margin: 0; color: var(--sndb-ink-muted); font-size: 12px; line-height: 1.6; }.transfer-limit { width: 220px; }.transfer-panel > .n-button { align-self: flex-start; }
.import-actions span { overflow: hidden; color: var(--sndb-ink-muted); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }.sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0, 0, 0, 0); }
.maintenance-stage > .n-button { align-self: flex-start; }.maintenance-approval dl { font-size: 12px; }.maintenance-empty { display: grid; min-height: 190px; place-content: center; gap: 9px; color: var(--sndb-ink-muted); text-align: center; }
.approval-actions { justify-content: flex-end; }.audit-panel { min-height: 280px; }
@media (max-width: 1050px) { .operations-grid { grid-template-columns: 1fr 1fr; }.operations-panel--wide { grid-column: 1 / -1; grid-row: 2; }.graph-canvas-layout { grid-template-columns: minmax(0, 1fr) 220px; } }
@media (max-width: 800px) { .graph-head { align-items: flex-start; flex-direction: column; }.graph-head__actions, .graph-select { width: 100%; }.graph-head__actions .n-button { flex: 0 0 auto; }.graph-section { padding: 10px; }.graph-stat-strip { min-width: 100%; justify-content: space-between; gap: 8px; }.graph-canvas-layout, .editor-layout, .transfer-grid, .maintenance-layout, .operations-grid { grid-template-columns: 1fr; }.graph-canvas-layout { overflow: auto; }.graph-canvas { min-height: 430px; }.graph-inspector { max-height: 260px; border-top: 1px solid var(--sndb-border); border-left: 0; }.editor-rail { border-right: 0; border-bottom: 1px solid var(--sndb-border); }.edge-fields { grid-template-columns: 1fr; }.operations-panel--wide { grid-column: auto; grid-row: auto; } }
</style>
