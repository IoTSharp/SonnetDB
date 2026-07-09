import type { SqlResultSet } from '@/api/sql';
import { rowsToObjects } from '@/api/sql';
import { getStudioNativeBridge, type StudioFileDialogFilter } from '@/api/studioNativeBridge';
import { formatSqlValue } from '@/utils/sqlValue';

export type ResultExportFormat = 'csv' | 'json';

export interface ResultRowObject extends Record<string, unknown> {
  __rowIndex?: number;
}

export function resultRowsToObjects(result: SqlResultSet | null | undefined): ResultRowObject[] {
  if (!result?.hasColumns) return [];
  return rowsToObjects<Record<string, unknown>>(result)
    .map((row, index) => ({ __rowIndex: index, ...row }));
}

export function buildCsv(rows: readonly Record<string, unknown>[], columns: readonly string[]): string {
  const escape = (value: unknown): string => {
    const text = formatSqlValue(value);
    const normalized = text.replace(/\r?\n/g, ' ');
    return /[",\n]/.test(normalized) ? `"${normalized.replace(/"/g, '""')}"` : normalized;
  };

  const lines = [
    columns.map(escape).join(','),
    ...rows.map((row) => columns.map((column) => escape(row[column])).join(',')),
  ];
  return `${lines.join('\n')}\n`;
}

export function buildJson(rows: readonly Record<string, unknown>[], columns: readonly string[]): string {
  const payload = rows.map((row) => Object.fromEntries(columns.map((column) => [column, row[column]])));
  return `${JSON.stringify(payload, null, 2)}\n`;
}

export function buildExportText(
  rows: readonly Record<string, unknown>[],
  columns: readonly string[],
  format: ResultExportFormat,
): string {
  return format === 'json' ? buildJson(rows, columns) : buildCsv(rows, columns);
}

export async function copyText(value: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(value);
    return true;
  } catch {
    return false;
  }
}

export function downloadText(fileName: string, value: string, contentType: string): void {
  const blob = new Blob([value], { type: contentType });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

export async function saveTextFile(fileName: string, value: string, contentType: string): Promise<'native' | 'browser' | 'cancelled'> {
  const bridge = await getStudioNativeBridge();
  if (bridge?.manifest.capabilities.includes('dialogs.saveFile')) {
    const result = await bridge.saveTextFile({
      title: 'Save export',
      suggestedName: fileName,
      content: value,
      contentType,
      filters: filtersFor(fileName, contentType),
    });
    if (result.error) throw new Error(result.error);
    if (result.canceled) return 'cancelled';
    return 'native';
  }

  downloadText(fileName, value, contentType);
  return 'browser';
}

export function safeFileStem(value: string, fallback: string): string {
  const normalized = value
    .trim()
    .replace(/\s+/g, '_')
    .replace(/[^\w.-]+/g, '_')
    .replace(/^_+|_+$/g, '');
  return normalized || fallback;
}

function filtersFor(fileName: string, contentType: string): StudioFileDialogFilter[] {
  const lowerName = fileName.toLowerCase();
  if (lowerName.endsWith('.json') || contentType.includes('json')) {
    return [{ name: 'JSON', extensions: ['json'] }];
  }
  if (lowerName.endsWith('.csv') || contentType.includes('csv')) {
    return [{ name: 'CSV', extensions: ['csv'] }];
  }
  return [{ name: 'Text', extensions: ['txt'] }];
}
