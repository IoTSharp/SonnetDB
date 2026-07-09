import type { DropdownOption } from 'naive-ui';
import type { SqlResultSet } from '@/api/sql';
import { splitSqlStatements } from '@/api/sqlSplit';
import { parseSqlMetaCommand } from '@/api/sqlMeta';
import type { MeasurementInfo } from '@/api/schema';
import {
  createWriteApprovalPlan,
  type WriteApprovalItem,
  type WriteApprovalPlan,
} from '@/utils/writeApproval';

export type WorkbenchTool = 'sql' | 'trajectory' | 'table' | 'document' | 'kv' | 'mq' | 'vector' | 'fulltext' | 'bucket';

export interface EditorCursorInfo {
  line: number;
  column: number;
  position: number;
  length: number;
}

export interface PlannedStatement extends WriteApprovalItem {
  sql: string;
  meta: boolean;
}

export interface StagedPreview extends WriteApprovalPlan {
  tabId: string;
  db: string;
  statements: PlannedStatement[];
}

export function makeStatementId(): string {
  return `stmt_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

export function normalizeSql(value: string): string {
  return splitSqlStatements(value).map((stmt) => stmt.trim()).join('\n;\n');
}

export function formatSqlIdentifier(name: string): string {
  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(name)
    ? name
    : `"${name.replace(/"/g, '""')}"`;
}

export function normalizeFieldType(dataType: string): string {
  const text = dataType.trim().toUpperCase();
  if (!text) return 'FLOAT';
  if (/^(FLOAT|FLOAT32|FLOAT64|DOUBLE|INT|INT32|INT64|BOOL|BOOLEAN|STRING|TEXT|VECTOR\(\d+\))$/.test(text)) {
    return text;
  }
  if (text.includes('VECTOR')) return text;
  return text;
}

export function classifyStatement(stmt: string): PlannedStatement {
  const normalized = stmt.trim().replace(/;+\s*$/u, '');
  const meta = parseSqlMetaCommand(normalized);
  if (meta) {
    return {
      id: makeStatementId(),
      command: normalized,
      sql: normalized,
      severity: 'read',
      label: meta.kind === 'use' ? '元命令 / 切库' : '元命令 / 查询上下文',
      meta: true,
    };
  }

  if (/^(select|show|describe|explain|with)\b/i.test(normalized)) {
    return {
      id: makeStatementId(),
      command: normalized,
      sql: normalized,
      severity: 'read',
      label: '读取语句',
      meta: false,
    };
  }

  const dangerous = /^(delete|drop|grant|revoke|issue\s+token|create\s+user|drop\s+user|alter\s+user)\b/i.test(normalized);
  return {
    id: makeStatementId(),
    command: normalized,
    sql: normalized,
    severity: dangerous ? 'danger' : 'write',
    label: dangerous ? '危险写入' : '写操作 / 结构变更',
    meta: false,
  };
}

export function buildPreviewPlan(statements: string[], tabId: string, db: string): StagedPreview {
  const planned = statements.map(classifyStatement);
  const plan = createWriteApprovalPlan({
    id: `preview_${tabId}_${Date.now().toString(36)}`,
    title: 'SQL execution',
    target: db === '__control_plane__' ? 'system control plane' : db,
    items: planned,
  });

  return {
    ...plan,
    tabId,
    db,
    statements: planned,
  };
}

export function isSchemaMutating(sqlText: string): boolean {
  return /^(create|drop|alter)\s+measurement\b/i.test(sqlText.trim())
    || /^(create|drop|alter)\s+database\b/i.test(sqlText.trim());
}

export function isDatabaseCatalogMutating(sqlText: string): boolean {
  return /^(create|drop)\s+database\b/i.test(sqlText.trim());
}

export function defaultSqlForDb(db: string, isSuperuser: boolean): string {
  return db === '__control_plane__' && isSuperuser ? 'SHOW DATABASES' : 'SHOW MEASUREMENTS';
}

export function buildSelectDraft(measurement: MeasurementInfo, sample = false): string {
  const selectColumns = measurement.columns
    .filter((column) => column.name.toLowerCase() !== 'time')
    .map((column) => formatSqlIdentifier(column.name));
  const projection = selectColumns.length > 0
    ? `time, ${selectColumns.join(', ')}`
    : '*';
  const limit = sample ? 20 : 100;
  return [
    `SELECT ${projection}`,
    `FROM ${formatSqlIdentifier(measurement.name)}`,
    `LIMIT ${limit};`,
  ].join('\n');
}

export function buildCreateDraft(measurement: MeasurementInfo): string {
  const newName = formatSqlIdentifier(`${measurement.name}_copy`);
  const columns = measurement.columns
    .filter((column) => column.name.toLowerCase() !== 'time')
    .map((column) => {
      const columnName = formatSqlIdentifier(column.name);
      const role = column.role.toUpperCase();
      if (role === 'TAG') {
        return `  ${columnName} TAG`;
      }
      return `  ${columnName} FIELD ${normalizeFieldType(column.dataType)}`;
    });

  const columnBody = columns.length > 0
    ? columns.join(',\n')
    : '  -- 在此补充 TAG / FIELD';

  return [
    `CREATE MEASUREMENT ${newName} (`,
    columnBody,
    `)`,
    ';',
  ].join('\n');
}

export function summarizeSqlResult(result: SqlResultSet): string {
  if (result.error) return result.error.message;
  if (!result.end) return result.hasColumns ? `${result.rows.length} rows` : 'Statement executed.';
  const parts: string[] = [];
  if (result.hasColumns) {
    parts.push(`${result.end.rowCount} rows`);
  }
  if (result.end.recordsAffected >= 0) {
    parts.push(`affected ${result.end.recordsAffected}`);
  }
  parts.push(`${result.end.elapsedMs.toFixed(2)} ms`);
  return parts.join(' · ');
}

export function statementTitle(sqlText: string): string {
  const text = sqlText.replace(/\s+/g, ' ').trim();
  if (!text) return 'SQL statement';
  return text.length > 64 ? `${text.slice(0, 61)}...` : text;
}

export function quickSqlOptions(isSuperuser: boolean, hasSelectedMeasurement: boolean): DropdownOption[] {
  const options: DropdownOption[] = [
    { label: 'SHOW MEASUREMENTS', key: 'show-measurements' },
    { label: 'SELECT active measurement', key: 'select-active', disabled: !hasSelectedMeasurement },
    { label: 'DESCRIBE active measurement', key: 'describe-active', disabled: !hasSelectedMeasurement },
    { label: 'CREATE MEASUREMENT draft', key: 'create-active', disabled: !hasSelectedMeasurement },
  ];

  if (isSuperuser) {
    options.unshift(
      { label: 'SHOW DATABASES', key: 'show-databases' },
      { label: 'SHOW USERS', key: 'show-users' },
      { label: 'SHOW GRANTS', key: 'show-grants' },
    );
  }

  return options;
}
