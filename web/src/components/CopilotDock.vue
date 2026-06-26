<template>
  <!-- 折叠态：右下角圆形按钮 -->
  <div v-if="!visible" class="copilot-fab" @click="open" title="打开 Copilot 助手">
    <span class="copilot-fab-icon">AI</span>
  </div>

  <!-- 展开态：浮窗面板 -->
  <div
    v-else
    ref="dockEl"
    class="copilot-dock"
    :class="{ 'is-fullscreen': fullscreen }"
    :style="dockStyle"
  >
    <header class="copilot-dock__header" @mousedown="onDragStart">
      <div class="copilot-dock__title">
        <span class="copilot-dock__avatar">AI</span>
        <strong>Copilot</strong>
        <n-tag v-if="pageContext" size="tiny" type="info">{{ pageContext.routeLabel }}</n-tag>
      </div>
      <n-space size="small" :wrap="false">
        <n-button text size="tiny" title="新建会话" @click="onNewSession">+ 新会话</n-button>
        <n-popover trigger="click" placement="bottom-end" :width="300" :show-arrow="false" :to="dockEl ?? 'body'">
          <template #trigger>
            <n-button text size="tiny" title="会话历史">历史</n-button>
          </template>
          <div class="copilot-dock__sessions" @mousedown.stop>
            <div class="copilot-dock__sessions-head">
              <n-text strong style="font-size: 12px">最近会话</n-text>
              <n-space size="small">
                <n-button size="tiny" type="primary" @click="onNewSession">+ 新会话</n-button>
                <n-popconfirm @positive-click="onClearAll" :to="dockEl ?? 'body'">
                  <template #trigger>
                    <n-button size="tiny" quaternary type="error" :disabled="sessions.recent.length === 0">清空</n-button>
                  </template>
                  确认清空全部本地会话历史？此操作不可恢复。
                </n-popconfirm>
              </n-space>
            </div>
            <div v-if="sessions.recent.length === 0" class="copilot-dock__sessions-empty">
              暂无会话，发送第一条消息即可创建。
            </div>
            <ul v-else class="copilot-dock__sessions-list">
              <li
                v-for="s in sessions.recent"
                :key="s.id"
                :class="{ 'is-active': s.id === sessions.currentId }"
                @click="onSwitchSession(s.id)"
              >
                <div class="copilot-dock__sessions-item-main">
                  <div class="copilot-dock__sessions-item-title" :title="s.title">{{ s.title }}</div>
                  <div class="copilot-dock__sessions-item-meta">
                    {{ s.db || '(无数据库)' }} · {{ Math.floor(s.messages.length / 2) }} 轮 · {{ formatRelative(s.updatedAt) }}
                  </div>
                </div>
                <n-space size="small" :wrap="false" class="copilot-dock__sessions-item-actions">
                  <n-button quaternary size="tiny" @click.stop="onRenameSession(s)" title="重命名">✎</n-button>
                  <n-button quaternary size="tiny" type="error" @click.stop="onRemoveSession(s.id)" title="删除">×</n-button>
                </n-space>
              </li>
            </ul>
          </div>
        </n-popover>
        <n-button text size="tiny" @click="fullscreen = !fullscreen" :title="fullscreen ? '还原' : '全屏'">{{ fullscreen ? '⊟' : '⊕' }}</n-button>
        <n-button text size="tiny" @click="close" title="收起到角标">×</n-button>
      </n-space>
    </header>

    <!-- 数据库选择 已移除：数据库由 AI 根据上下文自动推断，无需手动选择 -->

    <!-- M7: 权限模式选择 -->
    <section class="copilot-dock__perm">
      <!-- 只读模式：点击 tag 展开内联确认，避免 popconfirm teleport 被遮挡 -->
      <template v-if="permissionMode === 'read-only'">
        <n-tag
          size="tiny"
          type="success"
          :bordered="false"
          style="cursor: pointer"
          title="点击切换为读写模式"
          @click="permConfirmVisible = !permConfirmVisible"
        >只读模式</n-tag>
        <!-- 内联确认条：直接渲染在 dock 内，不 teleport，不会被遮挡 -->
        <transition name="perm-confirm">
          <div v-if="permConfirmVisible" class="copilot-dock__perm-confirm">
            <n-text depth="3" style="font-size: 11px; flex: 1">
              切换后 Copilot 可执行写入语句，是否启用？
            </n-text>
            <n-button
              size="tiny"
              type="primary"
              @click="permissionMode = 'read-write'; permConfirmVisible = false"
            >启用读写</n-button>
            <n-button
              size="tiny"
              @click="permConfirmVisible = false"
            >取消</n-button>
          </div>
        </transition>
      </template>
      <n-tag
        v-else
        size="tiny"
        type="warning"
        :bordered="false"
        closable
        style="cursor: pointer"
        title="点击 × 切换回只读"
        @close="permissionMode = 'read-only'"
      >读写模式</n-tag>
      <n-text depth="3" style="font-size: 11px; margin-left: 6px">
        {{ permissionMode === 'read-only' ? 'Copilot 只能查询' : '可执行写入（仍受凭据权限约束）' }}
      </n-text>
    </section>

    <!-- M6: 页面上下文 -->
    <section v-if="pageContextSummary" class="copilot-dock__ctx">
      <n-tag
        size="tiny"
        :type="contextEnabled ? 'info' : 'default'"
        :bordered="false"
        closable
        @close="contextEnabled = false"
      >
        {{ pageContextSummary }}
      </n-tag>
      <n-button
        v-if="!contextEnabled"
        size="tiny"
        text
        type="primary"
        style="margin-left: 6px"
        @click="contextEnabled = true"
      >启用</n-button>
    </section>

    <!-- 页面感知快捷能力 -->
    <section v-if="assistantActions.length > 0" class="copilot-dock__actions">
      <n-button
        v-for="action in assistantActions"
        :key="action.key"
        size="tiny"
        secondary
        :type="action.type"
        :disabled="running || action.disabled"
        :title="action.disabledReason || action.title"
        @click="onAssistantAction(action)"
      >
        {{ action.title }}
      </n-button>
    </section>

    <!-- 消息流 -->
    <section class="copilot-dock__messages" ref="msgContainer">
      <div v-if="messages.length === 0 && !running" class="copilot-dock__empty">
        <p class="copilot-dock__empty-tip">问点什么？点击下方模板可直接填入：</p>
        <div class="copilot-dock__starters">
          <button
            v-for="s in starters"
            :key="s.title"
            class="copilot-dock__starter"
            :title="s.description"
            @click="onStarterClick(s)"
          >
            <span class="copilot-dock__starter-cat">{{ s.category }}</span>
            <span class="copilot-dock__starter-title">{{ s.title }}</span>
          </button>
        </div>
      </div>
      <div v-for="(msg, idx) in messages" :key="idx" class="copilot-dock__msg" :class="`copilot-dock__msg--${msg.role}`">
        <div class="copilot-dock__msg-role">{{ msg.role === 'user' ? '我' : 'Copilot' }}</div>
        <div class="copilot-dock__msg-body">
          <div class="copilot-dock__markdown" v-html="renderMessageMarkdown(msg.content)" />
          <div
            v-if="msg.role === 'assistant' && hasVisibleCitationsForContent(msg.content, msg.citations)"
            class="copilot-dock__citations"
          >
            <div class="copilot-dock__citations-title">引用</div>
            <div
              v-for="citation in visibleCitationsForContent(msg.content, msg.citations)"
              :key="citation.id"
              class="copilot-dock__citation"
            >
              <div class="copilot-dock__citation-head">
                <span class="copilot-dock__citation-id">{{ citation.id }}</span>
                <span>{{ citation.title || citation.source || citation.kind }}</span>
              </div>
              <div v-if="citation.source" class="copilot-dock__citation-source">{{ citation.source }}</div>
              <div v-if="citation.snippet" class="copilot-dock__citation-snippet">{{ citation.snippet }}</div>
            </div>
          </div>
        </div>
      </div>
      <div v-if="streamBuffer" class="copilot-dock__msg copilot-dock__msg--assistant">
        <div class="copilot-dock__msg-role">Copilot</div>
        <div class="copilot-dock__msg-body">
          <div class="copilot-dock__markdown" v-html="renderMessageMarkdown(streamBuffer)" />
          <span class="copilot-dock__caret" />
          <div v-if="hasVisibleCitations(streamCitations)" class="copilot-dock__citations">
            <div class="copilot-dock__citations-title">引用</div>
            <div
              v-for="citation in visibleCitations(streamCitations)"
              :key="citation.id"
              class="copilot-dock__citation"
            >
              <div class="copilot-dock__citation-head">
                <span class="copilot-dock__citation-id">{{ citation.id }}</span>
                <span>{{ citation.title || citation.source || citation.kind }}</span>
              </div>
              <div v-if="citation.source" class="copilot-dock__citation-source">{{ citation.source }}</div>
              <div v-if="citation.snippet" class="copilot-dock__citation-snippet">{{ citation.snippet }}</div>
            </div>
          </div>
        </div>
      </div>
      <div v-if="errorMsg" class="copilot-dock__error">{{ errorMsg }}</div>
    </section>

    <!-- 输入框 -->
    <footer class="copilot-dock__input">
      <n-input
        v-model:value="prompt"
        type="textarea"
        :autosize="{ minRows: 2, maxRows: 5 }"
        placeholder="向 Copilot 提问，回车发送（Shift+Enter 换行）"
        :disabled="running"
        @keydown="onKeydown"
      />
      <n-space size="small" justify="space-between" style="margin-top: 6px">
        <n-text depth="3" style="font-size: 11px">
          按 Enter 发送
        </n-text>
        <n-space size="small" :wrap="false">
          <n-button v-if="running" size="tiny" type="error" ghost @click="stop">停止</n-button>
          <n-button size="tiny" type="primary" :disabled="!prompt.trim() || running" @click="send">发送</n-button>
        </n-space>
      </n-space>
    </footer>
  </div>
</template>

<script setup lang="ts">
import { computed, h, nextTick, onMounted, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { marked, Renderer, type Tokens } from 'marked';
import {
  NButton, NInput, NPopconfirm, NPopover, NSpace, NTag, NText,
  useDialog, useMessage,
} from 'naive-ui';
import { useAuthStore } from '@/stores/auth';
import { useCopilotSessionsStore, type CopilotSession } from '@/stores/copilotSessions';
import { CONTROL_PLANE_KEY, useSqlConsoleStore } from '@/stores/sqlConsole';
import { buildClientResultSet } from '@/api/sqlMeta';
import type { SqlResultSet } from '@/api/sql';
import { listDatabases } from '@/api/server';
import { execControlPlaneSql, isValidIdentifier } from '@/api/sql';
import {
  streamCopilotChat,
  type CopilotChatEvent,
  type CopilotCitation,
  type CopilotMessage,
} from '@/api/copilot';
import { pickStarters, type CopilotStarter } from '@/copilot/starters';

const auth = useAuthStore();
const sessions = useCopilotSessionsStore();
const sqlConsole = useSqlConsoleStore();
if (!auth.isSuperuser) {
  sessions.hideControlPlaneForRegularUser();
}
const route = useRoute();
const message = useMessage();
const dialog = useDialog();

const visible = ref(false);
const fullscreen = ref(false);
const dockEl = ref<HTMLElement | null>(null);  // dock 容器引用，供 Naive UI 浮层 teleport 到 dock 内部

const dbs = ref<string[]>([]);
const selectedDb = ref<string>('');

const prompt = ref('');
/** 来自当前会话的历史消息（只读引用，写入通过 sessions store）。 */
const messages = computed<CopilotMessage[]>(() => sessions.current?.messages ?? []);
const streamBuffer = ref('');
const streamCitations = ref<CopilotCitation[]>([]);
const running = ref(false);
const errorMsg = ref('');
const abort = ref<AbortController | null>(null);

const msgContainer = ref<HTMLElement | null>(null);

// 浮窗位置（可拖拽）
const dockPos = ref({ right: 24, bottom: 24 });
const dockStyle = computed(() => fullscreen.value
  ? { right: '0', bottom: '0', top: '0', left: '0', width: 'auto', height: 'auto' }
  : { right: `${dockPos.value.right}px`, bottom: `${dockPos.value.bottom}px` });

// === M6: 页面上下文 ===
const contextEnabled = ref(true);

// === M7: 权限模式 ===
type PermissionMode = 'read-only' | 'read-write';
type CloudMode = 'sql_assist' | 'sql_analyze' | 'db_maintenance' | 'knowledge_qa';
const PERM_STORAGE_KEY = 'sndb.copilot.permission.v1';
const permissionMode = ref<PermissionMode>('read-only');
const permConfirmVisible = ref(false);  // 内联确认条显示状态
try {
  const saved = localStorage.getItem(PERM_STORAGE_KEY);
  if (saved === 'read-write') permissionMode.value = 'read-write';
} catch {
  // 忽略 localStorage 不可用
}
watch(permissionMode, (mode) => {
  try {
    localStorage.setItem(PERM_STORAGE_KEY, mode);
  } catch {
    // 忽略
  }
});

const markdownRenderer = new Renderer();
markdownRenderer.html = ({ text }: Tokens.HTML | Tokens.Tag) => escapeHtml(text);
markdownRenderer.link = function ({ href, title, tokens }: Tokens.Link): string {
  const safeHref = sanitizeMarkdownHref(href);
  const label = this.parser.parseInline(tokens);
  if (!safeHref) return label;

  const titleAttr = title ? ` title="${escapeHtml(title)}"` : '';
  return `<a href="${escapeHtml(safeHref)}"${titleAttr} target="_blank" rel="noopener noreferrer">${label}</a>`;
};
markdownRenderer.image = ({ text }: Tokens.Image): string => escapeHtml(text);

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;',
  }[ch] ?? ch));
}

function sanitizeMarkdownHref(href: string): string {
  const trimmed = href.trim();
  if (/^(https?:|mailto:)/i.test(trimmed) || trimmed.startsWith('/') || trimmed.startsWith('#')) {
    return trimmed;
  }
  return '';
}

function stripCitationMarkers(markdown: string): string {
  const fence = /```[\s\S]*?```/g;
  let lastIndex = 0;
  let output = '';
  let match: RegExpExecArray | null;

  while ((match = fence.exec(markdown)) !== null) {
    output += markdown.slice(lastIndex, match.index).replace(/\s*\[C\d+\]/g, '');
    output += match[0];
    lastIndex = match.index + match[0].length;
  }

  output += markdown.slice(lastIndex).replace(/\s*\[C\d+\]/g, '');
  return output.trim();
}

function renderMessageMarkdown(content: string): string {
  const normalized = stripCitationMarkers(content);
  return marked.parse(normalized, {
    async: false,
    breaks: true,
    renderer: markdownRenderer,
  }) as string;
}

function visibleCitations(citations: CopilotCitation[] | undefined): CopilotCitation[] {
  return (citations ?? []).filter((citation) =>
    Boolean(citation.title?.trim() || citation.source?.trim() || citation.snippet?.trim()));
}

function extractCitationIds(content: string): Set<string> {
  const ids = new Set<string>();
  const re = /\[(C\d+)\]/g;
  let match: RegExpExecArray | null;
  while ((match = re.exec(content)) !== null) {
    ids.add(match[1]);
  }
  return ids;
}

function visibleCitationsForContent(content: string, citations: CopilotCitation[] | undefined): CopilotCitation[] {
  const visible = visibleCitations(citations);
  const usedIds = extractCitationIds(content);
  if (usedIds.size === 0) return visible;
  return visible.filter((citation) => usedIds.has(citation.id));
}

function hasVisibleCitations(citations: CopilotCitation[] | undefined): boolean {
  return visibleCitations(citations).length > 0;
}

function hasVisibleCitationsForContent(content: string, citations: CopilotCitation[] | undefined): boolean {
  return visibleCitationsForContent(content, citations).length > 0;
}

const ROUTE_LABELS: Record<string, string> = {
  dashboard: '概览',
  sql: 'Studio',
  chat: 'Copilot Chat',
  databases: '数据库管理',
  events: '事件流',
  users: '用户',
  grants: '权限',
  tokens: 'Token',
  'ai-settings': 'Copilot 设置',
  home: '产品首页',
};

interface PageContext {
  routeKey: string;
  routeLabel: string;
  routePath: string;
  sql: string;
  sqlDb: string;
}

const pageContext = computed<PageContext | null>(() => {
  const key = (route.name as string | undefined) ?? '';
  if (!key) return null;
  return {
    routeKey: key,
    routeLabel: ROUTE_LABELS[key] ?? key,
    routePath: route.path,
    sql: sqlConsole.currentSql.trim(),
    sqlDb: sqlConsole.currentDb,
  };
});

const effectiveDb = computed<string>(() => {
  const ctx = pageContext.value;
  if (ctx?.routeKey === 'sql' && ctx.sqlDb) {
    return ctx.sqlDb;
  }
  return selectedDb.value;
});

const activeRequestDb = ref<string>('');
const toolTabDb = computed<string>(() => activeRequestDb.value || effectiveDb.value || selectedDb.value);

const pageContextSummary = computed<string>(() => {
  const ctx = pageContext.value;
  if (!ctx) return '';
  const parts: string[] = [`当前页面：${ctx.routeLabel}`];
  if (ctx.routeKey === 'sql' && ctx.sql.length > 0) {
    parts.push(`SQL ${ctx.sql.length} 字符`);
  }
  if (ctx.sqlDb) {
    parts.push(`db=${ctx.sqlDb}`);
  }
  return parts.join(' · ');
});

type AssistantActionType = 'default' | 'primary' | 'info' | 'success' | 'warning' | 'error';

interface AssistantAction {
  key: string;
  title: string;
  prompt: () => string;
  autoSend?: boolean;
  disabled?: boolean;
  disabledReason?: string;
  type?: AssistantActionType;
}

function sqlBlockForPrompt(sqlText: string): string {
  return [
    '```sql',
    sqlText,
    '```',
  ].join('\n');
}

function currentDbLine(): string {
  const db = effectiveDb.value || pageContext.value?.sqlDb || '';
  return db ? `当前数据库：${db}\n` : '';
}

function sqlActionPrompt(kind: 'fix' | 'explain' | 'optimize', sqlText: string): string {
  const prefix = currentDbLine();
  const task = kind === 'fix'
    ? '请修复下面这条 SonnetDB SQL。保留原意，指出问题，并给出一条可直接执行的修正版。'
    : kind === 'explain'
      ? '请解释下面这条 SonnetDB SQL 的含义、会访问哪些 measurement/字段，以及结果大概是什么形态。'
      : '请优化下面这条 SonnetDB SQL。优先考虑 SonnetDB 方言、time 范围过滤、measurement/schema 约束和可读性。';
  return `${prefix}${task}\n\n${sqlBlockForPrompt(sqlText)}`;
}

const assistantActions = computed<AssistantAction[]>(() => {
  const ctx = pageContext.value;
  if (!ctx) return [];

  const sqlText = ctx.sql.trim();
  const needsSql = !sqlText;
  const noSqlReason = '请先在 SQL Console 中输入一条 SQL。';

  if (ctx.routeKey === 'sql') {
    return [
      {
        key: 'sql-generate',
        title: '生成 SQL',
        type: 'primary',
        prompt: () => `${currentDbLine()}请根据当前数据库 schema，帮我生成一条 SonnetDB SQL。\n需求：`,
      },
      {
        key: 'sql-fix',
        title: '修复 SQL',
        type: 'warning',
        disabled: needsSql,
        disabledReason: noSqlReason,
        autoSend: true,
        prompt: () => sqlActionPrompt('fix', sqlText),
      },
      {
        key: 'sql-explain',
        title: '解释 SQL',
        type: 'info',
        disabled: needsSql,
        disabledReason: noSqlReason,
        autoSend: true,
        prompt: () => sqlActionPrompt('explain', sqlText),
      },
      {
        key: 'sql-optimize',
        title: '优化 SQL',
        type: 'success',
        disabled: needsSql,
        disabledReason: noSqlReason,
        autoSend: true,
        prompt: () => sqlActionPrompt('optimize', sqlText),
      },
    ];
  }

  if (ctx.routeKey === 'databases') {
    return [
      {
        key: 'db-overview',
        title: '梳理结构',
        type: 'info',
        autoSend: true,
        prompt: () => `${currentDbLine()}请帮我梳理当前可见数据库和 measurement 的结构，并指出适合从哪里开始查询。`,
      },
      {
        key: 'db-design',
        title: '设计建表',
        type: 'primary',
        prompt: () => '请帮我设计一个 SonnetDB measurement。\n业务场景：',
      },
    ];
  }

  if (ctx.routeKey === 'events') {
    return [
      {
        key: 'events-explain',
        title: '解释事件',
        type: 'info',
        autoSend: true,
        prompt: () => '请根据我当前在事件流页面的上下文，解释最近事件可能代表什么。若信息不足，请告诉我该看哪些字段。',
      },
      {
        key: 'events-troubleshoot',
        title: '排查异常',
        type: 'warning',
        autoSend: true,
        prompt: () => '请帮我按 SonnetDB 的写入、查询、权限、Copilot 四类方向排查事件流里的异常线索。',
      },
    ];
  }

  if (ctx.routeKey === 'users' || ctx.routeKey === 'grants' || ctx.routeKey === 'tokens') {
    return [
      {
        key: 'auth-check',
        title: '权限检查',
        type: 'info',
        autoSend: true,
        prompt: () => '请帮我检查当前权限配置思路，说明用户、Token、GRANT 之间该如何配合。',
      },
      {
        key: 'auth-sql',
        title: '授权 SQL',
        type: 'primary',
        prompt: () => '请帮我生成 SonnetDB 控制面授权 SQL。\n目标用户/Token：\n目标数据库：\n权限：',
      },
    ];
  }

  if (ctx.routeKey === 'ai-settings') {
    return [
      {
        key: 'ai-config',
        title: '检查配置',
        type: 'info',
        autoSend: true,
        prompt: () => '请帮我检查 Copilot 云端配置是否完整，并说明 sonnetdb.com 账号绑定和平台模型分别影响什么。',
      },
      {
        key: 'cloud-copilot',
        title: '云端说明',
        type: 'primary',
        autoSend: true,
        prompt: () => '请解释 SonnetDB Copilot 云端模式的工作方式，以及本地服务会提供哪些数据库上下文和工具结果。',
      },
    ];
  }

  if (ctx.routeKey === 'dashboard') {
    return [
      {
        key: 'dashboard-summary',
        title: '总结状态',
        type: 'info',
        autoSend: true,
        prompt: () => '请帮我从当前概览页面出发，总结 SonnetDB 实例应重点关注的健康状态指标。',
      },
      {
        key: 'dashboard-perf',
        title: '性能排查',
        type: 'warning',
        autoSend: true,
        prompt: () => '请给我一份 SonnetDB 性能排查清单，优先覆盖写入吞吐、查询延迟、WAL、Segment 和 Compaction。',
      },
    ];
  }

  return [
    {
      key: 'general-help',
      title: '使用建议',
      type: 'info',
      autoSend: true,
      prompt: () => `我当前在「${ctx.routeLabel}」页面。请告诉我这个页面最常用的操作和下一步建议。`,
    },
    {
      key: 'general-sql',
      title: '写查询',
      type: 'primary',
      prompt: () => `${currentDbLine()}请帮我写一条 SonnetDB 查询。\n需求：`,
    },
  ];
});

function onAssistantAction(action: AssistantAction): void {
  if (action.disabled || running.value) return;
  prompt.value = action.prompt();
  if (action.autoSend) {
    void nextTick(() => send());
  }
}

watch(
  () => [pageContext.value?.routeKey ?? '', pageContext.value?.sqlDb ?? ''] as const,
  ([routeKey, sqlDb]) => {
    if (routeKey === 'sql' && sqlDb && sqlDb !== selectedDb.value) {
      selectedDb.value = sqlDb;
    }
  },
  { immediate: true },
);

/** 把页面上下文构造成一条 system message（仅在 send 时临时注入，不进入会话历史）。 */
function buildContextMessage(): CopilotMessage | null {
  if (!contextEnabled.value) return null;
  const ctx = pageContext.value;
  if (!ctx) return null;
  const lines: string[] = [
    '[页面上下文 / Page Context]',
    `用户当前所在页面：${ctx.routeLabel}（路由：${ctx.routePath}）。`,
  ];
  if (ctx.routeKey === 'sql') {
    if (ctx.sqlDb) lines.push(`SQL Console 当前选中的数据库：${ctx.sqlDb}。`);
    if (ctx.sql) {
      // 截断超长 SQL 避免超过 token 预算
      const snippet = ctx.sql.length > 2000 ? `${ctx.sql.slice(0, 2000)}\n…(已截断 ${ctx.sql.length - 2000} 字符)` : ctx.sql;
      lines.push('用户正在编辑的 SQL：');
      lines.push('```sql');
      lines.push(snippet);
      lines.push('```');
    } else {
      lines.push('SQL Console 编辑器当前为空。');
    }
    lines.push('如果用户提问与「这条 SQL / 当前查询 / 报错」相关，请优先围绕上面这段 SQL 回答。');
  } else if (ctx.routeKey === 'databases') {
    lines.push('用户正在查看「数据库管理」页面。如果用户问到 measurement / schema 信息，可调用工具列出当前选中数据库的 measurement。');
  } else if (ctx.routeKey === 'events') {
    lines.push('用户正在查看「事件流」页面（实时 SSE 事件）。');
  } else if (ctx.routeKey === 'ai-settings') {
    lines.push('用户正在「Copilot 设置」页面，可能在排查 sonnetdb.com 账号绑定或平台模型相关问题。');
  }
  return { role: 'system', content: lines.join('\n') };
}

function open(): void {
  visible.value = true;
  void reloadDbs();
}

function close(): void {
  visible.value = false;
  fullscreen.value = false;
}

/** 系统内置数据库，不应暴露给用户或 AI 推断使用。 */
const SYSTEM_DATABASES = new Set(['_internal', '__copilot__']);

/** 判断给定库名是否属于系统库（名字以双下划线开头并以双下划线结尾，或在显式列表中）。 */
function isSystemDatabase(name: string): boolean {
  if (SYSTEM_DATABASES.has(name)) return true;
  return name.length >= 4 && name.startsWith('__') && name.endsWith('__');
}

async function reloadDbs(): Promise<void> {
  try {
    const result = await listDatabases(auth.api);
    dbs.value = result.databases.filter((db: string) => !isSystemDatabase(db));
    if (!auth.isSuperuser && selectedDb.value === CONTROL_PLANE_KEY) {
      selectedDb.value = '';
    }
    if (!selectedDb.value && dbs.value.length > 0) {
      selectedDb.value = dbs.value[0];
    }
  } catch {
    // ignore — user 可能无 list 权限
  }
}

const starters = computed<CopilotStarter[]>(() => pickStarters(pageContext.value?.routeKey ?? null, 6));

function onStarterClick(s: CopilotStarter): void {
  prompt.value = s.prompt;
}

function onKeydown(e: KeyboardEvent): void {
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault();
    void send();
  }
}

const SqlToolNames = new Set(['draft_sql', 'query_sql', 'execute_sql']);
const copilotToolTabs = new Map<string, string>();
const copilotSqlSeen = new Set<string>();

/**
 * 当账号没有任何业务数据库时，弹出对话框引导用户即时新建一个。
 * 成功返回新数据库名；用户取消或失败返回空串。
 */
async function promptCreateDatabase(): Promise<string> {
  if (!auth.isSuperuser) {
    message.error('当前账号下没有可用的业务数据库，请联系管理员先创建一个。');
    return '';
  }
  const inputName = ref('metrics');
  return new Promise<string>((resolve) => {
    const d = dialog.create({
      title: '尚无业务数据库，请先创建一个',
      content: () => h('div', { style: 'display: flex; flex-direction: column; gap: 8px' }, [
        h(NText, { depth: 3, style: 'font-size: 12px' }, {
          default: () => 'Copilot 需要绑定到某个数据库才能工作。请输入新数据库名（字母开头，仅含字母数字下划线）：',
        }),
        h(NInput, {
          value: inputName.value,
          'onUpdate:value': (v: string) => { inputName.value = v; },
          placeholder: '例如 metrics、host_perf',
          autofocus: true,
        }),
      ]),
      positiveText: '创建并继续',
      negativeText: '取消',
      onPositiveClick: async () => {
        const name = inputName.value.trim();
        if (!isValidIdentifier(name)) {
          message.error('名称必须以字母开头，仅包含字母数字下划线。');
          return false;
        }
        const rs = await execControlPlaneSql(auth.api, `CREATE DATABASE ${name}`);
        if (rs.error) {
          message.error(rs.error.message);
          return false;
        }
        message.success(`已创建数据库 ${name}`);
        await reloadDbs();
        selectedDb.value = name;
        resolve(name);
        return true;
      },
      onNegativeClick: () => { resolve(''); },
      onClose: () => { resolve(''); },
      onMaskClick: () => { resolve(''); d.destroy(); },
    });
  });
}

interface ToolArgumentsPayload {
  database?: string;
  sql?: string;
  maxRows?: number;
}

interface DraftSqlPayload {
  database?: string;
  statementType?: string;
  sql?: string;
  measurement?: string | null;
  isWrite?: boolean;
  measurementExists?: boolean | null;
  notes?: string[];
}

interface QuerySqlPayload {
  database?: string;
  statementType?: string;
  columns?: string[];
  rows?: unknown[][];
  returnedRows?: number;
  truncated?: boolean;
}

interface ExecuteSqlPayload extends QuerySqlPayload {
  sql?: string;
  measurement?: string | null;
  rowsAffected?: number | null;
}

interface ToolErrorPayload {
  error?: string;
  phase?: string;
  message?: string;
  sql?: string;
}

function parseJsonObject<T>(value: string | undefined): T | null {
  if (!value) return null;
  try {
    const parsed = JSON.parse(value) as unknown;
    return parsed && typeof parsed === 'object' ? parsed as T : null;
  } catch {
    return null;
  }
}

function normalizeSqlForDedupe(value: string): string {
  return value.replace(/\s+/g, ' ').trim().toLowerCase();
}

function tabTitleForSqlTool(toolName: string, sql: string): string {
  const oneLine = sql.replace(/\s+/g, ' ').trim();
  const prefix = toolName === 'draft_sql'
    ? 'Copilot 起草'
    : toolName === 'execute_sql'
      ? 'Copilot 执行'
      : 'Copilot 查询';
  if (!oneLine) return prefix;
  return `${prefix}: ${oneLine.length > 24 ? `${oneLine.slice(0, 21)}...` : oneLine}`;
}

function toolEventKey(event: CopilotChatEvent, sql: string): string {
  return `${event.toolName ?? ''}|${event.toolArguments ?? ''}|${normalizeSqlForDedupe(sql)}`;
}

function extractToolSql(event: CopilotChatEvent): string {
  const args = parseJsonObject<ToolArgumentsPayload>(event.toolArguments);
  if (args?.sql) return args.sql;

  const draft = parseJsonObject<DraftSqlPayload>(event.toolResult);
  if (draft?.sql) return draft.sql;

  const executed = parseJsonObject<ExecuteSqlPayload>(event.toolResult);
  if (executed?.sql) return executed.sql;

  const err = parseJsonObject<ToolErrorPayload>(event.toolResult);
  return err?.sql ?? '';
}

function extractToolDatabase(event: CopilotChatEvent): string {
  const args = parseJsonObject<ToolArgumentsPayload>(event.toolArguments);
  if (args?.database) return args.database;

  const draft = parseJsonObject<DraftSqlPayload>(event.toolResult);
  if (draft?.database) return draft.database;

  const executed = parseJsonObject<ExecuteSqlPayload>(event.toolResult);
  if (executed?.database) return executed.database;

  return '';
}

function ensureCopilotSqlTab(event: CopilotChatEvent, sql: string): string {
  const key = toolEventKey(event, sql);
  const existing = copilotToolTabs.get(key);
  if (existing) return existing;

  copilotSqlSeen.add(normalizeSqlForDedupe(sql));
  const database = extractToolDatabase(event) || toolTabDb.value;
  const tab = sqlConsole.createTab({
    title: tabTitleForSqlTool(event.toolName ?? 'query_sql', sql),
    db: database,
    sql,
    source: 'copilot',
    summary: event.type === 'tool_call'
      ? `Copilot 正在调用 ${event.toolName}。`
      : 'Copilot 已同步 SQL。',
  });
  copilotToolTabs.set(key, tab.id);
  return tab.id;
}

function toolErrorResult(payload: ToolErrorPayload): SqlResultSet {
  return {
    columns: [],
    rows: [],
    end: null,
    error: {
      type: 'error',
      code: payload.error ?? payload.phase ?? 'copilot_tool_error',
      message: payload.message ?? 'Copilot SQL 工具返回错误。',
    },
    hasColumns: false,
  };
}

function draftSqlResult(payload: DraftSqlPayload): SqlResultSet {
  const rows: unknown[][] = [
    ['statement_type', payload.statementType ?? 'draft_sql'],
  ];
  if (payload.measurement) rows.push(['measurement', payload.measurement]);
  rows.push(['is_write', payload.isWrite ? 'true' : 'false']);
  if (typeof payload.measurementExists === 'boolean') {
    rows.push(['measurement_exists', payload.measurementExists ? 'true' : 'false']);
  }
  for (const note of payload.notes ?? []) {
    rows.push(['note', note]);
  }
  return buildClientResultSet(['字段', '值'], rows);
}

function executedSqlResult(payload: ExecuteSqlPayload | QuerySqlPayload): SqlResultSet {
  const maybeError = payload as ToolErrorPayload;
  if (maybeError.error || maybeError.message) {
    return toolErrorResult(maybeError);
  }

  const columns = Array.isArray(payload.columns) ? payload.columns : [];
  const rows = Array.isArray(payload.rows) ? payload.rows : [];
  const returnedRows = typeof payload.returnedRows === 'number' ? payload.returnedRows : rows.length;
  const rowsAffected = 'rowsAffected' in payload && typeof payload.rowsAffected === 'number'
    ? payload.rowsAffected
    : (columns.length > 0 ? -1 : 0);

  return {
    columns,
    rows,
    end: {
      type: 'end',
      rowCount: returnedRows,
      recordsAffected: rowsAffected,
      elapsedMs: 0,
    },
    error: null,
    hasColumns: columns.length > 0,
  };
}

function syncCopilotSqlEvent(event: CopilotChatEvent): void {
  const toolName = event.toolName ?? '';
  if (!SqlToolNames.has(toolName)) return;

  const sql = extractToolSql(event).trim();
  if (!sql) return;

  if (event.type === 'tool_call') {
    ensureCopilotSqlTab(event, sql);
    return;
  }

  if (event.type !== 'tool_result') return;

  const tabId = ensureCopilotSqlTab(event, sql);
  let result: SqlResultSet | null = null;
  let summary = `Copilot 工具 ${toolName} 已返回。`;

  const errorPayload = parseJsonObject<ToolErrorPayload>(event.toolResult);
  if (errorPayload?.error || errorPayload?.message) {
    result = toolErrorResult(errorPayload);
    summary = `Copilot 工具 ${toolName} 返回错误。`;
  } else if (toolName === 'draft_sql') {
    const payload = parseJsonObject<DraftSqlPayload>(event.toolResult);
    if (payload) {
      result = draftSqlResult(payload);
      summary = 'Copilot 已起草 SQL，等待你确认后运行。';
    }
  } else {
    const payload = parseJsonObject<ExecuteSqlPayload | QuerySqlPayload>(event.toolResult);
    if (payload) {
      result = executedSqlResult(payload);
      const affected = 'rowsAffected' in payload && typeof payload.rowsAffected === 'number'
        ? `受影响 ${payload.rowsAffected}`
        : `返回 ${payload.returnedRows ?? payload.rows?.length ?? 0} 行`;
      summary = toolName === 'execute_sql'
        ? `Copilot 已执行 SQL，${affected}。`
        : `Copilot 已查询 SQL，${affected}。`;
    }
  }

  if (!result) return;
  sqlConsole.setTabResults(
    tabId,
    [{
      id: `stmt_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`,
      sql,
      result,
      createdAt: Date.now(),
      source: 'copilot',
    }],
    summary,
    '',
    true,
  );
}

function extractSqlBlocks(answer: string): string[] {
  const blocks: string[] = [];
  const re = /```(?:sql)?\s*([\s\S]*?)```/gi;
  let match: RegExpExecArray | null;
  while ((match = re.exec(answer)) !== null) {
    const sql = match[1]?.trim();
    if (sql && /^(select|show|describe|create|insert|delete)\b/i.test(sql)) {
      blocks.push(sql);
    }
  }
  return blocks;
}

function syncFinalAnswerSql(answer: string): void {
  for (const sql of extractSqlBlocks(answer)) {
    const key = normalizeSqlForDedupe(sql);
    if (copilotSqlSeen.has(key)) continue;
    copilotSqlSeen.add(key);
    sqlConsole.createTab({
      title: tabTitleForSqlTool('draft_sql', sql),
      db: toolTabDb.value,
      sql,
      source: 'copilot',
      summary: 'Copilot 最终回答中的 SQL，等待你确认后运行。',
    });
  }
}

const ProvisioningDatabaseNameRegex = /(?:数据库|仓库|库)\s*(?:叫|名为|叫做|叫作)?\s*([A-Za-z][A-Za-z0-9_]*)/i;

function looksLikeProvisioningRequest(message: string): boolean {
  const lowered = message.trim().toLowerCase();
  const asksToCreate = lowered.includes('新建')
    || lowered.includes('创建')
    || lowered.includes('建一个')
    || lowered.includes('建一套')
    || lowered.includes('create database');
  const mentionsDatabase = lowered.includes('数据库')
    || lowered.includes('仓库')
    || lowered.includes('库')
    || lowered.includes('database');

  return asksToCreate && mentionsDatabase;
}

function inferProvisioningDatabaseName(message: string): string {
  const explicit = ProvisioningDatabaseNameRegex.exec(message)?.[1];
  if (explicit) return explicit;

  const lowered = message.trim().toLowerCase();
  const looksLikeComputerPerformance = (lowered.includes('电脑')
      || lowered.includes('计算机')
      || lowered.includes('主机')
      || lowered.includes('系统')
      || lowered.includes('host'))
    && (lowered.includes('性能')
      || lowered.includes('perf')
      || lowered.includes('监控')
      || lowered.includes('指标')
      || lowered.includes('usage'));
  if (looksLikeComputerPerformance) return 'computer_perf';

  const looksLikeEnvironmentTelemetry = lowered.includes('温度')
    || lowered.includes('湿度')
    || lowered.includes('humidity')
    || lowered.includes('temperature');
  if (looksLikeEnvironmentTelemetry) return 'sensor_metrics';

  return 'metrics';
}

function resolveCloudMode(message: string): CloudMode {
  const ctx = pageContext.value;
  const lowered = message.toLowerCase();

  if (ctx?.routeKey === 'sql' && ctx.sql.trim()) {
    return 'sql_analyze';
  }

  if (looksLikeProvisioningRequest(message)
    || /(retention|compaction|wal|recover|recovery|grant|revoke|delete|drop|slow query|bulk|ingest|导入|回填|恢复|权限|授权|撤权|慢查询|清理|压缩|保留)/i.test(message)) {
    return 'db_maintenance';
  }

  if (/(fix|repair|explain|analyze|optimize|修复|解释|分析|优化|报错|错误)/i.test(message)) {
    return 'sql_analyze';
  }

  if (ctx?.routeKey === 'ai-settings'
    || /(文档|知识|帮助|是什么|为什么|介绍|docs|help|guide)/i.test(lowered)) {
    return 'knowledge_qa';
  }

  return 'sql_assist';
}

async function send(): Promise<void> {
  if (!prompt.value.trim() || running.value) return;
  if (!auth.state?.token) return;

  const userText = prompt.value.trim();
  const isProvisioningRequest = looksLikeProvisioningRequest(userText);
  const provisioningDb = isProvisioningRequest ? inferProvisioningDatabaseName(userText) : '';

  // 数据库自动推断：优先 SQL Console 当前库，其次已知库列表第一个，最后留空让后端处理
  let targetDb = effectiveDb.value || (dbs.value.length > 0 ? dbs.value[0] : '');

  // 无库场景下，建库意图允许直接走后端 provisioning；其它请求仍先引导建库。
  if (!targetDb && !isProvisioningRequest) {
    const created = await promptCreateDatabase();
    if (!created) return;  // 用户取消或无权创建
    targetDb = created;
  }

  const requestDb = provisioningDb || targetDb;

  // 没有当前会话则先建一个；切换数据库时同步到当前会话。
  const activeSession = sessions.current ?? sessions.create(requestDb);
  if (requestDb && activeSession.db !== requestDb) {
    activeSession.db = requestDb;
  }
  const sessionId = activeSession.id;

  const userMsg: CopilotMessage = { role: 'user', content: userText };
  prompt.value = '';
  errorMsg.value = '';
  // 把 user 消息立即加到发起请求的会话；assistant 最终回复也会按同一个 sessionId 追加。
  const requestSession = sessions.appendMessage(sessionId, requestDb, userMsg);
  streamBuffer.value = '';
  streamCitations.value = [];
  running.value = true;
  await scrollToBottom();

  const ac = new AbortController();
  abort.value = ac;

  // M6: 构造请求载荷 = [可选 system 上下文] + 会话历史
  const ctxMsg = buildContextMessage();
  const requestMessages: CopilotMessage[] = ctxMsg
    ? [ctxMsg, ...requestSession.messages]
    : [...requestSession.messages];

  const stepLog: string[] = [];
  let finalAnswer = '';
  let finalCitations: CopilotCitation[] = [];
  copilotToolTabs.clear();
  copilotSqlSeen.clear();
  activeRequestDb.value = requestDb;
  try {
    for await (const event of streamCopilotChat(
      auth.state.token,
      {
        ...(requestDb ? { db: requestDb } : {}),
        messages: requestMessages,
        mode: permissionMode.value,
        conversationId: sessionId,
        cloudMode: resolveCloudMode(userText),
      },
      ac.signal,
    )) {
      if (ac.signal.aborted) break;
      syncCopilotSqlEvent(event);
      if (event.type === 'final' && event.answer) {
        finalAnswer = event.answer;
        finalCitations = visibleCitationsForContent(event.answer, event.citations);
        streamCitations.value = finalCitations;
        syncFinalAnswerSql(event.answer);
        streamBuffer.value = event.answer;
      } else if (event.type === 'error') {
        errorMsg.value = event.message ?? 'Copilot 请求失败';
      } else if (event.message) {
        stepLog.push(event.message);
        // 仅当尚无 final 时显示进度
        if (!finalAnswer) streamBuffer.value = stepLog.slice(-3).join('\n');
      }
      await scrollToBottom();
    }
    if (finalAnswer) {
      sessions.appendMessage(sessionId, requestDb, {
        role: 'assistant',
        content: finalAnswer,
        ...(finalCitations.length > 0 ? { citations: finalCitations } : {}),
      });
      streamBuffer.value = '';
      streamCitations.value = [];
      if (isProvisioningRequest) {
        void reloadDbs();
      }
    }
  } catch (e: unknown) {
    if (!ac.signal.aborted) {
      errorMsg.value = e instanceof Error ? e.message : String(e);
    }
  } finally {
    running.value = false;
    abort.value = null;
    activeRequestDb.value = '';
    await scrollToBottom();
  }
}

function stop(): void {
  abort.value?.abort();
}

// === 会话历史（M5）===
function onNewSession(): void {
  sessions.create(effectiveDb.value);
  streamBuffer.value = '';
  streamCitations.value = [];
  errorMsg.value = '';
}

function onSwitchSession(id: string): void {
  sessions.switchTo(id);
  streamBuffer.value = '';
  streamCitations.value = [];
  errorMsg.value = '';
  if (sessions.current?.db) {
    if (sessions.current.db === CONTROL_PLANE_KEY && !auth.isSuperuser) {
      sessions.current.db = '';
      return;
    }
    selectedDb.value = sessions.current.db;
  }
}

function onRemoveSession(id: string): void {
  sessions.remove(id);
  streamBuffer.value = '';
  streamCitations.value = [];
  errorMsg.value = '';
}

function onClearAll(): void {
  sessions.clearAll();
  streamBuffer.value = '';
  streamCitations.value = [];
  errorMsg.value = '';
}

function onRenameSession(s: CopilotSession): void {
  const inputRef = ref(s.title);
  dialog.create({
    title: '重命名会话',
    content: () => h(NInput, { value: inputRef.value, 'onUpdate:value': (v: string) => { inputRef.value = v; } }),
    positiveText: '保存',
    negativeText: '取消',
    onPositiveClick: () => {
      sessions.rename(s.id, inputRef.value);
    },
  });
}

function formatRelative(ts: number): string {
  const diff = Date.now() - ts;
  if (diff < 60_000) return '刚刚';
  if (diff < 3600_000) return `${Math.floor(diff / 60_000)} 分钟前`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3600_000)} 小时前`;
  if (diff < 7 * 86_400_000) return `${Math.floor(diff / 86_400_000)} 天前`;
  try { return new Date(ts).toLocaleDateString(); } catch { return ''; }
}

async function scrollToBottom(): Promise<void> {
  await nextTick();
  const el = msgContainer.value;
  if (el) el.scrollTop = el.scrollHeight;
}

// 拖拽
let dragStart: { x: number; y: number; right: number; bottom: number } | null = null;
function onDragStart(e: MouseEvent): void {
  if (fullscreen.value) return;
  dragStart = { x: e.clientX, y: e.clientY, right: dockPos.value.right, bottom: dockPos.value.bottom };
  document.addEventListener('mousemove', onDragMove);
  document.addEventListener('mouseup', onDragEnd);
}
function onDragMove(e: MouseEvent): void {
  if (!dragStart) return;
  const dx = e.clientX - dragStart.x;
  const dy = e.clientY - dragStart.y;
  dockPos.value = {
    right: Math.max(0, dragStart.right - dx),
    bottom: Math.max(0, dragStart.bottom - dy),
  };
}
function onDragEnd(): void {
  dragStart = null;
  document.removeEventListener('mousemove', onDragMove);
  document.removeEventListener('mouseup', onDragEnd);
}

// 当用户登录后，预加载状态
watch(() => auth.isAuthenticated, (val) => {
  if (val && visible.value) {
    void reloadDbs();
  }
});

onMounted(() => {
  // 不主动 open，只在用户点击 FAB 时才请求接口，避免未启用 Copilot 时报 409。
});
</script>

<style scoped>
.copilot-fab {
  position: fixed;
  right: 24px;
  bottom: 24px;
  width: 52px;
  height: 52px;
  border-radius: 50%;
  background: linear-gradient(135deg, #2c7be5, #0d3b66);
  color: #fff;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  box-shadow: 0 8px 24px rgba(13, 59, 102, 0.32);
  z-index: 9999;
  transition: transform 0.15s ease;
}
.copilot-fab:hover { transform: scale(1.05); }
.copilot-fab-icon { font-weight: 700; font-size: 14px; letter-spacing: 0.5px; }

.copilot-dock {
  position: fixed;
  width: 420px;
  /* M3：浮窗高度按窗口高度的黄金比例（≈61.8%）自适应，
     避免在大屏上显得过小，同时保留最小/最大边界。 */
  height: 61.8vh;
  min-height: 480px;
  max-height: calc(100vh - 48px);
  background: #fff;
  border: 1px solid rgba(0, 0, 0, 0.08);
  border-radius: 12px;
  box-shadow: 0 20px 48px rgba(13, 59, 102, 0.22);
  display: flex;
  flex-direction: column;
  z-index: 9998;
  overflow: hidden;
}
.copilot-dock.is-fullscreen {
  width: auto !important;
  height: auto !important;
  border-radius: 0;
  border: none;
}
.copilot-dock__header {
  padding: 8px 12px;
  border-bottom: 1px solid rgba(0, 0, 0, 0.06);
  display: flex;
  align-items: center;
  justify-content: space-between;
  cursor: move;
  background: linear-gradient(180deg, rgba(248, 251, 255, 1), rgba(238, 245, 249, 1));
  user-select: none;
}
.copilot-dock__title {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
}
.copilot-dock__avatar {
  width: 22px;
  height: 22px;
  border-radius: 50%;
  background: linear-gradient(135deg, #2c7be5, #0d3b66);
  color: #fff;
  font-size: 10px;
  font-weight: 700;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}
.copilot-dock__options {
  display: flex;
  flex-direction: column;
  gap: 12px;
  font-size: 12px;
}
.copilot-dock__options-block {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.copilot-dock__options-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.copilot-dock__options-row {
  display: flex;
  align-items: center;
  gap: 10px;
  color: var(--sndb-ink, #1f2937);
}
.copilot-dock__options-muted {
  color: var(--sndb-ink-soft, #678);
  font-size: 11px;
}
.copilot-dock__perm {
  padding: 6px 12px 0;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px;
}
.copilot-dock__perm-confirm {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  margin-top: 4px;
  padding: 6px 8px;
  background: rgba(44, 123, 229, 0.06);
  border: 1px solid rgba(44, 123, 229, 0.18);
  border-radius: 6px;
  font-size: 11px;
}
/* 滑入/滑出动画 */
.perm-confirm-enter-active,
.perm-confirm-leave-active {
  transition: opacity 0.15s ease, transform 0.15s ease;
}
.perm-confirm-enter-from,
.perm-confirm-leave-to {
  opacity: 0;
  transform: translateY(-4px);
}
.copilot-dock__ctx {
  padding: 6px 12px 0;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px;
}
.copilot-dock__actions {
  padding: 6px 12px 0;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
}
.copilot-dock__messages {
  flex: 1;
  overflow-y: auto;
  padding: 8px 12px;
  font-size: 13px;
}
.copilot-dock__empty {
  color: var(--sndb-ink-soft, #678);
  font-size: 12px;
}
.copilot-dock__empty ul { margin: 6px 0 0; padding: 0; list-style: none; }
.copilot-dock__empty li {
  padding: 6px 8px;
  border-radius: 6px;
  margin: 2px 0;
  cursor: pointer;
  background: rgba(44, 123, 229, 0.06);
}
.copilot-dock__empty li:hover { background: rgba(44, 123, 229, 0.12); }
.copilot-dock__empty-tip { margin: 0 0 8px; }
.copilot-dock__starters {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
  gap: 6px;
}
.copilot-dock__starter {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  padding: 8px 10px;
  border: 1px solid rgba(44, 123, 229, 0.18);
  border-radius: 8px;
  background: rgba(44, 123, 229, 0.04);
  color: var(--sndb-ink, #1f2937);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
}
.copilot-dock__starter:hover {
  background: rgba(44, 123, 229, 0.12);
  border-color: rgba(44, 123, 229, 0.45);
}
.copilot-dock__starter-cat {
  font-size: 10px;
  padding: 1px 6px;
  border-radius: 999px;
  background: rgba(44, 123, 229, 0.15);
  color: rgb(44, 123, 229);
  line-height: 1.4;
}
.copilot-dock__starter-title {
  font-weight: 600;
  line-height: 1.3;
}
.copilot-dock__msg { margin: 8px 0; }
.copilot-dock__msg-role { font-size: 11px; color: var(--sndb-ink-soft, #678); margin-bottom: 2px; }
.copilot-dock__msg-body { word-break: break-word; }
.copilot-dock__msg--user .copilot-dock__msg-body {
  background: rgba(44, 123, 229, 0.08);
  padding: 6px 8px;
  border-radius: 6px;
}
.copilot-dock__markdown {
  line-height: 1.55;
}
.copilot-dock__markdown :deep(p) {
  margin: 0 0 6px;
}
.copilot-dock__markdown :deep(p:last-child),
.copilot-dock__markdown :deep(pre:last-child),
.copilot-dock__markdown :deep(ul:last-child),
.copilot-dock__markdown :deep(ol:last-child) {
  margin-bottom: 0;
}
.copilot-dock__markdown :deep(pre) {
  margin: 6px 0;
  padding: 8px 10px;
  overflow-x: auto;
  background: rgba(13, 59, 102, 0.08);
  border: 1px solid rgba(13, 59, 102, 0.08);
  border-radius: 6px;
  white-space: pre;
}
.copilot-dock__markdown :deep(code) {
  font-family: 'JetBrains Mono', Consolas, Menlo, monospace;
  font-size: 12px;
  background: rgba(13, 59, 102, 0.08);
  padding: 1px 4px;
  border-radius: 4px;
}
.copilot-dock__markdown :deep(pre code) {
  display: block;
  padding: 0;
  background: transparent;
  border-radius: 0;
}
.copilot-dock__markdown :deep(ul),
.copilot-dock__markdown :deep(ol) {
  margin: 6px 0;
  padding-left: 18px;
}
.copilot-dock__markdown :deep(blockquote) {
  margin: 6px 0;
  padding-left: 10px;
  color: var(--sndb-ink-soft, #678);
  border-left: 3px solid rgba(13, 59, 102, 0.16);
}
.copilot-dock__markdown :deep(table) {
  width: 100%;
  border-collapse: collapse;
  margin: 6px 0;
  font-size: 12px;
}
.copilot-dock__markdown :deep(th),
.copilot-dock__markdown :deep(td) {
  border: 1px solid rgba(0, 0, 0, 0.08);
  padding: 4px 6px;
  text-align: left;
}
.copilot-dock__markdown :deep(th) {
  background: rgba(13, 59, 102, 0.06);
  font-weight: 600;
}
.copilot-dock__markdown :deep(a) {
  color: #2c7be5;
  text-decoration: none;
}
.copilot-dock__markdown :deep(a:hover) {
  text-decoration: underline;
}
.copilot-dock__citations {
  margin-top: 8px;
  padding-top: 6px;
  border-top: 1px solid rgba(13, 59, 102, 0.08);
}
.copilot-dock__citations-title {
  margin-bottom: 4px;
  color: var(--sndb-ink-soft, #678);
  font-size: 11px;
  font-weight: 600;
}
.copilot-dock__citation {
  padding: 6px 8px;
  border: 1px solid rgba(13, 59, 102, 0.08);
  border-radius: 6px;
  background: rgba(13, 59, 102, 0.035);
}
.copilot-dock__citation + .copilot-dock__citation {
  margin-top: 6px;
}
.copilot-dock__citation-head {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--sndb-ink, #1f2937);
  font-size: 12px;
  font-weight: 600;
}
.copilot-dock__citation-id {
  flex: 0 0 auto;
  padding: 0 5px;
  border-radius: 4px;
  background: rgba(44, 123, 229, 0.12);
  color: #2c7be5;
  font-family: 'JetBrains Mono', Consolas, Menlo, monospace;
  font-size: 11px;
}
.copilot-dock__citation-source {
  margin-top: 2px;
  color: var(--sndb-ink-soft, #678);
  font-size: 11px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.copilot-dock__citation-snippet {
  margin-top: 4px;
  color: var(--sndb-ink, #1f2937);
  font-size: 12px;
  line-height: 1.45;
}
.copilot-dock__error {
  color: #d03050;
  font-size: 12px;
  padding: 6px 8px;
  background: rgba(208, 48, 80, 0.06);
  border-radius: 6px;
  margin-top: 6px;
}
.copilot-dock__caret {
  display: inline-block;
  width: 6px;
  height: 12px;
  background: currentColor;
  margin-left: 2px;
  animation: blink 1s infinite;
  vertical-align: text-bottom;
}
@keyframes blink { 50% { opacity: 0; } }
.copilot-dock__input {
  padding: 8px 12px 12px;
  border-top: 1px solid rgba(0, 0, 0, 0.06);
  background: #fff;
}

/* 会话历史 popover */
.copilot-dock__sessions { font-size: 12px; }
.copilot-dock__sessions-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 4px 4px 8px;
  border-bottom: 1px solid rgba(0, 0, 0, 0.06);
  margin-bottom: 6px;
}
.copilot-dock__sessions-empty {
  padding: 16px 4px;
  color: var(--sndb-ink-soft, #678);
  text-align: center;
}
.copilot-dock__sessions-list {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 320px;
  overflow-y: auto;
}
.copilot-dock__sessions-list li {
  display: flex;
  gap: 8px;
  padding: 6px 8px;
  border-radius: 6px;
  cursor: pointer;
  align-items: center;
}
.copilot-dock__sessions-list li:hover { background: rgba(13, 59, 102, 0.05); }
.copilot-dock__sessions-list li.is-active { background: rgba(44, 123, 229, 0.12); }
.copilot-dock__sessions-item-main { flex: 1; min-width: 0; }
.copilot-dock__sessions-item-title {
  font-weight: 600;
  color: var(--sndb-ink-strong, #111);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.copilot-dock__sessions-item-meta {
  color: var(--sndb-ink-soft, #678);
  font-size: 11px;
  margin-top: 2px;
}
.copilot-dock__sessions-item-actions { flex-shrink: 0; }
</style>
