<template>
  <n-space vertical :size="16">
    <n-card title="实时指标" :bordered="false">
      <n-grid :cols="4" :x-gap="16" :y-gap="16">
        <n-gi>
          <n-statistic label="运行时长 (s)" :value="metrics ? Math.floor(metrics.uptimeSeconds) : 0" />
        </n-gi>
        <n-gi>
          <n-statistic label="数据库" :value="metrics?.databases ?? 0" />
        </n-gi>
        <n-gi>
          <n-statistic label="累计 SQL" :value="metrics?.sqlRequests ?? 0" />
        </n-gi>
        <n-gi>
          <n-statistic label="累计错误" :value="metrics?.sqlErrors ?? 0" />
        </n-gi>
        <n-gi>
          <n-statistic label="INSERT 行数" :value="metrics?.rowsInserted ?? 0" />
        </n-gi>
        <n-gi>
          <n-statistic label="SELECT 行数" :value="metrics?.rowsReturned ?? 0" />
        </n-gi>
        <n-gi>
          <n-statistic label="SSE 订阅数" :value="metrics?.subscriberCount ?? 0" />
        </n-gi>
        <n-gi>
          <n-statistic label="最近更新" :value="lastUpdated" />
        </n-gi>
      </n-grid>
      <n-text v-if="!metrics" depth="3">等待第一个 metrics 帧（通常 ≤5 秒）。</n-text>
    </n-card>

    <n-card title="慢查询" :bordered="false">
      <n-data-table
        :columns="slowCols"
        :data="events.slowQueries"
        :bordered="false"
        size="small"
        :max-height="320"
      />
      <n-text v-if="events.slowQueries.length === 0" depth="3">
        尚无慢查询。阈值由服务端 <code>SonnetDBServer:Observability:SlowQueryLog:ThresholdMs</code> 配置。
      </n-text>
    </n-card>

    <n-card title="数据库事件" :bordered="false">
      <n-data-table
        :columns="dbCols"
        :data="events.dbEvents"
        :bordered="false"
        size="small"
        :max-height="240"
      />
      <n-text v-if="events.dbEvents.length === 0" depth="3">尚无 CREATE / DROP DATABASE 事件。</n-text>
    </n-card>
  </n-space>
</template>

<script setup lang="ts">
import { computed, h } from 'vue';
import {
  NCard, NSpace, NGrid, NGi, NStatistic, NDataTable, NText, NTag,
  type DataTableColumns,
} from 'naive-ui';
import { useEventsStore, type SlowQueryEntry, type DatabaseEventEntry } from '@/stores/events';

const events = useEventsStore();
const metrics = computed(() => events.metrics);

const lastUpdated = computed(() => {
  if (!events.metricsUpdatedAt) return '—';
  return new Date(events.metricsUpdatedAt).toLocaleTimeString();
});

const slowCols: DataTableColumns<SlowQueryEntry> = [
  { title: '时间', key: 'receivedAt', width: 110, render: (r) => new Date(r.receivedAt).toLocaleTimeString() },
  { title: '数据库', key: 'database', width: 140, ellipsis: { tooltip: true } },
  { title: '耗时(ms)', key: 'elapsedMs', width: 110, render: (r) => r.elapsedMs.toFixed(2) },
  { title: '行数', key: 'rowCount', width: 90 },
  { title: '受影响', key: 'recordsAffected', width: 90 },
  {
    title: '状态', key: 'failed', width: 90,
    render: (r) => h(NTag, { size: 'small', type: r.failed ? 'error' : 'success' }, {
      default: () => r.failed ? '失败' : '成功',
    }),
  },
  { title: 'SQL', key: 'sql', ellipsis: { tooltip: true } },
];

const dbCols: DataTableColumns<DatabaseEventEntry> = [
  { title: '时间', key: 'receivedAt', width: 110, render: (r) => new Date(r.receivedAt).toLocaleTimeString() },
  { title: '数据库', key: 'database' },
  {
    title: '操作', key: 'action', width: 110,
    render: (r) => h(NTag, {
      size: 'small',
      type: r.action === 'created' ? 'success' : 'warning',
    }, { default: () => r.action }),
  },
];
</script>
