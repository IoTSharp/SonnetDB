<template>
  <section class="write-approval">
    <div class="write-approval__head">
      <div class="write-approval__identity">
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" :type="plan.dangerous ? 'error' : 'warning'" :bordered="false">
            {{ plan.dangerous ? 'Dangerous staged preview' : 'Staged preview' }}
          </n-tag>
          <n-text class="write-approval__title">{{ plan.title }}</n-text>
        </n-space>
        <n-text depth="3" class="write-approval__summary">
          {{ plan.target }} · {{ plan.summary }}
        </n-text>
      </div>
      <n-button size="small" quaternary @click="$emit('cancel')">Cancel</n-button>
    </div>

    <div class="write-approval__items">
      <article
        v-for="item in plan.items"
        :key="item.id"
        class="write-approval-item"
      >
        <div class="write-approval-item__meta">
          <n-tag
            size="tiny"
            :type="approvalSeverityTagType(item.severity)"
            :bordered="false"
          >
            {{ item.label }}
          </n-tag>
          <n-text v-if="item.detail" depth="3" class="write-approval-item__detail">
            {{ item.detail }}
          </n-text>
        </div>
        <code>{{ item.command }}</code>
      </article>
    </div>

    <div class="write-approval__actions">
      <n-checkbox v-if="plan.dangerous" v-model:checked="dangerConfirmed">
        I understand this may modify or delete target data.
      </n-checkbox>
      <n-text v-if="stale" depth="3">The preview is stale. Run preview again before executing.</n-text>
      <n-button
        v-if="plan.dryRunAvailable"
        size="small"
        secondary
        :loading="dryRunBusy"
        :disabled="stale || busy"
        @click="$emit('dry-run')"
      >
        {{ plan.dryRunLabel }}
      </n-button>
      <n-button
        size="small"
        type="primary"
        :disabled="stale || (plan.dangerous && !dangerConfirmed)"
        :loading="busy"
        @click="$emit('confirm')"
      >
        {{ plan.dangerous ? 'Confirm danger run' : 'Confirm run' }}
      </n-button>
    </div>
  </section>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue';
import { NButton, NCheckbox, NSpace, NTag, NText } from 'naive-ui';
import {
  approvalSeverityTagType,
  type WriteApprovalPlan,
} from '@/utils/writeApproval';

const props = defineProps<{
  plan: WriteApprovalPlan;
  stale?: boolean;
  busy?: boolean;
  dryRunBusy?: boolean;
}>();

defineEmits<{
  cancel: [];
  confirm: [];
  'dry-run': [];
}>();

const dangerConfirmed = ref(false);

watch(() => props.plan.id, () => {
  dangerConfirmed.value = false;
});
</script>

<style scoped>
.write-approval {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fffef8;
}

.write-approval__head,
.write-approval__actions {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.write-approval__identity {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.write-approval__title {
  font-weight: 700;
}

.write-approval__summary,
.write-approval-item__detail {
  font-size: 12px;
}

.write-approval__items {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.write-approval-item {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
}

.write-approval-item__meta {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.write-approval-item code {
  display: block;
  overflow-x: auto;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.write-approval__actions {
  align-items: center;
  flex-wrap: wrap;
  justify-content: flex-end;
}

@media (max-width: 840px) {
  .write-approval__head {
    flex-direction: column;
  }

  .write-approval__actions {
    justify-content: flex-start;
  }
}
</style>
