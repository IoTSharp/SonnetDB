<template>
  <Teleport to="body">
    <div class="write-approval-backdrop" role="presentation" @click.self="cancelIfIdle">
      <section
        class="write-approval"
        :class="{ 'is-danger': plan.dangerous }"
        role="dialog"
        aria-modal="true"
        aria-labelledby="write-approval-title"
      >
        <header class="write-approval__head">
          <span class="write-approval__risk" :class="{ 'is-danger': plan.dangerous }">
            <TriangleAlert :size="22" />
          </span>
          <div class="write-approval__identity">
            <n-space size="small" align="center" :wrap="true">
              <n-tag size="small" :type="plan.dangerous ? 'error' : 'warning'" :bordered="false">
                {{ plan.dangerous ? '高风险操作' : '暂存预览' }}
              </n-tag>
              <n-text id="write-approval-title" class="write-approval__title">{{ plan.title }}</n-text>
            </n-space>
            <n-text depth="3" class="write-approval__summary">执行前请核对目标、影响范围和命令内容</n-text>
          </div>
          <n-button size="small" quaternary title="返回编辑" :disabled="busy" @click="$emit('cancel')">
            <template #icon><X :size="17" /></template>
          </n-button>
        </header>

        <dl class="write-approval__facts">
          <div><dt>目标对象</dt><dd>{{ plan.target }}</dd></div>
          <div><dt>操作数量</dt><dd>{{ plan.items.length }} 项</dd></div>
          <div><dt>写入 / 危险</dt><dd>{{ plan.writeCount }} / {{ plan.dangerCount }}</dd></div>
          <div><dt>暂存时间</dt><dd>{{ formatTime(plan.createdAt) }}</dd></div>
        </dl>

        <div class="write-approval__items">
          <article v-for="item in plan.items" :key="item.id" class="write-approval-item">
            <div class="write-approval-item__meta">
              <n-tag size="tiny" :type="approvalSeverityTagType(item.severity)" :bordered="false">
                {{ item.label }}
              </n-tag>
              <n-text v-if="item.detail" depth="3" class="write-approval-item__detail">{{ item.detail }}</n-text>
            </div>
            <code>{{ item.command }}</code>
          </article>
        </div>

        <footer class="write-approval__actions">
          <n-checkbox v-if="plan.dangerous" v-model:checked="dangerConfirmed">
            我已核对目标范围和参数，了解该操作可能修改或删除数据。
          </n-checkbox>
          <n-text v-if="stale" depth="3">预览已过期，请重新生成后再执行。</n-text>
          <span class="write-approval__spacer" />
          <n-button size="small" secondary :disabled="busy" @click="$emit('cancel')">返回编辑</n-button>
          <n-button
            v-if="busy && abortable"
            size="small"
            tertiary
            type="error"
            @click="$emit('abort')"
          >停止后续批次</n-button>
          <n-button
            v-if="plan.dryRunAvailable"
            size="small"
            secondary
            :loading="dryRunBusy"
            :disabled="stale || busy"
            @click="$emit('dry-run')"
          >{{ plan.dryRunLabel }}</n-button>
          <n-button
            size="small"
            type="primary"
            :disabled="busy || stale || (plan.dangerous && !dangerConfirmed)"
            :loading="busy"
            @click="$emit('confirm')"
          >
            <template #icon><ShieldCheck :size="16" /></template>
            {{ confirmLabel }}
          </n-button>
        </footer>
      </section>
    </div>
  </Teleport>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { NButton, NCheckbox, NSpace, NTag, NText } from 'naive-ui';
import { ShieldCheck, TriangleAlert, X } from 'lucide-vue-next';
import { approvalSeverityTagType, type WriteApprovalPlan } from '@/utils/writeApproval';

const props = defineProps<{
  plan: WriteApprovalPlan;
  stale?: boolean;
  busy?: boolean;
  dryRunBusy?: boolean;
  abortable?: boolean;
}>();

const emit = defineEmits<{
  cancel: [];
  confirm: [];
  abort: [];
  'dry-run': [];
}>();

const dangerConfirmed = ref(false);
const confirmLabel = computed(() => props.plan.dangerous
  ? `确认执行 ${props.plan.dangerCount} 项高风险操作`
  : `确认执行 ${props.plan.writeCount || props.plan.items.length} 项操作`);

watch(() => props.plan.id, () => {
  dangerConfirmed.value = false;
});

function formatTime(value: number): string {
  return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && !props.busy) emit('cancel');
}

function cancelIfIdle(): void {
  if (!props.busy) emit('cancel');
}

onMounted(() => window.addEventListener('keydown', onKeydown));
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown));
</script>

<style scoped>
.write-approval-backdrop {
  position: fixed;
  z-index: 1200;
  inset: 0;
  display: grid;
  place-items: center;
  padding: 24px;
  background: rgba(23, 33, 43, 0.42);
  backdrop-filter: blur(2px);
}

.write-approval {
  display: flex;
  width: min(720px, calc(100vw - 32px));
  max-height: min(760px, calc(100vh - 48px));
  flex-direction: column;
  gap: 16px;
  overflow: hidden;
  padding: 20px;
  border: 1px solid var(--sndb-border-strong);
  border-radius: var(--sndb-radius);
  background: #fff;
  box-shadow: 0 24px 64px rgba(23, 33, 43, 0.2);
}

.write-approval__head,
.write-approval__actions {
  display: flex;
  align-items: flex-start;
  gap: 12px;
}

.write-approval__head > .n-button {
  margin-left: auto;
}

.write-approval__risk {
  display: grid;
  flex: 0 0 40px;
  place-items: center;
  width: 40px;
  height: 40px;
  border-radius: 50%;
  background: #fff4ce;
  color: #8a4b08;
}

.write-approval__risk.is-danger {
  background: #fde7e9;
  color: var(--sndb-danger);
}

.write-approval__identity {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.write-approval__title {
  font-size: 17px;
  font-weight: 600;
}

.write-approval__summary,
.write-approval-item__detail {
  font-size: 12px;
}

.write-approval__facts {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  margin: 0;
  border: 1px solid var(--sndb-border);
  border-radius: var(--sndb-radius);
  background: var(--sndb-surface);
}

.write-approval__facts div {
  display: grid;
  grid-template-columns: 110px minmax(0, 1fr);
  gap: 10px;
  padding: 10px 12px;
  border-bottom: 1px solid var(--sndb-border);
}

.write-approval__facts div:nth-last-child(-n + 2) {
  border-bottom: 0;
}

.write-approval__facts dt {
  color: var(--sndb-ink-muted);
}

.write-approval__facts dd {
  min-width: 0;
  margin: 0;
  overflow: hidden;
  font-family: "Cascadia Code", Consolas, monospace;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.write-approval__items {
  display: flex;
  min-height: 0;
  flex-direction: column;
  gap: 8px;
  overflow: auto;
}

.write-approval-item {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px;
  border: 1px solid var(--sndb-border);
  border-radius: var(--sndb-radius);
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
  font-family: "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.write-approval__actions {
  align-items: center;
  flex-wrap: wrap;
}

.write-approval__spacer {
  flex: 1;
}

.write-approval :deep(.n-button--primary-type) {
  --n-color: var(--sndb-brand) !important;
  --n-color-hover: #123b4a !important;
  --n-color-pressed: #041820 !important;
  --n-border: 1px solid var(--sndb-brand) !important;
  --n-border-hover: 1px solid #123b4a !important;
  --n-border-pressed: 1px solid #041820 !important;
}

.write-approval.is-danger :deep(.n-button--primary-type) {
  --n-color: var(--sndb-danger) !important;
  --n-color-hover: #a40c19 !important;
  --n-color-pressed: #820914 !important;
  --n-border: 1px solid var(--sndb-danger) !important;
  --n-border-hover: 1px solid #a40c19 !important;
  --n-border-pressed: 1px solid #820914 !important;
}

@media (max-width: 840px) {
  .write-approval__facts {
    grid-template-columns: 1fr;
  }

  .write-approval__facts div,
  .write-approval__facts div:nth-last-child(-n + 2) {
    border-bottom: 1px solid var(--sndb-border);
  }

  .write-approval__facts div:last-child {
    border-bottom: 0;
  }
}
</style>
