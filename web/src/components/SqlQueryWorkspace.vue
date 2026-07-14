<template>
  <main class="query-workspace" data-testid="workbench-sql">
    <div class="query-toolbar">
      <n-space align="center" :size="8" :wrap="false">
        <n-button size="small" type="primary" :loading="running" @click="$emit('run')">
          {{ previewPlan ? 'Preview' : 'Run' }}
        </n-button>
        <n-button size="small" @click="$emit('explain')">Explain</n-button>
        <n-button size="small" @click="$emit('format')">Format</n-button>
        <n-dropdown trigger="click" placement="bottom-start" :options="quickSqlOptions" @select="$emit('quick-sql-select', $event)">
          <n-button size="small">Quick SQL</n-button>
        </n-dropdown>
        <n-button size="small" disabled title="SonnetDB 当前版本尚未暴露 active process 列表接口">Processes</n-button>
      </n-space>

      <div class="query-toolbar__meta">
        <n-tag size="small" :type="activeTab?.source === 'copilot' ? 'info' : 'default'" :bordered="false">
          {{ activeTab?.source === 'copilot' ? 'Copilot draft' : 'Manual' }}
        </n-tag>
        <n-tag v-if="activeTab?.ranOnce" size="small" type="success" :bordered="false">executed</n-tag>
      </div>
    </div>

    <section class="editor-shell">
      <SqlEditor
        :model-value="sql"
        :schema="currentSchema"
        placeholder="SHOW MEASUREMENTS;"
        @update:model-value="$emit('update:sql', $event)"
        @cursor="editorCursor = $event"
      />
    </section>

    <div class="editor-status">
      <span>search_path: {{ targetDb === CONTROL_PLANE_KEY ? 'system' : (targetDb || 'public') }}</span>
      <span>Ln {{ editorCursor.line }}, Col {{ editorCursor.column }}, Pos {{ editorCursor.position }}/{{ editorCursor.length }}</span>
    </div>

    <section ref="outputSection" class="query-output" data-testid="sql-result-region">
      <WorkbenchSectionTabs
        :model-value="activeSection"
        :items="sqlSections"
        aria-label="SQL 查询结果"
        @update:model-value="selectSection($event as SqlSection)"
      />

      <div class="query-output__content">
        <section v-if="activeSection === 'message'" class="query-message-panel" aria-live="polite">
          <WriteApprovalPanel
            v-if="previewPlan"
            :plan="previewPlan"
            :stale="previewIsStale"
            :busy="running"
            @cancel="$emit('cancel-preview')"
            @confirm="$emit('confirm-preview')"
          />

          <n-alert
            v-else-if="errorMsg || latestResultSet?.error"
            type="error"
            :title="errorMsg || latestResultSet?.error?.message"
            closable
            @close="$emit('clear-error')"
          />

          <div v-else class="query-message-state" :class="{ 'is-complete': ranOnce }">
            <span class="query-message-state__marker" />
            <div>
              <strong>{{ messageTitle }}</strong>
              <p>{{ messageDescription }}</p>
            </div>
          </div>
        </section>

        <WorkbenchResultPanel
          v-else
          inline
          :title="resultPanelTitle"
          :summary="resultSummary"
          :error-message="errorMsg"
          :ran-once="ranOnce"
          :result="latestResultSet"
          :sql="latestResultSql"
          :file-name="fileName"
          :view-mode="resultViewMode"
          :show-view-switcher="activeSection === 'table'"
          :available-views="activeSection === 'table' ? ['plan', 'table', 'raw', 'json'] : undefined"
          empty-description="执行 SQL 后在此查看结果。"
          @clear-error="$emit('clear-error')"
        >
          <template #actions>
            <n-button size="small" quaternary title="Open query and operation history" @click="showHistoryDrawer = true">
              History
            </n-button>
          </template>
        </WorkbenchResultPanel>
      </div>
    </section>

    <WorkbenchHistoryDrawer
      v-model:show="showHistoryDrawer"
      :active-database="targetDb"
      @select="$emit('history-select', $event)"
    />
  </main>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { DropdownOption } from 'naive-ui';
import { NAlert, NButton, NDropdown, NSpace, NTag } from 'naive-ui';
import type { SqlResultSet } from '@/api/sql';
import type { MeasurementInfo } from '@/api/schema';
import SqlEditor from '@/components/SqlEditor.vue';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import WorkbenchResultPanel from '@/components/WorkbenchResultPanel.vue';
import WorkbenchSectionTabs, { type WorkbenchSectionTab } from '@/components/WorkbenchSectionTabs.vue';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { CONTROL_PLANE_KEY, type SqlConsoleTab } from '@/stores/sqlConsole';
import type { WorkbenchHistoryEntry } from '@/stores/workbenchHistory';
import type { EditorCursorInfo, StagedPreview } from '@/utils/sqlWorkbench';

const props = defineProps<{
  tabs: SqlConsoleTab[];
  activeTabId: string;
  activeTab: SqlConsoleTab | null;
  sql: string;
  targetDb: string;
  currentSchema: MeasurementInfo[];
  running: boolean;
  previewPlan: StagedPreview | null;
  previewIsStale: boolean;
  quickSqlOptions: DropdownOption[];
  resultSummary: string;
  errorMsg: string;
  ranOnce: boolean;
  latestResultSet: SqlResultSet | null;
  latestResultSql: string;
  fileName: string;
}>();

defineEmits<{
  'update:activeTabId': [value: string];
  'update:sql': [value: string];
  'create-tab': [];
  'close-tab': [id: string];
  run: [];
  explain: [];
  format: [];
  'quick-sql-select': [key: string | number];
  'cancel-preview': [];
  'confirm-preview': [];
  'clear-error': [];
  'history-select': [entry: WorkbenchHistoryEntry];
}>();

const editorCursor = ref<EditorCursorInfo>({
  line: 1,
  column: 1,
  position: 0,
  length: 0,
});
const showHistoryDrawer = ref(false);
const outputSection = ref<HTMLElement | null>(null);
type SqlSection = 'message' | 'table' | 'chart' | 'trajectory';
const activeSection = ref<SqlSection>('message');
const sqlSections: WorkbenchSectionTab[] = [
  { key: 'message', label: '提示' },
  { key: 'table', label: '表格' },
  { key: 'chart', label: '图表' },
  { key: 'trajectory', label: '轨迹' },
];

const resultViewMode = computed<'table' | 'chart' | 'map'>(() => {
  if (activeSection.value === 'chart') return 'chart';
  if (activeSection.value === 'trajectory') return 'map';
  return 'table';
});

const resultPanelTitle = computed(() => {
  if (activeSection.value === 'chart') return 'SQL 图表';
  if (activeSection.value === 'trajectory') return 'SQL 轨迹';
  return 'SQL 结果';
});

const messageTitle = computed(() => {
  if (props.running) return '正在执行';
  return props.ranOnce ? '执行完成' : '等待执行';
});

const messageDescription = computed(() => {
  if (props.running) return '查询正在由当前连接处理。';
  if (props.resultSummary) return props.resultSummary;
  if (props.ranOnce) return '语句已完成，没有返回可显示的结果行。';
  return '当前 SQL 尚未执行。';
});

function selectSection(section: SqlSection): void {
  activeSection.value = section;
}

function focusResultRegion(): void {
  activeSection.value = props.latestResultSet?.hasColumns ? 'table' : 'message';
  requestAnimationFrame(() => outputSection.value?.scrollIntoView({ block: 'nearest' }));
}

watch(() => props.previewPlan, (plan) => {
  if (plan) activeSection.value = 'message';
});

watch(
  [() => props.latestResultSet, () => props.errorMsg, () => props.ranOnce],
  ([result, error, ranOnce]) => {
    if (!ranOnce || props.previewPlan) return;
    activeSection.value = error || result?.error || !result?.hasColumns ? 'message' : 'table';
  },
);

onMounted(() => window.addEventListener('sndb:toggle-result', focusResultRegion));
onBeforeUnmount(() => window.removeEventListener('sndb:toggle-result', focusResultRegion));
</script>

<style scoped>
.query-workspace {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.query-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.query-toolbar__meta {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.editor-shell {
  flex: 1 1 48%;
  min-height: 220px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.editor-shell :deep(.sql-editor) {
  height: 100%;
}

.editor-status {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 6px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fafcff;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.query-output {
  display: flex;
  flex: 0 0 clamp(240px, 38vh, 380px);
  flex-direction: column;
  min-width: 0;
  min-height: 220px;
  border-top: 1px solid var(--sndb-border-strong);
  background: #fff;
}

.query-output__content {
  flex: 1;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

.query-message-panel {
  height: 100%;
  padding: 16px;
  overflow: auto;
  background: var(--sndb-surface-subtle);
}

.query-message-state {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  max-width: 680px;
  padding: 16px 4px;
  color: var(--sndb-ink-soft);
}

.query-message-state__marker {
  width: 8px;
  height: 8px;
  margin-top: 6px;
  border-radius: 50%;
  background: var(--sndb-ink-muted);
}

.query-message-state.is-complete .query-message-state__marker {
  background: var(--sndb-success);
}

.query-message-state strong {
  color: var(--sndb-ink-strong);
  font-size: 14px;
}

.query-message-state p {
  margin: 4px 0 0;
  font-size: 13px;
  line-height: 1.6;
}

@media (max-width: 1280px) {
  .editor-shell {
    min-height: 220px;
  }
}

@media (max-width: 840px) {
  .query-toolbar {
    flex-direction: column;
    align-items: stretch;
  }

  .query-output {
    flex-basis: 300px;
  }
}
</style>
