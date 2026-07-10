<template>
  <nav class="workbench-section-tabs" :aria-label="ariaLabel">
    <button
      v-for="item in items"
      :key="item.key"
      type="button"
      class="workbench-section-tab"
      :class="{ 'is-active': item.key === modelValue }"
      :aria-current="item.key === modelValue ? 'page' : undefined"
      @click="$emit('update:modelValue', item.key)"
    >
      <component v-if="item.icon" :is="item.icon" :size="16" :stroke-width="1.8" />
      <span>{{ item.label }}</span>
      <strong v-if="item.count !== undefined">{{ item.count }}</strong>
    </button>
  </nav>
</template>

<script setup lang="ts">
import type { Component } from 'vue';

export interface WorkbenchSectionTab {
  key: string;
  label: string;
  icon?: Component;
  count?: string | number;
}

withDefaults(defineProps<{
  modelValue: string;
  items: WorkbenchSectionTab[];
  ariaLabel?: string;
}>(), {
  ariaLabel: '工作台视图',
});

defineEmits<{
  'update:modelValue': [value: string];
}>();
</script>

<style scoped>
.workbench-section-tabs {
  display: flex;
  flex: 0 0 43px;
  align-items: end;
  gap: 2px;
  min-width: 0;
  padding: 0 20px;
  overflow-x: auto;
  border-bottom: 1px solid var(--sndb-border);
  background: var(--sndb-surface-strong);
  scrollbar-width: thin;
}

.workbench-section-tab {
  position: relative;
  display: inline-flex;
  flex: 0 0 auto;
  align-items: center;
  justify-content: center;
  gap: 7px;
  min-width: 80px;
  height: 42px;
  padding: 0 12px;
  border: 0;
  background: transparent;
  color: var(--sndb-ink-muted);
  font: inherit;
  cursor: pointer;
}

.workbench-section-tab:hover {
  background: var(--sndb-hover);
  color: var(--sndb-ink-strong);
}

.workbench-section-tab.is-active {
  color: var(--sndb-interactive);
  font-weight: 600;
}

.workbench-section-tab.is-active::after {
  position: absolute;
  right: 8px;
  bottom: 0;
  left: 8px;
  height: 2px;
  background: var(--sndb-interactive);
  content: '';
}

.workbench-section-tab strong {
  min-width: 18px;
  height: 18px;
  padding: 0 5px;
  border-radius: 9px;
  background: var(--sndb-hover);
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 600;
  line-height: 18px;
}

@media (max-width: 800px) {
  .workbench-section-tabs {
    padding: 0 10px;
  }

  .workbench-section-tab {
    min-width: 72px;
    padding: 0 9px;
  }
}
</style>
