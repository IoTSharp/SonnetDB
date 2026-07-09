<template>
  <section class="relation-ddl">
    <div class="relation-ddl__toolbar">
      <div>
        <n-text class="relation-ddl__title">DDL script export</n-text>
        <n-text depth="3" class="relation-ddl__meta">
          Generates CREATE TABLE, index, and foreign key scripts from schema metadata.
        </n-text>
      </div>
      <n-space size="small" align="center" :wrap="true">
        <n-radio-group v-model:value="scope" size="small">
          <n-radio-button value="current" :disabled="!table">Current table</n-radio-button>
          <n-radio-button value="all">All tables</n-radio-button>
        </n-radio-group>
        <n-button size="small" secondary :disabled="!ddlScript.trim()" @click="copyDdlScript">Copy</n-button>
        <n-button size="small" secondary :disabled="!ddlScript.trim()" @click="downloadDdlScript">Download</n-button>
        <n-button size="small" type="primary" :disabled="!ddlScript.trim()" @click="emit('openSql', ddlScript)">Open in SQL</n-button>
      </n-space>
    </div>

    <n-input
      :value="ddlScript"
      readonly
      type="textarea"
      :autosize="{ minRows: 20, maxRows: 34 }"
      class="relation-ddl__text"
    />
  </section>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import {
  NButton,
  NInput,
  NRadioButton,
  NRadioGroup,
  NSpace,
  NText,
  useMessage,
} from 'naive-ui';
import type { TableInfo } from '@/api/schema';
import { buildSchemaDdl } from '@/utils/relationalImportExport';
import { copyText, downloadText, safeFileStem } from '@/utils/resultExport';

const props = defineProps<{
  targetDb: string;
  table: TableInfo | null;
  tables: TableInfo[];
}>();

const emit = defineEmits<{
  openSql: [sql: string];
}>();

const message = useMessage();
const scope = ref<'current' | 'all'>('current');

const ddlScript = computed(() => {
  if (scope.value === 'current') {
    return props.table ? buildSchemaDdl(props.tables, props.table) : '';
  }
  return buildSchemaDdl(props.tables);
});

async function copyDdlScript(): Promise<void> {
  const ok = await copyText(ddlScript.value);
  if (ok) {
    message.success('DDL copied');
  } else {
    message.warning('Clipboard is unavailable');
  }
}

function downloadDdlScript(): void {
  const name = scope.value === 'current'
    ? `${props.targetDb}_${props.table?.name ?? 'table'}_ddl`
    : `${props.targetDb}_schema_ddl`;
  downloadText(`${safeFileStem(name, 'schema')}.sql`, ddlScript.value, 'application/sql;charset=utf-8');
}
</script>

<style scoped>
.relation-ddl {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
  min-height: 0;
  padding: 12px;
  overflow: auto;
  background: #fff;
}

.relation-ddl__toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 12px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 7px;
  background: #fbfdff;
}

.relation-ddl__title {
  display: block;
  color: var(--sndb-ink-strong);
  font-size: 15px;
  font-weight: 800;
}

.relation-ddl__meta {
  display: block;
  font-size: 12px;
}

.relation-ddl__text {
  flex: 1;
  min-height: 0;
}

.relation-ddl__text :deep(textarea) {
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  line-height: 1.55;
}

@media (max-width: 760px) {
  .relation-ddl__toolbar {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
