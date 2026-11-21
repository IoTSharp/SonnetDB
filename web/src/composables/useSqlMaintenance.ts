import { computed, ref, type ComputedRef, type WritableComputedRef } from 'vue';
import type { MessageApi } from 'naive-ui';
import {
  createBackupTask,
  listBackupTasks,
  renameDatabase,
  restoreBackup,
  runBackupTask,
  uploadBackup,
  runMaintenance,
  type BackupTaskDefinition,
  type BackupTaskResponse,
  type IndexLifecycleInfo,
  type MaintenanceResponse,
} from '@/api/schema';
import type { useAuthStore } from '@/stores/auth';
import type { useConnectionsStore } from '@/stores/connections';
import { CONTROL_PLANE_KEY } from '@/stores/sqlConsole';
import type { useWorkbenchHistoryStore } from '@/stores/workbenchHistory';

type AuthStore = ReturnType<typeof useAuthStore>;
type ConnectionsStore = ReturnType<typeof useConnectionsStore>;
type WorkbenchHistoryStore = ReturnType<typeof useWorkbenchHistoryStore>;
type MaintenanceRequest = Parameters<typeof runMaintenance>[2];

export interface SqlMaintenanceOptions {
  auth: AuthStore;
  connections: ConnectionsStore;
  workbenchHistory: WorkbenchHistoryStore;
  targetDb: WritableComputedRef<string>;
  selectedIndex: ComputedRef<IndexLifecycleInfo | null>;
  message: MessageApi;
  loadSchema: (db: string, force?: boolean, syncActive?: boolean) => Promise<void>;
}

export function useSqlMaintenance(options: SqlMaintenanceOptions) {
  const {
    auth,
    connections,
    workbenchHistory,
    targetDb,
    selectedIndex,
    message,
    loadSchema,
  } = options;

  const maintenanceBackupDirectory = ref('');
  const maintenanceRestoreTargetDirectory = ref('');
  const maintenanceBusy = ref('');
  const maintenanceLastResult = ref<MaintenanceResponse | null>(null);
  const backupTasks = ref<BackupTaskResponse[]>([]);
  const backupTaskBusy = ref(false);
  const backupRestoreBusy = ref(false);

  const maintenanceStatus = computed(() =>
    maintenanceLastResult.value
      ? `${maintenanceLastResult.value.status} · ${maintenanceLastResult.value.message}`
      : '');

  async function runHealthCheck(): Promise<void> {
    await runMaintenanceAction(
      'health_check',
      () => ({ operation: 'health_check' }),
      (result) => `${result.message} ${result.checks.length} checks`,
    );
  }

  async function rebuildSelectedIndex(): Promise<void> {
    const index = selectedIndex.value;
    if (!index) {
      message.error('请先在左侧选择一个索引。');
      return;
    }
    await runMaintenanceAction(
      `rebuild:${index.id}`,
      () => ({
        operation: 'rebuild_index',
        targetModel: index.kind === 'fulltext' ? 'document_fulltext' : index.model,
        targetOwner: index.owner,
        targetName: index.name,
      }),
      (result) => result.index?.planned ? '索引重建计划已返回。' : result.message,
    );
  }

  async function verifyBackup(): Promise<void> {
    const path = maintenanceBackupDirectory.value.trim();
    if (!path) {
      message.error('请填写备份目录。');
      return;
    }
    await runMaintenanceAction(
      'backup_verify',
      () => ({ operation: 'backup_verify', backupDirectory: path }),
      (result) => result.backupVerification?.isValid
        ? `备份校验通过，检查 ${result.backupVerification.checkedFiles} 个文件。`
        : result.message,
    );
  }

  async function restoreDryRun(): Promise<void> {
    const backupDirectory = maintenanceBackupDirectory.value.trim();
    const restoreTargetDirectory = maintenanceRestoreTargetDirectory.value.trim();
    if (!backupDirectory || !restoreTargetDirectory) {
      message.error('请填写备份目录和恢复目标目录。');
      return;
    }
    await runMaintenanceAction(
      'restore_dry_run',
      () => ({
        operation: 'restore_dry_run',
        backupDirectory,
        restoreTargetDirectory,
        overwrite: true,
      }),
      (result) => result.restoreDryRun?.isValid
        ? `恢复 dry-run 通过，${result.restoreDryRun.fileCount} 个文件。`
        : result.message,
    );
  }

  async function loadBackupTasks(): Promise<void> {
    try {
      backupTasks.value = (await listBackupTasks(auth.api)).items;
    } catch {
      backupTasks.value = [];
    }
  }

  /** 按用户选定的完整模型创建每日备份任务。 */
  async function createDailyBackupTask(selections: Pick<BackupTaskDefinition['scope'],
    'includeMeasurements' | 'includeTables' | 'includeKeyValue' | 'includeDocuments' | 'includeGraphs'>): Promise<void> {
    backupTaskBusy.value = true;
    try {
      const task: BackupTaskDefinition = {
        id: `daily-${Date.now().toString(36)}`,
        name: '每日备份',
        enabled: true,
        localTime: '02:00',
        timeZoneId: 'Asia/Shanghai',
        destinationRoot: '',
        databaseNames: [],
        scope: {
          ...selections,
          includeObjects: false,
          includeMessageQueue: false,
          includeFullTextIndexes: true,
          measurements: [],
          tables: [],
          keyspaces: [],
          documentCollections: [],
          buckets: [],
          graphs: [],
        },
        retentionDays: 15,
        retentionCount: 15,
        verifyAfterCreate: true,
      };
      const created = await createBackupTask(auth.api, task);
      backupTasks.value = [created, ...backupTasks.value.filter(item => item.definition.id !== created.definition.id)];
      message.success('每日备份任务已创建。');
    } catch (error) {
      message.error(error instanceof Error ? error.message : '创建备份任务失败');
    } finally {
      backupTaskBusy.value = false;
    }
  }


  async function runDailyBackupTask(id: string): Promise<void> {
    backupTaskBusy.value = true;
    try {
      const result = await runBackupTask(auth.api, id);
      backupTasks.value = backupTasks.value.map(item => item.definition.id === id ? result : item);
      message.success(`备份任务已执行，状态 ${result.state.status}。`);
    } catch (error) {
      message.error(error instanceof Error ? error.message : '执行备份任务失败');
    } finally {
      backupTaskBusy.value = false;
    }
  }

  async function restoreFromPathOrUrl(path: string, url: string): Promise<void> {
    backupRestoreBusy.value = true;
    try {
      const result = await restoreBackup(auth.api, path.trim() ? { path: path.trim() } : { url: url.trim() });
      message.success(`已恢复为新数据库 ${result.databaseName}。`);
      await loadSchema(result.databaseName, true);
    } catch (error) {
      message.error(error instanceof Error ? error.message : '在线恢复失败');
    } finally {
      backupRestoreBusy.value = false;
    }
  }

  async function restoreFromUpload(file: File): Promise<void> {
    backupRestoreBusy.value = true;
    try {
      const result = await uploadBackup(auth.api, file);
      message.success(`已恢复为新数据库 ${result.databaseName}。`);
      await loadSchema(result.databaseName, true);
    } catch (error) {
      message.error(error instanceof Error ? error.message : '上传恢复失败');
    } finally {
      backupRestoreBusy.value = false;
    }
  }

  async function renameDatabaseNow(name: string): Promise<void> {
    const current = targetDb.value;
    if (!current || current === CONTROL_PLANE_KEY || !name.trim()) {
      message.error('请输入新的数据库名称。');
      return;
    }
    backupTaskBusy.value = true;
    try {
      await renameDatabase(auth.api, current, name.trim());
      message.success(`数据库已重命名为 ${name.trim()}。`);
      await loadSchema(name.trim(), true);
    } catch (error) {
      message.error(error instanceof Error ? error.message : '数据库重命名失败');
    } finally {
      backupTaskBusy.value = false;
    }
  }

  async function runMaintenanceAction(
    busyKey: string,
    requestFactory: () => MaintenanceRequest,
    successText: (result: MaintenanceResponse) => string,
  ): Promise<void> {
    const db = targetDb.value;
    if (!db || db === CONTROL_PLANE_KEY) {
      message.error('请先选择业务数据库。');
      return;
    }

    maintenanceBusy.value = busyKey;
    try {
      const request = requestFactory();
      const result = await runMaintenance(auth.api, db, request);
      maintenanceLastResult.value = result;
      recordMaintenanceHistory(db, busyKey, request.operation, result);
      if (result.success) {
        message.success(successText(result));
      } else {
        message.warning(result.message);
      }
      await loadSchema(db, true);
    } catch (error) {
      const errorMessage = error instanceof Error ? error.message : '维护操作失败';
      maintenanceLastResult.value = null;
      message.error(errorMessage);
    } finally {
      maintenanceBusy.value = '';
    }
  }

  function recordMaintenanceHistory(
    db: string,
    busyKey: string,
    operation: string,
    result: MaintenanceResponse,
  ): void {
    workbenchHistory.record({
      kind: 'operation',
      status: operation === 'restore_dry_run' ? 'dry-run' : result.success ? 'success' : 'error',
      title: operation.replace(/_/g, ' '),
      target: db,
      database: db,
      connectionId: connections.activeProfileId,
      connectionName: connections.activeProfile.name,
      model: 'maintenance',
      action: busyKey,
      command: operation,
      summary: result.message,
    });
  }

  return {
    maintenanceBackupDirectory,
    maintenanceRestoreTargetDirectory,
    maintenanceBusy,
    maintenanceLastResult,
    maintenanceStatus,
    runHealthCheck,
    rebuildSelectedIndex,
    verifyBackup,
    restoreDryRun,
    backupTasks,
    backupTaskBusy,
    backupRestoreBusy,
    loadBackupTasks,
    createDailyBackupTask,
    runDailyBackupTask,
    restoreFromPathOrUrl,
    restoreFromUpload,
    renameDatabaseNow,
  };
}
