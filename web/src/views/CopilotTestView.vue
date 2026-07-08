<template>
  <div class="copilot-test">
    <div class="copilot-test__head">
      <div>
        <h2>Copilot 测试</h2>
        <p>对比 embedding 与知识库召回效果，先验证离线问答的底座。</p>
      </div>
      <n-space>
        <n-button :loading="loadingStatus" @click="loadStatus">刷新状态</n-button>
        <n-button type="primary" :loading="ingesting" @click="runIngest(false)">增量摄入</n-button>
        <n-button :loading="dryRunning" @click="runIngest(true)">仅扫描</n-button>
        <n-button :loading="reloadingSkills" @click="reloadSkills">Reload 技能</n-button>
      </n-space>
    </div>

    <n-alert v-if="errorMsg" type="error" closable @close="errorMsg = ''">
      {{ errorMsg }}
    </n-alert>
    <n-alert v-if="infoMsg" type="success" closable @close="infoMsg = ''">
      {{ infoMsg }}
    </n-alert>

    <n-grid :cols="4" :x-gap="12" :y-gap="12" responsive="screen">
      <n-gi>
        <n-card size="small">
          <n-statistic label="Embedding" :value="providerLabel" />
          <n-tag v-if="status?.embeddingFallback" size="small" type="warning">fallback</n-tag>
        </n-card>
      </n-gi>
      <n-gi>
        <n-card size="small">
          <n-statistic label="向量维度" :value="status?.vectorDimension ?? '-'" />
        </n-card>
      </n-gi>
      <n-gi>
        <n-card size="small">
          <n-statistic label="已索引文件" :value="status?.indexedFiles ?? 0" />
        </n-card>
      </n-gi>
      <n-gi>
        <n-card size="small">
          <n-statistic label="文档切片" :value="status?.indexedChunks ?? 0" />
        </n-card>
      </n-gi>
    </n-grid>

    <n-card size="small" title="测试问题" :bordered="false">
      <n-space vertical :size="12">
        <n-space>
          <n-button
            v-for="item in samples"
            :key="item"
            size="small"
            secondary
            @click="query = item"
          >
            {{ item }}
          </n-button>
        </n-space>
        <n-input
          v-model:value="query"
          type="textarea"
          :autosize="{ minRows: 2, maxRows: 4 }"
          placeholder="输入要测试的问题，例如：如何用 knn 做向量检索？"
          @keydown.ctrl.enter.prevent="runSearch"
        />
        <n-space align="center" justify="space-between">
          <n-space align="center">
            <span class="copilot-test__label">Top K</span>
            <n-input-number v-model:value="topK" :min="1" :max="20" size="small" style="width: 110px" />
          </n-space>
          <n-space>
            <n-button :loading="searching" :disabled="!query.trim()" @click="runSearch">检索文档</n-button>
            <n-button :loading="searchingSkills" :disabled="!query.trim()" @click="runSkillSearch">检索技能</n-button>
          </n-space>
        </n-space>
      </n-space>
    </n-card>

    <section class="copilot-test__results">
      <div class="copilot-test__panel">
        <div class="copilot-test__panel-head">
          <strong>文档召回</strong>
          <span v-if="docsElapsed">{{ docsElapsed }}</span>
        </div>
        <n-empty v-if="docHits.length === 0" description="还没有文档检索结果" />
        <article v-for="hit in docHits" :key="`${hit.source}-${hit.section}-${hit.score}`" class="copilot-test__hit">
          <div class="copilot-test__hit-head">
            <span>{{ hit.title || hit.source }}</span>
            <n-tag size="small">{{ formatScore(hit.score) }}</n-tag>
          </div>
          <div class="copilot-test__meta">{{ hit.source }} · {{ hit.section || '默认段落' }}</div>
          <p>{{ hit.content }}</p>
        </article>
      </div>

      <div class="copilot-test__panel">
        <div class="copilot-test__panel-head">
          <strong>技能召回</strong>
          <span v-if="skillsElapsed">{{ skillsElapsed }}</span>
        </div>
        <n-empty v-if="skillHits.length === 0" description="还没有技能检索结果" />
        <article v-for="hit in skillHits" :key="hit.name" class="copilot-test__hit">
          <div class="copilot-test__hit-head">
            <span>{{ hit.name }}</span>
            <n-tag size="small">{{ formatScore(hit.score) }}</n-tag>
          </div>
          <p>{{ hit.description }}</p>
          <div class="copilot-test__tags">
            <n-tag v-for="tool in hit.requiresTools" :key="tool" size="small" type="info">{{ tool }}</n-tag>
          </div>
        </article>
      </div>
    </section>

    <n-card size="small" title="文档根目录" :bordered="false">
      <n-list v-if="status?.docsRoots.length" size="small">
        <n-list-item v-for="root in status.docsRoots" :key="root">
          <n-text code>{{ root }}</n-text>
        </n-list-item>
      </n-list>
      <n-empty v-else description="未读取到文档根目录" />
    </n-card>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import {
  NAlert,
  NButton,
  NCard,
  NEmpty,
  NGi,
  NGrid,
  NInput,
  NInputNumber,
  NList,
  NListItem,
  NSpace,
  NStatistic,
  NTag,
  NText,
} from 'naive-ui';
import { useAuthStore } from '@/stores/auth';
import {
  fetchCopilotKnowledgeStatus,
  ingestCopilotDocs,
  reloadCopilotSkills,
  searchCopilotDocs,
  searchCopilotSkills,
  type CopilotKnowledgeStatusResponse,
  type CopilotSearchHit,
  type CopilotSkillsSearchHit,
} from '@/api/copilotKnowledge';

const auth = useAuthStore();

const status = ref<CopilotKnowledgeStatusResponse | null>(null);
const loadingStatus = ref(false);
const ingesting = ref(false);
const dryRunning = ref(false);
const searching = ref(false);
const searchingSkills = ref(false);
const reloadingSkills = ref(false);
const errorMsg = ref('');
const infoMsg = ref('');
const query = ref('如何用 knn 做向量检索？');
const topK = ref(5);
const docHits = ref<CopilotSearchHit[]>([]);
const skillHits = ref<CopilotSkillsSearchHit[]>([]);
const docsElapsedMs = ref<number | null>(null);
const skillsElapsedMs = ref<number | null>(null);

const samples = [
  '如何用 knn 做向量检索？',
  'SonnetDB 支持哪些 SQL 聚合函数？',
  '怎么创建 measurement 并写入设备数据？',
  'Copilot 如何保护写入操作？',
];

const providerLabel = computed(() => {
  if (!status.value) return '-';
  return status.value.embeddingProvider || 'unknown';
});

const docsElapsed = computed(() => docsElapsedMs.value === null ? '' : `${docsElapsedMs.value.toFixed(1)} ms`);
const skillsElapsed = computed(() => skillsElapsedMs.value === null ? '' : `${skillsElapsedMs.value.toFixed(1)} ms`);

async function loadStatus(): Promise<void> {
  loadingStatus.value = true;
  errorMsg.value = '';
  try {
    status.value = await fetchCopilotKnowledgeStatus(auth.api);
  } catch (e: unknown) {
    errorMsg.value = `读取知识库状态失败：${formatError(e)}`;
  } finally {
    loadingStatus.value = false;
  }
}

async function runIngest(dryRun: boolean): Promise<void> {
  if (dryRun) dryRunning.value = true;
  else ingesting.value = true;
  errorMsg.value = '';
  infoMsg.value = '';
  try {
    const result = await ingestCopilotDocs(auth.api, { dryRun });
    infoMsg.value = dryRun
      ? `扫描完成：${result.scannedFiles} 个文件，预计写入 ${result.writtenChunks} 个切片。`
      : `摄入完成：写入 ${result.writtenChunks} 个切片，跳过 ${result.skippedFiles} 个文件。`;
    await loadStatus();
  } catch (e: unknown) {
    errorMsg.value = `${dryRun ? '扫描' : '摄入'}失败：${formatError(e)}`;
  } finally {
    if (dryRun) dryRunning.value = false;
    else ingesting.value = false;
  }
}

async function runSearch(): Promise<void> {
  const text = query.value.trim();
  if (!text) return;
  searching.value = true;
  errorMsg.value = '';
  try {
    const result = await searchCopilotDocs(auth.api, text, topK.value ?? 5);
    docHits.value = result.hits;
    docsElapsedMs.value = result.elapsedMilliseconds;
  } catch (e: unknown) {
    errorMsg.value = `文档检索失败：${formatError(e)}`;
  } finally {
    searching.value = false;
  }
}

async function runSkillSearch(): Promise<void> {
  const text = query.value.trim();
  if (!text) return;
  searchingSkills.value = true;
  errorMsg.value = '';
  try {
    const result = await searchCopilotSkills(auth.api, text, topK.value ?? 5);
    skillHits.value = result.hits;
    skillsElapsedMs.value = result.elapsedMilliseconds;
  } catch (e: unknown) {
    errorMsg.value = `技能检索失败：${formatError(e)}`;
  } finally {
    searchingSkills.value = false;
  }
}

async function reloadSkills(): Promise<void> {
  reloadingSkills.value = true;
  errorMsg.value = '';
  infoMsg.value = '';
  try {
    const result = await reloadCopilotSkills(auth.api);
    infoMsg.value = `技能库已加载：扫描 ${result.scannedSkills} 个，写入 ${result.indexedSkills} 个，跳过 ${result.skippedSkills} 个。`;
    await loadStatus();
  } catch (e: unknown) {
    errorMsg.value = `Reload 技能失败：${formatError(e)}`;
  } finally {
    reloadingSkills.value = false;
  }
}

function formatScore(score: number): string {
  return Number.isFinite(score) ? score.toFixed(4) : '-';
}

function formatError(e: unknown): string {
  if (e instanceof Error) return e.message;
  if (typeof e === 'object' && e !== null && 'message' in e) {
    return String((e as { message?: unknown }).message);
  }
  return String(e);
}

onMounted(() => {
  void loadStatus();
});
</script>

<style scoped>
.copilot-test {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.copilot-test__head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 20px;
}

.copilot-test__head h2 {
  margin: 0;
  font-size: 24px;
  font-weight: 700;
}

.copilot-test__head p {
  margin: 6px 0 0;
  color: var(--n-text-color-3);
}

.copilot-test__label {
  color: var(--n-text-color-3);
  font-size: 12px;
}

.copilot-test__results {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(320px, 0.55fr);
  gap: 16px;
}

.copilot-test__panel {
  min-width: 0;
  padding: 16px;
  border: 1px solid var(--n-border-color);
  border-radius: 8px;
  background: var(--n-color);
}

.copilot-test__panel-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
}

.copilot-test__panel-head span {
  color: var(--n-text-color-3);
  font-size: 12px;
}

.copilot-test__hit {
  padding: 12px 0;
  border-top: 1px solid var(--n-border-color);
}

.copilot-test__hit:first-of-type {
  border-top: 0;
}

.copilot-test__hit-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  font-weight: 650;
}

.copilot-test__meta {
  margin-top: 4px;
  color: var(--n-text-color-3);
  font-size: 12px;
  overflow-wrap: anywhere;
}

.copilot-test__hit p {
  margin: 8px 0 0;
  color: var(--n-text-color-2);
  line-height: 1.65;
  white-space: pre-wrap;
}

.copilot-test__tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 10px;
}

@media (max-width: 960px) {
  .copilot-test__head,
  .copilot-test__results {
    grid-template-columns: 1fr;
  }

  .copilot-test__head {
    flex-direction: column;
  }
}
</style>
