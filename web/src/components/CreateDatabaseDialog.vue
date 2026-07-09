<template>
  <n-modal
    :show="show"
    :mask-closable="!busy"
    :close-on-esc="!busy"
    @update:show="$emit('update:show', $event)"
  >
    <n-card
      title="Create database"
      :bordered="false"
      size="small"
      class="create-database-dialog"
      role="dialog"
      aria-modal="true"
    >
      <n-space vertical :size="12">
        <n-text depth="3">Enter a valid database name, then confirm to create it.</n-text>
        <n-input
          :value="name"
          size="small"
          clearable
          placeholder="new database"
          autofocus
          @update:value="$emit('update:name', $event)"
          @keyup.enter="$emit('create')"
        />
        <n-space justify="end">
          <n-button tertiary :disabled="busy" @click="$emit('cancel')">
            Cancel
          </n-button>
          <n-button
            type="primary"
            :loading="busy"
            :disabled="!canCreate"
            @click="$emit('create')"
          >
            Create
          </n-button>
        </n-space>
      </n-space>
    </n-card>
  </n-modal>
</template>

<script setup lang="ts">
import { NButton, NCard, NInput, NModal, NSpace, NText } from 'naive-ui';

defineProps<{
  show: boolean;
  name: string;
  busy: boolean;
  canCreate: boolean;
}>();

defineEmits<{
  'update:show': [value: boolean];
  'update:name': [value: string];
  cancel: [];
  create: [];
}>();
</script>

<style scoped>
.create-database-dialog {
  width: min(440px, calc(100vw - 32px));
}
</style>
