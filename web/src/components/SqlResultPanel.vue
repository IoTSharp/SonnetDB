<template>
  <n-card
    size="small"
    :bordered="true"
    :segmented="{ content: true, footer: false }"
    class="sql-result-card"
  >
    <template #header>
      <n-space size="small" align="center" :wrap="false" style="font-size: 13px">
        <n-tag size="small" :type="statusType" :bordered="false">#{{ index + 1 }}</n-tag>
        <code class="sql-result-card__sql" :title="sql">{{ trimmedSql }}</code>
      </n-space>
    </template>
    <template #header-extra>
      <n-space size="small" align="center" :wrap="false">
        <span v-if="meta" class="sql-result-card__meta">{{ meta }}</span>
        <n-tabs
          v-if="hasRows"
          v-model:value="view"
          type="segment"
          size="small"
          style="min-width: 220px"
        >
          <n-tab v-if="explainPlan" name="plan" tab="Plan" />
          <n-tab name="table" tab="Table" />
          <n-tab name="raw" tab="Raw" />
          <n-tab name="json" tab="JSON" />
          <n-tab name="chart" tab="Chart" />
          <n-tab v-if="hasGeoPoints" name="map" tab="Map" />
        </n-tabs>
      </n-space>
    </template>

    <n-alert
      v-if="result.error"
      type="error"
      :title="`[${result.error.code ?? 'error'}] ${result.error.message}`"
    />

    <template v-else>
      <template v-if="hasRows">
        <VisualExplainPanel
          v-if="view === 'plan' && explainPlan"
          :plan="explainPlan"
        />

        <!-- 表格 -->
        <n-data-table
          v-if="view === 'table'"
          :columns="dataColumns"
          :data="rows"
          :bordered="false"
          size="small"
          :max-height="420"
        />

        <pre v-else-if="view === 'raw'" class="sql-result-card__pre">{{ rawText }}</pre>

        <pre v-else-if="view === 'json'" class="sql-result-card__pre">{{ jsonText }}</pre>

        <!-- 图表（SVG 折线） -->
        <SqlResultChart
          v-else-if="view === 'chart'"
          :columns="result.columns"
          :rows="rows"
        />

        <ResultMapPreview
          v-else
          :columns="result.columns"
          :rows="rows"
        />
      </template>
      <n-text v-else depth="3">{{ emptyText }}</n-text>
    </template>
  </n-card>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import {
  NAlert, NCard, NDataTable, NSpace, NTabs, NTab, NTag, NText,
  type DataTableColumns,
} from 'naive-ui';
import SqlResultChart from './SqlResultChart.vue';
import ResultMapPreview from './ResultMapPreview.vue';
import VisualExplainPanel from './VisualExplainPanel.vue';
import { rowsToObjects, type SqlResultSet } from '@/api/sql';
import { parseVisualExplainPlan } from '@/utils/explainPlan';
import { formatSqlValue, parseGeoPointValue } from '@/utils/sqlValue';

interface Props {
  index: number;
  sql: string;
  result: SqlResultSet;
  displayRows?: unknown[][];
}
const props = defineProps<Props>();

type View = 'plan' | 'table' | 'raw' | 'json' | 'chart' | 'map';
const view = ref<View>('table');

const visibleRows = computed(() => props.displayRows ?? props.result.rows);
const visibleResult = computed<SqlResultSet>(() => ({
  ...props.result,
  rows: visibleRows.value,
}));

const hasRows = computed(() => props.result.hasColumns && visibleRows.value.length > 0);

const rows = computed(() => rowsToObjects(visibleResult.value));
const explainPlan = computed(() => parseVisualExplainPlan(props.result));

const hasGeoPoints = computed(() => rows.value.some((row) =>
  props.result.columns.some((column) => parseGeoPointValue(row[column]) !== null)));

const hasChartData = computed(() => {
  if (!hasRows.value || rows.value.length === 0) return false;

  const numericColumns = props.result.columns.filter((column) =>
    rows.value.some((row) => isFiniteNumber(row[column])));

  if (numericColumns.length === 0) return false;

  return props.result.columns.some((column) => isTimeLikeColumn(column));
});

watch([hasRows, explainPlan, () => props.result.columns, visibleRows], () => {
  if (!hasRows.value) {
    view.value = 'raw';
    return;
  }

  if (explainPlan.value) {
    view.value = 'plan';
    return;
  }

  if (hasGeoPoints.value) {
    view.value = 'map';
    return;
  }

  if (hasChartData.value) {
    view.value = 'chart';
    return;
  }

  view.value = 'table';
}, { immediate: true });

const trimmedSql = computed(() => {
  const oneLine = props.sql.replace(/\s+/g, ' ').trim();
  return oneLine.length > 120 ? `${oneLine.slice(0, 117)}…` : oneLine;
});

const meta = computed(() => {
  if (!props.result.end) return '';
  const parts: string[] = [];
  if (props.result.hasColumns) parts.push(`${props.result.end.rowCount} 行`);
  if (props.result.end.recordsAffected >= 0) parts.push(`受影响 ${props.result.end.recordsAffected}`);
  parts.push(`${props.result.end.elapsedMs.toFixed(2)} ms`);
  return parts.join(' · ');
});

const statusType = computed<'success' | 'error' | 'info'>(() => {
  if (props.result.error) return 'error';
  if (hasRows.value) return 'success';
  return 'info';
});

const emptyText = computed(() => {
  if (props.displayRows && props.result.rows.length > 0 && visibleRows.value.length === 0) {
    return '没有匹配的结果行。';
  }

  return '语句已执行，没有结果集。';
});

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function tryParseTime(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value === 'string') {
    const time = Date.parse(value);
    if (!Number.isNaN(time)) return time;
    const parsed = Number(value);
    if (Number.isFinite(parsed)) return parsed;
  }
  return null;
}

function isTimeLikeColumn(column: string): boolean {
  if (/^(time|ts|timestamp)$/i.test(column)) return true;

  let parsedCount = 0;
  for (const row of rows.value) {
    if (tryParseTime(row[column]) !== null) parsedCount += 1;
  }
  return parsedCount > rows.value.length / 2;
}

const dataColumns = computed<DataTableColumns<Record<string, unknown>>>(() =>
  props.result.columns.map((c) => ({
    title: c,
    key: c,
    ellipsis: { tooltip: true },
    render: (row) => formatSqlValue(row[c]),
  })));

const rawText = computed(() => {
  if (!props.result.hasColumns) return '语句执行成功，但没有结果集。';
  const cols = props.result.columns;
  if (cols.length === 0) return '语句执行成功，但没有列。';

  const escape = (v: unknown): string => {
    if (v === null || v === undefined) return '';
    return formatSqlValue(v).replace(/\r?\n/g, ' ');
  };

  const lines: string[] = [];
  lines.push(cols.join('\t'));
  for (const row of visibleRows.value) {
    lines.push(cols.map((_, i) => escape(row[i])).join('\t'));
  }
  return lines.join('\n');
});

const jsonText = computed(() => JSON.stringify(rows.value, null, 2));
</script>

<style scoped>
.sql-result-card { background: #fff; }
.sql-result-card__sql {
  font-family: 'JetBrains Mono', Consolas, Menlo, monospace;
  font-size: 12px;
  color: #345;
  background: rgba(44, 123, 229, 0.08);
  padding: 1px 6px;
  border-radius: 4px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 600px;
  display: inline-block;
}
.sql-result-card__meta { color: #888; font-size: 12px; }
.sql-result-card__pre {
  max-height: 420px;
  padding: 10px 12px;
  margin: 0;
  overflow: auto;
  border: 1px solid rgba(13, 59, 102, 0.08);
  border-radius: 6px;
  background: #fafcfe;
  color: #234;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  line-height: 1.55;
}
</style>
