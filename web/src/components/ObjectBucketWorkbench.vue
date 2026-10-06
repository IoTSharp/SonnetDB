<template>
  <main class="object-workbench" data-testid="workbench-bucket" :data-page-state="objectState" :data-database="targetDb" :data-resource-key="`bucket:${activeBucket}`">
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

    <n-alert v-if="permissionDenied" type="warning" data-testid="object-permission">当前身份没有 Object 访问权限。</n-alert>
    <n-alert v-else-if="readOnly" type="info" data-testid="object-readonly">只读模式：可浏览、读取和下载，写入操作不可用。</n-alert>
    <p data-testid="object-preview-budget">对象列表最多保留 1000 条预览；{{ listIncomplete ? '当前预览不完整。' : '仅显示当前已加载窗口。' }}</p>
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

    <section v-if="!permissionDenied" class="object-stats">
      <article v-for="item in statItems" :key="item.label" class="object-stat">
        <span>{{ item.label }}</span>
        <strong>{{ item.value }}</strong>
      </article>
    </section>

    <section v-if="!permissionDenied" class="object-body" :class="{ 'is-focused': inspectorTab !== 'preview' }">
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
            <n-button size="small" type="primary" :disabled="!canWrite || (!newBucketName.trim())" @click="stageCreateBucket">
              Stage create
            </n-button>
            <n-button size="small" tertiary type="error" :disabled="!canWrite || (!activeBucket)" @click="stageDeleteBucket">
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
            <n-button size="small" tertiary type="error" :disabled="!canWrite || (checkedRowKeys.length === 0)" @click="stageDeleteSelected">
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
              <n-input-number v-model:value="rangeLength" size="small" :min="1" :max="4096" :show-button="false" placeholder="Bytes" />
              <n-select v-model:value="previewMode" size="small" :options="previewModeOptions" />
              <n-button size="small" secondary :loading="loadingPreview" @click="() => loadPreview()">Range read</n-button>
            </div>
            <pre class="object-preview">{{ previewText || 'Load a byte range to preview this object.' }}</pre>
            <p data-testid="object-range-budget">Range 预览最多 4096 字节；{{ rangeNotice || '不代表完整对象或传输预算。' }}</p>
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
              <n-button :disabled="!canWrite" size="small" secondary @click="stagePresign">Stage URL</n-button>
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
              <n-button size="small" secondary :disabled="!canWrite || (!activeBucket)" @click="stageSetLifecycle">Stage save</n-button>
              <n-button size="small" tertiary type="error" :disabled="!canWrite || (!activeBucket)" @click="stageApplyLifecycle">Stage apply</n-button>
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
              <n-button size="small" secondary :disabled="!canWrite || (!activeBucket)" @click="stageSetRetention">Stage retention</n-button>
              <n-button size="small" secondary :disabled="!canWrite || (!activeBucket)" @click="stageSetQuota">Stage quota</n-button>
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
            <n-button size="small" secondary :disabled="!canWrite || (!activeBucket)" @click="stageSetPolicy">Stage policy</n-button>
          </div>

          <div class="object-form-block">
            <n-text class="object-section-title object-section-title--standalone">Legal hold</n-text>
            <div class="object-form-row object-form-row--hold">
              <n-switch v-model:value="legalHoldEnabled" :disabled="!selectedObject">
                <template #checked>On</template>
                <template #unchecked>Off</template>
              </n-switch>
              <n-input v-model:value="legalHoldReason" size="small" placeholder="Reason" :disabled="!selectedObject" />
              <n-button size="small" secondary :disabled="!canWrite || (!selectedObject)" @click="stageSetLegalHold">
                Stage hold
              </n-button>
            </div>
          </div>
        </section>

        <section v-else-if="inspectorTab === 'semantic'" class="object-inspector-section object-semantic-section">
          <div class="object-form-block">
            <div class="object-section-title object-section-title--standalone">
              <span>异步摄取与缩略图</span>
              <n-tag
                size="small"
                :type="semanticRuntime?.ready ? 'success' : 'warning'"
                :bordered="false"
              >
                {{ semanticRuntime?.effectiveBackend ?? 'unavailable' }}
              </n-tag>
            </div>
            <div class="object-semantic-switches">
              <label>
                <n-switch v-model:value="semanticOptionsDraft.asyncIngestionEnabled" size="small" />
                <span>异步语义摄取</span>
              </label>
              <label>
                <n-switch v-model:value="semanticOptionsDraft.thumbnailEnabled" size="small" />
                <span>生成 WebP 缩略图</span>
              </label>
            </div>
            <div class="object-form-row object-form-row--three">
              <n-input-number v-model:value="semanticOptionsDraft.thumbnailMaxWidth" size="small" :min="16" :max="4096" placeholder="最大宽度" />
              <n-input-number v-model:value="semanticOptionsDraft.thumbnailMaxHeight" size="small" :min="16" :max="4096" placeholder="最大高度" />
              <n-input-number v-model:value="semanticOptionsDraft.thumbnailQuality" size="small" :min="1" :max="100" placeholder="WebP 质量" />
            </div>
            <n-space size="small" align="center" :wrap="true">
              <n-button size="small" secondary :disabled="!canWrite || (!activeBucket)" @click="stageSetSemanticOptions">
                <template #icon><Save :size="15" /></template>
                暂存配置
              </n-button>
              <n-button size="small" secondary :disabled="!canWrite || (!activeBucket)" @click="stageSemanticBackfill">
                <template #icon><Layers3 :size="15" /></template>
                补录当前对象
              </n-button>
              <n-text depth="3" class="object-semantic-runtime">
                {{ semanticRuntime?.provider ?? 'provider unknown' }} · {{ semanticRuntime?.profile ?? '-' }}
              </n-text>
            </n-space>
          </div>

          <div class="object-form-block">
            <div class="object-section-title object-section-title--standalone">
              <span>当前对象处理状态</span>
              <n-tag v-if="processingStatus" size="small" :type="processingTagType" :bordered="false">
                {{ processingStatus.status }}
              </n-tag>
            </div>
            <div v-if="selectedObject" class="object-semantic-current">
              <div class="object-thumbnail-frame">
                <img v-if="selectedThumbnailUrl" :src="selectedThumbnailUrl" :alt="selectedObject.key">
                <ImageIcon v-else :size="24" aria-hidden="true" />
              </div>
              <div class="object-semantic-current__meta">
                <strong>{{ selectedObject.key }}</strong>
                <span>{{ processingStatus?.operation ?? 'not queued' }} · attempts {{ processingStatus?.attempts ?? 0 }}</span>
                <span class="object-semantic-id">{{ processingStatus?.semanticImageId ?? 'No semantic image ID' }}</span>
              </div>
              <n-space size="small" align="center" :wrap="true" class="object-semantic-current__actions">
                <n-button size="small" quaternary :loading="loadingProcessing" @click="loadSelectedProcessing">
                  <template #icon><RefreshCw :size="15" /></template>
                  刷新
                </n-button>
                <n-button size="small" secondary :disabled="!canWrite || (!selectedObject)" @click="stageRequeueSelectedObject">
                  <template #icon><RotateCcw :size="15" /></template>
                  重新入队
                </n-button>
                <n-button
                  size="small"
                  secondary
                  :disabled="!processingStatus?.semanticImageId"
                  @click="searchSimilarToSelected"
                >
                  <template #icon><ScanSearch :size="15" /></template>
                  查找相似项
                </n-button>
              </n-space>
            </div>
            <n-empty v-else description="在对象浏览中选择一张图片。" />
          </div>

          <div class="object-semantic-search">
            <div class="object-semantic-search__head">
              <n-radio-group v-model:value="semanticSearchMode" size="small">
                <n-radio-button value="text">文搜图</n-radio-button>
                <n-radio-button value="image">图搜图</n-radio-button>
                <n-radio-button value="similar">按 ID 相似</n-radio-button>
              </n-radio-group>
              <n-switch v-model:value="semanticExplain" size="small">
                <template #checked>Explain</template>
                <template #unchecked>Explain</template>
              </n-switch>
            </div>

            <n-input
              v-if="semanticSearchMode === 'text'"
              v-model:value="semanticText"
              size="small"
              clearable
              placeholder="例如：夜间车道上的红色重型卡车"
              @keydown.enter="runSemanticSearch"
            />
            <template v-else-if="semanticSearchMode === 'image'">
              <input ref="semanticFileInput" type="file" accept="image/*" class="object-file-input" @change="onSemanticFileChange">
              <n-button size="small" secondary @click="semanticFileInput?.click()">
                <template #icon><ImageUp :size="15" /></template>
                {{ semanticImageFile?.name ?? '选择查询图片' }}
              </n-button>
            </template>
            <n-input
              v-else
              v-model:value="similarImageId"
              size="small"
              clearable
              placeholder="已摄取图片 ID"
              @keydown.enter="runSemanticSearch"
            />

            <div class="object-semantic-query-options">
              <n-input-number v-model:value="semanticTopK" size="small" :min="1" :max="100" placeholder="Top K" />
              <n-input-number v-model:value="semanticMinScore" size="small" :min="-1" :max="1" :step="0.05" clearable placeholder="最低分数" />
              <n-input v-model:value="semanticFilterBucket" size="small" clearable placeholder="Bucket" />
              <n-input v-model:value="semanticFilterPrefix" size="small" clearable placeholder="Key prefix" />
              <n-input v-model:value="semanticFilterContentType" size="small" clearable placeholder="Content-Type" />
            </div>
            <div class="object-form-row">
              <n-input v-model:value="semanticMetadataText" type="textarea" :autosize="{ minRows: 2, maxRows: 5 }" placeholder="Metadata key=value" />
              <n-input v-model:value="semanticTagsText" type="textarea" :autosize="{ minRows: 2, maxRows: 5 }" placeholder="Tags key=value" />
            </div>
            <n-button
              size="small"
              type="primary"
              :loading="searchingSemantic"
              :disabled="!canRunSemanticSearch"
              @click="runSemanticSearch"
            >
              <template #icon><Search :size="15" /></template>
              执行检索
            </n-button>
          </div>

          <div v-if="semanticSearchResult" class="object-semantic-results">
            <div class="object-semantic-result-summary">
              <span>{{ semanticSearchResult.hits.length }} hits</span>
              <span>{{ semanticSearchResult.backend }}</span>
              <span v-if="semanticSearchResult.searchMode">{{ semanticSearchResult.searchMode }}</span>
              <span v-if="semanticSearchResult.candidateCount != null">
                {{ semanticSearchResult.filteredCandidateCount ?? 0 }} / {{ semanticSearchResult.candidateCount }} candidates
              </span>
            </div>
            <button
              v-for="hit in semanticSearchResult.hits"
              :key="hit.id"
              type="button"
              class="object-semantic-hit"
              @click="selectSemanticHit(hit)"
            >
              <span class="object-semantic-hit__preview">
                <img v-if="semanticHitUrls[hit.id]" :src="semanticHitUrls[hit.id]" :alt="hit.sourceKey || hit.fileName || hit.id">
                <ImageIcon v-else :size="20" aria-hidden="true" />
              </span>
              <span class="object-semantic-hit__body">
                <strong>{{ hit.sourceKey || hit.fileName || hit.id }}</strong>
                <small>{{ hit.sourceBucket || 'direct ingest' }} · {{ hit.contentType }} · {{ formatBytes(hit.sizeBytes) }}</small>
              </span>
              <span class="object-semantic-hit__score">{{ hit.score.toFixed(4) }}</span>
              <ChevronRight :size="16" aria-hidden="true" />
            </button>
            <n-empty v-if="semanticSearchResult.hits.length === 0" description="没有符合条件的图片。" />
          </div>
        </section>

        <section v-else-if="inspectorTab === 'upload'" class="object-inspector-section">
          <div class="object-form-block">
            <n-text class="object-section-title object-section-title--standalone">Put object</n-text>
            <n-input v-model:value="uploadKey" size="small" placeholder="Object key" />
            <n-input v-model:value="uploadContentType" size="small" placeholder="Content-Type" />
            <input ref="uploadFileInput" type="file" :disabled="!canWrite" class="object-file-input" @change="onUploadFileChange">
            <n-button :disabled="!canWrite" size="small" secondary @click="pickUploadFile">
              {{ uploadFile ? uploadFile.name : '选择上传文件' }}
            </n-button>
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
              <n-button size="small" type="primary" :disabled="!canWrite || (!activeBucket || !uploadKey.trim() || !uploadFile)" @click="stageUploadFile">
                Stage file upload
              </n-button>
              <n-button size="small" secondary :disabled="!canWrite || (!activeBucket || !uploadKey.trim() || !uploadText)" @click="stageUploadText">
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
              <n-button size="small" secondary :disabled="!canWrite || (!selectedObject)" @click="stageSetTags">Stage tags</n-button>
              <n-button size="small" secondary :disabled="!canWrite || (!selectedObject || !copyTargetKey.trim())" @click="stageCopySelected">Stage copy</n-button>
              <n-button size="small" tertiary type="error" :disabled="!canWrite || (!selectedObject)" @click="stageDeleteCurrent">
                Stage delete
              </n-button>
            </n-space>
          </div>
        </section>

        <section v-else-if="inspectorTab === 'multipart'" class="object-inspector-section">
          <div class="object-form-block">
            <div class="object-form-row">
              <n-select
                :value="activeMultipart?.uploadId ?? null"
                :options="multipartSessionOptions"
                clearable
                filterable
                placeholder="选择服务器上的 Multipart 会话"
                @update:value="resumeMultipartSession"
              />
              <n-button size="small" secondary :loading="loadingMultipartSessions" @click="loadMultipartSessions(true)">刷新会话</n-button>
            </div>
            <n-button v-if="multipartSessionsHasMore" size="tiny" quaternary @click="loadMultipartSessions(false)">加载更多会话</n-button>
          </div>
          <div class="object-form-block">
            <n-text class="object-section-title object-section-title--standalone">Current multipart session</n-text>
            <n-input v-model:value="multipartKey" size="small" placeholder="Object key" />
            <n-input v-model:value="multipartContentType" size="small" placeholder="Content-Type" />
            <div class="object-form-row">
              <n-input-number v-model:value="multipartExpiresHours" size="small" :min="1" :show-button="false" placeholder="Expires hours" />
              <n-button size="small" type="primary" :disabled="!canWrite || (!activeBucket || !multipartKey.trim())" @click="stageInitiateMultipart">
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
            <span v-else>没有活动会话。可从服务器会话列表恢复或新建。</span>
          </div>

          <div class="object-form-block">
            <div class="object-form-row">
              <n-input-number v-model:value="multipartPartNumber" size="small" :min="1" :show-button="false" placeholder="Part number" :disabled="!canWrite || activeMultipartStatus !== 'active'" />
              <input ref="multipartFileInput" type="file" class="object-file-input" :disabled="!canWrite || activeMultipartStatus !== 'active'" @change="onMultipartFileChange">
              <n-button size="small" secondary :disabled="!canWrite || (activeMultipartStatus !== 'active')" @click="pickMultipartFile">
                {{ multipartFile ? multipartFile.name : '选择分片文件' }}
              </n-button>
            </div>
            <n-space size="small" align="center" :wrap="true">
              <n-button size="small" secondary :disabled="!canWrite || (activeMultipartStatus !== 'active' || !multipartFile)" @click="stageUploadPart">
                Stage upload part
              </n-button>
              <n-button size="small" secondary :disabled="!canWrite || (activeMultipartStatus !== 'active' || multipartParts.length === 0)" @click="stageCompleteMultipart">
                Stage complete
              </n-button>
              <n-button size="small" tertiary type="error" :disabled="!canWrite || (!activeMultipart)" @click="stageAbortMultipart">
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
import { computed, h, markRaw, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue';
import { createApiClient } from '@/api/client';
import type { AxiosInstance } from 'axios';
import {
  NAlert,
  NButton,
  NDataTable,
  NEmpty,
  NInput,
  NInputNumber,
  NRadioButton,
  NRadioGroup,
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
import {
  ChevronRight,
  Image as ImageIcon,
  ImageUp,
  Layers3,
  RefreshCw,
  RotateCcw,
  Save,
  ScanSearch,
  Search,
} from 'lucide-vue-next';
import type { ObjectBucketInfo } from '@/api/management';
import {
  abortMultipartUpload,
  applyBucketLifecycle,
  backfillBucketSemanticObjects,
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
  getBucketSemanticOptions,
  getObjectBlob,
  getObjectLegalHold,
  getObjectProcessingStatus,
  getObjectThumbnailBlob,
  getObjectTags,
  initiateMultipartUpload,
  listMultipartUploads,
  listBucketAudit,
  listObjectBuckets,
  listObjects,
  listObjectVersions,
  putObject,
  enqueueObjectProcessing,
  setBucketLifecycle,
  setBucketPolicy,
  setBucketQuota,
  setBucketRetention,
  setBucketSemanticOptions,
  setObjectLegalHold,
  setObjectTags,
  uploadMultipartPart,
  type MultipartPartResponse,
  type MultipartUploadCreateResponse,
  type MultipartUploadSessionResponse,
  type ObjectAuditEntryResponse,
  type ObjectBucketResponse,
  type ObjectInfoResponse,
  type ObjectProcessingStatusResponse,
  type ObjectLifecycleResponse,
  type ObjectQuotaResponse,
  type ObjectRetentionResponse,
  type ObjectStatsResponse,
} from '@/api/objectStorage';
import {
  getProtectedImageBlob,
  getSemanticSearchStatus,
  searchImagesByImage,
  searchImagesByText,
  searchSimilarImages,
  type ImageSearchFilter,
  type ImageSearchHit,
  type ImageSearchResponse,
  type SemanticSearchStatusResponse,
} from '@/api/semanticSearch';
import { currentStudioNativeBridge } from '@/api/studioNativeBridge';
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
  readOnly?: boolean;
  permissionDenied?: boolean;
}>(), {
  buckets: () => [],
  loading: false,
  readOnly: false,
  permissionDenied: false,
});

const emit = defineEmits<{
  selectBucket: [bucket: string];
  refreshSchema: [];
}>();

type PreviewMode = 'text' | 'hex' | 'base64';
type InspectorTab = 'preview' | 'governance' | 'semantic' | 'upload' | 'multipart' | 'audit';
type SemanticSearchMode = 'text' | 'image' | 'similar';

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
  expectedTarget: string;
  label: string;
  detail: string;
  severity: WriteApprovalSeverity;
  command: string;
  run: () => Promise<OperationOutcome>;
}

interface ObjectContext {
  revision: number; database: string; bucket: string; api: AxiosInstance; sourceApi: AxiosInstance;
  baseUrl: string | undefined; authorization: unknown; token: string | null | undefined;
  connectionId: string; connectionName: string; profileBaseUrl: string;
}
interface ReadTicket { context: ObjectContext; controller: AbortController; slot: string; selected: boolean; selection: string; }

interface OperationOutcome {
  action: string;
  target: string;
  succeeded: boolean;
  affected: number;
  detail: string;
}

class UnknownObjectOperationError extends Error {
  public readonly code = 'OBJECT_UNKNOWN_TERMINAL';
}

class FailedObjectOperationError extends Error {
  public readonly code = 'OBJECT_FAILED_TERMINAL';
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();
let disposed = false;
let contextRevision = 0;
let deniedContext: ObjectContext | null = null;
const readTickets = new Map<string, ReadTicket>();
const permissionLocked = ref(false);
const permissionDenied = computed(() => Boolean(props.permissionDenied || permissionLocked.value));
const readOnly = computed(() => Boolean(props.readOnly));
const canRead = computed(() => !disposed && !permissionDenied.value && Boolean(props.targetDb));
const canWrite = computed(() => canRead.value && !readOnly.value && !confirmBusy.value);
let approvalContext: ObjectContext | null = null;
let approvalInputs = '';
let uploadPickerContext: ObjectContext | null = null;
let multipartPickerContext: ObjectContext | null = null;
const listIncomplete = ref(false);
const rangeNotice = ref('');
const usedListTokens = new Set<string>();

const localBuckets = ref<ObjectBucketResponse[]>([]);
const rows = ref<ObjectRow[]>([]);
const stats = ref<ObjectStatsResponse | null>(null);
const versions = ref<ObjectInfoResponse[]>([]);
const auditEntries = ref<ObjectAuditEntryResponse[]>([]);
const activeMultipart = ref<MultipartUploadCreateResponse | null>(null);
const multipartParts = ref<MultipartPartResponse[]>([]);
const multipartSessions = ref<MultipartUploadSessionResponse[]>([]);
const multipartSessionsCursor = ref<string | null>(null);
const multipartSessionsHasMore = ref(false);
const loadingMultipartSessions = ref(false);
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
  { key: 'semantic', label: '图片语义' },
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
const uploadFileInput = ref<HTMLInputElement | null>(null);
const multipartFileInput = ref<HTMLInputElement | null>(null);
const semanticFileInput = ref<HTMLInputElement | null>(null);
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
const semanticRuntime = ref<SemanticSearchStatusResponse | null>(null);
const processingStatus = ref<ObjectProcessingStatusResponse | null>(null);
const loadingProcessing = ref(false);
const selectedThumbnailUrl = ref('');
const semanticSearchMode = ref<SemanticSearchMode>('text');
const semanticText = ref('');
const semanticImageFile = ref<File | null>(null);
const similarImageId = ref('');
const semanticTopK = ref<number | null>(12);
const semanticMinScore = ref<number | null>(null);
const semanticFilterBucket = ref('');
const semanticFilterPrefix = ref('');
const semanticFilterContentType = ref('');
const semanticMetadataText = ref('');
const semanticTagsText = ref('');
const semanticExplain = ref(true);
const searchingSemantic = ref(false);
const semanticSearchResult = ref<ImageSearchResponse | null>(null);
const semanticHitUrls = ref<Record<string, string>>({});

const multipartSessionOptions = computed(() => multipartSessions.value.map((session) => ({
  label: `${session.upload.key} · ${session.parts.length} parts · ${session.status}`,
  value: session.upload.uploadId,
})));
const activeMultipartStatus = computed(() =>
  multipartSessions.value.find((session) => session.upload.uploadId === activeMultipart.value?.uploadId)?.status
    ?? (activeMultipart.value ? 'active' : null));

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

const semanticOptionsDraft = reactive<{
  asyncIngestionEnabled: boolean;
  thumbnailEnabled: boolean;
  thumbnailMaxWidth: number | null;
  thumbnailMaxHeight: number | null;
  thumbnailQuality: number | null;
}>({
  asyncIngestionEnabled: false,
  thumbnailEnabled: false,
  thumbnailMaxWidth: 320,
  thumbnailMaxHeight: 320,
  thumbnailQuality: 80,
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
const objectState = computed(() => permissionDenied.value ? 'permission' : readOnly.value ? 'readonly' : errorMsg.value ? 'error'
  : listIncomplete.value || rangeNotice.value.includes('截断') ? 'longContent' : rows.value.length === 0 && !loadingObjects.value ? 'empty' : 'normal');

const processingTagType = computed<'default' | 'error' | 'info' | 'success' | 'warning'>(() => {
  switch (processingStatus.value?.status) {
    case 'completed': return 'success';
    case 'failed': return 'error';
    case 'processing': return 'warning';
    case 'pending': return 'info';
    default: return 'default';
  }
});

const canRunSemanticSearch = computed(() => {
  if (!canRead.value || searchingSemantic.value) return false;
  if (semanticSearchMode.value === 'text') return semanticText.value.trim().length > 0;
  if (semanticSearchMode.value === 'image') return semanticImageFile.value !== null;
  return similarImageId.value.trim().length > 0;
});

const selectedBucket = computed({
  get: () => activeBucket.value,
  set: (value: string) => selectBucket(value),
});

const bucketOptions = computed<SelectOption[]>(() => {
  if (permissionDenied.value) return [];
  const names = new Set(localBuckets.value.map((bucket) => bucket.name));
  if (props.bucket) names.add(props.bucket);
  return [...names].sort().map((name) => ({ label: name, value: name }));
});

const filteredBuckets = computed(() =>
  permissionDenied.value ? [] : [...localBuckets.value].sort((a, b) => a.name.localeCompare(b.name)));

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
  if (!canWrite.value && !confirmBusy.value || permissionDenied.value || readOnly.value || pendingOperations.value.length === 0) return null;
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

function captureContext(): ObjectContext {
  const token = auth.state?.token;
  const baseUrl = auth.api.defaults.baseURL;
  const authorization = auth.api.defaults.headers?.common?.Authorization;
  const api = markRaw(createApiClient(() => token ?? null));
  api.defaults.baseURL = baseUrl;
  if (typeof authorization === 'string') api.defaults.headers.common.Authorization = authorization;
  return { revision: contextRevision, database: props.targetDb, bucket: activeBucket.value, api, sourceApi: auth.api,
    baseUrl, authorization, token, connectionId: connections.activeProfileId, connectionName: connections.activeProfile.name,
    profileBaseUrl: connections.activeBaseUrl };
}
function isCurrentContext(context: ObjectContext): boolean {
  return canRead.value && context.revision === contextRevision && context.database === props.targetDb && context.bucket === activeBucket.value
    && context.sourceApi === auth.api && context.baseUrl === auth.api.defaults.baseURL
    && context.authorization === auth.api.defaults.headers?.common?.Authorization && context.token === auth.state?.token
    && context.connectionId === connections.activeProfileId && context.profileBaseUrl === connections.activeBaseUrl;
}
function selectionIdentity(): string { return JSON.stringify([selectedKey.value, selectedObject.value?.versionId ?? '']); }
function beginRead(slot: string, selected = false): ReadTicket {
  cancelRead(slot);
  const ticket = { context: captureContext(), controller: new AbortController(), slot, selected, selection: selectionIdentity() };
  readTickets.set(slot, ticket); return ticket;
}
function currentRead(ticket: ReadTicket): boolean {
  return isCurrentContext(ticket.context) && readTickets.get(ticket.slot) === ticket && !ticket.controller.signal.aborted
    && (!ticket.selected || ticket.selection === selectionIdentity());
}
function cancelRead(slot: string): void { readTickets.get(slot)?.controller.abort(); readTickets.delete(slot); }
function finishRead(ticket: ReadTicket): void { if (readTickets.get(ticket.slot) === ticket) readTickets.delete(ticket.slot); }
function isPermissionError(error: unknown): boolean {
  const status = (error as { response?: { status?: number } } | null)?.response?.status; return status === 401 || status === 403;
}
function readError(error: unknown, ticket: ReadTicket, fallback: string): void {
  if (!currentRead(ticket)) return;
  if (isPermissionError(error)) {
    deniedContext = ticket.context; permissionLocked.value = true; invalidateContext(); localBuckets.value = [];
    errorMsg.value = '当前身份没有 Object 访问权限。';
  } else errorMsg.value = fallback;
}
async function readValue<T>(promise: Promise<T>, ticket: ReadTicket): Promise<T> {
  try { return await promise; } catch (error) { readError(error, ticket, 'Object 读取失败。'); throw error; }
}
async function readGroup<T extends readonly unknown[]>(promises: { [K in keyof T]: Promise<T[K]> }, ticket: ReadTicket): Promise<T> {
  const results = await Promise.allSettled(promises.map((promise) => readValue(promise, ticket)));
  return results.map((result) => { if (result.status === 'rejected') throw result.reason; return result.value; }) as unknown as T;
}
function normalizedPrefix(value: string): string { return value.replace(/^\/+/, ''); }
function boundedListLimit(): number { return Number.isFinite(listLimit.value) ? Math.max(1, Math.min(1000, Math.floor(listLimit.value))) : 100; }
function validObjectTarget(item: ObjectInfoResponse, context: ObjectContext, prefix = ''): boolean {
  return item?.bucket === context.bucket && typeof item.key === 'string' && item.key.startsWith(prefix)
    && typeof item.versionId === 'string' && typeof item.contentType === 'string'
    && item.metadata !== null && typeof item.metadata === 'object' && item.tags !== null && typeof item.tags === 'object';
}

async function refreshAll(): Promise<void> {
  if (!canRead.value) return;
  const ticket = beginRead('refresh'); loading.value = true; errorMsg.value = '';
  try {
    await loadBucketList(); if (!currentRead(ticket)) return;
    if (ticket.context.bucket) await Promise.all([loadObjects(true), loadGovernance(ticket.context.bucket), loadVersions(), loadAudit(), loadMultipartSessions(true)]);
    else { clearRows(); clearBucketMetadata(); }
  } catch (error) { readError(error, ticket, '加载对象桶工作台失败'); }
  finally { if (currentRead(ticket)) loading.value = false; finishRead(ticket); }
}
async function loadBucketList(): Promise<void> {
  if (!canRead.value) return;
  const ticket = beginRead('buckets');
  try {
    const buckets = await listObjectBuckets(ticket.context.api, ticket.context.database, ticket.controller.signal);
    if (!currentRead(ticket)) return;
    localBuckets.value = buckets;
    if (!props.bucket && buckets[0] && canRead.value) emit('selectBucket', buckets[0].name);
  } catch (error) { readError(error, ticket, '加载对象桶失败'); }
  finally { finishRead(ticket); }
}
async function loadGovernance(bucket: string): Promise<void> {
  if (!canRead.value || !bucket || bucket !== activeBucket.value) return;
  const ticket = beginRead('governance'); const { api, database } = ticket.context; const signal = ticket.controller.signal;
  try {
    const [nextStats, lifecycle, retention, quota, policy, semanticOptions, runtime] = await readGroup([
      readValue(getBucketStats(api, database, bucket, signal), ticket), readValue(getBucketLifecycle(api, database, bucket, signal), ticket),
      readValue(getBucketRetention(api, database, bucket, signal), ticket), readValue(getBucketQuota(api, database, bucket, signal), ticket),
      readValue(getBucketPolicy(api, database, bucket, signal), ticket), readValue(getBucketSemanticOptions(api, database, bucket, signal), ticket),
      readValue(getSemanticSearchStatus(api, signal), ticket),
    ] as const, ticket);
    if (!currentRead(ticket)) return;
    if ([nextStats, lifecycle, retention, quota, policy, semanticOptions].some((item) => item.bucket !== bucket)) throw new Error('Wrong bucket');
    stats.value = nextStats;
    Object.assign(lifecycleDraft, { expireCurrentAfterDays: lifecycle.expireCurrentAfterDays ?? null, expireNoncurrentAfterDays: lifecycle.expireNoncurrentAfterDays ?? null, expireDeleteMarkerAfterDays: lifecycle.expireDeleteMarkerAfterDays ?? null });
    Object.assign(retentionDraft, { retainCurrentForDays: retention.retainCurrentForDays ?? null, retainNoncurrentForDays: retention.retainNoncurrentForDays ?? null });
    Object.assign(quotaDraft, { maxSizeBytes: quota.maxSizeBytes ?? null, maxObjectVersions: quota.maxObjectVersions ?? null });
    policyDraft.value = policy.policyJson ?? '';
    Object.assign(semanticOptionsDraft, { asyncIngestionEnabled: semanticOptions.asyncIngestionEnabled, thumbnailEnabled: semanticOptions.thumbnailEnabled,
      thumbnailMaxWidth: semanticOptions.thumbnailMaxWidth, thumbnailMaxHeight: semanticOptions.thumbnailMaxHeight, thumbnailQuality: semanticOptions.thumbnailQuality });
    semanticRuntime.value = runtime; semanticFilterBucket.value = bucket;
  } catch (error) { readError(error, ticket, '加载对象桶治理失败'); }
  finally { finishRead(ticket); }
}
async function loadObjects(reset: boolean): Promise<void> {
  if (!canRead.value || !activeBucket.value || (!reset && (!hasMore.value || !cursor.value || rows.value.length >= 1000))) return;
  const ticket = beginRead('objects'); const { api, database, bucket } = ticket.context;
  const prefix = normalizedPrefix(currentPrefix.value); const token = reset ? null : cursor.value;
  const limit = Math.min(boundedListLimit(), reset ? 1000 : 1000 - rows.value.length);
  if (reset) { clearRows(); usedListTokens.clear(); listIncomplete.value = false; }
  loadingObjects.value = true; errorMsg.value = '';
  try {
    const response = await listObjects(api, database, bucket, { prefix, maxKeys: limit, continuationToken: token }, ticket.controller.signal);
    if (!currentRead(ticket)) return;
    if (response.bucket !== bucket || response.prefix !== prefix || (response.continuationToken ?? '') !== (token ?? '')) throw new Error('Wrong list target');
    const retained = response.objects.slice(0, limit);
    if (retained.some((item) => !validObjectTarget(item, ticket.context, prefix))) throw new Error('Wrong object target');
    const mapped = retained.map(mapObject); const next = response.nextContinuationToken ?? null;
    const overreturned = response.objects.length > limit;
    if (!overreturned && response.isTruncated && (!next || next === token || usedListTokens.has(next))) throw new Error('Non advancing list token');
    if (token) usedListTokens.add(token);
    rows.value = (reset ? mapped : mergeRows(rows.value, mapped)).slice(0, 1000);
    cursor.value = !overreturned && rows.value.length < 1000 && response.isTruncated ? next : null;
    hasMore.value = Boolean(cursor.value); listIncomplete.value = overreturned || response.isTruncated || Boolean(token);
    syncSelectedAfterRows();
    latestCommand.value = `GET /v1/db/${database}/s3/${bucket}?list-type=2&prefix=${prefix}`;
    const result = resultFromObjects(mapped, 0); if (result.end) result.end.truncated = listIncomplete.value;
    latestResult.value = result;
    ranOnce.value = true;
    recordHistory('success', 'Object list', 'browse', latestCommand.value, `${mapped.length} objects · ${listIncomplete.value ? '预览不完整' : '当前列表窗口'}`, mapped.length, -1, 0, ticket.context, listIncomplete.value ? 'truncated' : 'complete');
  } catch (error) {
    if (currentRead(ticket)) { readError(error, ticket, '加载对象列表失败'); if (currentRead(ticket)) { latestResult.value = errorResult('加载对象列表失败'); ranOnce.value = true; cursor.value = null; hasMore.value = false; } }
  } finally { if (currentRead(ticket)) loadingObjects.value = false; finishRead(ticket); }
}
async function loadMore(): Promise<void> { if (!loadingObjects.value) await loadObjects(false); }
async function loadVersions(versionKey?: string): Promise<void> {
  if (!canRead.value || !activeBucket.value) return;
  const ticket = beginRead('versions', true); const { api, database, bucket } = ticket.context;
  const key = versionKey ?? selectedObject.value?.key ?? '';
  try {
    const response = await listObjectVersions(api, database, bucket, key || null, ticket.controller.signal);
    if (!currentRead(ticket)) return;
    if (response.bucket !== bucket || (response.key ?? '') !== key || response.versions.some((item) => !validObjectTarget(item, ticket.context) || key && item.key !== key)) throw new Error('Wrong versions');
    versions.value = response.versions;
  } catch (error) { readError(error, ticket, '加载对象版本失败'); }
  finally { finishRead(ticket); }
}
async function loadAudit(): Promise<void> {
  if (!canRead.value || !activeBucket.value) return;
  const ticket = beginRead('audit'); const { api, database, bucket } = ticket.context;
  const prefix = auditPrefix.value || currentPrefix.value; const max = auditMaxEntries.value;
  loadingAudit.value = true;
  try {
    const response = await listBucketAudit(api, database, bucket, prefix, max, ticket.controller.signal);
    if (!currentRead(ticket)) return;
    if (response.bucket !== bucket || response.entries.some((item) => item.bucket !== bucket)) throw new Error('Wrong audit');
    auditEntries.value = response.entries;
  } catch (error) { readError(error, ticket, '加载对象桶审计失败'); }
  finally { if (currentRead(ticket)) loadingAudit.value = false; finishRead(ticket); }
}
async function loadMultipartSessions(reset: boolean): Promise<void> {
  if (!canRead.value || !activeBucket.value) return;
  const ticket = beginRead('multipart'); const { api, database, bucket } = ticket.context;
  const token = reset ? null : multipartSessionsCursor.value; loadingMultipartSessions.value = true;
  try {
    const response = await listMultipartUploads(api, database, bucket, 100, token, ticket.controller.signal);
    if (!currentRead(ticket)) return;
    if (response.bucket !== bucket || response.uploads.some((item) => item.upload.bucket !== bucket)) throw new Error('Wrong multipart');
    multipartSessions.value = reset ? response.uploads : mergeMultipartSessions(multipartSessions.value, response.uploads);
    multipartSessionsCursor.value = response.nextContinuationToken ?? null; multipartSessionsHasMore.value = response.isTruncated;
    if (activeMultipart.value) { const refreshed = multipartSessions.value.find((item) => item.upload.uploadId === activeMultipart.value?.uploadId); if (refreshed) applyMultipartSession(refreshed); }
  } catch (error) { readError(error, ticket, '加载 Multipart 会话失败'); }
  finally { if (currentRead(ticket)) loadingMultipartSessions.value = false; finishRead(ticket); }
}
function resumeMultipartSession(uploadId: string | null): void {
  if (!canRead.value) return;
  if (!uploadId) { activeMultipart.value = null; multipartParts.value = []; multipartFile.value = null; return; }
  const session = multipartSessions.value.find((item) => item.upload.uploadId === uploadId); if (session) applyMultipartSession(session);
}
function applyMultipartSession(session: MultipartUploadSessionResponse): void {
  if (!canRead.value || session.upload.bucket !== activeBucket.value) return;
  activeMultipart.value = session.upload; multipartParts.value = [...session.parts].sort((left, right) => left.partNumber - right.partNumber);
  multipartKey.value = session.upload.key; multipartContentType.value = session.upload.contentType;
  multipartPartNumber.value = (multipartParts.value.at(-1)?.partNumber ?? 0) + 1; multipartFile.value = null;
}

function selectBucket(bucket: string): void {
  if (!bucket) return;
  emit('selectBucket', bucket);
}

function selectObject(key: string): void {
  selectedKey.value = key;
}

function applyPrefix(): void {
  currentPrefix.value = normalizedPrefix(prefixInput.value);
  cursor.value = null;
  hasMore.value = false;
  rows.value = [];
  void loadObjects(true);
  void loadAudit();
}

function openPrefix(prefix: string): void {
  prefixInput.value = prefix;
  currentPrefix.value = normalizedPrefix(prefix);
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
  multipartSessions.value = [];
  multipartSessionsCursor.value = null;
  multipartSessionsHasMore.value = false;
  activeMultipart.value = null;
  multipartParts.value = [];
  policyDraft.value = '';
  lifecycleDraft.expireCurrentAfterDays = null;
  lifecycleDraft.expireNoncurrentAfterDays = null;
  lifecycleDraft.expireDeleteMarkerAfterDays = null;
  retentionDraft.retainCurrentForDays = null;
  retentionDraft.retainNoncurrentForDays = null;
  quotaDraft.maxSizeBytes = null;
  quotaDraft.maxObjectVersions = null;
  semanticOptionsDraft.asyncIngestionEnabled = false;
  semanticOptionsDraft.thumbnailEnabled = false;
  semanticOptionsDraft.thumbnailMaxWidth = 320;
  semanticOptionsDraft.thumbnailMaxHeight = 320;
  semanticOptionsDraft.thumbnailQuality = 80;
  semanticRuntime.value = null;
  processingStatus.value = null;
  semanticFilterBucket.value = '';
  clearSelectedThumbnail();
  clearSemanticHitUrls();
  semanticSearchResult.value = null;
}

async function loadSelectedProcessing(): Promise<void> {
  const row = selectedObject.value; if (!canRead.value || !row) return;
  const ticket = beginRead('processing', true); processingStatus.value = null; clearSelectedThumbnail(); loadingProcessing.value = true;
  const { api, database } = ticket.context;
  try {
    const status = await getObjectProcessingStatus(api, database, row.bucket, row.key, ticket.controller.signal);
    if (!currentRead(ticket)) return;
    if (status.bucket !== row.bucket || status.key !== row.key || status.versionId !== row.versionId) throw new Error('Wrong processing target');
    processingStatus.value = status; if (status.semanticImageId) similarImageId.value = status.semanticImageId;
    if (status.thumbnailUrl) {
      const blob = await getObjectThumbnailBlob(api, database, row.bucket, row.key, ticket.controller.signal);
      if (!currentRead(ticket)) return;
      selectedThumbnailUrl.value = URL.createObjectURL(blob);
    }
  } catch (error) { readError(error, ticket, '读取对象处理状态失败'); }
  finally { if (currentRead(ticket)) loadingProcessing.value = false; finishRead(ticket); }
}
async function searchSimilarToSelected(): Promise<void> {
  if (!canRead.value) return;
  const id = processingStatus.value?.semanticImageId; if (!id) return;
  similarImageId.value = id; semanticSearchMode.value = 'similar'; await runSemanticSearch();
}
function onSemanticFileChange(event: Event): void {
  const input = event.target as HTMLInputElement;
  if (!canRead.value) { input.value = ''; return; }
  semanticImageFile.value = input.files?.[0] ?? null;
}
async function runSemanticSearch(): Promise<void> {
  if (!canRunSemanticSearch.value) return;
  const metadata = parseKeyValueMap(semanticMetadataText.value); const tags = parseKeyValueMap(semanticTagsText.value);
  if (!metadata.ok || !tags.ok) { errorMsg.value = '图片语义过滤条件无效。'; return; }
  const ticket = beginRead('search'); cancelRead('images'); clearSemanticHitUrls();
  const { api, database } = ticket.context; const mode = semanticSearchMode.value;
  const text = semanticText.value.trim(); const image = semanticImageFile.value; const id = similarImageId.value.trim();
  const request = { topK: semanticTopK.value, minScore: semanticMinScore.value, filter: buildSemanticFilter(metadata.value, tags.value), explain: semanticExplain.value };
  searchingSemantic.value = true; errorMsg.value = ''; const started = performance.now();
  const command = mode === 'text' ? `POST /v1/db/${database}/images/search/text` : mode === 'image' ? `POST /v1/db/${database}/images/search/image` : `POST /v1/db/${database}/images/${id}/similar`;
  try {
    const response = mode === 'text' ? await searchImagesByText(api, database, text, request, ticket.controller.signal)
      : mode === 'image' ? await searchImagesByImage(api, database, image!, request, ticket.controller.signal)
        : await searchSimilarImages(api, database, id, request, ticket.controller.signal);
    if (!currentRead(ticket)) return;
    semanticSearchResult.value = response; latestCommand.value = command; latestResult.value = resultFromSemanticHits(response.hits, performanceElapsed(started)); ranOnce.value = true;
    recordHistory('success', 'Semantic image search', 'search', command, `${response.hits.length} hits via ${response.backend}`, response.hits.length, -1, performanceElapsed(started), ticket.context);
    await loadSemanticHitPreviews(response.hits, ticket);
  } catch (error) {
    readError(error, ticket, '图片语义检索失败'); if (currentRead(ticket)) { latestResult.value = errorResult('图片语义检索失败'); ranOnce.value = true; }
  } finally { if (currentRead(ticket)) searchingSemantic.value = false; finishRead(ticket); }
}
function buildSemanticFilter(metadata: Record<string, string>, tags: Record<string, string>): ImageSearchFilter | null {
  const filter = { sourceBucket: semanticFilterBucket.value.trim() || null, sourceKeyPrefix: semanticFilterPrefix.value.trim() || null,
    contentType: semanticFilterContentType.value.trim() || null, metadata: Object.keys(metadata).length > 0 ? metadata : null, tags: Object.keys(tags).length > 0 ? tags : null };
  return filter.sourceBucket || filter.sourceKeyPrefix || filter.contentType || filter.metadata || filter.tags ? filter : null;
}
function selectSemanticHit(hit: ImageSearchHit): void {
  if (!canRead.value) return;
  similarImageId.value = hit.id; semanticSearchMode.value = 'similar';
  if (hit.sourceBucket === activeBucket.value && hit.sourceKey) { const row = rows.value.find((item) => item.key === hit.sourceKey); if (row) selectedKey.value = row.key; }
}
async function loadSemanticHitPreviews(hits: ImageSearchHit[], parent?: ReadTicket): Promise<void> {
  if (!canRead.value || parent && !currentRead(parent)) return;
  const ticket = beginRead('images'); clearSemanticHitUrls();
  const deadline = Date.now() + 60000;
  try {
    await Promise.all(hits.slice(0, 24).map(async (hit) => {
      const source = hit.thumbnailUrl || hit.contentUrl; if (!source || Date.now() >= deadline) return;
      try {
        const blob = await getProtectedImageBlob(ticket.context.api, source, ticket.controller.signal);
        if (!currentRead(ticket) || parent && !currentRead(parent) || Date.now() >= deadline) return;
        semanticHitUrls.value = { ...semanticHitUrls.value, [hit.id]: URL.createObjectURL(blob) };
      } catch (error) { if (!parent || currentRead(parent)) readError(error, ticket, '读取图片预览失败'); }
    }));
  } finally { finishRead(ticket); }
}
function clearSelectedThumbnail(): void { if (selectedThumbnailUrl.value) URL.revokeObjectURL(selectedThumbnailUrl.value); selectedThumbnailUrl.value = ''; }
function clearSemanticHitUrls(): void {
  for (const url of Object.values(semanticHitUrls.value).slice(0, 24)) URL.revokeObjectURL(url);
  semanticHitUrls.value = {};
}
async function loadPreview(versionId?: string | null): Promise<void> {
  const row = selectedObject.value; if (!canRead.value || !row) return;
  const start = rangeStart.value ?? 0; const requestedLength = rangeLength.value ?? 4096;
  if (!Number.isSafeInteger(start) || start < 0 || !Number.isSafeInteger(requestedLength) || requestedLength <= 0) { errorMsg.value = 'Range 必须使用安全非负起点与正整数长度。'; return; }
  const length = Math.min(4096, requestedLength);
  if (start > Number.MAX_SAFE_INTEGER - (length - 1)) { errorMsg.value = 'Range 终点超出安全整数范围。'; return; }
  const end = start + (length - 1);
  if (!Number.isSafeInteger(end)) { errorMsg.value = 'Range 终点超出安全整数范围。'; return; }
  const ticket = beginRead('range', true); const mode = previewMode.value; const version = versionId ?? row.versionId;
  loadingPreview.value = true; errorMsg.value = ''; rangeNotice.value = '';
  try {
    const response = await getObjectBlob(ticket.context.api, ticket.context.database, row.bucket, row.key, { versionId: version, range: { start, end } }, ticket.controller.signal);
    if (!currentRead(ticket)) return;
    if (version && response.head.versionId !== version) throw new Error('Wrong object version');
    let total = row.sizeBytes; let retainedLength = length;
    if (response.status === 206) {
      const match = /^bytes (\d+)-(\d+)\/(\d+)$/.exec(response.contentRange ?? '');
      if (!match || !match.slice(1).every((value) => Number.isSafeInteger(Number(value))) || Number(match[1]) !== start || Number(match[2]) < start || Number(match[2]) > end || Number(match[3]) <= Number(match[2])
        || response.blob.size < Number(match[2]) - start + 1) throw new Error('Invalid range response');
      total = Number(match[3]);
      retainedLength = Math.min(length, Number(match[2]) - start + 1);
    } else if (response.status !== undefined && (response.status !== 200 || start !== 0)) throw new Error('Unsupported range response');
    const clipped = response.blob.slice(0, retainedLength); const bytes = new Uint8Array(await clipped.arrayBuffer());
    if (!currentRead(ticket)) return;
    previewText.value = formatBytesForPreview(bytes, mode, response.head.contentType);
    const incomplete = start > 0 || total > start + bytes.length || response.blob.size > retainedLength || requestedLength > length;
    rangeNotice.value = `Range ${start}–${start + Math.max(0, bytes.length - 1)} · ${bytes.length} 字节${incomplete ? ' · 截断/局部预览' : ' · 当前对象读取'}，不代表传输预算。`;
    recordHistory('success', 'Object Range', 'range', `GET ${ticket.context.database}/${row.bucket}/${row.key}?versionId=${version}`, rangeNotice.value, 0, -1, 0, ticket.context, incomplete ? 'truncated' : 'complete');
  } catch (error) { readError(error, ticket, '读取对象预览失败'); }
  finally { if (currentRead(ticket)) loadingPreview.value = false; finishRead(ticket); }
}
async function loadVersionPreview(versionId: string): Promise<void> {
  const row = selectedObject.value;
  if (!row || !versions.value.some((item) => item.bucket === row.bucket && item.key === row.key && item.versionId === versionId)) return;
  await loadPreview(versionId);
}
async function downloadSelectedObject(): Promise<void> {
  const row = selectedObject.value; if (!canRead.value || !row) return;
  const ticket = beginRead('download', true);
  try {
    const response = await getObjectBlob(ticket.context.api, ticket.context.database, row.bucket, row.key, { versionId: row.versionId }, ticket.controller.signal);
    if (!currentRead(ticket)) return;
    if (row.versionId && response.head.versionId !== row.versionId) throw new Error('Wrong download version');
    const bridge = currentStudioNativeBridge();
    if (bridge) {
      const saved = await bridge.saveBinaryFile({ title: `保存 ${row.key}`, suggestedName: fileNameFromKey(row.key), content: response.blob });
      if (currentRead(ticket) && !saved.canceled) message.success(`已保存 ${saved.fileName ?? fileNameFromKey(row.key)}`); return;
    }
    const url = URL.createObjectURL(response.blob);
    try { if (!currentRead(ticket)) return; const link = document.createElement('a'); link.href = url; link.download = fileNameFromKey(row.key); document.body.appendChild(link); link.click(); link.remove(); message.success('Download started'); }
    finally { URL.revokeObjectURL(url); }
  } catch (error) { readError(error, ticket, '下载对象失败'); }
  finally { finishRead(ticket); }
}

function stageCreateBucket(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = newBucketName.value.trim();
  if (!bucket) return;
  const purpose = newBucketPurpose.value.trim();
  pendingOperations.value = [{
    id: makeOperationId('bucket_create'),
    expectedTarget: bucket,
    label: 'Create bucket',
    detail: bucket,
    severity: 'write',
    command: `PUT /v1/db/${context.database}/s3/${bucket}`,
    run: async () => {
      assertWriteContext(context);
      const response = await createObjectBucket(context.api, context.database, bucket, purpose || null);
      assertWriteContext(context);
      validateBucketResponse(response, bucket);
      newBucketName.value = '';
      newBucketPurpose.value = '';
      return { action: 'bucket.create', target: bucket, succeeded: true, affected: 1, detail: purpose || 'created' };
    },
  }];
}

function stageDeleteBucket(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = context.bucket;
  if (!bucket) return;
  pendingOperations.value = [{
    id: makeOperationId('bucket_delete'),
    expectedTarget: bucket,
    label: 'Delete bucket',
    detail: bucket,
    severity: 'danger',
    command: `DELETE /v1/db/${context.database}/s3/${bucket}`,
    run: async () => {
      assertWriteContext(context);
      await deleteObjectBucket(context.api, context.database, bucket);
      assertWriteContext(context);
      return { action: 'bucket.delete', target: bucket, succeeded: true, affected: 1, detail: 'deleted' };
    },
  }];
}

function stageUploadFile(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = context.bucket;
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
    expectedTarget: key,
    label: 'Put object',
    detail: `${key} · ${formatBytes(file.size)}`,
    severity: 'write',
    command: `PUT /v1/db/${context.database}/s3/${bucket}/${key}`,
    run: async () => {
      assertWriteContext(context);
      const response = await putObject(context.api, context.database, bucket, key, file, {
        contentType,
        metadata: maps.metadata,
        tags: maps.tags,
      });
      assertWriteContext(context);
      validateObjectInfoResponse(response, bucket, key);
      return { action: 'object.put', target: key, succeeded: true, affected: 1, detail: response.versionId };
    },
  }];
}

function stageUploadText(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = context.bucket;
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
    expectedTarget: key,
    label: 'Put text object',
    detail: `${key} · ${formatBytes(blob.size)}`,
    severity: 'write',
    command: `PUT /v1/db/${context.database}/s3/${bucket}/${key}`,
    run: async () => {
      assertWriteContext(context);
      const response = await putObject(context.api, context.database, bucket, key, blob, {
        contentType,
        metadata: maps.metadata,
        tags: maps.tags,
      });
      assertWriteContext(context);
      validateObjectInfoResponse(response, bucket, key);
      return { action: 'object.put', target: key, succeeded: true, affected: 1, detail: response.versionId };
    },
  }];
}

function stageSetTags(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const row = selectedObject.value ? { ...selectedObject.value } : null;
  if (!row) return;
  const parsed = parseKeyValueMap(selectedTagsText.value);
  if (!parsed.ok) {
    errorMsg.value = parsed.message;
    return;
  }
  pendingOperations.value = [{
    id: makeOperationId('object_tags'),
    expectedTarget: row.key,
    label: 'Set tags',
    detail: row.key,
    severity: 'write',
    command: `PUT /v1/db/${context.database}/s3/${row.bucket}/${row.key}?tagging`,
    run: async () => {
      assertWriteContext(context);
      const response = await setObjectTags(context.api, context.database, row.bucket, row.key, parsed.value);
      assertWriteContext(context);
      validateObjectInfoResponse(response, row.bucket, row.key);
      return { action: 'object.tags.set', target: row.key, succeeded: true, affected: 1, detail: `${Object.keys(parsed.value).length} tags` };
    },
  }];
}

function stageCopySelected(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const row = selectedObject.value ? { ...selectedObject.value } : null;
  const targetKey = copyTargetKey.value.trim();
  if (!row || !targetKey) return;
  pendingOperations.value = [{
    id: makeOperationId('object_copy'),
    expectedTarget: targetKey,
    label: 'Copy object',
    detail: `${row.key} -> ${targetKey}`,
    severity: 'write',
    command: `COPY ${row.bucket}/${row.key} TO ${row.bucket}/${targetKey}`,
    run: async () => {
      assertWriteContext(context);
      const response = await copyObject(context.api, context.database, row.bucket, row.key, row.bucket, targetKey);
      assertWriteContext(context);
      validateCopyResponse(response);
      return { action: 'object.copy', target: targetKey, succeeded: true, affected: 1, detail: response.versionId };
    },
  }];
}

function stageDeleteCurrent(): void {
  if (!canWrite.value) return;
  const row = selectedObject.value;
  if (!row) return;
  pendingOperations.value = [deleteOperation(row.key)];
}

function stageDeleteSelected(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const keys = checkedRowKeys.value.map(String).filter(Boolean);
  if (!context.bucket || keys.length === 0) return;
  pendingOperations.value = [{
    id: makeOperationId('object_delete_many'),
    expectedTarget: context.bucket,
    label: 'Delete selected',
    detail: `${keys.length} objects`,
    severity: 'danger',
    command: `POST /v1/db/${context.database}/s3/${context.bucket}?delete`,
    run: async () => {
      assertWriteContext(context);
      const response = await deleteManyObjects(context.api, context.database, context.bucket, keys);
      assertWriteContext(context);
      const { affected, failures } = validateDeleteManyResponse(response, context.bucket, keys);
      return { action: 'object.delete_many', target: context.bucket, succeeded: failures === 0, affected, detail: `${affected}/${keys.length} deleted; ${failures} failed` };
    },
  }];
}

function deleteOperation(key: string): PendingOperation {
  const context = captureContext();
  const bucket = context.bucket;
  return {
    id: makeOperationId('object_delete'),
    expectedTarget: key,
    label: 'Delete object',
    detail: key,
    severity: 'danger',
    command: `DELETE /v1/db/${context.database}/s3/${bucket}/${key}`,
    run: async () => {
      assertWriteContext(context);
      await deleteObject(context.api, context.database, bucket, key);
      assertWriteContext(context);
      return { action: 'object.delete', target: key, succeeded: true, affected: 1, detail: 'delete marker created' };
    },
  };
}

function stagePresign(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const row = selectedObject.value ? { ...selectedObject.value } : null;
  if (!row) return;
  const minutes = Math.max(1, Math.min(1440, presignMinutes.value ?? 60));
  const method = presignMethod.value;
  pendingOperations.value = [{
    id: makeOperationId('object_presign'),
    expectedTarget: row.key,
    label: 'Create presigned URL',
    detail: `${method} ${row.key} · ${minutes} min`,
    severity: 'write',
    command: `POST /v1/db/${context.database}/s3/${row.bucket}/${row.key}?presign`,
    run: async () => {
      assertWriteContext(context);
      const response = await createPresignedObjectUrl(context.api, context.database, row.bucket, row.key, method, minutes);
      assertWriteContext(context);
      validatePresignResponse(response, row.bucket, row.key, method);
      presignedUrl.value = response.url;
      await copyText(response.url, 'Presigned URL copied');
      assertWriteContext(context);
      return { action: 'object.presign.create', target: row.key, succeeded: true, affected: 1, detail: response.expiresUtc };
    },
  }];
}

function stageSetLifecycle(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = context.bucket;
  if (!bucket) return;
  const request = nullifyDraft(lifecycleDraft);
  pendingOperations.value = [{
    id: makeOperationId('bucket_lifecycle'),
    expectedTarget: bucket,
    label: 'Set lifecycle',
    detail: bucket,
    severity: 'write',
    command: `PUT /v1/db/${context.database}/s3/${bucket}?lifecycle`,
    run: async () => {
      assertWriteContext(context);
      const response = await setBucketLifecycle(context.api, context.database, bucket, request);
      assertWriteContext(context);
      validateBucketSetterResponse(response, bucket, request);
      return { action: 'bucket.lifecycle.set', target: bucket, succeeded: true, affected: 1, detail: 'saved' };
    },
  }];
}

function stageApplyLifecycle(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = context.bucket;
  if (!bucket) return;
  pendingOperations.value = [{
    id: makeOperationId('bucket_lifecycle_apply'),
    expectedTarget: bucket,
    label: 'Apply lifecycle',
    detail: bucket,
    severity: 'danger',
    command: `POST /v1/db/${context.database}/s3/${bucket}?lifecycle`,
    run: async () => {
      assertWriteContext(context);
      const response = await applyBucketLifecycle(context.api, context.database, bucket);
      assertWriteContext(context);
      validateBucketFieldResponse(response, bucket);
      if (![response.expiredCurrentObjects, response.removedNoncurrentVersions, response.removedDeleteMarkers, response.semanticCleanupJobs].every(validCount)) throw new UnknownObjectOperationError('Invalid lifecycle terminal.');
      const affected = response.expiredCurrentObjects + response.removedNoncurrentVersions + response.removedDeleteMarkers;
      return { action: 'bucket.lifecycle.apply', target: bucket, succeeded: true, affected, detail: `${affected} versions/markers affected` };
    },
  }];
}

function stageSetSemanticOptions(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = context.bucket;
  if (!bucket) return;
  const request = {
    asyncIngestionEnabled: semanticOptionsDraft.asyncIngestionEnabled,
    thumbnailEnabled: semanticOptionsDraft.thumbnailEnabled,
    thumbnailMaxWidth: semanticOptionsDraft.thumbnailMaxWidth ?? 320,
    thumbnailMaxHeight: semanticOptionsDraft.thumbnailMaxHeight ?? 320,
    thumbnailQuality: semanticOptionsDraft.thumbnailQuality ?? 80,
  };
  pendingOperations.value = [{
    id: makeOperationId('bucket_semantic_options'),
    expectedTarget: bucket,
    label: 'Set semantic image options',
    detail: bucket,
    severity: 'write',
    command: `PUT /v1/db/${context.database}/s3/${bucket}?semantic`,
    run: async () => {
      assertWriteContext(context);
      const response = await setBucketSemanticOptions(context.api, context.database, bucket, request);
      assertWriteContext(context);
      validateBucketSetterResponse(response, bucket, request);
      return {
        action: 'bucket.semantic.set',
        target: bucket,
        succeeded: true,
        affected: 1,
        detail: `ingestion=${response.asyncIngestionEnabled}, thumbnail=${response.thumbnailEnabled}`,
      };
    },
  }];
}

function stageSemanticBackfill(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = context.bucket;
  if (!bucket) return;
  pendingOperations.value = [{
    id: makeOperationId('bucket_semantic_backfill'),
    expectedTarget: bucket,
    label: 'Backfill semantic images',
    detail: bucket,
    severity: 'write',
    command: `POST /v1/db/${context.database}/s3/${bucket}?semantic`,
    run: async () => {
      assertWriteContext(context);
      const response = await backfillBucketSemanticObjects(context.api, context.database, bucket);
      assertWriteContext(context);
      validateBucketFieldResponse(response, bucket);
      if (![response.scannedObjects, response.queuedObjects, response.skippedObjects].every(validCount)
        || response.queuedObjects + response.skippedObjects !== response.scannedObjects
        || typeof response.hasMore !== 'boolean' || typeof response.completed !== 'boolean'
        || response.hasMore === response.completed || response.continuationToken != null && typeof response.continuationToken !== 'string') throw new UnknownObjectOperationError('Invalid backfill terminal.');
      return {
        action: 'bucket.semantic.backfill',
        target: bucket,
        succeeded: true,
        affected: response.queuedObjects,
        detail: `${response.queuedObjects}/${response.scannedObjects} queued; backfill ${response.completed ? 'enumerated' : 'pending'}; derived processing unverified`,
      };
    },
  }];
}

function stageRequeueSelectedObject(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const row = selectedObject.value ? { ...selectedObject.value } : null;
  if (!row) return;
  pendingOperations.value = [{
    id: makeOperationId('object_semantic_requeue'),
    expectedTarget: row.key,
    label: 'Requeue image processing',
    detail: row.key,
    severity: 'write',
    command: `POST /v1/db/${context.database}/s3/${row.bucket}/${row.key}?processing`,
    run: async () => {
      assertWriteContext(context);
      const response = await enqueueObjectProcessing(
        context.api,
        context.database,
        row.bucket,
        row.key,
      );
      assertWriteContext(context);
      if (!response || response.bucket !== row.bucket || response.key !== row.key || response.versionId !== row.versionId
        || response.operation !== 'upsert' || !nonemptyString(response.jobId)
        || !['pending', 'processing', 'retry', 'completed', 'failed', 'cancelled', 'superseded'].includes(response.status)) throw new UnknownObjectOperationError('Invalid processing terminal.');
      if (['failed', 'cancelled', 'superseded'].includes(response.status)) return { action: 'object.semantic.requeue', target: row.key, succeeded: false, affected: 0, detail: `processing ${response.status}` };
      processingStatus.value = response;
      return {
        action: 'object.semantic.requeue',
        target: row.key,
        succeeded: true,
        affected: 1,
        detail: `${response.jobId}: task accepted/current status ${response.status}; derived processing unverified`,
      };
    },
  }];
}

function stageSetRetention(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = context.bucket;
  if (!bucket) return;
  const request = nullifyDraft(retentionDraft);
  pendingOperations.value = [{
    id: makeOperationId('bucket_retention'),
    expectedTarget: bucket,
    label: 'Set retention',
    detail: bucket,
    severity: 'write',
    command: `PUT /v1/db/${context.database}/s3/${bucket}?retention`,
    run: async () => {
      assertWriteContext(context);
      const response = await setBucketRetention(context.api, context.database, bucket, request);
      assertWriteContext(context);
      validateBucketSetterResponse(response, bucket, request);
      return { action: 'bucket.retention.set', target: bucket, succeeded: true, affected: 1, detail: 'saved' };
    },
  }];
}

function stageSetQuota(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = context.bucket;
  if (!bucket) return;
  const request = nullifyDraft(quotaDraft);
  pendingOperations.value = [{
    id: makeOperationId('bucket_quota'),
    expectedTarget: bucket,
    label: 'Set quota',
    detail: bucket,
    severity: 'write',
    command: `PUT /v1/db/${context.database}/s3/${bucket}?quota`,
    run: async () => {
      assertWriteContext(context);
      const response = await setBucketQuota(context.api, context.database, bucket, request);
      assertWriteContext(context);
      validateBucketSetterResponse(response, bucket, request);
      return { action: 'bucket.quota.set', target: bucket, succeeded: true, affected: 1, detail: 'saved' };
    },
  }];
}

function stageSetPolicy(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = context.bucket;
  if (!bucket) return;
  const policy = policyDraft.value.trim();
  if (policy) {
    try {
      JSON.parse(policy);
    } catch (error) {
      errorMsg.value = 'Policy JSON is invalid.';
      return;
    }
  }
  pendingOperations.value = [{
    id: makeOperationId('bucket_policy'),
    expectedTarget: bucket,
    label: 'Set policy',
    detail: bucket,
    severity: 'write',
    command: `PUT /v1/db/${context.database}/s3/${bucket}?policy`,
    run: async () => {
      assertWriteContext(context);
      const response = await setBucketPolicy(context.api, context.database, bucket, policy || null);
      assertWriteContext(context);
      validateBucketSetterResponse(response, bucket, { policyJson: policy || null });
      return { action: 'bucket.policy.set', target: bucket, succeeded: true, affected: 1, detail: policy ? 'saved' : 'cleared' };
    },
  }];
}

function stageSetLegalHold(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const row = selectedObject.value ? { ...selectedObject.value } : null;
  if (!row) return;
  const versionId = versions.value.find((version) => version.versionId === row.versionId)?.versionId ?? row.versionId;
  const enabled = legalHoldEnabled.value;
  const reason = legalHoldReason.value.trim() || null;
  pendingOperations.value = [{
    id: makeOperationId('object_legal_hold'),
    expectedTarget: row.key,
    label: enabled ? 'Enable legal hold' : 'Disable legal hold',
    detail: row.key,
    severity: enabled ? 'write' : 'danger',
    command: `PUT /v1/db/${context.database}/s3/${row.bucket}/${row.key}?legal-hold&versionId=${versionId}`,
    run: async () => {
      assertWriteContext(context);
      const response = await setObjectLegalHold(context.api, context.database, row.bucket, row.key, enabled, reason, versionId);
      assertWriteContext(context);
      validateBucketSetterResponse(response, row.bucket, { key: row.key, versionId, enabled, reason });
      return { action: enabled ? 'object.legal_hold.enable' : 'object.legal_hold.disable', target: row.key, succeeded: true, affected: 1, detail: versionId };
    },
  }];
}

function stageInitiateMultipart(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const bucket = context.bucket;
  const key = multipartKey.value.trim();
  if (!bucket || !key) return;
  const maps = parseUploadMaps();
  if (!maps.ok) {
    errorMsg.value = maps.message;
    return;
  }
  const options = {
    contentType: multipartContentType.value || 'application/octet-stream',
    metadata: maps.metadata,
    tags: maps.tags,
    expiresHours: multipartExpiresHours.value,
  };
  pendingOperations.value = [{
    id: makeOperationId('multipart_init'),
    expectedTarget: key,
    label: 'Initiate multipart',
    detail: key,
    severity: 'write',
    command: `POST /v1/db/${context.database}/s3/${bucket}/${key}?uploads`,
    run: async () => {
      assertWriteContext(context);
      const response = await initiateMultipartUpload(context.api, context.database, bucket, key, options);
      assertWriteContext(context);
      if (!response || response.bucket !== bucket || response.key !== key || !nonemptyString(response.uploadId)
        || response.contentType !== options.contentType || !validTerminalDate(response.initiatedUtc) || !validTerminalDate(response.expiresUtc)) throw new UnknownObjectOperationError('Invalid multipart terminal.');
      activeMultipart.value = response;
      multipartParts.value = [];
      return { action: 'multipart.initiate', target: key, succeeded: true, affected: 1, detail: response.uploadId };
    },
  }];
}

function stageUploadPart(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const upload = activeMultipart.value ? { ...activeMultipart.value } : null;
  const file = multipartFile.value;
  const partNumber = multipartPartNumber.value ?? 1;
  if (!upload || !file || partNumber <= 0) return;
  pendingOperations.value = [{
    id: makeOperationId('multipart_part'),
    expectedTarget: upload.key,
    label: 'Upload part',
    detail: `part ${partNumber} · ${formatBytes(file.size)}`,
    severity: 'write',
    command: `PUT /v1/db/${context.database}/s3/${upload.bucket}/${upload.key}?uploadId=${upload.uploadId}&partNumber=${partNumber}`,
    run: async () => {
      assertWriteContext(context);
      const part = await uploadMultipartPart(context.api, context.database, upload.bucket, upload.key, upload.uploadId, partNumber, file);
      assertWriteContext(context);
      if (!part || part.partNumber !== partNumber || part.sizeBytes !== file.size || !Number.isSafeInteger(part.sizeBytes)
        || part.sizeBytes < 0 || !nonemptyString(part.eTag) || !nonemptyString(part.sha256)) throw new UnknownObjectOperationError('Invalid multipart part terminal.');
      multipartParts.value = mergeParts(multipartParts.value, part);
      multipartPartNumber.value = partNumber + 1;
      multipartFile.value = null;
      return { action: 'multipart.part.put', target: upload.key, succeeded: true, affected: 1, detail: `part ${part.partNumber}` };
    },
  }];
}

function stageCompleteMultipart(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const upload = activeMultipart.value ? { ...activeMultipart.value } : null;
  if (!upload || multipartParts.value.length === 0) return;
  const partNumbers = multipartParts.value.map((part) => part.partNumber).sort((a, b) => a - b);
  pendingOperations.value = [{
    id: makeOperationId('multipart_complete'),
    expectedTarget: upload.key,
    label: 'Complete multipart',
    detail: `${partNumbers.length} parts`,
    severity: 'write',
    command: `POST /v1/db/${context.database}/s3/${upload.bucket}/${upload.key}?uploadId=${upload.uploadId}`,
    run: async () => {
      assertWriteContext(context);
      const response = await completeMultipartUpload(context.api, context.database, upload.bucket, upload.key, upload.uploadId, partNumbers);
      assertWriteContext(context);
      validateObjectInfoResponse(response, upload.bucket, upload.key);
      activeMultipart.value = null;
      multipartParts.value = [];
      return { action: 'multipart.complete', target: response.key, succeeded: true, affected: 1, detail: response.versionId };
    },
  }];
}

function stageAbortMultipart(): void {
  if (!canWrite.value) return;
  const context = captureContext();
  const upload = activeMultipart.value ? { ...activeMultipart.value } : null;
  if (!upload) return;
  pendingOperations.value = [{
    id: makeOperationId('multipart_abort'),
    expectedTarget: upload.key,
    label: 'Abort multipart',
    detail: upload.uploadId,
    severity: 'danger',
    command: `DELETE /v1/db/${context.database}/s3/${upload.bucket}/${upload.key}?uploadId=${upload.uploadId}`,
    run: async () => {
      assertWriteContext(context);
      await abortMultipartUpload(context.api, context.database, upload.bucket, upload.key, upload.uploadId);
      assertWriteContext(context);
      activeMultipart.value = null;
      multipartParts.value = [];
      return { action: 'multipart.abort', target: upload.key, succeeded: true, affected: 1, detail: 'aborted' };
    },
  }];
}

async function confirmPendingOperations(): Promise<void> {
  const context = approvalContext;
  if (!canWrite.value || !context || !isCurrentContext(context) || approvalInputs !== approvalInputKey() || pendingOperations.value.length === 0) { clearPendingOperations(); return; }
  const operations = [...pendingOperations.value];
  if (operations.length > 1000) { clearPendingOperations(); return; }
  confirmBusy.value = true;
  errorMsg.value = '';
  clearPendingOperations();
  const command = operations.map((operation) => operation.command).join('\n');
  const started = performance.now();
  const outcomes: OperationOutcome[] = [];
  let startedOperations = 0;
  const counts = () => `${operations.length} approved; ${startedOperations} started; ${outcomes.length} confirmed`;
  const affected = () => outcomes.reduce((sum, item) => sum + item.affected, 0);
  let successRecorded = false;
  try {
    // 60 秒约束新操作的启动窗口；在途请求沿用 Axios 的 30 秒超时，不代表服务端取消或网络发送计量。
    const deadline = Date.now() + 60000;
    for (const operation of operations) {
      if (Date.now() >= deadline) throw new Error('Object batch deadline');
      assertWriteContext(context);
      startedOperations += 1;
      const outcome = await operation.run();
      validateOperationOutcome(operation, outcome);
      outcomes.push(outcome);
      if (!outcome.succeeded) throw new FailedObjectOperationError('对象操作已明确失败。');
    }
    assertWriteContext(context);
    const elapsed = performanceElapsed(started);
    latestCommand.value = command;
    latestResult.value = resultFromOutcomes(outcomes, elapsed);
    ranOnce.value = true;
    pendingOperations.value = [];
    checkedRowKeys.value = [];
    recordHistory('success', 'Object operation batch', operations.map((operation) => operation.label).join(', '), command, counts(), outcomes.length, affected(), elapsed, context);
    successRecorded = true;
    message.success(`Committed ${outcomes.length} object action${outcomes.length === 1 ? '' : 's'}.`);
    await refreshAll();
    if (isCurrentContext(context)) emit('refreshSchema');
  } catch (error) {
    // 已证实的写终态不因后续只读刷新失败而改写或重复记录。
    if (successRecorded) return;
    const elapsed = performanceElapsed(started);
    const msg = errorToMessage(error, '提交对象桶操作失败');
    if (!isCurrentContext(context)) { recordHistory('unknown', 'Object operation batch', 'confirm', command, `结果未知，请核对原目标；不要重放审批。 ${counts()}`, outcomes.length, affected(), elapsed, context, 'unknown'); return; }
    if (isPermissionError(error)) {
      const ticket = beginRead('write-denial'); readError(error, ticket, '提交对象桶操作失败');
      recordHistory('error', 'Object operation batch', 'confirm', command, `当前身份没有 Object 写入权限。 ${counts()}`, outcomes.length, affected(), elapsed, context);
      return;
    }
    const unknown = isUnknownOperationError(error);
    const safeMessage = unknown ? '结果未知，请核对原目标；不要重放审批。' : msg;
    errorMsg.value = safeMessage;
    latestCommand.value = command;
    latestResult.value = errorResult(safeMessage);
    if (unknown && latestResult.value.error) latestResult.value.error.code = 'OBJECT_UNKNOWN_TERMINAL';
    ranOnce.value = true;
    recordHistory(unknown ? 'unknown' : 'error', 'Object operation batch', 'confirm', command, `${safeMessage} ${counts()}`, outcomes.length, affected(), elapsed, context, unknown ? 'unknown' : undefined);
  } finally {
    if (isCurrentContext(context)) confirmBusy.value = false;
  }
}

function validateOperationOutcome(operation: PendingOperation, outcome: OperationOutcome): void {
  if (!outcome || typeof outcome !== 'object' || typeof outcome.action !== 'string' || !outcome.action.trim()
    || typeof outcome.target !== 'string' || outcome.target !== operation.expectedTarget
    || typeof outcome.succeeded !== 'boolean' || !Number.isSafeInteger(outcome.affected) || outcome.affected < 0
    || typeof outcome.detail !== 'string' || !outcome.detail.trim()) {
    throw new UnknownObjectOperationError(`Object operation ${operation.id} returned an invalid terminal.`);
  }
}

function validateObjectInfoResponse(value: unknown, bucket: string, key: string): void {
  const response = value as Partial<ObjectInfoResponse> | null;
  if (!response || response.bucket !== bucket || response.key !== key || !nonemptyString(response.versionId)
    || !nonemptyString(response.contentType) || response.isDeleteMarker !== false || !Number.isSafeInteger(response.sizeBytes)
    || (response.sizeBytes ?? -1) < 0 || !nonemptyString(response.eTag) || !nonemptyString(response.sha256)
    || !validTerminalDate(response.createdUtc) || !validTerminalDate(response.updatedUtc)) throw new UnknownObjectOperationError('Invalid object terminal.');
}

function validateBucketResponse(value: unknown, bucket: string): void {
  const response = value as { name?: unknown; createdUtc?: unknown; updatedUtc?: unknown } | null;
  if (!response || response.name !== bucket || typeof response.createdUtc !== 'string' || typeof response.updatedUtc !== 'string') throw new UnknownObjectOperationError('Invalid bucket terminal.');
}

function validateBucketFieldResponse(value: unknown, bucket: string): void {
  const response = value as { bucket?: unknown } | null;
  if (!response || response.bucket !== bucket) throw new UnknownObjectOperationError('Invalid bucket terminal.');
}

function validateBucketSetterResponse(value: unknown, bucket: string, expected: Record<string, unknown>): void {
  validateBucketFieldResponse(value, bucket);
  const response = value as Record<string, unknown>;
  const fields = Object.entries(expected);
  if (!validTerminalDate(response.updatedUtc) || fields.length > 16) throw new UnknownObjectOperationError('Invalid bucket setter terminal.');
  const deadline = Date.now() + 1000;
  for (let index = 0; index < fields.length; index += 1) {
    const [name, expectedValue] = fields[index]!;
    // ServerJsonContext 的 WhenWritingNull 会省略真实 nullable null；非 null 回显必须存在。
    if (Date.now() >= deadline || (expectedValue === null ? response[name] != null
      : !Object.prototype.hasOwnProperty.call(response, name) || response[name] !== expectedValue)) throw new UnknownObjectOperationError('Invalid bucket setter terminal.');
  }
}

function nonemptyString(value: unknown): value is string { return typeof value === 'string' && value.trim().length > 0; }
function validTerminalDate(value: unknown): boolean { return nonemptyString(value) && Number.isFinite(Date.parse(value)); }
function validCount(value: unknown): boolean { return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0; }

function validateDeleteManyResponse(value: unknown, bucket: string, keys: readonly string[]): { affected: number; failures: number } {
  const response = value as { bucket?: unknown; deleted?: unknown } | null;
  if (!response || response.bucket !== bucket || !Array.isArray(response.deleted) || keys.length > 1000
    || response.deleted.length !== keys.length) throw new UnknownObjectOperationError('Invalid delete terminal.');
  const expected = new Set(keys);
  if (expected.size !== keys.length) throw new UnknownObjectOperationError('Invalid approved delete targets.');
  let affected = 0; let failures = 0;
  const deadline = Date.now() + 1000;
  for (let index = 0; index < response.deleted.length; index += 1) {
    const item = response.deleted[index] as { key?: unknown; versionId?: unknown; deleteMarker?: unknown; errorCode?: unknown; errorMessage?: unknown } | null;
    if (Date.now() >= deadline || !item || typeof item.key !== 'string' || !expected.delete(item.key)
      || typeof item.versionId !== 'string' || typeof item.deleteMarker !== 'boolean') throw new UnknownObjectOperationError('Invalid delete item terminal.');
    if (item.errorCode != null) {
      if (!nonemptyString(item.errorCode) || !nonemptyString(item.errorMessage) || item.deleteMarker !== false) throw new UnknownObjectOperationError('Invalid failed delete terminal.');
      failures += 1;
    } else {
      if (!nonemptyString(item.versionId) || item.deleteMarker !== true || item.errorMessage != null) throw new UnknownObjectOperationError('Invalid successful delete terminal.');
      affected += 1;
    }
  }
  if (expected.size !== 0) throw new UnknownObjectOperationError('Missing delete terminal.');
  return { affected, failures };
}

function validatePresignResponse(value: unknown, bucket: string, key: string, method: string): void {
  const response = value as { bucket?: unknown; key?: unknown; url?: unknown; method?: unknown; expiresUtc?: unknown } | null;
  if (!response || response.bucket !== bucket || response.key !== key || typeof response.url !== 'string' || !response.url.trim()
    || response.method !== method || !validTerminalDate(response.expiresUtc)) throw new UnknownObjectOperationError('Invalid presign terminal.');
}

function validateCopyResponse(value: unknown): void {
  const response = value as { eTag?: unknown; sha256?: unknown; versionId?: unknown } | null;
  if (!response || typeof response.eTag !== 'string' || !response.eTag.trim() || typeof response.sha256 !== 'string'
    || !response.sha256.trim() || typeof response.versionId !== 'string' || !response.versionId.trim()) throw new UnknownObjectOperationError('Invalid copy terminal.');
}

function isUnknownOperationError(error: unknown): boolean {
  if (error instanceof UnknownObjectOperationError) return true;
  if (error instanceof FailedObjectOperationError) return false;
  const code = (error as { code?: unknown } | null)?.code;
  if (code === 'ERR_CANCELED' || code === 'ECONNABORTED' || code === 'ETIMEDOUT' || code === 'OBJECT_UNKNOWN_TERMINAL') return true;
  const response = (error as { response?: { status?: unknown } } | null)?.response;
  return !response || response.status === 408 || (typeof response.status === 'number' && response.status >= 500);
}

function clearPendingOperations(): void {
  pendingOperations.value = [];
}
function assertWriteContext(context: ObjectContext): void { if (!isCurrentContext(context) || readOnly.value) throw new Error('Object write context changed'); }

function openHistoryEntry(entry: WorkbenchHistoryEntry): void {
  latestCommand.value = entry.command;
}

function onUploadFileChange(event: Event): void {
  const input = event.target as HTMLInputElement;
  const pickerContext = uploadPickerContext; uploadPickerContext = null;
  if (!canWrite.value || pickerContext && !isCurrentContext(pickerContext)) { input.value = ''; return; }
  uploadFile.value = input.files?.[0] ?? null;
  if (uploadFile.value) {
    uploadKey.value ||= currentPrefix.value + uploadFile.value.name;
    uploadContentType.value = uploadFile.value.type || uploadContentType.value;
  }
}

async function pickUploadFile(): Promise<void> {
  if (!canWrite.value) return;
  const context = captureContext();
  const bridge = currentStudioNativeBridge();
  if (!bridge) {
    uploadPickerContext = context;
    uploadFileInput.value?.click();
    return;
  }
  try {
    const selected = await bridge.openBinaryFile({ title: '选择要上传的对象文件' });
    if (!isCurrentContext(context) || !canWrite.value || selected.canceled || !selected.content) return;
    const name = selected.fileName || 'upload.bin';
    uploadFile.value = new File([selected.content], name, { type: selected.content.type || 'application/octet-stream' });
    uploadKey.value ||= currentPrefix.value + name;
    uploadContentType.value = uploadFile.value.type || uploadContentType.value;
  } catch (error) {
    if (isCurrentContext(context) && canWrite.value) errorMsg.value = errorToMessage(error, '打开对象文件失败');
  }
}

function onMultipartFileChange(event: Event): void {
  const input = event.target as HTMLInputElement;
  const pickerContext = multipartPickerContext; multipartPickerContext = null;
  if (!canWrite.value || pickerContext && !isCurrentContext(pickerContext)) { input.value = ''; return; }
  multipartFile.value = input.files?.[0] ?? null;
}

async function pickMultipartFile(): Promise<void> {
  if (!canWrite.value) return;
  const context = captureContext();
  const bridge = currentStudioNativeBridge();
  if (!bridge) {
    multipartPickerContext = context;
    multipartFileInput.value?.click();
    return;
  }
  try {
    const selected = await bridge.openBinaryFile({ title: '选择 Multipart 分片文件' });
    if (!isCurrentContext(context) || !canWrite.value || selected.canceled || !selected.content) return;
    multipartFile.value = new File(
      [selected.content],
      selected.fileName || `part-${multipartPartNumber.value ?? 1}.bin`,
      { type: selected.content.type || 'application/octet-stream' },
    );
  } catch (error) {
    if (isCurrentContext(context) && canWrite.value) errorMsg.value = errorToMessage(error, '打开分片文件失败');
  }
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

function mergeMultipartSessions(
  existing: MultipartUploadSessionResponse[],
  incoming: MultipartUploadSessionResponse[],
): MultipartUploadSessionResponse[] {
  const map = new Map(existing.map((session) => [session.upload.uploadId, session]));
  for (const session of incoming) map.set(session.upload.uploadId, session);
  return [...map.values()].sort((left, right) =>
    right.upload.initiatedUtc.localeCompare(left.upload.initiatedUtc));
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

function resultFromSemanticHits(hits: ImageSearchHit[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['id', 'score', 'distance', 'bucket', 'key', 'contentType', 'sizeBytes', 'updatedUtc'],
    rows: hits.map((hit) => [
      hit.id,
      hit.score,
      hit.distance,
      hit.sourceBucket ?? null,
      hit.sourceKey ?? hit.fileName ?? null,
      hit.contentType,
      hit.sizeBytes,
      hit.updatedUtc,
    ]),
    end: { type: 'end', rowCount: hits.length, recordsAffected: -1, elapsedMs },
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

function errorToMessage(error: unknown, fallback: string): string { return isPermissionError(error) ? '当前身份没有 Object 访问权限。' : fallback; }

async function copyText(text: string, success: string): Promise<void> {
  const context = captureContext(); if (!isCurrentContext(context)) return;
  try {
    await navigator.clipboard.writeText(text);
    if (isCurrentContext(context)) message.success(success);
  } catch {
    if (isCurrentContext(context)) message.warning('复制失败。');
  }
}

function recordHistory(status: 'success' | 'error' | 'unknown', title: string, action: string, command: string, summary: string,
  rowCount: number, recordsAffected: number, elapsedMs: number, context = captureContext(), completeness?: 'complete' | 'truncated' | 'unknown'): void {
  history.record({ kind: action === 'browse' || action === 'search' ? 'query' : 'operation', status, title,
    target: context.bucket, database: context.database, connectionId: context.connectionId, connectionName: context.connectionName,
    model: 'object', action, command, summary, rowCount, recordsAffected, elapsedMs, completeness });
}
function approvalInputKey(): string {
  return JSON.stringify([selectionIdentity(), currentPrefix.value, checkedRowKeys.value, newBucketName.value, newBucketPurpose.value,
    uploadKey.value, uploadContentType.value, uploadText.value, metadataText.value, tagsText.value, selectedTagsText.value, copyTargetKey.value,
    presignMethod.value, presignMinutes.value, multipartKey.value, multipartContentType.value, multipartExpiresHours.value, multipartPartNumber.value,
    activeMultipart.value?.uploadId, multipartParts.value, legalHoldEnabled.value, legalHoldReason.value, policyDraft.value,
    lifecycleDraft, retentionDraft, quotaDraft, semanticOptionsDraft]);
}
function invalidateContext(): void {
  contextRevision += 1;
  for (const ticket of [...readTickets.values()].slice(0, 32)) ticket.controller.abort();
  readTickets.clear(); clearRows(); clearBucketMetadata(); clearPendingOperations();
  loading.value = loadingObjects.value = loadingPreview.value = loadingAudit.value = loadingProcessing.value = loadingMultipartSessions.value = searchingSemantic.value = confirmBusy.value = false;
  latestResult.value = null; latestCommand.value = ''; ranOnce.value = false; errorMsg.value = ''; rangeNotice.value = ''; listIncomplete.value = false; usedListTokens.clear();
  presignedUrl.value = ''; newBucketName.value = ''; newBucketPurpose.value = ''; uploadKey.value = ''; uploadFile.value = null; uploadText.value = '';
  metadataText.value = ''; tagsText.value = ''; selectedTagsText.value = ''; copyTargetKey.value = ''; multipartKey.value = ''; multipartFile.value = null;
  legalHoldEnabled.value = false; legalHoldReason.value = ''; similarImageId.value = ''; semanticText.value = ''; semanticImageFile.value = null;
  semanticFilterPrefix.value = ''; semanticFilterContentType.value = ''; semanticMetadataText.value = ''; semanticTagsText.value = '';
}
async function loadSelection(): Promise<void> {
  const row = selectedObject.value; if (!canRead.value || !row) return;
  const ticket = beginRead('selection', true); const { api, database } = ticket.context; const signal = ticket.controller.signal;
  try {
    const [tags, hold] = await readGroup([getObjectTags(api, database, row.bucket, row.key, signal), getObjectLegalHold(api, database, row.bucket, row.key, row.versionId, signal)] as const, ticket);
    if (!currentRead(ticket)) return;
    if (hold.bucket !== row.bucket || hold.key !== row.key || hold.versionId !== row.versionId) throw new Error('Wrong legal hold target');
    selectedTagsText.value = formatMap(tags); legalHoldEnabled.value = hold.enabled; legalHoldReason.value = hold.reason ?? '';
  } catch (error) { readError(error, ticket, '读取对象详情失败'); }
  finally { finishRead(ticket); }
}
watch(() => props.buckets, (buckets) => {
  if (canRead.value && buckets.length > 0 && localBuckets.value.length === 0) localBuckets.value = buckets.map((bucket) => ({ name: bucket.name, purpose: bucket.purpose, createdUtc: bucket.createdUtc, updatedUtc: bucket.updatedUtc }));
}, { immediate: true, flush: 'sync' });
watch(pendingOperations, (operations) => { approvalContext = operations.length ? captureContext() : null; approvalInputs = operations.length ? approvalInputKey() : ''; }, { flush: 'sync' });
watch(approvalInputKey, clearPendingOperations, { flush: 'sync' });
watch([uploadFile, multipartFile], clearPendingOperations, { flush: 'sync' });
watch(() => [props.targetDb, activeBucket.value, connections.activeProfileId, connections.activeBaseUrl, auth.state, auth.state?.token,
  auth.api, auth.api.defaults.baseURL, auth.api.defaults.headers?.common?.Authorization, props.permissionDenied, readOnly.value], (_values, previous) => {
  invalidateContext(); currentPrefix.value = ''; prefixInput.value = '';
  if (previous && previous[0] !== props.targetDb) localBuckets.value = [];
  const denied = deniedContext;
  const changed = denied && (props.targetDb && props.targetDb !== denied.database || activeBucket.value && activeBucket.value !== denied.bucket
    || connections.activeProfileId && connections.activeProfileId !== denied.connectionId || connections.activeBaseUrl && connections.activeBaseUrl !== denied.profileBaseUrl
    || auth.api.defaults.baseURL && auth.api.defaults.baseURL !== denied.baseUrl);
  if (changed) { permissionLocked.value = false; deniedContext = null; }
  if (permissionDenied.value) { localBuckets.value = []; errorMsg.value = '当前身份没有 Object 访问权限。'; }
  const revision = contextRevision;
  void nextTick().then(() => { if (canRead.value && revision === contextRevision) return refreshAll(); });
}, { immediate: true, flush: 'sync' });
watch(selectionIdentity, () => {
  for (const ticket of [...readTickets.values()].slice(0, 32)) if (ticket.selected) cancelRead(ticket.slot);
  loadingPreview.value = loadingProcessing.value = false;
  selectedTagsText.value = ''; copyTargetKey.value = ''; previewText.value = ''; presignedUrl.value = ''; versions.value = [];
  legalHoldEnabled.value = false; legalHoldReason.value = ''; processingStatus.value = null; clearSelectedThumbnail(); rangeNotice.value = '';
  const row = selectedObject.value;
  if (row && canRead.value) { selectedTagsText.value = formatMap(row.tags); copyTargetKey.value = `${row.key}.copy`; uploadKey.value = row.key; multipartKey.value = row.key;
    void loadSelection(); void loadVersions(row.key); void loadSelectedProcessing(); }
}, { flush: 'sync' });
watch([currentPrefix, listLimit], () => { cancelRead('objects'); cancelRead('audit'); loadingObjects.value = loadingAudit.value = false; cursor.value = null; hasMore.value = false; }, { flush: 'sync' });
watch([rangeStart, rangeLength, previewMode], () => { cancelRead('range'); loadingPreview.value = false; previewText.value = ''; rangeNotice.value = ''; }, { flush: 'sync' });
watch([auditPrefix, auditMaxEntries], () => { cancelRead('audit'); loadingAudit.value = false; }, { flush: 'sync' });
watch(() => JSON.stringify([semanticSearchMode.value, semanticText.value, similarImageId.value, semanticTopK.value, semanticMinScore.value,
  semanticFilterBucket.value, semanticFilterPrefix.value, semanticFilterContentType.value, semanticMetadataText.value, semanticTagsText.value, semanticExplain.value]), () => {
  cancelRead('search'); cancelRead('images'); searchingSemantic.value = false; semanticSearchResult.value = null; clearSemanticHitUrls();
}, { flush: 'sync' });
watch(semanticImageFile, () => { cancelRead('search'); cancelRead('images'); searchingSemantic.value = false; clearSemanticHitUrls(); }, { flush: 'sync' });
onBeforeUnmount(() => { disposed = true; invalidateContext(); });
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

.object-semantic-section {
  gap: 12px;
}

.object-semantic-switches {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
}

.object-semantic-switches label {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  color: var(--sndb-ink-strong);
  font-size: 12px;
  font-weight: 700;
}

.object-semantic-runtime {
  font-size: 11px;
}

.object-semantic-current {
  display: grid;
  grid-template-columns: 58px minmax(0, 1fr);
  gap: 9px;
  align-items: center;
}

.object-thumbnail-frame,
.object-semantic-hit__preview {
  display: grid;
  place-items: center;
  overflow: hidden;
  border: 1px solid rgba(15, 23, 42, 0.12);
  border-radius: 5px;
  background: #f1f5f5;
  color: var(--sndb-ink-soft);
}

.object-thumbnail-frame {
  width: 58px;
  height: 58px;
}

.object-thumbnail-frame img,
.object-semantic-hit__preview img {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.object-semantic-current__meta {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 2px;
  color: var(--sndb-ink-soft);
  font-size: 11px;
}

.object-semantic-current__meta strong,
.object-semantic-id {
  overflow: hidden;
  color: var(--sndb-ink-strong);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.object-semantic-current__actions {
  grid-column: 1 / -1;
}

.object-semantic-search {
  display: flex;
  flex-direction: column;
  gap: 9px;
  padding: 10px 0;
  border-top: 1px solid rgba(15, 23, 42, 0.1);
}

.object-semantic-search__head,
.object-semantic-result-summary {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.object-semantic-query-options {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
}

.object-semantic-results {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-height: 0;
  padding-top: 4px;
}

.object-semantic-result-summary {
  justify-content: flex-start;
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
}

.object-semantic-result-summary span {
  padding-right: 8px;
  border-right: 1px solid rgba(15, 23, 42, 0.14);
}

.object-semantic-result-summary span:last-child {
  border-right: 0;
}

.object-semantic-hit {
  display: grid;
  grid-template-columns: 38px minmax(0, 1fr) auto 16px;
  gap: 8px;
  align-items: center;
  width: 100%;
  padding: 6px 4px;
  border: 0;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: transparent;
  color: inherit;
  text-align: left;
  cursor: pointer;
}

.object-semantic-hit:hover {
  background: rgba(13, 59, 102, 0.06);
}

.object-semantic-hit__preview {
  width: 38px;
  height: 38px;
}

.object-semantic-hit__body {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 2px;
}

.object-semantic-hit__body strong,
.object-semantic-hit__body small {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.object-semantic-hit__body strong {
  color: var(--sndb-ink-strong);
  font-size: 12px;
}

.object-semantic-hit__body small {
  color: var(--sndb-ink-soft);
  font-size: 10px;
}

.object-semantic-hit__score {
  color: var(--sndb-brand);
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  font-weight: 800;
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
  display: none;
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

  .object-body.is-focused {
    grid-template-columns: minmax(0, 1fr);
    padding: 10px;
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

  .object-semantic-query-options {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 480px) {
  .object-semantic-section {
    padding-right: 56px;
  }
}
</style>
