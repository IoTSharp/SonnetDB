import { shallowRef } from 'vue';
import { previewEnabled } from './policy';

export const previewSession = shallowRef({ generation: 0, database: '', canWrite: false, blocked: false, message: '' });
const controllers = new Set<AbortController>();
let retainedBytes = 0;

export function registerPreviewRequest(controller: AbortController): () => void {
  controllers.add(controller);
  return () => controllers.delete(controller);
}

export function clearPreviewSession(message = '', blocked = false): void {
  if (!previewEnabled) return;
  for (const controller of controllers) controller.abort();
  controllers.clear();
  retainedBytes = 0;
  previewSession.value = { generation: previewSession.value.generation + 1, database: '', canWrite: false, blocked, message };
  // Preview never restores drafts/results across identities, page loads or Server restarts.
  for (const key of ['sndb.sql.console.tabs.v1', 'sndb.workbench.history.v1']) localStorage.removeItem(key);
}

export function selectPreviewDatabase(database: string, canWrite: boolean): void {
  clearPreviewSession();
  previewSession.value = { ...previewSession.value, database, canWrite };
}

export function retainPreviewBytes(bytes: number, maximum: number): void {
  if (retainedBytes + bytes > maximum) throw new Error('当前结果窗口超过 4 MiB；请清空后显式重读。');
  retainedBytes += bytes;
}
