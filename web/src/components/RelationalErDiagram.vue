<template>
  <section class="relation-er">
    <div class="relation-er__toolbar">
      <div>
        <n-text class="relation-er__title">Entity relationship diagram</n-text>
        <n-text depth="3" class="relation-er__meta">
          {{ tables.length }} tables · {{ relations.length }} relationships
        </n-text>
      </div>
      <n-button size="small" secondary @click="$emit('refresh-schema')">Refresh schema</n-button>
    </div>

    <div v-if="tables.length" class="relation-er__canvas">
      <article
        v-for="entity in tables"
        :key="entity.name"
        class="relation-er-table"
        :class="{ 'is-selected': entity.name === table?.name, 'has-relations': tableHasRelation(entity.name) }"
      >
        <header>
          <span>{{ entity.name }}</span>
          <n-tag size="tiny" :bordered="false">{{ entity.columns.length }} cols</n-tag>
        </header>
        <div class="relation-er-table__columns">
          <p v-for="column in entity.columns" :key="`${entity.name}:${column.name}`">
            <strong>{{ column.name }}</strong>
            <span>
              {{ column.dataType }}{{ column.isPrimaryKey ? ' · PK' : '' }}{{ column.isNullable ? ' · NULL' : '' }}
            </span>
          </p>
        </div>
      </article>
    </div>
    <n-empty v-else description="No relation tables in this database." />

    <section class="relation-er__relations">
      <div class="relation-er__relations-head">
        <n-text class="relation-er__section-title">Foreign key relationships</n-text>
        <n-tag size="small" :bordered="false">{{ relations.length }}</n-tag>
      </div>
      <n-data-table
        v-if="relations.length"
        :columns="relationColumns"
        :data="relations"
        :bordered="false"
        :pagination="false"
        size="small"
      />
      <n-empty v-else description="No foreign keys declared yet." />
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import {
  NButton,
  NDataTable,
  NEmpty,
  NTag,
  NText,
  type DataTableColumns,
} from 'naive-ui';
import type { TableInfo } from '@/api/schema';

const props = defineProps<{
  table: TableInfo | null;
  tables: TableInfo[];
}>();

defineEmits<{
  'refresh-schema': [];
}>();

interface RelationRow {
  key: string;
  name: string;
  from: string;
  to: string;
  onDelete: string;
}

const relations = computed<RelationRow[]>(() =>
  props.tables.flatMap((table) =>
    (table.foreignKeys ?? []).map((foreignKey) => ({
      key: `${table.name}:${foreignKey.name}`,
      name: foreignKey.name,
      from: `${table.name}(${foreignKey.columns.join(', ')})`,
      to: `${foreignKey.principalTable}(${foreignKey.principalColumns.join(', ')})`,
      onDelete: formatOnDelete(foreignKey.onDelete),
    }))));

const relationColumns: DataTableColumns<RelationRow> = [
  { title: 'Constraint', key: 'name', minWidth: 180 },
  { title: 'From', key: 'from', minWidth: 220 },
  { title: 'To', key: 'to', minWidth: 220 },
  { title: 'On delete', key: 'onDelete', width: 130 },
];

function tableHasRelation(tableName: string): boolean {
  return relations.value.some((relation) =>
    relation.from.startsWith(`${tableName}(`) || relation.to.startsWith(`${tableName}(`));
}

function formatOnDelete(value: string): string {
  return value.replace(/([a-z])([A-Z])/gu, '$1 $2').toUpperCase() || 'NO ACTION';
}
</script>

<style scoped>
.relation-er {
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

.relation-er__toolbar,
.relation-er__relations-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.relation-er__toolbar,
.relation-er__relations {
  padding: 12px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 7px;
  background: #fbfdff;
}

.relation-er__title,
.relation-er__section-title {
  display: block;
  color: var(--sndb-ink-strong);
  font-weight: 800;
}

.relation-er__title {
  font-size: 15px;
}

.relation-er__section-title {
  font-size: 13px;
}

.relation-er__meta {
  display: block;
  font-size: 12px;
}

.relation-er__canvas {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
  gap: 12px;
  align-items: start;
}

.relation-er-table {
  display: flex;
  flex-direction: column;
  min-width: 0;
  overflow: hidden;
  border: 1px solid rgba(15, 23, 42, 0.09);
  border-left: 4px solid rgba(15, 23, 42, 0.18);
  border-radius: 7px;
  background: #fff;
}

.relation-er-table.is-selected {
  border-left-color: #2c7be5;
  box-shadow: 0 10px 24px rgba(44, 123, 229, 0.08);
}

.relation-er-table.has-relations:not(.is-selected) {
  border-left-color: #18a058;
}

.relation-er-table header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 9px 10px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfcfe;
}

.relation-er-table header span {
  overflow: hidden;
  color: #20384d;
  font-size: 13px;
  font-weight: 900;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.relation-er-table__columns {
  display: flex;
  flex-direction: column;
  padding: 7px 10px 9px;
}

.relation-er-table__columns p {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 8px;
  margin: 0;
  padding: 4px 0;
  border-bottom: 1px solid rgba(15, 23, 42, 0.05);
}

.relation-er-table__columns p:last-child {
  border-bottom: 0;
}

.relation-er-table__columns strong {
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.relation-er-table__columns span {
  color: var(--sndb-ink-soft);
  font-size: 11px;
}

.relation-er__relations {
  display: flex;
  flex-direction: column;
  gap: 10px;
  background: #fff;
}

@media (max-width: 760px) {
  .relation-er__toolbar,
  .relation-er__relations-head {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
