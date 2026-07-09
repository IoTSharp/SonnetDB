import { computed, ref, type ComputedRef, type WritableComputedRef } from 'vue';
import type { MessageApi } from 'naive-ui';
import {
  runMaintenance,
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
  };
}
