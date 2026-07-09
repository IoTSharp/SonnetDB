import { computed, ref, type Ref } from 'vue';
import { useRouter } from 'vue-router';
import type { DropdownOption, MessageApi } from 'naive-ui';
import type {
  FullTextIndexStat,
  VectorIndexStat,
} from '@/api/management';
import type {
  DocumentCollectionInfo,
  IndexLifecycleInfo,
  MeasurementInfo,
  TableInfo,
} from '@/api/schema';
import {
  buildSelectDraft,
  formatSqlIdentifier,
  type WorkbenchTool,
} from '@/utils/sqlWorkbench';
import type {
  ExplorerContextMenuState,
  ExplorerItem,
} from '@/utils/managementExplorer';

export interface SqlExplorerRoutingOptions {
  activeExplorerKey: Ref<string>;
  message: MessageApi;
  selectDatabase: (db: string) => void;
  setWorkbenchTool: (tool: WorkbenchTool) => void;
  setSqlDraft: (sqlText: string) => void;
  loadSchema: (db: string, force?: boolean, syncActive?: boolean) => Promise<void>;
  runHealthCheck: () => Promise<void>;
}

export function useSqlExplorerRouting(options: SqlExplorerRoutingOptions) {
  const {
    activeExplorerKey,
    message,
    selectDatabase,
    setWorkbenchTool,
    setSqlDraft,
    loadSchema,
    runHealthCheck,
  } = options;
  const router = useRouter();

  const contextMenu = ref<ExplorerContextMenuState>({
    show: false,
    x: 0,
    y: 0,
    db: '',
    item: null,
  });

  const contextMenuOptions = computed<DropdownOption[]>(() => {
    const item = contextMenu.value.item;
    if (!item) return [];

    const options: DropdownOption[] = [
      { label: 'Open workbench', key: 'open-workbench' },
      { label: 'Copy name', key: 'copy-name' },
      { label: 'Refresh database', key: 'refresh-database' },
    ];

    if (canOpenInSql(item)) {
      options.splice(1, 0, { label: 'Open in SQL', key: 'open-sql' });
    }
    return options;
  });

  function openExplorerContextMenu(event: MouseEvent, db: string, item: ExplorerItem): void {
    selectExplorerItem(db, item);
    contextMenu.value = {
      show: true,
      x: event.clientX,
      y: event.clientY,
      db,
      item,
    };
  }

  function hideExplorerContextMenu(): void {
    contextMenu.value.show = false;
  }

  async function onExplorerContextSelect(key: string | number): Promise<void> {
    const action = String(key);
    const { db, item } = contextMenu.value;
    hideExplorerContextMenu();
    if (!item) return;

    if (action === 'open-workbench') {
      routeExplorerItem(db, item);
      return;
    }
    if (action === 'open-sql') {
      openExplorerItemInSql(item);
      return;
    }
    if (action === 'copy-name') {
      await copyText(item.name);
      return;
    }
    if (action === 'refresh-database') {
      await loadSchema(db, true);
    }
  }

  function canOpenInSql(item: ExplorerItem): boolean {
    return item.model === 'measurement'
      || item.model === 'table'
      || item.model === 'document'
      || item.model === 'index'
      || item.model === 'vector'
      || item.model === 'fulltext'
      || item.model === 'backup';
  }

  function selectExplorerItem(db: string, item: ExplorerItem): void {
    selectDatabase(db);
    activeExplorerKey.value = item.key;
  }

  function routeExplorerItem(db: string, item: ExplorerItem): void {
    selectExplorerItem(db, item);
    void router.replace({
      name: 'sql',
      query: item.model === 'table'
        ? { tool: 'table', model: item.model, node: item.name }
        : item.model === 'kv'
          ? { tool: 'kv', model: item.model, node: item.name }
        : item.model === 'mq'
          ? { tool: 'mq', model: item.model, node: item.name }
        : item.model === 'measurement'
          ? {}
          : { model: item.model, node: item.name },
    });
  }

  function openExplorerItem(db: string, item: ExplorerItem): void {
    routeExplorerItem(db, item);
  }

  function openExplorerItemInSql(item: ExplorerItem): void {
    if (item.model === 'measurement') {
      openMeasurement(item.payload as MeasurementInfo);
      return;
    }
    if (item.model === 'table') {
      openTable(item.payload as TableInfo);
      return;
    }
    if (item.model === 'document') {
      openDocumentCollection(item.payload as DocumentCollectionInfo);
      return;
    }
    if (item.model === 'index') {
      showIndex(item.payload as IndexLifecycleInfo);
      return;
    }
    if (item.model === 'vector') {
      const index = item.payload as VectorIndexStat;
      setWorkbenchTool('sql');
      setSqlDraft(`DESCRIBE MEASUREMENT ${formatSqlIdentifier(index.measurement)}`);
      return;
    }
    if (item.model === 'fulltext') {
      const index = item.payload as FullTextIndexStat;
      setWorkbenchTool('sql');
      setSqlDraft(`SHOW FULLTEXT INDEXES ON ${formatSqlIdentifier(index.collection)}`);
      return;
    }
    if (item.model === 'backup') {
      void runHealthCheck();
    }
  }

  function openMeasurement(measurement: MeasurementInfo): void {
    setWorkbenchTool('sql');
    setSqlDraft(buildSelectDraft(measurement, false));
  }

  function openTable(table: TableInfo): void {
    setWorkbenchTool('sql');
    setSqlDraft(`DESCRIBE TABLE ${formatSqlIdentifier(table.name)}`);
  }

  function openDocumentCollection(collection: DocumentCollectionInfo): void {
    setWorkbenchTool('sql');
    setSqlDraft(`DESCRIBE DOCUMENT COLLECTION ${formatSqlIdentifier(collection.name)}`);
  }

  function showIndex(index: IndexLifecycleInfo): void {
    setWorkbenchTool('sql');
    if (index.model === 'table') {
      setSqlDraft(`SHOW INDEXES ON ${formatSqlIdentifier(index.owner)}`);
      return;
    }
    if (index.kind === 'json_path') {
      setSqlDraft(`SHOW JSON INDEXES ON ${formatSqlIdentifier(index.owner)}`);
      return;
    }
    if (index.kind === 'fulltext') {
      setSqlDraft(`SHOW FULLTEXT INDEXES ON ${formatSqlIdentifier(index.owner)}`);
      return;
    }
    setSqlDraft(`DESCRIBE MEASUREMENT ${formatSqlIdentifier(index.owner)}`);
  }

  async function copyText(value: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(value);
      message.success('Copied');
    } catch {
      message.warning(value);
    }
  }

  return {
    contextMenu,
    contextMenuOptions,
    openExplorerContextMenu,
    hideExplorerContextMenu,
    onExplorerContextSelect,
    selectExplorerItem,
    openExplorerItem,
  };
}
