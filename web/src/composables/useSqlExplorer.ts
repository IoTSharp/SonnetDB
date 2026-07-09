import { computed, ref, type ComputedRef, type WritableComputedRef } from 'vue';
import type { MessageApi } from 'naive-ui';
import {
  execControlPlaneSql,
  isValidIdentifier,
} from '@/api/sql';
import { listDatabases } from '@/api/server';
import {
  fetchFullTextIndexes,
  fetchKvKeyspaces,
  fetchMqTopics,
  fetchObjectBuckets,
  fetchVectorIndexes,
} from '@/api/management';
import {
  fetchSchema,
  type MeasurementInfo,
  type SchemaResponse,
  type TableInfo,
} from '@/api/schema';
import type { useAuthStore } from '@/stores/auth';
import {
  CONTROL_PLANE_KEY,
  type SqlConsoleTab,
  type useSqlConsoleStore,
} from '@/stores/sqlConsole';
import {
  databaseEmptyText,
  databaseMeta,
  documentCollectionMatchesFilter,
  emptyManagementInfo,
  explorerGroups,
  fullTextIndexMatchesFilter,
  indexMatchesFilter,
  measurementMatchesFilter,
  normalizeActiveExplorerKey,
  tableMatchesFilter,
  vectorIndexMatchesFilter,
  type DatabaseTreeNode,
  type ManagementExplorerInfo,
  type SystemTreeNode,
} from '@/utils/managementExplorer';

type AuthStore = ReturnType<typeof useAuthStore>;
type SqlConsoleStore = ReturnType<typeof useSqlConsoleStore>;

export interface SqlExplorerOptions {
  auth: AuthStore;
  sqlConsole: SqlConsoleStore;
  targetDb: WritableComputedRef<string>;
  activeTab: ComputedRef<SqlConsoleTab | null>;
  message: MessageApi;
}

export function useSqlExplorer(options: SqlExplorerOptions) {
  const {
    auth,
    sqlConsole,
    targetDb,
    activeTab,
    message,
  } = options;

  const databases = ref<string[]>([]);
  const schema = ref<MeasurementInfo[]>([]);
  const schemaByDb = ref<Record<string, SchemaResponse>>({});
  const managementByDb = ref<Record<string, ManagementExplorerInfo>>({});
  const schemaLoadingByDb = ref<Record<string, boolean>>({});
  const schemaErrorByDb = ref<Record<string, string>>({});
  const schemaFilter = ref('');
  const newDatabaseName = ref('');
  const showCreateDatabaseDialog = ref(false);
  const databaseActionBusy = ref(false);
  const expandedDatabases = ref<Record<string, boolean>>({});
  const loadingDbs = ref(false);
  const loadingSchema = ref(false);
  const activeExplorerKey = ref('');
  const openGroups = ref<Record<string, boolean>>({
    databases: true,
  });

  const currentSchemaResponse = computed<SchemaResponse | null>(() => {
    const db = targetDb.value;
    if (!db || db === CONTROL_PLANE_KEY) return null;
    return schemaByDb.value[db] ?? null;
  });

  const currentSchema = computed(() => currentSchemaResponse.value?.measurements ?? schema.value);

  const databaseTree = computed<DatabaseTreeNode[]>(() => {
    const keyword = schemaFilter.value.trim().toLowerCase();
    return databases.value.flatMap((name) => {
      const dbSchema = schemaByDb.value[name];
      const measurements = dbSchema?.measurements ?? [];
      const tables = dbSchema?.tables ?? [];
      const documents = dbSchema?.documentCollections ?? [];
      const indexes = dbSchema?.indexes ?? [];
      const management = managementByDb.value[name] ?? emptyManagementInfo();
      const kvKeyspaces = management.kvKeyspaces;
      const vectorIndexes = management.vectorIndexes;
      const fullTextIndexes = management.fullTextIndexes;
      const mqTopics = management.mqTopics;
      const buckets = management.buckets;
      const backupStatus = dbSchema?.backupStatus ?? null;
      const loaded = hasCachedSchema(name);
      const loading = Boolean(schemaLoadingByDb.value[name]);
      const error = schemaErrorByDb.value[name] || management.error || '';
      const dbMatches = !keyword || name.toLowerCase().includes(keyword);
      const filteredMeasurements = !keyword || dbMatches
        ? measurements
        : measurements.filter((measurement) => measurementMatchesFilter(measurement, keyword));
      const filteredTables = !keyword || dbMatches
        ? tables
        : tables.filter((table) => tableMatchesFilter(table, keyword));
      const filteredDocuments = !keyword || dbMatches
        ? documents
        : documents.filter((collection) => documentCollectionMatchesFilter(collection, keyword));
      const filteredIndexes = !keyword || dbMatches
        ? indexes
        : indexes.filter((index) => indexMatchesFilter(index, keyword));
      const filteredKvKeyspaces = !keyword || dbMatches
        ? kvKeyspaces
        : kvKeyspaces.filter((keyspace) => keyspace.toLowerCase().includes(keyword));
      const filteredVectorIndexes = !keyword || dbMatches
        ? vectorIndexes
        : vectorIndexes.filter((index) => vectorIndexMatchesFilter(index, keyword));
      const filteredFullTextIndexes = !keyword || dbMatches
        ? fullTextIndexes
        : fullTextIndexes.filter((index) => fullTextIndexMatchesFilter(index, keyword));
      const filteredMqTopics = !keyword || dbMatches
        ? mqTopics
        : mqTopics.filter((topic) => topic.topic.toLowerCase().includes(keyword));
      const filteredBuckets = !keyword || dbMatches
        ? buckets
        : buckets.filter((bucket) => bucket.name.toLowerCase().includes(keyword));

      if (keyword && !dbMatches
        && filteredMeasurements.length === 0
        && filteredTables.length === 0
        && filteredDocuments.length === 0
        && filteredIndexes.length === 0
        && filteredKvKeyspaces.length === 0
        && filteredVectorIndexes.length === 0
        && filteredFullTextIndexes.length === 0
        && filteredMqTopics.length === 0
        && filteredBuckets.length === 0) {
        return [];
      }

      return [{
        name,
        meta: databaseMeta(
          loaded,
          loading,
          error,
          measurements.length,
          tables.length,
          documents.length,
          indexes.length,
          kvKeyspaces.length,
          mqTopics.length,
          buckets.length,
        ),
        measurements: filteredMeasurements,
        tables: filteredTables,
        documents: filteredDocuments,
        indexes: filteredIndexes,
        kvKeyspaces: filteredKvKeyspaces,
        vectorIndexes: filteredVectorIndexes,
        fullTextIndexes: filteredFullTextIndexes,
        mqTopics: filteredMqTopics,
        buckets: filteredBuckets,
        backupStatus,
        loading,
        error,
        emptyText: databaseEmptyText(loaded, loading, error, keyword),
      }];
    });
  });

  const systemTreeNode = computed<SystemTreeNode | null>(() => {
    if (!auth.isSuperuser) return null;
    const keyword = schemaFilter.value.trim().toLowerCase();
    if (keyword && !'system control plane'.includes(keyword)) return null;
    return { name: 'system', meta: 'control plane' };
  });

  const canCreateDatabase = computed(() => {
    const name = newDatabaseName.value.trim();
    return auth.isSuperuser
      && !databaseActionBusy.value
      && isValidIdentifier(name)
      && !databases.value.includes(name);
  });

  const canDropDatabase = computed(() =>
    auth.isSuperuser
    && !databaseActionBusy.value
    && targetDb.value.length > 0
    && targetDb.value !== CONTROL_PLANE_KEY
    && databases.value.includes(targetDb.value));

  const trajectoryInitialDb = computed(() => {
    if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) return targetDb.value;
    return databases.value[0] ?? '';
  });

  const trajectoryInitialMeasurement = computed(() => {
    if (!trajectoryInitialDb.value) return '';
    const measurements = schemaByDb.value[trajectoryInitialDb.value]?.measurements ?? [];
    const active = measurements.find((measurement) => measurement.name === activeExplorerKey.value);
    if (active?.columns.some(isGeoField)) return active.name;
    return measurements.find((measurement) => measurement.columns.some(isGeoField))?.name ?? '';
  });

  const selectedMeasurement = computed(() => {
    if (!schema.value.length) return null;
    const byActive = schema.value.find((measurement) => measurement.name === activeExplorerKey.value);
    return byActive ?? schema.value[0] ?? null;
  });

  const selectedTable = computed<TableInfo | null>(() => {
    const tables = currentSchemaResponse.value?.tables ?? [];
    if (tables.length === 0) return null;
    const activeTableName = activeExplorerKey.value.startsWith('table:')
      ? activeExplorerKey.value.slice('table:'.length)
      : '';
    return tables.find((table) => table.name === activeTableName)
      ?? (activeTableName ? null : tables[0] ?? null);
  });

  const selectedIndex = computed(() => {
    const indexes = currentSchemaResponse.value?.indexes ?? [];
    return indexes.find((index) => index.id === activeExplorerKey.value) ?? null;
  });

  function hasCachedSchema(db: string): boolean {
    return Object.prototype.hasOwnProperty.call(schemaByDb.value, db);
  }

  function isGeoField(column: { role: string; dataType: string }): boolean {
    return column.role.toLowerCase() === 'field' && column.dataType.toLowerCase() === 'geopoint';
  }

  function openCreateDatabaseDialog(): void {
    if (!auth.isSuperuser || databaseActionBusy.value) return;
    newDatabaseName.value = '';
    showCreateDatabaseDialog.value = true;
  }

  function closeCreateDatabaseDialog(): void {
    if (databaseActionBusy.value) return;
    showCreateDatabaseDialog.value = false;
    newDatabaseName.value = '';
  }

  function toggleGroup(key: string): void {
    openGroups.value[key] = !openGroups.value[key];
  }

  function toggleDatabaseExpansion(db: string): void {
    expandedDatabases.value = {
      ...expandedDatabases.value,
      [db]: !expandedDatabases.value[db],
    };
    if (expandedDatabases.value[db] && !hasCachedSchema(db) && db !== CONTROL_PLANE_KEY) {
      void loadSchema(db, false, db === targetDb.value);
    }
  }

  function selectDatabase(db: string): void {
    if (db === CONTROL_PLANE_KEY && !auth.isSuperuser) return;
    targetDb.value = db;
    if (db !== CONTROL_PLANE_KEY) {
      expandedDatabases.value = {
        ...expandedDatabases.value,
        [db]: true,
      };
      void loadSchema(db);
    } else {
      schema.value = [];
      activeExplorerKey.value = '';
    }
  }

  async function refreshWorkbench(): Promise<void> {
    await reloadDbs();
    if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) {
      await loadSchema(targetDb.value, true);
    }
  }

  async function reloadDbs(): Promise<void> {
    loadingDbs.value = true;
    if (activeTab.value) {
      sqlConsole.patchActiveTab({ errorMsg: '' });
    }
    try {
      const result = await listDatabases(auth.api);
      if (result.error) {
        if (activeTab.value) {
          sqlConsole.patchActiveTab({ errorMsg: result.error.message });
        }
        return;
      }
      databases.value = result.databases;
      syncDatabaseState(result.databases);
      normalizeTarget();
    } finally {
      loadingDbs.value = false;
    }
  }

  function syncDatabaseState(currentDatabases: string[]): void {
    const currentSet = new Set(currentDatabases);
    schemaByDb.value = Object.fromEntries(
      Object.entries(schemaByDb.value).filter(([name]) => currentSet.has(name)),
    );
    managementByDb.value = Object.fromEntries(
      Object.entries(managementByDb.value).filter(([name]) => currentSet.has(name)),
    );
    schemaLoadingByDb.value = Object.fromEntries(
      Object.entries(schemaLoadingByDb.value).filter(([name]) => currentSet.has(name)),
    );
    schemaErrorByDb.value = Object.fromEntries(
      Object.entries(schemaErrorByDb.value).filter(([name]) => currentSet.has(name)),
    );
    expandedDatabases.value = Object.fromEntries(
      Object.entries(expandedDatabases.value).filter(([name]) => currentSet.has(name)),
    );
  }

  function normalizeTarget(): void {
    if (auth.isSuperuser) {
      if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY && databases.value.includes(targetDb.value)) {
        return;
      }
      if (databases.value.length > 0) {
        targetDb.value = databases.value[0];
        return;
      }
      targetDb.value = CONTROL_PLANE_KEY;
      return;
    }

    if (targetDb.value && databases.value.includes(targetDb.value)) {
      return;
    }
    targetDb.value = databases.value[0] ?? '';
  }

  async function loadSchema(db: string, force = false, syncActive = true): Promise<void> {
    if (!db || db === CONTROL_PLANE_KEY) {
      if (syncActive && targetDb.value === db) {
        schema.value = [];
        activeExplorerKey.value = '';
      }
      return;
    }

    if (hasCachedSchema(db) && !force) {
      if (syncActive && targetDb.value === db) {
        const dbSchema = schemaByDb.value[db];
        schema.value = dbSchema?.measurements ?? [];
        if (dbSchema) {
          activeExplorerKey.value = normalizeActiveExplorerKey(activeExplorerKey.value, dbSchema, managementByDb.value[db]);
        }
      }
      return;
    }

    schemaLoadingByDb.value = {
      ...schemaLoadingByDb.value,
      [db]: true,
    };
    schemaErrorByDb.value = {
      ...schemaErrorByDb.value,
      [db]: '',
    };
    loadingSchema.value = true;
    try {
      const [schemaResult, managementResult] = await Promise.allSettled([
        fetchSchema(auth.api, db),
        loadManagementExplorerInfo(db),
      ]);
      if (schemaResult.status === 'rejected') {
        throw schemaResult.reason;
      }

      const resp = schemaResult.value;
      const measurements = resp.measurements ?? [];
      schemaByDb.value = {
        ...schemaByDb.value,
        [db]: {
          measurements,
          tables: resp.tables ?? [],
          documentCollections: resp.documentCollections ?? [],
          indexes: resp.indexes ?? [],
          backupStatus: resp.backupStatus ?? null,
        },
      };
      managementByDb.value = {
        ...managementByDb.value,
        [db]: managementResult.status === 'fulfilled'
          ? managementResult.value
          : emptyManagementInfo(managementResult.reason instanceof Error
            ? managementResult.reason.message
            : '加载管理元数据失败'),
      };
      if (syncActive && targetDb.value === db) {
        schema.value = measurements;
        activeExplorerKey.value = normalizeActiveExplorerKey(activeExplorerKey.value, schemaByDb.value[db], managementByDb.value[db]);
      }
    } catch (error) {
      const errorMessage = error instanceof Error ? error.message : '加载 Schema 失败';
      schemaErrorByDb.value = {
        ...schemaErrorByDb.value,
        [db]: errorMessage,
      };
      if (syncActive && targetDb.value === db) {
        schema.value = [];
        activeExplorerKey.value = '';
      }
    } finally {
      schemaLoadingByDb.value = {
        ...schemaLoadingByDb.value,
        [db]: false,
      };
      loadingSchema.value = false;
    }
  }

  async function loadManagementExplorerInfo(db: string): Promise<ManagementExplorerInfo> {
    const [kv, vector, fullText, mq, buckets] = await Promise.allSettled([
      fetchKvKeyspaces(auth.api, db),
      fetchVectorIndexes(auth.api, db),
      fetchFullTextIndexes(auth.api, db),
      fetchMqTopics(auth.api, db),
      fetchObjectBuckets(auth.api, db),
    ]);

    const errors = [kv, vector, fullText, mq, buckets]
      .filter((item): item is PromiseRejectedResult => item.status === 'rejected')
      .map((item) => item.reason instanceof Error ? item.reason.message : String(item.reason))
      .filter(Boolean);

    return {
      kvKeyspaces: kv.status === 'fulfilled' ? kv.value : [],
      vectorIndexes: vector.status === 'fulfilled' ? vector.value : [],
      fullTextIndexes: fullText.status === 'fulfilled' ? fullText.value : [],
      mqTopics: mq.status === 'fulfilled' ? mq.value : [],
      buckets: buckets.status === 'fulfilled' ? buckets.value : [],
      error: errors[0] ?? '',
    };
  }

  async function createDatabase(): Promise<void> {
    const name = newDatabaseName.value.trim();
    if (!isValidIdentifier(name)) {
      message.error('数据库名必须以字母开头，仅包含字母数字下划线。');
      return;
    }
    if (!auth.isSuperuser) {
      message.error('当前账号没有创建数据库权限。');
      return;
    }

    databaseActionBusy.value = true;
    try {
      const rs = await execControlPlaneSql(auth.api, `CREATE DATABASE ${name}`);
      if (rs.error) {
        message.error(rs.error.message);
        return;
      }
      message.success(`已创建数据库 ${name}`);
      newDatabaseName.value = '';
      showCreateDatabaseDialog.value = false;
      await reloadDbs();
      selectDatabase(name);
    } finally {
      databaseActionBusy.value = false;
    }
  }

  async function dropActiveDatabase(): Promise<void> {
    if (!canDropDatabase.value) return;
    const db = targetDb.value;
    databaseActionBusy.value = true;
    try {
      const rs = await execControlPlaneSql(auth.api, `DROP DATABASE ${db}`);
      if (rs.error) {
        message.error(rs.error.message);
        return;
      }
      message.success(`已删除数据库 ${db}`);
      schemaByDb.value = Object.fromEntries(
        Object.entries(schemaByDb.value).filter(([name]) => name !== db),
      );
      await reloadDbs();
      normalizeTarget();
      if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) {
        await loadSchema(targetDb.value, true);
      }
    } finally {
      databaseActionBusy.value = false;
    }
  }

  function resetExplorerCache(): void {
    databases.value = [];
    schema.value = [];
    schemaByDb.value = {};
    managementByDb.value = {};
    schemaLoadingByDb.value = {};
    schemaErrorByDb.value = {};
    expandedDatabases.value = {};
    activeExplorerKey.value = '';
  }

  return {
    databases,
    schema,
    schemaByDb,
    managementByDb,
    schemaLoadingByDb,
    schemaErrorByDb,
    schemaFilter,
    newDatabaseName,
    showCreateDatabaseDialog,
    databaseActionBusy,
    expandedDatabases,
    loadingDbs,
    loadingSchema,
    activeExplorerKey,
    openGroups,
    currentSchemaResponse,
    currentSchema,
    databaseTree,
    systemTreeNode,
    canCreateDatabase,
    canDropDatabase,
    trajectoryInitialDb,
    trajectoryInitialMeasurement,
    selectedMeasurement,
    selectedTable,
    selectedIndex,
    explorerGroups,
    openCreateDatabaseDialog,
    closeCreateDatabaseDialog,
    toggleGroup,
    toggleDatabaseExpansion,
    selectDatabase,
    refreshWorkbench,
    reloadDbs,
    loadSchema,
    createDatabase,
    dropActiveDatabase,
    resetExplorerCache,
  };
}
