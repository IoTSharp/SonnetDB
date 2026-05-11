<template>
  <div ref="editorContainer" class="sql-editor" />
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { EditorView, keymap, lineNumbers, highlightActiveLine, placeholder } from '@codemirror/view';
import { EditorState } from '@codemirror/state';
import { defaultKeymap, history, historyKeymap } from '@codemirror/commands';
import {
  bracketMatching,
  defaultHighlightStyle,
  indentOnInput,
  syntaxHighlighting,
} from '@codemirror/language';
import {
  closeBrackets, closeBracketsKeymap, autocompletion, completionKeymap,
} from '@codemirror/autocomplete';
import { keywordCompletionSource, schemaCompletionSource, sql, type SQLConfig, type SQLNamespace } from '@codemirror/lang-sql';
import { SonnetDbSQL } from './sonnetdb-dialect';

export interface ColumnInfo { name: string; role: string; dataType: string; }
export interface MeasurementInfo { name: string; columns: ColumnInfo[]; }
export interface CursorInfo { line: number; column: number; position: number; length: number; }

const props = defineProps<{
  modelValue: string;
  schema?: MeasurementInfo[];
  placeholder?: string;
}>();

const emit = defineEmits<{
  (e: 'update:modelValue', v: string): void;
  (e: 'cursor', v: CursorInfo): void;
}>();

const editorContainer = ref<HTMLElement | null>(null);
let view: EditorView | null = null;

function buildSqlSchema(measurements?: MeasurementInfo[]): SQLNamespace {
  if (!measurements?.length) return {};
  const schemaMap: Record<string, SQLNamespace> = {};
  for (const m of measurements) {
    schemaMap[m.name] = {
      self: {
        label: m.name,
        type: 'type',
        detail: 'measurement',
      },
      children: m.columns.map((c) => ({
        label: c.name,
        type: c.role.toUpperCase() === 'TAG' ? 'property' : 'variable',
        detail: `${c.role.toLowerCase()} ${c.dataType}`,
      })),
    };
  }
  return schemaMap;
}

function getCursorInfo(editor: EditorView): CursorInfo {
  const position = editor.state.selection.main.head;
  const line = editor.state.doc.lineAt(position);
  return {
    line: line.number,
    column: position - line.from + 1,
    position,
    length: editor.state.doc.length,
  };
}

function emitCursor(editor: EditorView): void {
  emit('cursor', getCursorInfo(editor));
}

function createView(el: HTMLElement, initialDoc = props.modelValue) {
  const schemaMap = buildSqlSchema(props.schema);
  const sqlConfig: SQLConfig = {
    dialect: SonnetDbSQL,
    schema: schemaMap,
    upperCaseKeywords: true,
  };
  const sqlLang = sql(sqlConfig);

  const startState = EditorState.create({
    doc: initialDoc,
    extensions: [
      lineNumbers(),
      history(),
      highlightActiveLine(),
      sqlLang,
      syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
      bracketMatching(),
      closeBrackets(),
      indentOnInput(),
      placeholder(props.placeholder ?? ''),
      autocompletion({
        activateOnTyping: true,
        activateOnTypingDelay: 60,
        override: [
          schemaCompletionSource(sqlConfig),
          keywordCompletionSource(SonnetDbSQL, true),
        ],
      }),
      keymap.of([
        ...completionKeymap,
        ...closeBracketsKeymap,
        ...defaultKeymap,
        ...historyKeymap,
      ]),
      EditorView.updateListener.of((update) => {
        if (update.docChanged) {
          emit('update:modelValue', update.state.doc.toString());
        }
        if (update.docChanged || update.selectionSet) {
          emitCursor(update.view);
        }
      }),
      EditorView.theme({
        '&': {
          minHeight: '100%',
          height: '100%',
          border: '1px solid #e0e0e6',
          borderRadius: '0',
          fontSize: '13px',
          fontFamily: '"JetBrains Mono", "Fira Code", "Cascadia Code", Consolas, monospace',
        },
        '.cm-scroller': { overflow: 'auto', minHeight: '100%' },
        '.cm-content': { padding: '8px 0' },
        '.cm-placeholder': { color: '#8a97a8' },
        '.cm-tooltip.cm-tooltip-autocomplete': { zIndex: '30' },
        '.cm-focused': { outline: 'none' },
        '&.cm-focused': { borderColor: '#18a058' },
      }),
    ],
  });

  view = new EditorView({ state: startState, parent: el });
  emitCursor(view);
}

onMounted(() => {
  if (editorContainer.value) {
    createView(editorContainer.value);
  }
});

onBeforeUnmount(() => {
  view?.destroy();
  view = null;
});

// 外部修改 modelValue 时同步到编辑器（如 AI 生成 SQL 填充）
watch(
  () => props.modelValue,
  (val) => {
    if (!view) return;
    const current = view.state.doc.toString();
    if (current !== val) {
      view.dispatch({
        changes: { from: 0, to: current.length, insert: val },
      });
    }
  },
);

// schema 变化时重建编辑器（数据库切换）
watch(
  () => props.schema,
  () => {
    if (!editorContainer.value) return;
    const content = view?.state.doc.toString() ?? '';
    view?.destroy();
    createView(editorContainer.value, content);
  },
  { deep: true },
);
</script>

<style scoped>
.sql-editor {
  width: 100%;
  height: 100%;
}
</style>
