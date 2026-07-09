import type { TableColumnInfo, TableForeignKeyInfo, TableInfo } from '@/api/schema';
import {
  sqlParameterFromValue,
  type SqlParameters,
  type SqlStatementRequest,
} from '@/api/sql';
import { buildCsv, buildJson, type ResultExportFormat } from '@/utils/resultExport';
import { formatSqlIdentifier } from '@/utils/sqlWorkbench';

export type RelationalImportFormat = 'csv' | 'json';

export interface ParsedImportData {
  headers: string[];
  rows: Record<string, unknown>[];
  errors: ImportRowError[];
}

export interface ImportRowError {
  rowNumber: number;
  column?: string;
  message: string;
}

export interface PreparedImportRow {
  rowNumber: number;
  values: Record<string, unknown>;
}

export interface ImportValidationResult {
  rows: PreparedImportRow[];
  errors: ImportRowError[];
}

export type ImportMapping = Record<string, string>;

export function parseImportText(format: RelationalImportFormat, text: string): ParsedImportData {
  return format === 'json' ? parseJsonImport(text) : parseCsvImport(text);
}

export function buildDefaultImportMapping(table: TableInfo, headers: readonly string[]): ImportMapping {
  const byLower = new Map(headers.map((header) => [header.toLowerCase(), header]));
  const mapping: ImportMapping = {};
  for (const column of table.columns) {
    mapping[column.name] = byLower.get(column.name.toLowerCase()) ?? '';
  }
  return mapping;
}

export function validateImportRows(
  table: TableInfo,
  parsed: ParsedImportData,
  mapping: ImportMapping,
): ImportValidationResult {
  const errors: ImportRowError[] = [...parsed.errors];
  const prepared: PreparedImportRow[] = [];
  const mappedColumns = table.columns.filter((column) => Boolean(mapping[column.name]));

  for (let rowIndex = 0; rowIndex < parsed.rows.length; rowIndex++) {
    const rowNumber = rowIndex + 2;
    const source = parsed.rows[rowIndex];
    const values: Record<string, unknown> = {};
    let rowHasErrors = false;

    for (const column of table.columns) {
      const sourceName = mapping[column.name];
      if (!sourceName) {
        if (!column.isNullable && !column.isRowVersion && !column.isPrimaryKey) {
          errors.push({
            rowNumber,
            column: column.name,
            message: '必填列未映射。',
          });
          rowHasErrors = true;
        }
        if (column.isPrimaryKey && !column.isRowVersion) {
          errors.push({
            rowNumber,
            column: column.name,
            message: '主键列必须映射。',
          });
          rowHasErrors = true;
        }
        continue;
      }

      const coerced = coerceImportValue(column, source[sourceName]);
      if (!coerced.ok) {
        errors.push({
          rowNumber,
          column: column.name,
          message: coerced.message,
        });
        rowHasErrors = true;
        continue;
      }

      values[column.name] = coerced.value;
    }

    if (!rowHasErrors && mappedColumns.length > 0) {
      prepared.push({ rowNumber, values });
    }
  }

  return { rows: prepared, errors };
}

export function buildImportStatements(
  table: TableInfo,
  rows: readonly PreparedImportRow[],
  mapping: ImportMapping,
): SqlStatementRequest[] {
  const columns = table.columns.filter((column) => Boolean(mapping[column.name]));
  return rows.map((row, rowIndex) => {
    const parameters: SqlParameters = {};
    const parameterNames = columns.map((column, columnIndex) => {
      const name = makeParameterName(column.name, rowIndex, columnIndex);
      parameters[name] = sqlParameterFromValue(row.values[column.name]);
      return `@${name}`;
    });

    const sql = [
      `INSERT INTO ${formatSqlIdentifier(table.name)} (`,
      `  ${columns.map((column) => formatSqlIdentifier(column.name)).join(', ')}`,
      ') VALUES (',
      `  ${parameterNames.join(', ')}`,
      ');',
    ].join('\n');

    return { sql, parameters };
  });
}

export function buildTableDdl(table: TableInfo): string {
  const columnLines = table.columns.map((column) => {
    const parts = [
      '  ' + formatSqlIdentifier(column.name),
      formatDdlType(column.dataType),
    ];
    if (column.isRowVersion) {
      parts.push('ROWVERSION');
    } else {
      parts.push(column.isNullable ? 'NULL' : 'NOT NULL');
    }
    return parts.join(' ');
  });

  columnLines.push(`  PRIMARY KEY (${table.primaryKey.map(formatSqlIdentifier).join(', ')})`);

  return [
    `CREATE TABLE ${formatSqlIdentifier(table.name)} (`,
    columnLines.join(',\n'),
    ');',
  ].join('\n');
}

export function buildTableIndexesDdl(table: TableInfo): string[] {
  return table.indexes.map((index) => {
    if (index.jsonPath) {
      return `CREATE JSON INDEX ${formatSqlIdentifier(index.name)} ON ${formatSqlIdentifier(table.name)} (${formatSqlIdentifier(index.columns[0] ?? '')}, ${quoteSqlString(index.jsonPath)});`;
    }
    const unique = index.isUnique ? 'UNIQUE ' : '';
    return `CREATE ${unique}INDEX ${formatSqlIdentifier(index.name)} ON ${formatSqlIdentifier(table.name)} (${index.columns.map(formatSqlIdentifier).join(', ')});`;
  });
}

export function buildForeignKeyDdl(table: TableInfo, foreignKey: TableForeignKeyInfo): string {
  const onDelete = formatOnDelete(foreignKey.onDelete);
  return [
    `ALTER TABLE ${formatSqlIdentifier(table.name)}`,
    `  ADD CONSTRAINT ${formatSqlIdentifier(foreignKey.name)}`,
    `  FOREIGN KEY (${foreignKey.columns.map(formatSqlIdentifier).join(', ')})`,
    `  REFERENCES ${formatSqlIdentifier(foreignKey.principalTable)} (${foreignKey.principalColumns.map(formatSqlIdentifier).join(', ')})${onDelete};`,
  ].join('\n');
}

export function buildSchemaDdl(tables: readonly TableInfo[], current?: TableInfo | null): string {
  const selected = current ? [current] : [...tables];
  const tableNames = new Set(selected.map((table) => table.name));
  const blocks: string[] = [];

  for (const table of selected) {
    blocks.push(buildTableDdl(table));
  }

  for (const table of selected) {
    blocks.push(...buildTableIndexesDdl(table));
  }

  for (const table of selected) {
    for (const foreignKey of table.foreignKeys ?? []) {
      if (current || tableNames.has(foreignKey.principalTable)) {
        blocks.push(buildForeignKeyDdl(table, foreignKey));
      }
    }
  }

  return blocks.join('\n\n') + '\n';
}

export function exportRowsText(
  rows: readonly Record<string, unknown>[],
  columns: readonly string[],
  format: ResultExportFormat,
): string {
  return format === 'json' ? buildJson(rows, columns) : buildCsv(rows, columns);
}

function parseCsvImport(text: string): ParsedImportData {
  const parsed = parseCsvRows(text);
  if (parsed.errors.length > 0) {
    return { headers: [], rows: [], errors: parsed.errors };
  }
  if (parsed.rows.length === 0) {
    return { headers: [], rows: [], errors: [{ rowNumber: 1, message: 'CSV 文件为空。' }] };
  }

  const headers = parsed.rows[0].map((value) => value.trim());
  const errors: ImportRowError[] = [];
  headers.forEach((header, index) => {
    if (!header) {
      errors.push({ rowNumber: 1, column: String(index + 1), message: 'CSV 表头不能为空。' });
    }
  });

  const seen = new Set<string>();
  for (const header of headers) {
    const normalized = header.toLowerCase();
    if (seen.has(normalized)) {
      errors.push({ rowNumber: 1, column: header, message: 'CSV 表头重复。' });
    }
    seen.add(normalized);
  }

  const rows = parsed.rows.slice(1)
    .filter((cells) => cells.some((cell) => cell.trim().length > 0))
    .map((cells) => Object.fromEntries(headers.map((header, index) => [header, cells[index] ?? ''])));

  return { headers, rows, errors };
}

function parseJsonImport(text: string): ParsedImportData {
  const trimmed = text.trim();
  if (!trimmed) {
    return { headers: [], rows: [], errors: [{ rowNumber: 1, message: 'JSON 文件为空。' }] };
  }

  const errors: ImportRowError[] = [];
  const rows: Record<string, unknown>[] = [];
  try {
    const values = trimmed.startsWith('[')
      ? JSON.parse(trimmed) as unknown
      : trimmed.split(/\r?\n/u).filter(Boolean).map((line) => JSON.parse(line) as unknown);
    const array = Array.isArray(values) ? values : [values];
    array.forEach((value, index) => {
      if (!value || typeof value !== 'object' || Array.isArray(value)) {
        errors.push({ rowNumber: index + 1, message: 'JSON 行必须是对象。' });
        return;
      }
      rows.push(value as Record<string, unknown>);
    });
  } catch (error) {
    return {
      headers: [],
      rows: [],
      errors: [{ rowNumber: 1, message: error instanceof Error ? error.message : 'JSON 解析失败。' }],
    };
  }

  const headers = [...new Set(rows.flatMap((row) => Object.keys(row)))];
  return { headers, rows, errors };
}

function parseCsvRows(text: string): { rows: string[][]; errors: ImportRowError[] } {
  const rows: string[][] = [];
  const errors: ImportRowError[] = [];
  let row: string[] = [];
  let value = '';
  let inQuotes = false;
  let i = 0;

  while (i < text.length) {
    const ch = text[i];
    if (inQuotes) {
      if (ch === '"') {
        if (text[i + 1] === '"') {
          value += '"';
          i += 2;
          continue;
        }
        inQuotes = false;
      } else {
        value += ch;
      }
      i += 1;
      continue;
    }

    if (ch === '"') {
      if (value.length > 0) {
        errors.push({ rowNumber: rows.length + 1, message: 'CSV 引号只能出现在字段开头。' });
      }
      inQuotes = true;
    } else if (ch === ',') {
      row.push(value);
      value = '';
    } else if (ch === '\n') {
      row.push(value);
      rows.push(row);
      row = [];
      value = '';
    } else if (ch !== '\r') {
      value += ch;
    }
    i += 1;
  }

  if (inQuotes) {
    errors.push({ rowNumber: rows.length + 1, message: 'CSV 引号未闭合。' });
  }
  if (value.length > 0 || row.length > 0) {
    row.push(value);
    rows.push(row);
  }

  return { rows, errors };
}

function coerceImportValue(column: TableColumnInfo, value: unknown):
  | { ok: true; value: unknown }
  | { ok: false; message: string } {
  if (value === null || value === undefined || value === '') {
    return column.isNullable
      ? { ok: true, value: null }
      : { ok: false, message: '非空列不能为空。' };
  }

  const type = normalizeType(column.dataType);
  if (type === 'int64') {
    const parsed = Number(value);
    if (!Number.isInteger(parsed)) return { ok: false, message: '需要整数。' };
    return { ok: true, value: parsed };
  }
  if (type === 'float64') {
    const parsed = Number(value);
    if (!Number.isFinite(parsed)) return { ok: false, message: '需要数字。' };
    return { ok: true, value: parsed };
  }
  if (type === 'boolean') {
    if (typeof value === 'boolean') return { ok: true, value };
    const text = String(value).trim().toLowerCase();
    if (['true', '1', 'yes', 'y'].includes(text)) return { ok: true, value: true };
    if (['false', '0', 'no', 'n'].includes(text)) return { ok: true, value: false };
    return { ok: false, message: '需要 TRUE/FALSE。' };
  }
  if (type === 'json') {
    const text = typeof value === 'string' ? value : JSON.stringify(value);
    try {
      JSON.parse(text);
    } catch {
      return { ok: false, message: '需要有效 JSON。' };
    }
    return { ok: true, value: text };
  }
  if (type === 'datetime') {
    if (typeof value === 'number') return { ok: true, value };
    const text = String(value).trim();
    if (/^-?\d+$/u.test(text)) return { ok: true, value: Number(text) };
    if (Number.isNaN(Date.parse(text))) return { ok: false, message: '需要 ISO 时间或 Unix 毫秒。' };
    return { ok: true, value: text };
  }

  return { ok: true, value: String(value) };
}

function normalizeType(type: string): string {
  const normalized = type.trim().toLowerCase();
  if (/^(int|integer|long)$/u.test(normalized)) return 'int64';
  if (/^(float|double|real)$/u.test(normalized)) return 'float64';
  if (/^(bool)$/u.test(normalized)) return 'boolean';
  return normalized;
}

function formatDdlType(type: string): string {
  switch (normalizeType(type)) {
    case 'int64':
      return 'INT';
    case 'float64':
      return 'FLOAT';
    case 'boolean':
      return 'BOOL';
    case 'datetime':
      return 'DATETIME';
    case 'blob':
      return 'BLOB';
    case 'json':
      return 'JSON';
    default:
      return 'STRING';
  }
}

function formatOnDelete(value: string): string {
  const normalized = value.replace(/([a-z])([A-Z])/gu, '$1 $2').toUpperCase();
  return normalized && normalized !== 'NO ACTION' ? ` ON DELETE ${normalized}` : '';
}

function quoteSqlString(value: string): string {
  return `'${value.replace(/'/gu, "''")}'`;
}

function makeParameterName(column: string, rowIndex: number, columnIndex: number): string {
  const safe = column.replace(/[^A-Za-z0-9_]/gu, '_').replace(/^([^A-Za-z_])/u, '_$1');
  return `imp_${rowIndex}_${columnIndex}_${safe}`;
}
