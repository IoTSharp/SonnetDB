<template>
  <main class="query-workspace" data-testid="workbench-sql">
    <WorkbenchSectionTabs
      :model-value="activeSection"
      :items="sqlSections"
      aria-label="SQL 与时序工作区"
      @update:model-value="selectSection($event as SqlSection)"
    />

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

    <WriteApprovalPanel
      v-if="previewPlan"
      :plan="previewPlan"
      :stale="previewIsStale"
      :busy="running"
      @cancel="$emit('cancel-preview')"
      @confirm="$emit('confirm-preview')"
    />

    <WorkbenchResultPanel
      title="SQL results"
      :summary="resultSummary"
      :error-message="errorMsg"
      :ran-once="ranOnce"
      :result="latestResultSet"
      :sql="latestResultSql"
      :file-name="fileName"
      empty-description="Run a SQL statement to see results."
      @clear-error="$emit('clear-error')"
    >
      <template #actions>
        <n-button size="small" quaternary title="Open query and operation history" @click="showHistoryDrawer = true">
          History
        </n-button>
      </template>
    </WorkbenchResultPanel>

    <WorkbenchHistoryDrawer
      v-model:show="showHistoryDrawer"
      :active-database="targetDb"
      @select="$emit('history-select', $event)"
    />
  </main>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import type { DropdownOption } from 'naive-ui';
import { NButton, NDropdown, NSpace, NTag } from 'naive-ui';
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

defineProps<{
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

const emit = defineEmits<{
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
  'open-trajectory': [];
}>();

const editorCursor = ref<EditorCursorInfo>({
  line: 1,
  column: 1,
  position: 0,
  length: 0,
});
const showHistoryDrawer = ref(false);
type SqlSection = 'query' | 'result' | 'chart' | 'trajectory';
const activeSection = ref<SqlSection>('query');
const sqlSections: WorkbenchSectionTab[] = [
  { key: 'query', label: '查询' },
  { key: 'result', label: '结果' },
  { key: 'chart', label: '图表' },
  { key: 'trajectory', label: '轨迹' },
];

function selectSection(section: SqlSection): void {
  activeSection.value = section;
  if (section === 'result' || section === 'chart') {
    window.dispatchEvent(new CustomEvent('sndb:toggle-result', { detail: { open: true } }));
    return;
  }
  if (section === 'trajectory') emit('open-trajectory');
}
</script>

<style scoped>
.query-workspace {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.query-tabs {
  display: flex;
  align-items: stretch;
  gap: 1px;
  overflow-x: auto;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #f8fbff;
}

.query-tab {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  flex: 0 0 auto;
  padding: 10px 14px;
  border: 0;
  border-right: 1px solid rgba(15, 23, 42, 0.06);
  background: transparent;
  color: #567;
  font: inherit;
  cursor: pointer;
}

.query-tab.is-active {
  background: #fff;
  color: #1f2a44;
  box-shadow: inset 0 -2px 0 rgb(44, 123, 229);
}

.query-tab__icon {
  padding: 1px 5px;
  border-radius: 3px;
  background: rgba(44, 123, 229, 0.08);
  color: rgb(44, 123, 229);
  font-size: 11px;
  font-weight: 700;
}

.query-tab.is-active .query-tab__icon {
  background: rgba(44, 123, 229, 0.12);
}

.query-tab__title {
  max-width: 160px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.query-tab__close {
  margin-left: 2px;
  color: #789;
  font-size: 16px;
  line-height: 1;
}

.query-tab--add {
  width: 40px;
  justify-content: center;
  padding: 0;
  font-size: 20px;
  font-weight: 300;
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
  flex: 1;
  min-height: 260px;
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

@media (max-width: 1280px) {
  .editor-shell {
    min-height: 300px;
  }
}

@media (max-width: 840px) {
  .query-toolbar {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
