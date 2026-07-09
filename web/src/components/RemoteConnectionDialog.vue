<template>
  <n-modal
    :show="show"
    :mask-closable="true"
    @update:show="$emit('update:show', $event)"
  >
    <n-card
      title="Remote connection"
      :bordered="false"
      size="small"
      class="connection-dialog"
      role="dialog"
      aria-modal="true"
    >
      <n-space vertical :size="12">
        <n-input
          :value="name"
          size="small"
          clearable
          placeholder="Connection name"
          @update:value="$emit('update:name', $event)"
        />
        <n-input
          :value="baseUrl"
          size="small"
          clearable
          placeholder="https://sonnetdb.example.com"
          @update:value="$emit('update:baseUrl', $event)"
        />
        <n-input
          :value="defaultDatabase"
          size="small"
          clearable
          placeholder="Default database"
          @update:value="$emit('update:defaultDatabase', $event)"
        />
        <n-space justify="end">
          <n-button tertiary @click="$emit('update:show', false)">
            Cancel
          </n-button>
          <n-button type="primary" :disabled="!canSave" @click="$emit('save')">
            Save
          </n-button>
        </n-space>
      </n-space>
    </n-card>
  </n-modal>
</template>

<script setup lang="ts">
import { NButton, NCard, NInput, NModal, NSpace } from 'naive-ui';

defineProps<{
  show: boolean;
  name: string;
  baseUrl: string;
  defaultDatabase: string;
  canSave: boolean;
}>();

defineEmits<{
  'update:show': [value: boolean];
  'update:name': [value: string];
  'update:baseUrl': [value: string];
  'update:defaultDatabase': [value: string];
  save: [];
}>();
</script>

<style scoped>
.connection-dialog {
  width: min(480px, calc(100vw - 32px));
}
</style>
