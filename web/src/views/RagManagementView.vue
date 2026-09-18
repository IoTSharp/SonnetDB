<template>
  <main class="rag-management" data-testid="rag-management">
    <section class="rag-toolbar">
      <div>
        <h2>RAG 管理</h2>
        <p>查看已发布快照、恢复摄取任务并管理派生索引。操作由服务器校验数据库管理权限。</p>
      </div>
      <n-button :loading="loadingDatabases" :disabled="busy" @click="loadDatabases">刷新数据库</n-button>
    </section>

    <n-card size="small" :bordered="false">
      <div class="rag-target">
        <label>数据库<n-auto-complete v-model:value="database" :options="databaseOptions" :input-props="{ maxlength: 256 }" placeholder="选择或输入数据库，例如 __copilot__" :loading="loadingDatabases" /></label>
        <label>Stream<n-input v-model:value="streamInput" maxlength="256" placeholder="例如 copilot-docs" @keydown.enter="refresh" /></label>
        <n-button type="primary" :disabled="!validTarget || busy" :loading="loading" @click="refresh">加载状态</n-button>
      </div>
    </n-card>

    <n-alert v-if="errorMsg" type="error" closable @close="errorMsg = ''">{{ errorMsg }}</n-alert>
    <n-alert v-if="infoMsg" type="info" closable @close="infoMsg = ''">{{ infoMsg }}</n-alert>
    <n-alert v-if="status && !safeRevision" type="warning">当前版本号超出浏览器安全整数范围，写入已禁用。</n-alert>

    <section class="rag-stats" aria-label="已发布状态">
      <article><span>Active revision</span><strong>{{ status?.activeRevision ?? '—' }}</strong></article>
      <article><span>Active profile</span><strong>{{ status?.activeProfileId || '—' }}</strong></article>
      <article><span>内容 / 分块</span><strong>{{ status ? `${status.activeContents} / ${status.activeChunks}` : '—' }}</strong></article>
      <article><span>待续跑任务</span><strong>{{ status ? status.pending ? '有待处理任务' : '无' : '—' }}</strong></article>
    </section>

    <div class="rag-panels">
      <n-card size="small" title="派生重建与 profile 换代" :bordered="false">
        <n-space vertical :size="12">
          <n-alert v-if="profileError" type="warning">Profile 暂不可用：{{ profileError }}。仍可查看状态、丢弃任务及清理退役版本。</n-alert>
          <n-text depth="3">从已发布快照重建。选择相同 profile 重建索引，选择新的已配置 profile 重新生成向量；新版本完整发布前继续使用当前版本。</n-text>
          <n-select v-model:value="selectedProfileId" :options="profileOptions" placeholder="选择服务器已配置的 profile" :disabled="!status || busy || loading" />
          <dl v-if="selectedProfile" class="rag-details">
            <div><dt>Provider / model</dt><dd>{{ selectedProfile.provider }} / {{ selectedProfile.model }}</dd></div>
            <div><dt>Revision</dt><dd>{{ selectedProfile.revision }}</dd></div>
            <div><dt>维度 / metric / normalization</dt><dd>{{ selectedProfile.dimensions }} / {{ selectedProfile.metric }} / {{ selectedProfile.normalization }}</dd></div>
          </dl>
          <n-text v-if="status && !profiles.length" depth="3">服务器未提供可用 profile，请先由管理员完成服务器配置。</n-text>
          <n-text v-if="status?.activeRevision === 0" depth="3">尚无已发布快照。请先通过摄取入口创建首个版本。</n-text>
          <n-text v-if="status?.pending" depth="3">存在待处理任务，请先续跑或丢弃，再发起重建。</n-text>
          <n-button :disabled="!canRebuild" :loading="busy" @click="stageOperation('rebuild')">预览重建 / 换代</n-button>
        </n-space>
      </n-card>

      <n-card size="small" title="持久任务" :bordered="false">
        <n-space v-if="status?.pending" vertical :size="12">
          <dl class="rag-details">
            <div><dt>Generation</dt><dd>{{ status.pending.generationId }}</dd></div>
            <div><dt>Profile</dt><dd>{{ status.pending.profileId }}</dd></div>
            <div><dt>预期 active revision</dt><dd>{{ status.pending.expectedRevision }}</dd></div>
            <div><dt>内容 / 分块</dt><dd>{{ status.pending.contents }} / {{ status.pending.chunks }}</dd></div>
          </dl>
          <n-text depth="3">续跑需选择与任务一致的已配置 profile。丢弃会删除该任务的未发布派生数据，保留已发布版本。</n-text>
          <n-space>
            <n-button :disabled="!canResume" @click="stageOperation('resume')">预览续跑</n-button>
            <n-button type="error" secondary :disabled="!canManage" @click="stageOperation('discard')">预览丢弃</n-button>
          </n-space>
        </n-space>
        <n-empty v-else :description="status ? '没有待处理任务' : '加载状态后查看任务'" />
      </n-card>

      <n-card size="small" title="清理已退役派生版本" :bordered="false">
        <n-space vertical :size="12">
          <n-text depth="3">清理此前发布且已退役的派生版本（含该时刻）；正在被查询使用的版本延后处理。删除后不能再使用这些派生资源，原始主数据保留。</n-text>
          <label>发布时间截止点（包含该时刻）<n-date-picker v-model:value="retiredBefore" type="datetime" :disabled="!status || busy" clearable /></label>
          <label>本次最多检查的版本数（含延后条目）<n-input-number v-model:value="maxGenerations" :min="1" :max="100" :precision="0" :disabled="!status || busy" /></label>
          <n-button type="error" secondary :disabled="!canManage || !validCleanup" @click="stageOperation('cleanup')">预览清理范围</n-button>
        </n-space>
      </n-card>
    </div>

    <n-card size="small" title="数据库 RAG 管理审计" :bordered="false">
      <template #header-extra>
        <n-space>
          <n-button size="small" :disabled="!validTarget || busy || loading" :loading="loadingAudit" @click="loadAudit(false)">第一页</n-button>
          <n-button size="small" :disabled="!auditToken || busy || loading || loadingAudit" @click="loadAudit(true)">下一页</n-button>
        </n-space>
      </template>
      <n-text depth="3">本页最多 50 条，覆盖当前数据库的所有 stream；stream 使用哈希标识。</n-text>
      <n-alert v-if="auditError" type="warning">审计暂不可用：{{ auditError }}</n-alert>
      <n-data-table :columns="auditColumns" :data="auditEntries" :loading="loadingAudit || loading" :row-key="auditRowKey" :bordered="false" :pagination="false" :scroll-x="1100" size="small" />
    </n-card>

    <WriteApprovalPanel v-if="approvalPlan" :plan="approvalPlan" :busy="busy" :abortable="busy"
      @cancel="clearApproval" @confirm="confirmOperation" @abort="cancelOperation" />
  </main>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import { NAlert, NAutoComplete, NButton, NCard, NDataTable, NDatePicker, NEmpty, NInput, NInputNumber, NSelect, NSpace, NText, type DataTableColumns } from 'naive-ui';
import { createApiClient } from '@/api/client';
import { fetchRagAudit, fetchRagDatabases, fetchRagProfiles, fetchRagStatus, runRagManagement,
  type RagAuditEntry, type RagConfiguredProfile, type RagManagementOperation, type RagManagementRequest, type RagManagementStatus } from '@/api/ragManagement';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import { createWriteApprovalPlan } from '@/utils/writeApproval';

const auth = useAuthStore();
const connections = useConnectionsStore();
const database = ref(connections.activeDatabase || '');
const databases = ref<string[]>([]);
const streamInput = ref('copilot-docs');
const status = ref<RagManagementStatus | null>(null);
const profiles = ref<RagConfiguredProfile[]>([]);
const selectedProfileId = ref<string | null>(null);
const retiredBefore = ref<number | null>(Date.now());
const maxGenerations = ref<number | null>(20);
const auditEntries = ref<RagAuditEntry[]>([]);
const auditToken = ref<string | null>(null);
const errorMsg = ref('');
const profileError = ref('');
const auditError = ref('');
const infoMsg = ref('');
const loading = ref(false);
const loadingAudit = ref(false);
const loadingDatabases = ref(false);
const busy = ref(false);
let disposed = false;
let loadedContext = '';
let readController: AbortController | null = null;
let auditController: AbortController | null = null;
let databaseController: AbortController | null = null;
let mutationController: AbortController | null = null;

function connectionKey(): string {
  return JSON.stringify([connections.activeProfileId, connections.activeBaseUrl, auth.api.defaults.baseURL, auth.state?.token]);
}
function contextKey(): string { return JSON.stringify([connectionKey(), database.value, streamInput.value]); }
function captureContext() {
  const token = auth.state?.token ?? null;
  const api = createApiClient(() => token);
  api.defaults.baseURL = auth.api.defaults.baseURL || connections.activeBaseUrl || '/';
  return { key: contextKey(), connection: connectionKey(), api, database: database.value.trim(), stream: streamInput.value.trim() };
}
type Context = ReturnType<typeof captureContext>;
interface PendingOperation { context: Context; operation: RagManagementOperation; request: RagManagementRequest; createdAt: number }
const pendingOperation = shallowRef<PendingOperation | null>(null);
const validTarget = computed(() => !!database.value.trim() && database.value.length <= 256
  && !!streamInput.value.trim() && streamInput.value.length <= 256);
const safeRevision = computed(() => !!status.value && Number.isSafeInteger(status.value.activeRevision) && status.value.activeRevision >= 0);
const canManage = computed(() => validTarget.value && safeRevision.value && loadedContext === contextKey() && !busy.value && !loading.value);
const selectedProfile = computed(() => profiles.value.find((profile) => profile.id === selectedProfileId.value));
const canRebuild = computed(() => canManage.value && !!selectedProfile.value && status.value!.activeRevision > 0 && !status.value!.pending);
const canResume = computed(() => canManage.value && !!status.value?.pending && selectedProfile.value?.id === status.value.pending.profileId);
const validCleanup = computed(() => retiredBefore.value !== null && Number.isFinite(new Date(retiredBefore.value).getTime())
  && retiredBefore.value <= Date.now() && maxGenerations.value !== null && Number.isInteger(maxGenerations.value)
  && maxGenerations.value >= 1 && maxGenerations.value <= 100);
const databaseOptions = computed(() => databases.value.map((value) => ({ label: value, value })));
const profileOptions = computed(() => profiles.value.map((profile) => ({ label: `${profile.id} · ${profile.model}`, value: profile.id })));
const operationLabels: Record<RagManagementOperation, string> = { rebuild: '重建 / profile 换代', resume: '续跑持久任务', discard: '丢弃持久任务', cleanup: '清理已退役版本' };
const approvalPlan = computed(() => {
  const pending = pendingOperation.value;
  if (!pending) return null;
  const destructive = pending.operation === 'discard' || pending.operation === 'cleanup';
  return createWriteApprovalPlan({ id: `rag-${pending.createdAt}`, title: operationLabels[pending.operation],
    target: `${pending.context.database} / ${pending.context.stream}`, createdAt: pending.createdAt,
    items: [{ id: pending.operation, label: operationLabels[pending.operation], severity: destructive ? 'danger' : 'write',
      detail: pending.operation === 'discard' ? '删除未发布任务及其派生数据，保留 active 版本。'
        : pending.operation === 'cleanup' ? '删除此前发布且已退役、无查询租约的派生资源（含该时刻）。'
          : `使用服务器已配置 profile：${pending.request.profileId}`,
      command: JSON.stringify(pending.request, null, 2) }],
  });
});
const auditColumns: DataTableColumns<RagAuditEntry> = [
  { title: '时间', key: 'startedUtc', width: 185 }, { title: '操作', key: 'operation', width: 110 },
  { title: '执行者', key: 'principal', width: 120 }, { title: '状态', key: 'status', width: 110 },
  { title: '预期版本', key: 'expectedRevision', width: 100 }, { title: '结果版本', key: 'revision', width: 100 },
  { title: '错误代码', key: 'errorCode', width: 180 }, { title: 'Stream hash', key: 'streamHash', width: 220, ellipsis: { tooltip: true } },
];
function auditRowKey(entry: RagAuditEntry): string { return entry.operationId; }
function current(context: Context): boolean { return !disposed && context.key === contextKey(); }
function frozenStatusIsCurrent(): boolean { return !!status.value && loadedContext === contextKey(); }

function invalidate(): void {
  readController?.abort(); auditController?.abort(); mutationController?.abort();
  readController = null; auditController = null; mutationController = null;
  loadedContext = ''; status.value = null; profiles.value = []; selectedProfileId.value = null;
  auditEntries.value = []; auditToken.value = null; pendingOperation.value = null;
  loading.value = false; loadingAudit.value = false; busy.value = false;
  errorMsg.value = ''; profileError.value = ''; auditError.value = ''; infoMsg.value = '';
}

async function loadDatabases(): Promise<void> {
  if (busy.value || disposed) return;
  databaseController?.abort();
  const controller = new AbortController(); databaseController = controller;
  const context = captureContext(); loadingDatabases.value = true;
  try {
    const result = await fetchRagDatabases(context.api, controller.signal);
    if (disposed || controller.signal.aborted || context.connection !== connectionKey()) return;
    databases.value = result;
    if (!database.value.trim()) database.value = result[0] ?? '';
  } catch (error) {
    if (!disposed && !controller.signal.aborted && context.connection === connectionKey()) errorMsg.value = formatError(error);
  } finally {
    if (databaseController === controller) { databaseController = null; loadingDatabases.value = false; }
  }
}

async function refresh(): Promise<void> {
  if (!validTarget.value || busy.value || disposed) return;
  readController?.abort(); auditController?.abort();
  auditController = null; loadingAudit.value = false;
  const controller = new AbortController(); readController = controller;
  const context = captureContext(); loading.value = true;
  loadedContext = ''; status.value = null; pendingOperation.value = null;
  errorMsg.value = ''; profileError.value = ''; auditError.value = '';
  try {
    const [statusResult, profilesResult, auditResult] = await Promise.allSettled([
      fetchRagStatus(context.api, context.database, context.stream, controller.signal),
      fetchRagProfiles(context.api, context.database, controller.signal),
      fetchRagAudit(context.api, context.database, controller.signal),
    ]);
    if (!current(context) || controller.signal.aborted || readController !== controller) return;
    if (statusResult.status === 'fulfilled') {
      if (statusResult.value.stream !== context.stream) throw new Error('服务器返回的 stream 与当前目标不一致。');
      loadedContext = context.key; status.value = statusResult.value;
    } else errorMsg.value = formatError(statusResult.reason);
    profiles.value = profilesResult.status === 'fulfilled' ? profilesResult.value : [];
    if (profilesResult.status === 'rejected') profileError.value = formatError(profilesResult.reason);
    const preferred = status.value?.pending?.profileId ?? status.value?.activeProfileId;
    selectedProfileId.value = profiles.value.find((profile) => profile.id === preferred)?.id ?? profiles.value[0]?.id ?? null;
    auditEntries.value = auditResult.status === 'fulfilled' ? auditResult.value.entries : [];
    auditToken.value = auditResult.status === 'fulfilled' ? auditResult.value.continuationToken ?? null : null;
    if (auditResult.status === 'rejected') auditError.value = formatError(auditResult.reason);
  } catch (error) {
    if (current(context) && !controller.signal.aborted && readController === controller) errorMsg.value = formatError(error);
  } finally {
    controller.abort();
    if (readController === controller) { readController = null; loading.value = false; }
  }
}

async function loadAudit(next: boolean): Promise<void> {
  if (!validTarget.value || busy.value || loading.value || loadingAudit.value || next && !auditToken.value) return;
  const context = captureContext();
  const controller = new AbortController(); auditController = controller; loadingAudit.value = true;
  try {
    const page = await fetchRagAudit(context.api, context.database, controller.signal, next ? auditToken.value! : undefined);
    if (!current(context) || controller.signal.aborted || auditController !== controller) return;
    auditEntries.value = page.entries; auditToken.value = page.continuationToken ?? null;
    auditError.value = '';
  } catch (error) {
    if (current(context) && !controller.signal.aborted && auditController === controller) auditError.value = formatError(error);
  } finally {
    if (auditController === controller) { auditController = null; loadingAudit.value = false; }
  }
}

function stageOperation(operation: RagManagementOperation): void {
  if (!canManage.value || !frozenStatusIsCurrent()) return;
  if (operation === 'rebuild' && !canRebuild.value || operation === 'resume' && !canResume.value
    || operation === 'discard' && !status.value!.pending || operation === 'cleanup' && !validCleanup.value) return;
  const context = captureContext();
  const request: RagManagementRequest = { stream: context.stream, expectedRevision: status.value!.activeRevision };
  if (operation === 'rebuild' || operation === 'resume') request.profileId = selectedProfileId.value!;
  if (operation === 'resume' || operation === 'discard') request.pendingGenerationId = status.value!.pending!.generationId;
  if (operation === 'cleanup') { request.retiredBeforeUtc = new Date(retiredBefore.value!).toISOString(); request.maxGenerations = maxGenerations.value!; }
  pendingOperation.value = { context, operation, request, createdAt: Date.now() };
}

async function confirmOperation(): Promise<void> {
  const pending = pendingOperation.value;
  if (!pending || busy.value) return;
  if (!canManage.value || !current(pending.context) || Date.now() - pending.createdAt > 120_000
    || pending.request.expectedRevision !== status.value?.activeRevision
    || pending.request.pendingGenerationId && pending.request.pendingGenerationId !== status.value?.pending?.generationId) {
    pendingOperation.value = null; errorMsg.value = '预览或目标已变化，请重新加载状态并预览。'; return;
  }
  const context = pending.context;
  const controller = new AbortController(); mutationController = controller;
  busy.value = true; errorMsg.value = ''; infoMsg.value = '';
  let completed = false;
  try {
    const result = await runRagManagement(context.api, context.database, pending.operation, pending.request, controller.signal);
    if (!current(context) || controller.signal.aborted || mutationController !== controller) return;
    completed = true;
    infoMsg.value = result.status === 'no_pending_task' ? '服务器没有待续跑任务。'
      : `${operationLabels[pending.operation]}完成，revision ${result.revision}。`
        + (pending.operation === 'cleanup' ? ` 已清理 ${result.removedRevisions.length} 个，租约延后 ${result.deferredRevisions.length} 个。` : '');
  } catch (error) {
    if (current(context) && mutationController === controller) {
      const responseStatus = (error as { response?: { status?: number } })?.response?.status;
      errorMsg.value = `${formatError(error)}${responseStatus && responseStatus >= 400 && responseStatus < 500 && responseStatus !== 408
        ? ' 请重新加载状态后再操作。' : ' 操作结果尚未确认，请刷新状态及审计后再操作；不会自动重试。'}`;
      loadedContext = ''; status.value = null;
    }
  } finally {
    if (mutationController === controller) { mutationController = null; busy.value = false; pendingOperation.value = null; }
  }
  if (completed && current(context)) await refresh();
}

function clearApproval(): void { if (!busy.value) pendingOperation.value = null; }
function cancelOperation(): void {
  if (!mutationController) return;
  loadedContext = ''; status.value = null;
  infoMsg.value = '已停止等待操作结果，请刷新状态及审计确认服务器进度；不会自动重试。';
  mutationController.abort();
}
function formatError(error: unknown): string {
  const value = error as { response?: { data?: { error?: string; message?: string } }; message?: string; code?: string };
  const code = value?.response?.data?.error ?? value?.code;
  const message = value?.response?.data?.message ?? value?.message ?? '请求失败';
  return code ? `${code}: ${message}` : message;
}

watch([database, streamInput], invalidate, { flush: 'sync' });
watch([selectedProfileId, retiredBefore, maxGenerations], () => { if (!busy.value) pendingOperation.value = null; }, { flush: 'sync' });
watch(() => [connections.activeProfileId, connections.activeBaseUrl, auth.api.defaults.baseURL, auth.state?.token], () => {
  invalidate(); databaseController?.abort(); database.value = connections.activeDatabase || ''; databases.value = [];
  void loadDatabases();
}, { immediate: true, flush: 'sync' });
onBeforeUnmount(() => { disposed = true; invalidate(); databaseController?.abort(); });
</script>

<style scoped>
.rag-management { display: flex; flex-direction: column; gap: 16px; padding: 20px; min-width: 0; }
.rag-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 16px; }
.rag-toolbar h2 { margin: 0 0 6px; font-size: 20px; }
.rag-toolbar p { margin: 0; color: var(--sndb-text-muted, #64748b); font-size: 13px; }
.rag-target { display: flex; align-items: end; gap: 12px; flex-wrap: wrap; }
.rag-target label { flex: 1; min-width: 220px; }
label { display: flex; flex-direction: column; gap: 6px; font-size: 13px; }
.rag-stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; }
.rag-stats article { display: flex; flex-direction: column; gap: 8px; padding: 14px; background: var(--sndb-panel, #fff); border: 1px solid var(--sndb-border, #e4e8ec); border-radius: var(--sndb-radius, 6px); }
.rag-stats span { font-size: 12px; color: var(--sndb-text-muted, #64748b); }
.rag-stats strong { font-size: 16px; overflow-wrap: anywhere; }
.rag-panels { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.rag-details { display: flex; flex-direction: column; gap: 8px; margin: 0; font-size: 13px; }
.rag-details div { display: grid; grid-template-columns: 140px 1fr; gap: 12px; }
.rag-details dt { color: var(--sndb-text-muted, #64748b); }
.rag-details dd { margin: 0; overflow-wrap: anywhere; }
@media (max-width: 900px) { .rag-panels { grid-template-columns: 1fr; } .rag-stats { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 560px) { .rag-management { padding: 12px; } .rag-toolbar { align-items: flex-start; } .rag-details div { grid-template-columns: 1fr; gap: 4px; } }
</style>
