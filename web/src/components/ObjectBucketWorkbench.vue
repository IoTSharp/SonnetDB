<template>
  <main class="object-workbench" data-testid="workbench-bucket">
    <section class="object-toolbar">
      <div class="object-toolbar__identity">
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="info" :bordered="false">Object</n-tag>
          <n-text class="object-toolbar__title">{{ activeBucket || 'No bucket selected' }}</n-text>
          <n-tag v-if="currentPrefix" size="tiny" :bordered="false">prefix {{ currentPrefix }}</n-tag>
        </n-space>
        <n-text depth="3" class="object-toolbar__meta">
          {{ targetDb || 'database' }} · {{ rows.length }} loaded objects · {{ checkedRowKeys.length }} selected
        </n-text>
      </div>

      <div class="object-toolbar__actions">
        <n-select
          v-model:value="selectedBucket"
          size="small"
          :options="bucketOptions"
          :disabled="bucketOptions.length === 0"
          class="object-toolbar__bucket"
        />
        <n-input
          v-model:value="prefixInput"
          size="small"
          clearable
          placeholder="Prefix"
          class="object-toolbar__prefix"
          @keydown.enter="applyPrefix"
        />
        <n-input
          v-model:value="delimiter"
          size="small"
          maxlength="4"
          placeholder="/"
          class="object-toolbar__delimiter"
        />
        <n-select v-model:value="listLimit" size="small" :options="listLimitOptions" class="object-toolbar__limit" />
        <n-button size="small" secondary :disabled="!activeBucket" :loading="loadingObjects" @click="applyPrefix">
          Browse
        </n-button>
        <n-button size="small" secondary :loading="loading" @click="refreshAll">
          Refresh
        </n-button>
        <n-button size="small" quaternary @click="historyVisible = true">History</n-button>
      </div>
    </section>

    <WorkbenchSectionTabs
      :model-value="inspectorTab"
      :items="objectSections"
      aria-label="对象存储工作区"
      @update:model-value="inspectorTab = $event as InspectorTab"
    />

    <WriteApprovalPanel
      v-if="previewPlan"
      :plan="previewPlan"
      :busy="confirmBusy"
      @cancel="clearPendingOperations"
      @confirm="confirmPendingOperations"
    />

    <n-alert
      v-if="errorMsg"
      type="error"
      :title="errorMsg"
      closable
      class="object-alert"
      @close="errorMsg = ''"
    />

    <section class="object-stats">
      <article v-for="item in statItems" :key="item.label" class="object-stat">
        <span>{{ item.label }}</span>
        <strong>{{ item.value }}</strong>
      </article>
    </section>

    <section class="object-body" :class="{ 'is-focused': inspectorTab !== 'preview' }">
      <aside v-if="inspectorTab === 'preview'" class="object-nav">
        <div class="object-panel-head">
          <div>
            <n-text class="object-panel-head__title">Buckets</n-text>
            <n-text depth="3" class="object-panel-head__meta">{{ localBuckets.length }} buckets</n-text>
          </div>
        </div>

        <div class="object-create">
          <n-input v-model:value="newBucketName" size="small" placeholder="New bucket" />
          <n-input v-model:value="newBucketPurpose" size="small" placeholder="Purpose" />
          <n-space size="small" align="center" :wrap="true">
            <n-button size="small" type="primary" :disabled="!newBucketName.trim()" @click="stageCreateBucket">
              Stage create
            </n-button>
            <n-button size="small" tertiary type="error" :disabled="!activeBucket" @click="stageDeleteBucket">
              Stage drop
            </n-button>
          </n-space>
        </div>

        <div class="object-bucket-list">
          <button
            v-for="bucket in filteredBuckets"
            :key="bucket.name"
            type="button"
            class="object-bucket-card"
            :class="{ 'is-active': bucket.name === activeBucket }"
            @click="selectBucket(bucket.name)"
          >
            <span>{{ bucket.name }}</span>
            <small>{{ bucket.purpose || 'general purpose' }}</small>
          </button>
          <n-empty v-if="filteredBuckets.length === 0" description="No object buckets." />
        </div>

        <div class="object-panel-head object-panel-head--compact">
          <div>
            <n-text class="object-panel-head__title">Prefix tree</n-text>
            <n-text depth="3" class="object-panel-head__meta">{{ namespaceSummary }}</n-text>
          </div>
          <n-button size="tiny" quaternary :disabled="!currentPrefix" @click="openParentPrefix">Up</n-button>
        </div>

        <div class="object-path">
          <button type="button" @click="openPrefix('')">root</button>
          <template v-for="crumb in prefixCrumbs" :key="crumb.prefix">
            <span>/</span>
            <button type="button" @click="openPrefix(crumb.prefix)">{{ crumb.label }}</button>
          </template>
        </div>

        <div class="object-folder-list">
          <button
            v-for="folder in namespaceFolders"
            :key="folder.prefix"
            type="button"
            class="object-folder"
            @click="openPrefix(folder.prefix)"
          >
            <span>{{ folder.name }}</span>
            <small>{{ folder.count }} loaded objects</small>
          </button>
          <n-empty v-if="namespaceFolders.length === 0" description="No child prefixes in loaded rows." />
        </div>
      </aside>

      <section v-if="inspectorTab === 'preview'" class="object-grid-panel">
        <div class="object-panel-head object-panel-head--grid">
          <div>
            <n-text class="object-panel-head__title">Objects</n-text>
            <n-text depth="3" class="object-panel-head__meta">{{ gridSummary }}</n-text>
          </div>
          <div class="object-grid-tools">
            <n-input
              v-model:value="objectFilter"
              size="small"
              clearable
              placeholder="Filter loaded objects"
              class="object-grid-tools__filter"
            />
            <n-button size="small" secondary :disabled="!selectedObject" @click="downloadSelectedObject">
              Download
            </n-button>
            <n-button size="small" tertiary type="error" :disabled="checkedRowKeys.length === 0" @click="stageDeleteSelected">
              Stage delete
            </n-button>
          </div>
        </div>

        <n-data-table
          :columns="objectColumns"
          :data="filteredRows"
          :loading="loadingObjects || props.loading"
          :bordered="false"
          :single-line="false"
          :pagination="false"
          :row-key="rowKey"
          :checked-row-keys="checkedRowKeys"
          size="small"
          remote
          flex-height
          class="object-grid"
          @update:checked-row-keys="checkedRowKeys = $event"
        />

        <footer class="object-pager">
          <span>{{ pagerText }}</span>
          <n-space size="small" align="center">
            <n-button size="small" :disabled="!hasMore || loadingObjects" :loading="loadingObjects" @click="loadMore">
              Load more
            </n-button>
            <n-button size="small" quaternary :disabled="rows.length === 0" @click="clearRows">Clear page</n-button>
          </n-space>
        </footer>
      </section>

      <aside class="object-inspector">
        <div class="object-panel-head">
          <div>
            <n-text class="object-panel-head__title">{{ objectSectionTitle }}</n-text>
            <n-text depth="3" class="object-panel-head__meta">
              {{ selectedObject?.key ?? 'No object selected' }}
            </n-text>
          </div>
          <n-tag v-if="selectedObject" size="tiny" :bordered="false">{{ selectedObject.contentType }}</n-tag>
        </div>

        <section v-if="inspectorTab === 'preview'" class="object-inspector-section">
          <template v-if="selectedObject">
            <div class="object-detail-strip">
              <span>{{ formatBytes(selectedObject.sizeBytes) }}</span>
              <span>versions {{ versions.length }}</span>
              <span>{{ selectedObject.isDeleteMarker ? 'delete marker' : 'current' }}</span>
            </div>
            <div class="object-preview-controls">
              <n-input-number v-model:value="rangeStart" size="small" :min="0" :show-button="false" placeholder="Start" />
              <n-input-number v-model:value="rangeLength" size="small" :min="1" :show-button="false" placeholder="Bytes" />
              <n-select v-model:value="previewMode" size="small" :options="previewModeOptions" />
              <n-button size="small" secondary :loading="loadingPreview" @click="() => loadPreview()">Range read</n-button>
            </div>
            <pre class="object-preview">{{ previewText || 'Load a byte range to preview this object.' }}</pre>
            <div class="object-section-title">
              <span>Versions</span>
              <n-button size="tiny" quaternary @click="() => loadVersions()">Refresh</n-button>
            </div>
            <n-data-table
              :columns="versionColumns"
              :data="versions"
              :bordered="false"
              :pagination="false"
              :single-line="false"
              size="small"
              class="object-version-grid"
            />
            <div class="object-presign">
              <n-select v-model:value="presignMethod" size="small" :options="presignMethodOptions" />
              <n-input-number v-model:value="presignMinutes" size="small" :min="1" :max="1440" :show-button="false" />
              <n-button size="small" secondary @click="stagePresign">Stage URL</n-button>
            </div>
            <n-input
              v-if="presignedUrl"
              :value="presignedUrl"
              size="small"
              readonly
              type="textarea"
              :autosize="{ minRows: 2, maxRows: 4 }"
            />
          </template>
          <n-empty v-else description="Select an object from the browser." />
        </section>

        <section v-else-if="inspectorTab === 'governance'" class="object-inspector-section">
          <div class="object-governance-grid">
            <span>
              <small>Current size</small>
              <strong>{{ formatBytes(stats?.currentSizeBytes) }}</strong>
            </span>
            <span>
              <small>Versions</small>
              <strong>{{ formatStat(stats?.objectVersionCount) }}</strong>
            </span>
            <span>
              <small>Delete markers</small>
              <strong>{{ formatStat(stats?.deleteMarkerCount) }}</strong>
            </span>
            <span>
              <small>Multipart bytes</small>
              <strong>{{ formatBytes(stats?.multipartPartSizeBytes) }}</strong>
            </span>
          </div>

          <div class="object-form-block">
            <n-text class="object-section-title object-section-title--standalone">Lifecycle</n-text>
            <div class="object-form-row object-form-row--three">
              <n-input-number v-model:value="lifecycleDraft.expireCurrentAfterDays" size="small" :min="0" :show-button="false" placeholder="Current days" />
              <n-input-number v-model:value="lifecycleDraft.expireNoncurrentAfterDays" size="small" :min="0" :show-button="false" placeholder="Noncurrent days" />
              <n-input-number v-model:value="lifecycleDraft.expireDeleteMarkerAfterDays" size="small" :min="0" :show-button="false" placeholder="Marker days" />
            </div>
            <n-space size="small" align="center">
              <n-button size="small" secondary :disabled="!activeBucket" @click="stageSetLifecycle">Stage save</n-button>
              <n-button size="small" tertiary type="error" :disabled="!activeBucket" @click="stageApplyLifecycle">Stage apply</n-button>
            </n-space>
          </div>

          <div class="object-form-block">
            <n-text class="object-section-title object-section-title--standalone">Retention & quota</n-text>
            <div class="object-form-row">
              <n-input-number v-model:value="retentionDraft.retainCurrentForDays" size="small" :min="0" :show-button="false" placeholder="Retain current days" />
              <n-input-number v-model:value="retentionDraft.retainNoncurrentForDays" size="small" :min="0" :show-button="false" placeholder="Retain noncurrent days" />
            </div>
            <div class="object-form-row">
              <n-input-number v-model:value="quotaDraft.maxSizeBytes" size="small" :min="0" :show-button="false" placeholder="Max size bytes" />
              <n-input-number v-model:value="quotaDraft.maxObjectVersions" size="small" :min="0" :show-button="false" placeholder="Max versions" />
            </div>
            <n-space size="small" align="center">
              <n-button size="small" secondary :disabled="!activeBucket" @click="stageSetRetention">Stage retention</n-button>
              <n-button size="small" secondary :disabled="!activeBucket" @click="stageSetQuota">Stage quota</n-button>
            </n-space>
          </div>

          <div class="object-form-block">
            <n-text class="object-section-title object-section-title--standalone">Policy JSON</n-text>
            <n-input
              v-model:value="policyDraft"
              type="textarea"
              :autosize="{ minRows: 4, maxRows: 8 }"
              placeholder="{ }"
            />
            <n-button size="small" secondary :disabled="!activeBucket" @click="stageSetPolicy">Stage policy</n-button>
          </div>

          <div class="object-form-block">
            <n-text class="object-section-title object-section-title--standalone">Legal hold</n-text>
            <div class="object-form-row object-form-row--hold">
              <n-switch v-model:value="legalHoldEnabled" :disabled="!selectedObject">
                <template #checked>On</template>
                <template #unchecked>Off</template>
              </n-switch>
              <n-input v-model:value="legalHoldReason" size="small" placeholder="Reason" :disabled="!selectedObject" />
              <n-button size="small" secondary :disabled="!selectedObject" @click="stageSetLegalHold">
                Stage hold
              </n-button>
            </div>
          </div>
        </section>

        <section v-else-if="inspectorTab === 'upload'" class="object-inspector-section">
          <div class="object-form-block">
            <n-text class="object-section-title object-section-title--standalone">Put object</n-text>
            <n-input v-model:value="uploadKey" size="small" placeholder="Object key" />
            <n-input v-model:value="uploadContentType" size="small" placeholder="Content-Type" />
            <input type="file" class="object-file-input" @change="onUploadFileChange">
            <n-input
              v-model:value="uploadText"
              type="textarea"
              :autosize="{ minRows: 5, maxRows: 9 }"
              placeholder="Or paste text content"
            />
            <div class="object-form-row">
              <n-input v-model:value="metadataText" type="textarea" :autosize="{ minRows: 3, maxRows: 6 }" placeholder="Metadata key=value" />
              <n-input v-model:value="tagsText" type="textarea" :autosize="{ minRows: 3, maxRows: 6 }" placeholder="Tags key=value" />
            </div>
            <n-space size="small" align="center" :wrap="true">
              <n-button size="small" type="primary" :disabled="!activeBucket || !uploadKey.trim() || !uploadFile" @click="stageUploadFile">
                Stage file upload
              </n-button>
              <n-button size="small" secondary :disabled="!activeBucket || !uploadKey.trim() || !uploadText" @click="stageUploadText">
                Stage text upload
              </n-button>
            </n-space>
          </div>

          <div class="object-form-block">
            <n-text class="object-section-title object-section-title--standalone">Tags & copy</n-text>
            <n-input
              v-model:value="selectedTagsText"
              type="textarea"
              :autosize="{ minRows: 3, maxRows: 6 }"
              placeholder="Tags key=value"
              :disabled="!selectedObject"
            />
            <n-input v-model:value="copyTargetKey" size="small" placeholder="Copy target key" :disabled="!selectedObject" />
            <n-space size="small" align="center" :wrap="true">
              <n-button size="small" secondary :disabled="!selectedObject" @click="stageSetTags">Stage tags</n-button>
              <n-button size="small" secondary :disabled="!selectedObject || !copyTargetKey.trim()" @click="stageCopySelected">Stage copy</n-button>
              <n-button size="small" tertiary type="error" :disabled="!selectedObject" @click="stageDeleteCurrent">
                Stage delete
              </n-button>
            </n-space>
          </div>
        </section>

        <section v-else-if="inspectorTab === 'multipart'" class="object-inspector-section">
          <div class="object-form-block">
            <n-text class="object-section-title object-section-title--standalone">Current multipart session</n-text>
            <n-input v-model:value="multipartKey" size="small" placeholder="Object key" />
            <n-input v-model:value="multipartContentType" size="small" placeholder="Content-Type" />
            <div class="object-form-row">
              <n-input-number v-model:value="multipartExpiresHours" size="small" :min="1" :show-button="false" placeholder="Expires hours" />
              <n-button size="small" type="primary" :disabled="!activeBucket || !multipartKey.trim()" @click="stageInitiateMultipart">
                Stage initiate
              </n-button>
            </div>
          </div>

          <div class="object-multipart-card" :class="{ 'is-empty': !activeMultipart }">
            <template v-if="activeMultipart">
              <strong>{{ activeMultipart.key }}</strong>
              <span>{{ activeMultipart.uploadId }}</span>
              <small>expires {{ formatDate(activeMultipart.expiresUtc) }} · {{ multipartParts.length }} parts</small>
            </template>
            <span v-else>No active multipart session in this browser tab.</span>
          </div>

          <div class="object-form-block">
            <div class="object-form-row">
              <n-input-number v-model:value="multipartPartNumber" size="small" :min="1" :show-button="false" placeholder="Part number" />
              <input type="file" class="object-file-input" :disabled="!activeMultipart" @change="onMultipartFileChange">
            </div>
            <n-space size="small" align="center" :wrap="true">
              <n-button size="small" secondary :disabled="!activeMultipart || !multipartFile" @click="stageUploadPart">
                Stage upload part
              </n-button>
              <n-button size="small" secondary :disabled="!activeMultipart || multipartParts.length === 0" @click="stageCompleteMultipart">
                Stage complete
              </n-button>
              <n-button size="small" tertiary type="error" :disabled="!activeMultipart" @click="stageAbortMultipart">
                Stage abort
              </n-button>
            </n-space>
          </div>

          <n-data-table
            :columns="partColumns"
            :data="multipartParts"
            :bordered="false"
            :pagination="false"
            :single-line="false"
            size="small"
          />
        </section>

        <section v-else class="object-inspector-section">
          <div class="object-form-row object-form-row--audit">
            <n-input v-model:value="auditPrefix" size="small" clearable placeholder="Audit prefix" />
            <n-input-number v-model:value="auditMaxEntries" size="small" :min="1" :show-button="false" placeholder="Max" />
            <n-button size="small" secondary :disabled="!activeBucket" :loading="loadingAudit" @click="loadAudit">
              Refresh
            </n-button>
          </div>
          <n-data-table
            :columns="auditColumns"
            :data="auditEntries"
            :loading="loadingAudit"
            :bordered="false"
            :pagination="false"
            :single-line="false"
            size="small"
            class="object-audit-grid"
          />
        </section>
      </aside>
    </section>

    <WorkbenchResultPanel
      class="object-result"
      title="Object bucket result"
      :sql="latestCommand"
      :result="latestResult"
      :ran-once="ranOnce"
      :summary="resultSummary"
      :file-name="`${targetDb}_${activeBucket || 'objects'}`"
      empty-description="Browse a bucket or stage object operations to see results."
      @clear-error="latestResult = null"
    />

    <WorkbenchHistoryDrawer
      v-model:show="historyVisible"
      :active-database="targetDb"
      @select="openHistoryEntry"
    />
  </main>
</template>

<script setup lang="ts">
import { computed, h, onMounted, reactive, ref, watch } from 'vue';
import {
  NAlert,
  NButton,
  NDataTable,
  NEmpty,
  NInput,
  NInputNumber,
  NSelect,
  NSpace,
  NSwitch,
  NTag,
  NText,
  useMessage,
  type DataTableColumns,
  type DataTableRowKey,
  type SelectOption,
} from 'naive-ui';
import type { ObjectBucketInfo } from '@/api/management';
import {
  abortMultipartUpload,
  applyBucketLifecycle,
  completeMultipartUpload,
  copyObject,
  createObjectBucket,
  createPresignedObjectUrl,
  deleteManyObjects,
  deleteObject,
  deleteObjectBucket,
  getBucketLifecycle,
  getBucketPolicy,
  getBucketQuota,
  getBucketRetention,
  getBucketStats,
  getObjectBlob,
  getObjectLegalHold,
  getObjectTags,
  initiateMultipartUpload,
  listBucketAudit,
  listObjectBuckets,
  listObjects,
  listObjectVersions,
  putObject,
  setBucketLifecycle,
  setBucketPolicy,
  setBucketQuota,
  setBucketRetention,
  setObjectLegalHold,
  setObjectTags,
  uploadMultipartPart,
  type MultipartPartResponse,
  type MultipartUploadCreateResponse,
  type ObjectAuditEntryResponse,
  type ObjectBucketResponse,
  type ObjectInfoResponse,
  type ObjectLifecycleResponse,
  type ObjectQuotaResponse,
  type ObjectRetentionResponse,
  type ObjectStatsResponse,
} from '@/api/objectStorage';
import type { SqlResultSet } from '@/api/sql';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import WorkbenchResultPanel from '@/components/WorkbenchResultPanel.vue';
import WorkbenchSectionTabs, { type WorkbenchSectionTab } from '@/components/WorkbenchSectionTabs.vue';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import {
  useWorkbenchHistoryStore,
  type WorkbenchHistoryEntry,
} from '@/stores/workbenchHistory';
import {
  createWriteApprovalPlan,
  type WriteApprovalItem,
  type WriteApprovalPlan,
  type WriteApprovalSeverity,
} from '@/utils/writeApproval';

const props = withDefaults(defineProps<{
  targetDb: string;
  bucket: string;
  buckets?: ObjectBucketInfo[];
  loading?: boolean;
}>(), {
  buckets: () => [],
  loading: false,
});

const emit = defineEmits<{
  selectBucket: [bucket: string];
  refreshSchema: [];
}>();

type PreviewMode = 'text' | 'hex' | 'base64';
type InspectorTab = 'preview' | 'governance' | 'upload' | 'multipart' | 'audit';

interface ObjectRow extends ObjectInfoResponse {
  tagCount: number;
  metadataCount: number;
}

interface NamespaceFolder {
  name: string;
  prefix: string;
  count: number;
}

interface PendingOperation {
  id: string;
  label: string;
  detail: string;
  severity: WriteApprovalSeverity;
  command: string;
  run: () => Promise<OperationOutcome>;
}

interface OperationOutcome {
  action: string;
  target: string;
  succeeded: boolean;
  affected: number;
  detail: string;
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();

const localBuckets = ref<ObjectBucketResponse[]>([]);
const rows = ref<ObjectRow[]>([]);
const stats = ref<ObjectStatsResponse | null>(null);
const versions = ref<ObjectInfoResponse[]>([]);
const auditEntries = ref<ObjectAuditEntryResponse[]>([]);
const activeMultipart = ref<MultipartUploadCreateResponse | null>(null);
const multipartParts = ref<MultipartPartResponse[]>([]);
const currentPrefix = ref('');
const prefixInput = ref('');
const delimiter = ref('/');
const listLimit = ref(100);
const cursor = ref<string | null>(null);
const hasMore = ref(false);
const loading = ref(false);
const loadingObjects = ref(false);
const loadingPreview = ref(false);
const loadingAudit = ref(false);
const errorMsg = ref('');
const objectFilter = ref('');
const selectedKey = ref('');
const checkedRowKeys = ref<DataTableRowKey[]>([]);
const inspectorTab = ref<InspectorTab>('preview');
const objectSections: WorkbenchSectionTab[] = [
  { key: 'preview', label: '对象浏览' },
  { key: 'governance', label: '治理' },
  { key: 'upload', label: '上传 / 下载' },
  { key: 'multipart', label: 'Multipart' },
  { key: 'audit', label: '审计' },
];
const objectSectionTitle = computed(() => objectSections.find((item) => item.key === inspectorTab.value)?.label ?? '对象详情');
const rangeStart = ref<number | null>(0);
const rangeLength = ref<number | null>(4096);
const previewMode = ref<PreviewMode>('text');
const previewText = ref('');
const presignMethod = ref('GET');
const presignMinutes = ref<number | null>(60);
const presignedUrl = ref('');
const newBucketName = ref('');
const newBucketPurpose = ref('');
const uploadKey = ref('');
const uploadContentType = ref('application/octet-stream');
const uploadFile = ref<File | null>(null);
const uploadText = ref('');
const metadataText = ref('');
const tagsText = ref('');
const selectedTagsText = ref('');
const copyTargetKey = ref('');
const multipartKey = ref('');
const multipartContentType = ref('application/octet-stream');
const multipartExpiresHours = ref<number | null>(24);
const multipartPartNumber = ref<number | null>(1);
const multipartFile = ref<File | null>(null);
const auditPrefix = ref('');
const auditMaxEntries = ref<number | null>(100);
const legalHoldEnabled = ref(false);
const legalHoldReason = ref('');
const policyDraft = ref('');
const pendingOperations = ref<PendingOperation[]>([]);
const confirmBusy = ref(false);
const latestResult = ref<SqlResultSet | null>(null);
const latestCommand = ref('');
const ranOnce = ref(false);
const historyVisible = ref(false);

const lifecycleDraft = reactive<Omit<ObjectLifecycleResponse, 'bucket' | 'updatedUtc'>>({
  expireCurrentAfterDays: null,
  expireNoncurrentAfterDays: null,
  expireDeleteMarkerAfterDays: null,
});

const retentionDraft = reactive<Omit<ObjectRetentionResponse, 'bucket' | 'updatedUtc'>>({
  retainCurrentForDays: null,
  retainNoncurrentForDays: null,
});

const quotaDraft = reactive<Omit<ObjectQuotaResponse, 'bucket' | 'updatedUtc'>>({
  maxSizeBytes: null,
  maxObjectVersions: null,
});

const listLimitOptions: SelectOption[] = [
  { label: '50 objects', value: 50 },
  { label: '100 objects', value: 100 },
  { label: '250 objects', value: 250 },
  { label: '500 objects', value: 500 },
  { label: '1000 objects', value: 1000 },
];

const previewModeOptions: SelectOption[] = [
  { label: 'Text', value: 'text' },
  { label: 'Hex', value: 'hex' },
  { label: 'Base64', value: 'base64' },
];

const presignMethodOptions: SelectOption[] = [
  { label: 'GET', value: 'GET' },
  { label: 'HEAD', value: 'HEAD' },
  { label: 'PUT', value: 'PUT' },
  { label: 'DELETE', value: 'DELETE' },
];

const activeBucket = computed(() => props.bucket || localBuckets.value[0]?.name || '');

const selectedBucket = computed({
  get: () => activeBucket.value,
  set: (value: string) => selectBucket(value),
});

const bucketOptions = computed<SelectOption[]>(() => {
  const names = new Set(localBuckets.value.map((bucket) => bucket.name));
  if (props.bucket) names.add(props.bucket);
  return [...names].sort().map((name) => ({ label: name, value: name }));
});

const filteredBuckets = computed(() =>
  [...localBuckets.value].sort((a, b) => a.name.localeCompare(b.name)));

const selectedObject = computed(() =>
  rows.value.find((row) => row.key === selectedKey.value) ?? null);

const filteredRows = computed(() => {
  const keyword = objectFilter.value.trim().toLowerCase();
  if (!keyword) return rows.value;
  return rows.value.filter((row) =>
    row.key.toLowerCase().includes(keyword)
    || row.contentType.toLowerCase().includes(keyword)
    || row.versionId.toLowerCase().includes(keyword)
    || Object.keys(row.tags).some((tag) => tag.toLowerCase().includes(keyword)));
});

const namespaceFolders = computed<NamespaceFolder[]>(() => {
  const sep = delimiter.value || '/';
  const folders = new Map<string, NamespaceFolder>();
  for (const row of rows.value) {
    if (!row.key.startsWith(currentPrefix.value)) continue;
    const rest = row.key.slice(currentPrefix.value.length);
    const index = rest.indexOf(sep);
    if (index <= -1) continue;
    const name = rest.slice(0, index);
    if (!name) continue;
    const prefix = `${currentPrefix.value}${name}${sep}`;
    const existing = folders.get(prefix);
    if (existing) existing.count += 1;
    else folders.set(prefix, { name, prefix, count: 1 });
  }
  return [...folders.values()].sort((a, b) => a.name.localeCompare(b.name));
});

const prefixCrumbs = computed(() => {
  const sep = delimiter.value || '/';
  const trimmed = currentPrefix.value.endsWith(sep)
    ? currentPrefix.value.slice(0, -sep.length)
    : currentPrefix.value;
  if (!trimmed) return [];
  const parts = trimmed.split(sep).filter(Boolean);
  let prefix = '';
  return parts.map((part) => {
    prefix += `${part}${sep}`;
    return { label: part, prefix };
  });
});

const namespaceSummary = computed(() =>
  currentPrefix.value
    ? `${currentPrefix.value} · ${namespaceFolders.value.length} child prefixes`
    : `${namespaceFolders.value.length} root prefixes`);

const gridSummary = computed(() =>
  `${filteredRows.value.length} visible · ${rows.value.length} loaded${hasMore.value ? ' · more available' : ''}`);

const pagerText = computed(() =>
  hasMore.value
    ? `Loaded ${rows.value.length} objects. Continuation token is ready for the next page.`
    : `Loaded ${rows.value.length} objects. End of current list window.`);

const statItems = computed(() => [
  { label: 'Objects', value: formatStat(stats.value?.currentObjectCount) },
  { label: 'Current bytes', value: formatBytes(stats.value?.currentSizeBytes) },
  { label: 'Versions', value: formatStat(stats.value?.objectVersionCount) },
  { label: 'Quota bytes left', value: formatBytes(stats.value?.quotaRemainingSizeBytes) },
  { label: 'Multipart', value: `${formatStat(stats.value?.multipartUploadCount)} / ${formatBytes(stats.value?.multipartPartSizeBytes)}` },
]);

const previewPlan = computed<WriteApprovalPlan | null>(() => {
  if (pendingOperations.value.length === 0) return null;
  const items: WriteApprovalItem[] = pendingOperations.value.map((operation) => ({
    id: operation.id,
    command: operation.command,
    severity: operation.severity,
    label: operation.label,
    detail: operation.detail,
  }));
  return createWriteApprovalPlan({
    id: `object_${props.targetDb}_${activeBucket.value}_${pendingOperations.value.map((item) => item.id).join('_')}`,
    title: 'Object bucket operation batch',
    target: `${props.targetDb}.${activeBucket.value || 'objects'}`,
    items,
  });
});

const resultSummary = computed(() => {
  if (!latestResult.value) return gridSummary.value;
  if (latestResult.value.error) return latestResult.value.error.message;
  if (latestResult.value.end) {
    const affected = latestResult.value.end.recordsAffected >= 0
      ? `affected ${latestResult.value.end.recordsAffected}`
      : `${latestResult.value.end.rowCount} rows`;
    return `${affected} · ${latestResult.value.end.elapsedMs.toFixed(2)} ms`;
  }
  return 'Ready';
});

const objectColumns = computed<DataTableColumns<ObjectRow>>(() => [
  { type: 'selection', width: 42 },
  {
    title: 'Key',
    key: 'key',
    minWidth: 260,
    ellipsis: { tooltip: true },
    render: (row) => h('button', {
      type: 'button',
      class: ['object-key-button', row.key === selectedKey.value ? 'is-active' : ''],
      onClick: () => selectObject(row.key),
    }, row.key),
  },
  {
    title: 'Size',
    key: 'sizeBytes',
    width: 106,
    render: (row) => h('code', formatBytes(row.sizeBytes)),
  },
  {
    title: 'Type',
    key: 'contentType',
    minWidth: 150,
    ellipsis: { tooltip: true },
  },
  {
    title: 'Tags',
    key: 'tags',
    width: 74,
    render: (row) => h(NTag, { size: 'tiny', bordered: false, type: row.tagCount > 0 ? 'success' : 'default' }, {
      default: () => String(row.tagCount),
    }),
  },
  {
    title: 'Updated',
    key: 'updatedUtc',
    width: 158,
    render: (row) => h('span', { class: 'object-time-cell' }, formatDate(row.updatedUtc)),
  },
  {
    title: 'Version',
    key: 'versionId',
    minWidth: 150,
    ellipsis: { tooltip: true },
    render: (row) => h('code', row.versionId),
  },
]);

const versionColumns: DataTableColumns<ObjectInfoResponse> = [
  {
    title: 'Version',
    key: 'versionId',
    minWidth: 160,
    ellipsis: { tooltip: true },
    render: (row) => h('button', {
      type: 'button',
      class: 'object-key-button',
      onClick: () => loadVersionPreview(row.versionId),
    }, row.versionId),
  },
  { title: 'Size', key: 'sizeBytes', width: 88, render: (row) => h('code', formatBytes(row.sizeBytes)) },
  { title: 'Marker', key: 'isDeleteMarker', width: 74, render: (row) => row.isDeleteMarker ? 'yes' : 'no' },
  { title: 'Created', key: 'createdUtc', width: 146, render: (row) => formatDate(row.createdUtc) },
];

const partColumns: DataTableColumns<MultipartPartResponse> = [
  { title: 'Part', key: 'partNumber', width: 70 },
  { title: 'Size', key: 'sizeBytes', width: 100, render: (row) => h('code', formatBytes(row.sizeBytes)) },
  { title: 'ETag', key: 'eTag', minWidth: 160, ellipsis: { tooltip: true } },
];

const auditColumns: DataTableColumns<ObjectAuditEntryResponse> = [
  { title: 'Time', key: 'timestampUtc', width: 146, render: (row) => formatDate(row.timestampUtc) },
  { title: 'Action', key: 'action', minWidth: 150, ellipsis: { tooltip: true } },
  { title: 'Key', key: 'key', minWidth: 180, ellipsis: { tooltip: true }, render: (row) => row.key ?? '-' },
  { title: 'Details', key: 'details', minWidth: 180, ellipsis: { tooltip: true }, render: (row) => mapSummary(row.details) },
];

async function refreshAll(): Promise<void> {
  if (!props.targetDb) return;
  loading.value = true;
  errorMsg.value = '';
  try {
    await loadBucketList();
    if (activeBucket.value) {
      await Promise.all([
        loadObjects(true),
        loadGovernance(activeBucket.value),
        loadVersions(),
        loadAudit(),
      ]);
    } else {
      clearRows();
      clearBucketMetadata();
    }
  } catch (error) {
    errorMsg.value = errorToMessage(error, '加载对象桶工作台失败');
  } finally {
    loading.value = false;
  }
}

async function loadBucketList(): Promise<void> {
  const buckets = await listObjectBuckets(auth.api, props.targetDb);
  localBuckets.value = buckets;
  if (!props.bucket && buckets[0]) {
    emit('selectBucket', buckets[0].name);
  }
}

async function loadGovernance(bucket: string): Promise<void> {
  const [nextStats, lifecycle, retention, quota, policy] = await Promise.all([
    getBucketStats(auth.api, props.targetDb, bucket),
    getBucketLifecycle(auth.api, props.targetDb, bucket),
    getBucketRetention(auth.api, props.targetDb, bucket),
    getBucketQuota(auth.api, props.targetDb, bucket),
    getBucketPolicy(auth.api, props.targetDb, bucket),
  ]);
  stats.value = nextStats;
  lifecycleDraft.expireCurrentAfterDays = lifecycle.expireCurrentAfterDays ?? null;
  lifecycleDraft.expireNoncurrentAfterDays = lifecycle.expireNoncurrentAfterDays ?? null;
  lifecycleDraft.expireDeleteMarkerAfterDays = lifecycle.expireDeleteMarkerAfterDays ?? null;
  retentionDraft.retainCurrentForDays = retention.retainCurrentForDays ?? null;
  retentionDraft.retainNoncurrentForDays = retention.retainNoncurrentForDays ?? null;
  quotaDraft.maxSizeBytes = quota.maxSizeBytes ?? null;
  quotaDraft.maxObjectVersions = quota.maxObjectVersions ?? null;
  policyDraft.value = policy.policyJson ?? '';
}

async function loadObjects(reset: boolean): Promise<void> {
  const bucket = activeBucket.value;
  if (!props.targetDb || !bucket) return;
  loadingObjects.value = true;
  errorMsg.value = '';
  try {
    const response = await listObjects(auth.api, props.targetDb, bucket, {
      prefix: currentPrefix.value,
      maxKeys: listLimit.value,
      continuationToken: reset ? null : cursor.value,
    });
    const mapped = response.objects.map(mapObject);
    rows.value = reset ? mapped : mergeRows(rows.value, mapped);
    cursor.value = response.nextContinuationToken ?? null;
    hasMore.value = response.isTruncated;
    syncSelectedAfterRows();
    latestCommand.value = `GET /v1/db/${props.targetDb}/s3/${bucket}?list-type=2&prefix=${currentPrefix.value}`;
    latestResult.value = resultFromObjects(mapped, 0);
    ranOnce.value = true;
    recordHistory('success', 'Object list', 'browse', latestCommand.value, `${mapped.length} objects`, mapped.length, -1, 0);
  } catch (error) {
    const msg = errorToMessage(error, '加载对象列表失败');
    errorMsg.value = msg;
    latestResult.value = errorResult(msg);
    ranOnce.value = true;
  } finally {
    loadingObjects.value = false;
  }
}

async function loadMore(): Promise<void> {
  if (!hasMore.value || !cursor.value) return;
  await loadObjects(false);
}

async function loadVersions(versionKey?: string): Promise<void> {
  const bucket = activeBucket.value;
  const key = versionKey ?? selectedObject.value?.key ?? '';
  if (!props.targetDb || !bucket) {
    versions.value = [];
    return;
  }
  const response = await listObjectVersions(auth.api, props.targetDb, bucket, key || null);
  versions.value = response.versions;
}

async function loadAudit(): Promise<void> {
  const bucket = activeBucket.value;
  if (!props.targetDb || !bucket) {
    auditEntries.value = [];
    return;
  }
  loadingAudit.value = true;
  try {
    const response = await listBucketAudit(auth.api, props.targetDb, bucket, auditPrefix.value || currentPrefix.value, auditMaxEntries.value);
    auditEntries.value = response.entries;
  } catch (error) {
    errorMsg.value = errorToMessage(error, '加载对象桶审计失败');
  } finally {
    loadingAudit.value = false;
  }
}

function selectBucket(bucket: string): void {
  if (!bucket) return;
  emit('selectBucket', bucket);
}

function selectObject(key: string): void {
  selectedKey.value = key;
}

function applyPrefix(): void {
  currentPrefix.value = prefixInput.value.trim();
  cursor.value = null;
  hasMore.value = false;
  rows.value = [];
  void loadObjects(true);
  void loadAudit();
}

function openPrefix(prefix: string): void {
  prefixInput.value = prefix;
  currentPrefix.value = prefix;
  cursor.value = null;
  rows.value = [];
  void loadObjects(true);
  void loadAudit();
}

function openParentPrefix(): void {
  openPrefix(parentPrefix(currentPrefix.value, delimiter.value || '/'));
}

function clearRows(): void {
  rows.value = [];
  cursor.value = null;
  hasMore.value = false;
  selectedKey.value = '';
  checkedRowKeys.value = [];
  versions.value = [];
  previewText.value = '';
}

function clearBucketMetadata(): void {
  stats.value = null;
  versions.value = [];
  auditEntries.value = [];
  policyDraft.value = '';
  lifecycleDraft.expireCurrentAfterDays = null;
  lifecycleDraft.expireNoncurrentAfterDays = null;
  lifecycleDraft.expireDeleteMarkerAfterDays = null;
  retentionDraft.retainCurrentForDays = null;
  retentionDraft.retainNoncurrentForDays = null;
  quotaDraft.maxSizeBytes = null;
  quotaDraft.maxObjectVersions = null;
}

async function loadPreview(versionId?: string | null): Promise<void> {
  const row = selectedObject.value;
  if (!row) return;
  loadingPreview.value = true;
  errorMsg.value = '';
  try {
    const start = Math.max(0, rangeStart.value ?? 0);
    const length = Math.max(1, rangeLength.value ?? 4096);
    const response = await getObjectBlob(auth.api, props.targetDb, row.bucket, row.key, {
      versionId: versionId ?? null,
      range: { start, end: start + length - 1 },
    });
    const bytes = new Uint8Array(await response.blob.arrayBuffer());
    previewText.value = formatBytesForPreview(bytes, previewMode.value, response.head.contentType);
  } catch (error) {
    errorMsg.value = errorToMessage(error, '读取对象预览失败');
  } finally {
    loadingPreview.value = false;
  }
}

async function loadVersionPreview(versionId: string): Promise<void> {
  await loadPreview(versionId);
}

async function downloadSelectedObject(): Promise<void> {
  const row = selectedObject.value;
  if (!row) return;
  try {
    const response = await getObjectBlob(auth.api, props.targetDb, row.bucket, row.key);
    const url = URL.createObjectURL(response.blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileNameFromKey(row.key);
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
    message.success('Download started');
  } catch (error) {
    errorMsg.value = errorToMessage(error, '下载对象失败');
  }
}

function stageCreateBucket(): void {
  const bucket = newBucketName.value.trim();
  if (!bucket) return;
  const purpose = newBucketPurpose.value.trim();
  pendingOperations.value = [{
    id: makeOperationId('bucket_create'),
    label: 'Create bucket',
    detail: bucket,
    severity: 'write',
    command: `PUT /v1/db/${props.targetDb}/s3/${bucket}`,
    run: async () => {
      await createObjectBucket(auth.api, props.targetDb, bucket, purpose || null);
      newBucketName.value = '';
      newBucketPurpose.value = '';
      return { action: 'bucket.create', target: bucket, succeeded: true, affected: 1, detail: purpose || 'created' };
    },
  }];
}

function stageDeleteBucket(): void {
  const bucket = activeBucket.value;
  if (!bucket) return;
  pendingOperations.value = [{
    id: makeOperationId('bucket_delete'),
    label: 'Delete bucket',
    detail: bucket,
    severity: 'danger',
    command: `DELETE /v1/db/${props.targetDb}/s3/${bucket}`,
    run: async () => {
      await deleteObjectBucket(auth.api, props.targetDb, bucket);
      return { action: 'bucket.delete', target: bucket, succeeded: true, affected: 1, detail: 'deleted' };
    },
  }];
}

function stageUploadFile(): void {
  const bucket = activeBucket.value;
  const key = uploadKey.value.trim();
  const file = uploadFile.value;
  if (!bucket || !key || !file) return;
  const maps = parseUploadMaps();
  if (!maps.ok) {
    errorMsg.value = maps.message;
    return;
  }
  const contentType = uploadContentType.value.trim() || file.type || 'application/octet-stream';
  pendingOperations.value = [{
    id: makeOperationId('object_put_file'),
    label: 'Put object',
    detail: `${key} · ${formatBytes(file.size)}`,
    severity: 'write',
    command: `PUT /v1/db/${props.targetDb}/s3/${bucket}/${key}`,
    run: async () => {
      const response = await putObject(auth.api, props.targetDb, bucket, key, file, {
        contentType,
        metadata: maps.metadata,
        tags: maps.tags,
      });
      return { action: 'object.put', target: key, succeeded: true, affected: 1, detail: response.versionId };
    },
  }];
}

function stageUploadText(): void {
  const bucket = activeBucket.value;
  const key = uploadKey.value.trim();
  if (!bucket || !key) return;
  const maps = parseUploadMaps();
  if (!maps.ok) {
    errorMsg.value = maps.message;
    return;
  }
  const contentType = uploadContentType.value.trim() || 'text/plain;charset=utf-8';
  const blob = new Blob([uploadText.value], { type: contentType });
  pendingOperations.value = [{
    id: makeOperationId('object_put_text'),
    label: 'Put text object',
    detail: `${key} · ${formatBytes(blob.size)}`,
    severity: 'write',
    command: `PUT /v1/db/${props.targetDb}/s3/${bucket}/${key}`,
    run: async () => {
      const response = await putObject(auth.api, props.targetDb, bucket, key, blob, {
        contentType,
        metadata: maps.metadata,
        tags: maps.tags,
      });
      return { action: 'object.put', target: key, succeeded: true, affected: 1, detail: response.versionId };
    },
  }];
}

function stageSetTags(): void {
  const row = selectedObject.value;
  if (!row) return;
  const parsed = parseKeyValueMap(selectedTagsText.value);
  if (!parsed.ok) {
    errorMsg.value = parsed.message;
    return;
  }
  pendingOperations.value = [{
    id: makeOperationId('object_tags'),
    label: 'Set tags',
    detail: row.key,
    severity: 'write',
    command: `PUT /v1/db/${props.targetDb}/s3/${row.bucket}/${row.key}?tagging`,
    run: async () => {
      await setObjectTags(auth.api, props.targetDb, row.bucket, row.key, parsed.value);
      return { action: 'object.tags.set', target: row.key, succeeded: true, affected: 1, detail: `${Object.keys(parsed.value).length} tags` };
    },
  }];
}

function stageCopySelected(): void {
  const row = selectedObject.value;
  const targetKey = copyTargetKey.value.trim();
  if (!row || !targetKey) return;
  pendingOperations.value = [{
    id: makeOperationId('object_copy'),
    label: 'Copy object',
    detail: `${row.key} -> ${targetKey}`,
    severity: 'write',
    command: `COPY ${row.bucket}/${row.key} TO ${row.bucket}/${targetKey}`,
    run: async () => {
      const response = await copyObject(auth.api, props.targetDb, row.bucket, row.key, row.bucket, targetKey);
      return { action: 'object.copy', target: targetKey, succeeded: true, affected: 1, detail: response.versionId };
    },
  }];
}

function stageDeleteCurrent(): void {
  const row = selectedObject.value;
  if (!row) return;
  pendingOperations.value = [deleteOperation(row.key)];
}

function stageDeleteSelected(): void {
  const keys = checkedRowKeys.value.map(String).filter(Boolean);
  if (!activeBucket.value || keys.length === 0) return;
  pendingOperations.value = [{
    id: makeOperationId('object_delete_many'),
    label: 'Delete selected',
    detail: `${keys.length} objects`,
    severity: 'danger',
    command: `POST /v1/db/${props.targetDb}/s3/${activeBucket.value}?delete`,
    run: async () => {
      const response = await deleteManyObjects(auth.api, props.targetDb, activeBucket.value, keys);
      const affected = response.deleted.filter((item) => !item.errorCode).length;
      return { action: 'object.delete_many', target: activeBucket.value, succeeded: true, affected, detail: `${affected}/${keys.length} deleted` };
    },
  }];
}

function deleteOperation(key: string): PendingOperation {
  const bucket = activeBucket.value;
  return {
    id: makeOperationId('object_delete'),
    label: 'Delete object',
    detail: key,
    severity: 'danger',
    command: `DELETE /v1/db/${props.targetDb}/s3/${bucket}/${key}`,
    run: async () => {
      await deleteObject(auth.api, props.targetDb, bucket, key);
      return { action: 'object.delete', target: key, succeeded: true, affected: 1, detail: 'delete marker created' };
    },
  };
}

function stagePresign(): void {
  const row = selectedObject.value;
  if (!row) return;
  const minutes = Math.max(1, Math.min(1440, presignMinutes.value ?? 60));
  const method = presignMethod.value;
  pendingOperations.value = [{
    id: makeOperationId('object_presign'),
    label: 'Create presigned URL',
    detail: `${method} ${row.key} · ${minutes} min`,
    severity: 'write',
    command: `POST /v1/db/${props.targetDb}/s3/${row.bucket}/${row.key}?presign`,
    run: async () => {
      const response = await createPresignedObjectUrl(auth.api, props.targetDb, row.bucket, row.key, method, minutes);
      presignedUrl.value = response.url;
      await copyText(response.url, 'Presigned URL copied');
      return { action: 'object.presign.create', target: row.key, succeeded: true, affected: 1, detail: response.expiresUtc };
    },
  }];
}

function stageSetLifecycle(): void {
  const bucket = activeBucket.value;
  if (!bucket) return;
  pendingOperations.value = [{
    id: makeOperationId('bucket_lifecycle'),
    label: 'Set lifecycle',
    detail: bucket,
    severity: 'write',
    command: `PUT /v1/db/${props.targetDb}/s3/${bucket}?lifecycle`,
    run: async () => {
      await setBucketLifecycle(auth.api, props.targetDb, bucket, nullifyDraft(lifecycleDraft));
      return { action: 'bucket.lifecycle.set', target: bucket, succeeded: true, affected: 1, detail: 'saved' };
    },
  }];
}

function stageApplyLifecycle(): void {
  const bucket = activeBucket.value;
  if (!bucket) return;
  pendingOperations.value = [{
    id: makeOperationId('bucket_lifecycle_apply'),
    label: 'Apply lifecycle',
    detail: bucket,
    severity: 'danger',
    command: `POST /v1/db/${props.targetDb}/s3/${bucket}?lifecycle`,
    run: async () => {
      const response = await applyBucketLifecycle(auth.api, props.targetDb, bucket);
      const affected = response.expiredCurrentObjects + response.removedNoncurrentVersions + response.removedDeleteMarkers;
      return { action: 'bucket.lifecycle.apply', target: bucket, succeeded: true, affected, detail: `${affected} versions/markers affected` };
    },
  }];
}

function stageSetRetention(): void {
  const bucket = activeBucket.value;
  if (!bucket) return;
  pendingOperations.value = [{
    id: makeOperationId('bucket_retention'),
    label: 'Set retention',
    detail: bucket,
    severity: 'write',
    command: `PUT /v1/db/${props.targetDb}/s3/${bucket}?retention`,
    run: async () => {
      await setBucketRetention(auth.api, props.targetDb, bucket, nullifyDraft(retentionDraft));
      return { action: 'bucket.retention.set', target: bucket, succeeded: true, affected: 1, detail: 'saved' };
    },
  }];
}

function stageSetQuota(): void {
  const bucket = activeBucket.value;
  if (!bucket) return;
  pendingOperations.value = [{
    id: makeOperationId('bucket_quota'),
    label: 'Set quota',
    detail: bucket,
    severity: 'write',
    command: `PUT /v1/db/${props.targetDb}/s3/${bucket}?quota`,
    run: async () => {
      await setBucketQuota(auth.api, props.targetDb, bucket, nullifyDraft(quotaDraft));
      return { action: 'bucket.quota.set', target: bucket, succeeded: true, affected: 1, detail: 'saved' };
    },
  }];
}

function stageSetPolicy(): void {
  const bucket = activeBucket.value;
  if (!bucket) return;
  const policy = policyDraft.value.trim();
  if (policy) {
    try {
      JSON.parse(policy);
    } catch (error) {
      errorMsg.value = error instanceof Error ? error.message : 'Policy JSON is invalid.';
      return;
    }
  }
  pendingOperations.value = [{
    id: makeOperationId('bucket_policy'),
    label: 'Set policy',
    detail: bucket,
    severity: 'write',
    command: `PUT /v1/db/${props.targetDb}/s3/${bucket}?policy`,
    run: async () => {
      await setBucketPolicy(auth.api, props.targetDb, bucket, policy || null);
      return { action: 'bucket.policy.set', target: bucket, succeeded: true, affected: 1, detail: policy ? 'saved' : 'cleared' };
    },
  }];
}

function stageSetLegalHold(): void {
  const row = selectedObject.value;
  if (!row) return;
  const versionId = versions.value.find((version) => version.versionId === row.versionId)?.versionId ?? row.versionId;
  pendingOperations.value = [{
    id: makeOperationId('object_legal_hold'),
    label: legalHoldEnabled.value ? 'Enable legal hold' : 'Disable legal hold',
    detail: row.key,
    severity: legalHoldEnabled.value ? 'write' : 'danger',
    command: `PUT /v1/db/${props.targetDb}/s3/${row.bucket}/${row.key}?legal-hold&versionId=${versionId}`,
    run: async () => {
      await setObjectLegalHold(auth.api, props.targetDb, row.bucket, row.key, legalHoldEnabled.value, legalHoldReason.value || null, versionId);
      return { action: legalHoldEnabled.value ? 'object.legal_hold.enable' : 'object.legal_hold.disable', target: row.key, succeeded: true, affected: 1, detail: versionId };
    },
  }];
}

function stageInitiateMultipart(): void {
  const bucket = activeBucket.value;
  const key = multipartKey.value.trim();
  if (!bucket || !key) return;
  const maps = parseUploadMaps();
  if (!maps.ok) {
    errorMsg.value = maps.message;
    return;
  }
  pendingOperations.value = [{
    id: makeOperationId('multipart_init'),
    label: 'Initiate multipart',
    detail: key,
    severity: 'write',
    command: `POST /v1/db/${props.targetDb}/s3/${bucket}/${key}?uploads`,
    run: async () => {
      const response = await initiateMultipartUpload(auth.api, props.targetDb, bucket, key, {
        contentType: multipartContentType.value || 'application/octet-stream',
        metadata: maps.metadata,
        tags: maps.tags,
        expiresHours: multipartExpiresHours.value,
      });
      activeMultipart.value = response;
      multipartParts.value = [];
      return { action: 'multipart.initiate', target: key, succeeded: true, affected: 1, detail: response.uploadId };
    },
  }];
}

function stageUploadPart(): void {
  const upload = activeMultipart.value;
  const file = multipartFile.value;
  const partNumber = multipartPartNumber.value ?? 1;
  if (!upload || !file || partNumber <= 0) return;
  pendingOperations.value = [{
    id: makeOperationId('multipart_part'),
    label: 'Upload part',
    detail: `part ${partNumber} · ${formatBytes(file.size)}`,
    severity: 'write',
    command: `PUT /v1/db/${props.targetDb}/s3/${upload.bucket}/${upload.key}?uploadId=${upload.uploadId}&partNumber=${partNumber}`,
    run: async () => {
      const part = await uploadMultipartPart(auth.api, props.targetDb, upload.bucket, upload.key, upload.uploadId, partNumber, file);
      multipartParts.value = mergeParts(multipartParts.value, part);
      multipartPartNumber.value = partNumber + 1;
      multipartFile.value = null;
      return { action: 'multipart.part.put', target: upload.key, succeeded: true, affected: 1, detail: `part ${part.partNumber}` };
    },
  }];
}

function stageCompleteMultipart(): void {
  const upload = activeMultipart.value;
  if (!upload || multipartParts.value.length === 0) return;
  const partNumbers = multipartParts.value.map((part) => part.partNumber).sort((a, b) => a - b);
  pendingOperations.value = [{
    id: makeOperationId('multipart_complete'),
    label: 'Complete multipart',
    detail: `${partNumbers.length} parts`,
    severity: 'write',
    command: `POST /v1/db/${props.targetDb}/s3/${upload.bucket}/${upload.key}?uploadId=${upload.uploadId}`,
    run: async () => {
      const response = await completeMultipartUpload(auth.api, props.targetDb, upload.bucket, upload.key, upload.uploadId, partNumbers);
      activeMultipart.value = null;
      multipartParts.value = [];
      return { action: 'multipart.complete', target: response.key, succeeded: true, affected: 1, detail: response.versionId };
    },
  }];
}

function stageAbortMultipart(): void {
  const upload = activeMultipart.value;
  if (!upload) return;
  pendingOperations.value = [{
    id: makeOperationId('multipart_abort'),
    label: 'Abort multipart',
    detail: upload.uploadId,
    severity: 'danger',
    command: `DELETE /v1/db/${props.targetDb}/s3/${upload.bucket}/${upload.key}?uploadId=${upload.uploadId}`,
    run: async () => {
      await abortMultipartUpload(auth.api, props.targetDb, upload.bucket, upload.key, upload.uploadId);
      activeMultipart.value = null;
      multipartParts.value = [];
      return { action: 'multipart.abort', target: upload.key, succeeded: true, affected: 1, detail: 'aborted' };
    },
  }];
}

async function confirmPendingOperations(): Promise<void> {
  if (pendingOperations.value.length === 0) return;
  confirmBusy.value = true;
  errorMsg.value = '';
  const operations = [...pendingOperations.value];
  const command = operations.map((operation) => operation.command).join('\n');
  const started = performance.now();
  try {
    const outcomes: OperationOutcome[] = [];
    for (const operation of operations) {
      outcomes.push(await operation.run());
    }
    const elapsed = performanceElapsed(started);
    latestCommand.value = command;
    latestResult.value = resultFromOutcomes(outcomes, elapsed);
    ranOnce.value = true;
    pendingOperations.value = [];
    checkedRowKeys.value = [];
    recordHistory('success', 'Object operation batch', operations.map((operation) => operation.label).join(', '), command, `${outcomes.length} actions`, outcomes.length, outcomes.reduce((sum, item) => sum + item.affected, 0), elapsed);
    message.success(`Committed ${outcomes.length} object action${outcomes.length === 1 ? '' : 's'}.`);
    await refreshAll();
    emit('refreshSchema');
  } catch (error) {
    const elapsed = performanceElapsed(started);
    const msg = errorToMessage(error, '提交对象桶操作失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult(msg);
    ranOnce.value = true;
    recordHistory('error', 'Object operation batch', 'confirm', command, msg, 0, 0, elapsed);
  } finally {
    confirmBusy.value = false;
  }
}

function clearPendingOperations(): void {
  pendingOperations.value = [];
}

function openHistoryEntry(entry: WorkbenchHistoryEntry): void {
  latestCommand.value = entry.command;
}

function onUploadFileChange(event: Event): void {
  const input = event.target as HTMLInputElement;
  uploadFile.value = input.files?.[0] ?? null;
  if (uploadFile.value) {
    uploadKey.value ||= currentPrefix.value + uploadFile.value.name;
    uploadContentType.value = uploadFile.value.type || uploadContentType.value;
  }
}

function onMultipartFileChange(event: Event): void {
  const input = event.target as HTMLInputElement;
  multipartFile.value = input.files?.[0] ?? null;
}

function rowKey(row: ObjectRow): string {
  return row.key;
}

function mapObject(item: ObjectInfoResponse): ObjectRow {
  return {
    ...item,
    metadata: item.metadata ?? {},
    tags: item.tags ?? {},
    tagCount: Object.keys(item.tags ?? {}).length,
    metadataCount: Object.keys(item.metadata ?? {}).length,
  };
}

function mergeRows(existing: ObjectRow[], incoming: ObjectRow[]): ObjectRow[] {
  const map = new Map(existing.map((row) => [row.key, row]));
  for (const row of incoming) map.set(row.key, row);
  return [...map.values()].sort((a, b) => a.key.localeCompare(b.key));
}

function mergeParts(existing: MultipartPartResponse[], part: MultipartPartResponse): MultipartPartResponse[] {
  const map = new Map(existing.map((item) => [item.partNumber, item]));
  map.set(part.partNumber, part);
  return [...map.values()].sort((a, b) => a.partNumber - b.partNumber);
}

function syncSelectedAfterRows(): void {
  if (selectedKey.value && rows.value.some((row) => row.key === selectedKey.value)) return;
  selectedKey.value = rows.value[0]?.key ?? '';
}

function parseUploadMaps():
  | { ok: true; metadata: Record<string, string>; tags: Record<string, string> }
  | { ok: false; message: string } {
  const metadata = parseKeyValueMap(metadataText.value);
  if (!metadata.ok) return metadata;
  const tags = parseKeyValueMap(tagsText.value);
  if (!tags.ok) return tags;
  return { ok: true, metadata: metadata.value, tags: tags.value };
}

function parseKeyValueMap(text: string):
  | { ok: true; value: Record<string, string> }
  | { ok: false; message: string } {
  const trimmed = text.trim();
  if (!trimmed) return { ok: true, value: {} };
  if (trimmed.startsWith('{')) {
    try {
      const parsed = JSON.parse(trimmed) as Record<string, unknown>;
      const value: Record<string, string> = {};
      for (const [key, item] of Object.entries(parsed)) {
        value[key] = item == null ? '' : String(item);
      }
      return { ok: true, value };
    } catch (error) {
      return { ok: false, message: error instanceof Error ? error.message : 'Invalid JSON map.' };
    }
  }
  const value: Record<string, string> = {};
  for (const line of trimmed.split(/\r?\n/g).map((item) => item.trim()).filter(Boolean)) {
    const index = line.indexOf('=');
    if (index <= 0) return { ok: false, message: `Invalid key=value line: ${line}` };
    value[line.slice(0, index).trim()] = line.slice(index + 1).trim();
  }
  return { ok: true, value };
}

function formatMap(map: Record<string, string>): string {
  return Object.entries(map ?? {}).map(([key, value]) => `${key}=${value}`).join('\n');
}

function nullifyDraft<T extends Record<string, number | null | undefined>>(draft: T): T {
  return Object.fromEntries(
    Object.entries(draft).map(([key, value]) => [key, value ?? null]),
  ) as T;
}

function parentPrefix(prefix: string, sep: string): string {
  if (!prefix) return '';
  const trimmed = prefix.endsWith(sep) ? prefix.slice(0, -sep.length) : prefix;
  const index = trimmed.lastIndexOf(sep);
  return index >= 0 ? `${trimmed.slice(0, index)}${sep}` : '';
}

function resultFromObjects(objects: ObjectRow[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['key', 'contentType', 'sizeBytes', 'versionId', 'etag', 'sha256', 'tags', 'updatedUtc'],
    rows: objects.map((item) => [
      item.key,
      item.contentType,
      item.sizeBytes,
      item.versionId,
      item.eTag,
      item.sha256,
      item.tagCount,
      item.updatedUtc,
    ]),
    end: { type: 'end', rowCount: objects.length, recordsAffected: -1, elapsedMs },
    error: null,
    hasColumns: true,
  };
}

function resultFromOutcomes(outcomes: OperationOutcome[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['action', 'target', 'succeeded', 'affected', 'detail'],
    rows: outcomes.map((item) => [item.action, item.target, item.succeeded, item.affected, item.detail]),
    end: {
      type: 'end',
      rowCount: outcomes.length,
      recordsAffected: outcomes.reduce((sum, item) => sum + item.affected, 0),
      elapsedMs,
    },
    error: null,
    hasColumns: true,
  };
}

function errorResult(messageText: string): SqlResultSet {
  return {
    columns: [],
    rows: [],
    end: null,
    error: { type: 'error', code: 'object_error', message: messageText },
    hasColumns: false,
  };
}

function formatBytesForPreview(bytes: Uint8Array, mode: PreviewMode, contentType: string): string {
  if (mode === 'base64') return bytesToBase64(bytes);
  if (mode === 'hex') return toHex(bytes);
  try {
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    if (contentType.includes('json')) {
      try {
        return JSON.stringify(JSON.parse(text), null, 2);
      } catch {
        return text;
      }
    }
    return text;
  } catch {
    return `Binary payload (${bytes.length} bytes). Use Hex or Base64 view.`;
  }
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = '';
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunkSize));
  }
  return btoa(binary);
}

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(' ');
}

function mapSummary(map: Record<string, string>): string {
  const entries = Object.entries(map ?? {});
  if (entries.length === 0) return '-';
  return entries.slice(0, 4).map(([key, value]) => `${key}=${value}`).join(', ');
}

function fileNameFromKey(key: string): string {
  const parts = key.split('/').filter(Boolean);
  return parts[parts.length - 1] || 'object.bin';
}

function formatDate(value?: string | null): string {
  if (!value) return '-';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) || date.getTime() <= 0 ? '-' : date.toLocaleString();
}

function formatStat(value?: number | null): string {
  return typeof value === 'number' && Number.isFinite(value) ? value.toLocaleString() : '-';
}

function formatBytes(value?: number | null): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '-';
  if (value >= 1024 ** 3) return `${(value / 1024 ** 3).toFixed(2)} GiB`;
  if (value >= 1024 ** 2) return `${(value / 1024 ** 2).toFixed(1)} MiB`;
  if (value >= 1024) return `${(value / 1024).toFixed(1)} KiB`;
  return `${value.toFixed(0)} B`;
}

function makeOperationId(prefix: string): string {
  return `${prefix}_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function performanceElapsed(started: number): number {
  return started > 0 ? performance.now() - started : 0;
}

function errorToMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const response = (error as { response?: { data?: unknown; status?: number } }).response;
    if (response?.data instanceof Blob) {
      return fallback;
    }
    if (response?.data && typeof response.data === 'object') {
      const data = response.data as Record<string, unknown>;
      if (typeof data.message === 'string') return data.message;
      if (typeof data.error === 'string') return data.error;
    }
    if (typeof (error as { message?: unknown }).message === 'string') {
      return (error as { message: string }).message;
    }
  }
  return fallback;
}

async function copyText(text: string, success: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    message.success(success);
  } catch {
    message.warning(text);
  }
}

function recordHistory(
  status: 'success' | 'error',
  title: string,
  action: string,
  command: string,
  summary: string,
  rowCount: number,
  recordsAffected: number,
  elapsedMs: number,
): void {
  history.record({
    kind: action === 'browse' ? 'query' : 'operation',
    status,
    title,
    target: activeBucket.value,
    database: props.targetDb,
    connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name,
    model: 'object',
    action,
    command,
    summary,
    rowCount,
    recordsAffected,
    elapsedMs,
  });
}

watch(
  () => props.buckets,
  (buckets) => {
    if (buckets.length > 0 && localBuckets.value.length === 0) {
      localBuckets.value = buckets.map((bucket) => ({
        name: bucket.name,
        purpose: bucket.purpose,
        createdUtc: bucket.createdUtc,
        updatedUtc: bucket.updatedUtc,
      }));
    }
  },
  { immediate: true },
);

watch(
  () => [props.targetDb, props.bucket] as const,
  () => {
    clearRows();
    currentPrefix.value = '';
    prefixInput.value = '';
    pendingOperations.value = [];
    previewText.value = '';
    presignedUrl.value = '';
    if (props.targetDb) {
      void refreshAll();
    }
  },
);

watch(selectedObject, async (row) => {
  selectedTagsText.value = row ? formatMap(row.tags) : '';
  copyTargetKey.value = row ? `${row.key}.copy` : '';
  uploadKey.value = row?.key ?? currentPrefix.value;
  multipartKey.value = row?.key ?? currentPrefix.value;
  previewText.value = '';
  presignedUrl.value = '';
  versions.value = [];
  legalHoldEnabled.value = false;
  legalHoldReason.value = '';
  if (row) {
    try {
      const [tags, hold] = await Promise.all([
        getObjectTags(auth.api, props.targetDb, row.bucket, row.key),
        getObjectLegalHold(auth.api, props.targetDb, row.bucket, row.key, row.versionId),
      ]);
      selectedTagsText.value = formatMap(tags);
      legalHoldEnabled.value = hold.enabled;
      legalHoldReason.value = hold.reason ?? '';
    } catch {
      // 对象可能在列表后被删除，下一次 refresh 会同步状态。
    }
    await loadVersions(row.key);
  }
});

onMounted(() => {
  void refreshAll();
});
</script>

<style scoped>
.object-workbench {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.object-toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #f7fbfb;
}

.object-toolbar__identity {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.object-toolbar__title {
  color: var(--sndb-ink-strong);
  font-size: 15px;
  font-weight: 800;
}

.object-toolbar__meta,
.object-panel-head__meta {
  font-size: 12px;
}

.object-toolbar__actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.object-toolbar__bucket {
  width: 170px;
}

.object-toolbar__prefix {
  width: 220px;
}

.object-toolbar__delimiter {
  width: 58px;
}

.object-toolbar__limit {
  width: 118px;
}

.object-alert {
  margin: 10px 12px 0;
}

.object-stats {
  display: grid;
  grid-template-columns: repeat(5, minmax(120px, 1fr));
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.object-stat {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
  padding: 9px 12px;
  border-right: 1px solid rgba(15, 23, 42, 0.08);
}

.object-stat span {
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.object-stat strong {
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 16px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.object-body {
  display: grid;
  flex: 1;
  min-height: 420px;
  grid-template-columns: 240px minmax(420px, 1fr) 390px;
  min-width: 0;
  overflow: hidden;
}

.object-body.is-focused {
  grid-template-columns: minmax(520px, 820px);
  justify-content: center;
  padding: 20px;
  overflow: auto;
  background: var(--sndb-surface);
}

.object-body.is-focused .object-inspector {
  border: 1px solid var(--sndb-border);
  border-radius: var(--sndb-radius);
  background: #fff;
}

.object-nav,
.object-grid-panel,
.object-inspector {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.object-nav {
  border-right: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfcfe;
}

.object-grid-panel {
  background: #fff;
}

.object-inspector {
  border-left: 1px solid rgba(15, 23, 42, 0.08);
  background: #fcfdf8;
}

.object-panel-head {
  display: flex;
  flex: 0 0 auto;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.object-panel-head--compact {
  padding: 9px 12px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

.object-panel-head--grid {
  align-items: center;
}

.object-panel-head__title,
.object-section-title {
  display: block;
  color: var(--sndb-ink-strong);
  font-weight: 800;
}

.object-section-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin: 8px 0 6px;
}

.object-section-title--standalone {
  margin: 0;
}

.object-create {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 9px 10px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.object-bucket-list,
.object-folder-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-height: 0;
  overflow: auto;
  padding: 8px;
}

.object-bucket-list {
  flex: 0 0 170px;
}

.object-folder-list {
  flex: 1;
}

.object-bucket-card,
.object-folder,
.object-path button,
.object-key-button {
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.object-bucket-card,
.object-folder {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  width: 100%;
  min-width: 0;
  padding: 8px;
  border-left: 2px solid rgba(13, 59, 102, 0.35);
  border-radius: 6px;
  text-align: left;
}

.object-bucket-card:hover,
.object-bucket-card.is-active,
.object-folder:hover,
.object-path button:hover,
.object-key-button:hover,
.object-key-button.is-active {
  background: rgba(13, 59, 102, 0.08);
}

.object-bucket-card.is-active {
  border-left-color: rgba(13, 59, 102, 0.9);
}

.object-bucket-card span,
.object-folder span {
  width: 100%;
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.object-bucket-card small,
.object-folder small {
  color: var(--sndb-ink-soft);
  font-size: 11px;
}

.object-path {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  align-items: center;
  padding: 8px 10px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.object-path button {
  padding: 2px 4px;
  border-radius: 4px;
  color: var(--sndb-brand);
  font-weight: 700;
}

.object-grid-tools {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.object-grid-tools__filter {
  width: 210px;
}

.object-grid {
  flex: 1;
  min-height: 0;
}

.object-grid :deep(.n-data-table-base-table-body) {
  min-height: 260px;
}

.object-key-button {
  max-width: 100%;
  overflow: hidden;
  padding: 2px 5px;
  border-radius: 4px;
  color: var(--sndb-ink-strong);
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  font-weight: 700;
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.object-time-cell {
  color: #31465d;
  font-size: 12px;
}

.object-pager {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.object-tabs {
  flex: 0 0 auto;
  padding: 8px 12px 0;
}

.object-inspector-section {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 10px;
  min-height: 0;
  overflow: auto;
  padding: 10px 12px 12px;
}

.object-detail-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.object-detail-strip span {
  padding: 2px 7px;
  border-radius: 999px;
  background: rgba(13, 59, 102, 0.07);
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
}

.object-preview-controls,
.object-presign,
.object-form-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: 8px;
  align-items: center;
}

.object-preview-controls {
  grid-template-columns: 80px 86px minmax(90px, 1fr) auto;
}

.object-presign {
  grid-template-columns: 100px 90px auto;
}

.object-form-row--three {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.object-form-row--hold {
  grid-template-columns: auto minmax(0, 1fr) auto;
}

.object-form-row--audit {
  grid-template-columns: minmax(0, 1fr) 86px auto;
}

.object-preview {
  min-height: 150px;
  max-height: 260px;
  margin: 0;
  overflow: auto;
  padding: 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
  color: #24384f;
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.object-version-grid,
.object-audit-grid {
  min-height: 0;
}

.object-governance-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
}

.object-governance-grid span {
  min-width: 0;
  padding: 7px 8px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
}

.object-governance-grid small {
  display: block;
  color: var(--sndb-ink-soft);
  font-size: 10px;
  font-weight: 800;
  text-transform: uppercase;
}

.object-governance-grid strong {
  display: block;
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 14px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.object-form-block {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 9px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
}

.object-file-input {
  width: 100%;
  min-width: 0;
  padding: 6px 8px;
  border: 1px solid rgba(15, 23, 42, 0.12);
  border-radius: 4px;
  background: #fff;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.object-multipart-card {
  display: flex;
  flex-direction: column;
  gap: 3px;
  padding: 9px;
  border-radius: 6px;
  background: rgba(13, 59, 102, 0.07);
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.object-multipart-card strong,
.object-multipart-card span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.object-multipart-card strong {
  color: var(--sndb-ink-strong);
}

.object-multipart-card.is-empty {
  align-items: center;
  justify-content: center;
  min-height: 70px;
  background: rgba(13, 59, 102, 0.04);
}

.object-result {
  flex: 0 0 240px;
  min-height: 220px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

@media (max-width: 1420px) {
  .object-body {
    grid-template-columns: 230px minmax(420px, 1fr);
  }

  .object-inspector {
    grid-column: 1 / -1;
    border-top: 1px solid rgba(15, 23, 42, 0.08);
    border-left: 0;
  }
}

@media (max-width: 980px) {
  .object-toolbar,
  .object-panel-head--grid,
  .object-pager {
    flex-direction: column;
    align-items: stretch;
  }

  .object-body {
    grid-template-columns: 1fr;
    overflow: visible;
  }

  .object-nav,
  .object-inspector {
    border-right: 0;
    border-left: 0;
  }

  .object-stats {
    grid-template-columns: repeat(2, minmax(120px, 1fr));
  }

  .object-toolbar__bucket,
  .object-toolbar__prefix,
  .object-toolbar__limit,
  .object-grid-tools__filter {
    width: 100%;
  }

  .object-preview-controls,
  .object-presign,
  .object-form-row,
  .object-form-row--three,
  .object-form-row--hold,
  .object-form-row--audit {
    grid-template-columns: 1fr;
  }
}
</style>
