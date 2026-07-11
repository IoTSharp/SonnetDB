import type { ColumnInfo, MeasurementInfo } from '@/api/schema';
import {
  sqlParameterFromValue,
  type SqlParameters,
  type SqlStatementRequest,
} from '@/api/sql';
import {
  parseImportText,
  type ImportRowError,
  type ParsedImportData,
  type RelationalImportFormat,
} from '@/utils/relationalImportExport';
import { formatSqlIdentifier } from '@/utils/sqlWorkbench';

export type MeasurementImportFormat = RelationalImportFormat;
export type MeasurementImportMapping = Record<string, string>;

export interface PreparedMeasurementPoint {
  rowNumber: number;
  values: Record<string, unknown>;
}

export interface MeasurementImportValidation {
  rows: PreparedMeasurementPoint[];
  errors: ImportRowError[];
}

export interface PointValidationResult {
  values: Record<string, unknown>;
  errors: ImportRowError[];
}

/** 返回包含隐式 time 列的完整 Measurement 列定义。 */
export function measurementColumns(measurement: MeasurementInfo | null): ColumnInfo[] {
  if (!measurement) return [];
  const columns = [...measurement.columns];
  if (!columns.some((column) => column.role.toLowerCase() === 'time' || column.name.toLowerCase() === 'time')) {
    columns.unshift({ name: 'time', role: 'Time', dataType: 'TIMESTAMP' });
  }
  return columns;
}

/** 解析 Measurement CSV、JSON 数组或 JSONL 文本。 */
export function parseMeasurementImport(
  format: MeasurementImportFormat,
  text: string,
): ParsedImportData {
  return parseImportText(format, text);
}

/** 按列名（不区分大小写）建立默认导入映射。 */
export function buildMeasurementImportMapping(
  measurement: MeasurementInfo,
  headers: readonly string[],
): MeasurementImportMapping {
  const sourceByName = new Map(headers.map((header) => [header.toLowerCase(), header]));
  return Object.fromEntries(measurementColumns(measurement).map((column) => [
    column.name,
    sourceByName.get(column.name.toLowerCase()) ?? '',
  ]));
}

/** 校验并转换文件中的时序点，确保 time、TAG 和至少一个 FIELD 有效。 */
export function validateMeasurementImport(
  measurement: MeasurementInfo,
  parsed: ParsedImportData,
  mapping: MeasurementImportMapping,
): MeasurementImportValidation {
  const errors = [...parsed.errors];
  const rows: PreparedMeasurementPoint[] = [];
  parsed.rows.forEach((source, index) => {
    const rowNumber = index + 2;
    const draft: Record<string, unknown> = {};
    for (const column of measurementColumns(measurement)) {
      const sourceColumn = mapping[column.name];
      if (sourceColumn) draft[column.name] = source[sourceColumn];
    }
    const validation = validateMeasurementPoint(measurement, draft, rowNumber);
    errors.push(...validation.errors);
    if (validation.errors.length === 0) {
      rows.push({ rowNumber, values: validation.values });
    }
  });
  return { rows, errors };
}

/** 校验点编辑器草稿并转换为服务端 SQL 参数可接受的标量。 */
export function validateMeasurementPoint(
  measurement: MeasurementInfo,
  draft: Record<string, unknown>,
  rowNumber = 1,
): PointValidationResult {
  const values: Record<string, unknown> = {};
  const errors: ImportRowError[] = [];
  let fieldCount = 0;

  for (const column of measurementColumns(measurement)) {
    const role = columnRole(column);
    const raw = draft[column.name];
    if (isBlank(raw)) {
      if (role === 'time') {
        errors.push({ rowNumber, column: column.name, message: '时间不能为空。' });
      } else if (role === 'tag') {
        errors.push({ rowNumber, column: column.name, message: 'TAG 不能为空。' });
      }
      continue;
    }

    const converted = convertMeasurementValue(column, raw);
    if (!converted.ok) {
      errors.push({ rowNumber, column: column.name, message: converted.message });
      continue;
    }
    values[column.name] = converted.value;
    if (role === 'field') fieldCount += 1;
  }

  if (fieldCount === 0) {
    errors.push({ rowNumber, message: '每个点至少需要一个 FIELD 值。' });
  }
  return { values, errors };
}

/** 为每个已校验时序点生成参数化 INSERT。 */
export function buildMeasurementInsertStatements(
  measurement: MeasurementInfo,
  points: readonly PreparedMeasurementPoint[],
): SqlStatementRequest[] {
  const schemaColumns = measurementColumns(measurement);
  return points.map((point) => {
    const columns = schemaColumns.filter((column) => Object.prototype.hasOwnProperty.call(point.values, column.name));
    const parameters: SqlParameters = {};
    const parameterNames = columns.map((column, index) => {
      const name = `point_${index}_${safeParameterName(column.name)}`;
      parameters[name] = sqlParameterFromValue(point.values[column.name]);
      return `@${name}`;
    });
    return {
      sql: `INSERT INTO ${formatSqlIdentifier(measurement.name)} (${columns.map((column) => formatSqlIdentifier(column.name)).join(', ')}) VALUES (${parameterNames.join(', ')});`,
      parameters,
    };
  });
}

/** 生成只命中一个 series/time 组合的 DELETE，用于点删除和替换。 */
export function buildMeasurementDeleteStatement(
  measurement: MeasurementInfo,
  point: Record<string, unknown>,
): SqlStatementRequest {
  const identityColumns = measurementColumns(measurement)
    .filter((column) => ['time', 'tag'].includes(columnRole(column)));
  const parameters: SqlParameters = {};
  const predicates = identityColumns.map((column, index) => {
    const name = `identity_${index}_${safeParameterName(column.name)}`;
    parameters[name] = sqlParameterFromValue(point[column.name]);
    return `${formatSqlIdentifier(column.name)} = @${name}`;
  });
  return {
    sql: `DELETE FROM ${formatSqlIdentifier(measurement.name)} WHERE ${predicates.join(' AND ')};`,
    parameters,
  };
}

export function columnRole(column: ColumnInfo): 'time' | 'tag' | 'field' {
  const role = column.role.toLowerCase();
  if (role === 'time' || column.name.toLowerCase() === 'time') return 'time';
  return role === 'tag' ? 'tag' : 'field';
}

export function normalizedMeasurementType(column: ColumnInfo): string {
  const type = column.dataType.trim().toLowerCase();
  if (/^(double|float|float64|real)$/u.test(type)) return 'float64';
  if (/^(int|integer|int64|long|timestamp|datetime)$/u.test(type)) return 'int64';
  if (/^(bool|boolean)$/u.test(type)) return 'boolean';
  if (/^vector/u.test(type)) return 'vector';
  return type;
}

function convertMeasurementValue(
  column: ColumnInfo,
  value: unknown,
): { ok: true; value: unknown } | { ok: false; message: string } {
  const role = columnRole(column);
  const type = normalizedMeasurementType(column);
  if (role === 'time') {
    if (typeof value === 'number' && Number.isInteger(value) && value >= 0) return { ok: true, value };
    const text = String(value).trim();
    if (/^\d+$/u.test(text)) return { ok: true, value: Number(text) };
    const parsed = Date.parse(text);
    return Number.isNaN(parsed)
      ? { ok: false, message: '需要 Unix 毫秒或 ISO 时间。' }
      : { ok: true, value: parsed };
  }
  if (role === 'tag') return { ok: true, value: String(value) };
  if (type === 'int64') {
    const parsed = Number(value);
    return Number.isInteger(parsed)
      ? { ok: true, value: parsed }
      : { ok: false, message: '需要整数。' };
  }
  if (type === 'float64') {
    const parsed = Number(value);
    return Number.isFinite(parsed)
      ? { ok: true, value: parsed }
      : { ok: false, message: '需要数字。' };
  }
  if (type === 'boolean') {
    if (typeof value === 'boolean') return { ok: true, value };
    const normalized = String(value).trim().toLowerCase();
    if (['true', '1', 'yes', 'y'].includes(normalized)) return { ok: true, value: true };
    if (['false', '0', 'no', 'n'].includes(normalized)) return { ok: true, value: false };
    return { ok: false, message: '需要 TRUE/FALSE。' };
  }
  if (type === 'vector') {
    return { ok: false, message: '向量 FIELD 暂不支持文件或表单写入，请使用 SQL 工作台。' };
  }
  return { ok: true, value: String(value) };
}

function isBlank(value: unknown): boolean {
  return value === null || value === undefined || (typeof value === 'string' && value.trim() === '');
}

function safeParameterName(value: string): string {
  return value.replace(/[^A-Za-z0-9_]/gu, '_').replace(/^([^A-Za-z_])/u, '_$1');
}

export type { ImportRowError, ParsedImportData };
