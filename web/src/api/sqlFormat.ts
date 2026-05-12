import { parseSqlMetaCommand } from './sqlMeta';
import { splitSqlStatements } from './sqlSplit';

type ClauseMatch = {
  index: number;
  keyword: string;
  length: number;
};

const SELECT_CLAUSES = ['group by', 'order by', 'where', 'limit', 'offset', 'fetch'] as const;

function isWordChar(ch: string | undefined): boolean {
  return !!ch && /[A-Za-z0-9_]/.test(ch);
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function indentBlock(text: string, spaces = 2): string {
  const indent = ' '.repeat(spaces);
  return text
    .split(/\r?\n/)
    .map((line) => (line.trim().length > 0 ? `${indent}${line}` : line))
    .join('\n');
}

function normalizeInlineSql(text: string): string {
  let out = '';
  let i = 0;
  let inString = false;
  let inLineComment = false;
  let inBlockComment = false;
  let pendingSpace = false;

  const appendSpace = (): void => {
    if (!out || out.endsWith(' ') || out.endsWith('(')) return;
    out += ' ';
  };

  while (i < text.length) {
    const ch = text[i];
    const next = text[i + 1];

    if (inString) {
      out += ch;
      if (ch === "'" && next === "'") {
        out += next;
        i += 2;
        continue;
      }
      if (ch === "'") {
        inString = false;
      }
      i += 1;
      continue;
    }

    if (inLineComment) {
      out += ch;
      if (ch === '\n' || ch === '\r') {
        inLineComment = false;
      }
      i += 1;
      continue;
    }

    if (inBlockComment) {
      out += ch;
      if (ch === '*' && next === '/') {
        out += next;
        i += 2;
        inBlockComment = false;
        continue;
      }
      i += 1;
      continue;
    }

    if (ch === "'" ) {
      if (pendingSpace) appendSpace();
      pendingSpace = false;
      inString = true;
      out += ch;
      i += 1;
      continue;
    }

    if (ch === '-' && next === '-') {
      if (pendingSpace) appendSpace();
      pendingSpace = false;
      inLineComment = true;
      out += '--';
      i += 2;
      continue;
    }

    if (ch === '/' && next === '*') {
      if (pendingSpace) appendSpace();
      pendingSpace = false;
      inBlockComment = true;
      out += '/*';
      i += 2;
      continue;
    }

    if (/\s/.test(ch)) {
      pendingSpace = out.length > 0;
      i += 1;
      continue;
    }

    const twoCharOp = text.slice(i, i + 2);
    if (twoCharOp === '>=' || twoCharOp === '<=' || twoCharOp === '<>' || twoCharOp === '!=') {
      out = out.trimEnd();
      if (out.length > 0 && !out.endsWith('(')) {
        out += ' ';
      }
      out += twoCharOp;
      out += ' ';
      pendingSpace = false;
      i += 2;
      continue;
    }

    if (ch === ',' || ch === '(' || ch === ')' || ch === '=' || ch === '>' || ch === '<' || ch === '+' || ch === '-' || ch === '*' || ch === '/' || ch === '%') {
      if (ch === ',') {
        out = out.trimEnd();
        out += ', ';
      } else if (ch === '(') {
        out = out.trimEnd();
        out += '(';
      } else if (ch === ')') {
        out = out.trimEnd();
        out += ')';
      } else {
        out = out.trimEnd();
        if (out.length > 0 && !out.endsWith(' ')) {
          out += ' ';
        }
        out += ch;
        out += ' ';
      }
      pendingSpace = false;
      i += 1;
      continue;
    }

    if (pendingSpace) appendSpace();
    pendingSpace = false;
    out += ch;
    i += 1;
  }

  return out.trim().replace(/[ \t]+\n/g, '\n').replace(/\n[ \t]+/g, '\n');
}

function matchesKeywordAt(input: string, index: number, keyword: string): number {
  if (index > 0 && isWordChar(input[index - 1])) return 0;

  const keywordPattern = keyword.trim().split(/\s+/).map(escapeRegExp).join('\\s+');
  const pattern = new RegExp(`^${keywordPattern}(?![A-Za-z0-9_])`, 'i');
  const match = input.slice(index).match(pattern);
  if (!match) return 0;

  const end = index + match[0].length;
  if (isWordChar(input[end])) return 0;
  return match[0].length;
}

function findTopLevelKeywordMatches(input: string, keywords: readonly string[], startIndex = 0): ClauseMatch[] {
  const matches: ClauseMatch[] = [];
  let i = startIndex;
  let depth = 0;
  let inString = false;
  let inLineComment = false;
  let inBlockComment = false;
  const orderedKeywords = [...keywords].sort((left, right) => right.length - left.length);

  while (i < input.length) {
    const ch = input[i];
    const next = input[i + 1];

    if (inString) {
      if (ch === "'" && next === "'") {
        i += 2;
        continue;
      }
      if (ch === "'") {
        inString = false;
      }
      i += 1;
      continue;
    }

    if (inLineComment) {
      if (ch === '\n' || ch === '\r') {
        inLineComment = false;
      }
      i += 1;
      continue;
    }

    if (inBlockComment) {
      if (ch === '*' && next === '/') {
        inBlockComment = false;
        i += 2;
        continue;
      }
      i += 1;
      continue;
    }

    if (ch === "'") {
      inString = true;
      i += 1;
      continue;
    }

    if (ch === '-' && next === '-') {
      inLineComment = true;
      i += 2;
      continue;
    }

    if (ch === '/' && next === '*') {
      inBlockComment = true;
      i += 2;
      continue;
    }

    if (ch === '(') {
      depth += 1;
      i += 1;
      continue;
    }

    if (ch === ')') {
      depth = Math.max(0, depth - 1);
      i += 1;
      continue;
    }

    if (depth === 0) {
      let matched = false;
      for (const keyword of orderedKeywords) {
        const length = matchesKeywordAt(input, i, keyword);
        if (length > 0) {
          matches.push({ index: i, keyword, length });
          i += length;
          matched = true;
          break;
        }
      }
      if (matched) {
        continue;
      }
    }

    i += 1;
  }

  return matches.sort((left, right) => left.index - right.index);
}

function splitTopLevelByComma(input: string): string[] {
  const parts: string[] = [];
  let current = '';
  let depth = 0;
  let i = 0;
  let inString = false;
  let inLineComment = false;
  let inBlockComment = false;

  while (i < input.length) {
    const ch = input[i];
    const next = input[i + 1];

    if (inString) {
      current += ch;
      if (ch === "'" && next === "'") {
        current += next;
        i += 2;
        continue;
      }
      if (ch === "'") {
        inString = false;
      }
      i += 1;
      continue;
    }

    if (inLineComment) {
      current += ch;
      if (ch === '\n' || ch === '\r') {
        inLineComment = false;
      }
      i += 1;
      continue;
    }

    if (inBlockComment) {
      current += ch;
      if (ch === '*' && next === '/') {
        current += next;
        i += 2;
        inBlockComment = false;
        continue;
      }
      i += 1;
      continue;
    }

    if (ch === "'") {
      current += ch;
      inString = true;
      i += 1;
      continue;
    }

    if (ch === '-' && next === '-') {
      current += '--';
      inLineComment = true;
      i += 2;
      continue;
    }

    if (ch === '/' && next === '*') {
      current += '/*';
      inBlockComment = true;
      i += 2;
      continue;
    }

    if (ch === '(') {
      depth += 1;
      current += ch;
      i += 1;
      continue;
    }

    if (ch === ')') {
      depth = Math.max(0, depth - 1);
      current += ch;
      i += 1;
      continue;
    }

    if (ch === ',' && depth === 0) {
      const value = current.trim();
      if (value.length > 0) {
        parts.push(value);
      }
      current = '';
      i += 1;
      continue;
    }

    current += ch;
    i += 1;
  }

  const tail = current.trim();
  if (tail.length > 0) {
    parts.push(tail);
  }
  return parts;
}

function splitTopLevelByKeyword(input: string, keyword: string): string[] {
  const parts: string[] = [];
  let current = '';
  let depth = 0;
  let i = 0;
  let inString = false;
  let inLineComment = false;
  let inBlockComment = false;

  while (i < input.length) {
    const ch = input[i];
    const next = input[i + 1];

    if (inString) {
      current += ch;
      if (ch === "'" && next === "'") {
        current += next;
        i += 2;
        continue;
      }
      if (ch === "'") {
        inString = false;
      }
      i += 1;
      continue;
    }

    if (inLineComment) {
      current += ch;
      if (ch === '\n' || ch === '\r') {
        inLineComment = false;
      }
      i += 1;
      continue;
    }

    if (inBlockComment) {
      current += ch;
      if (ch === '*' && next === '/') {
        current += next;
        i += 2;
        inBlockComment = false;
        continue;
      }
      i += 1;
      continue;
    }

    if (ch === "'") {
      current += ch;
      inString = true;
      i += 1;
      continue;
    }

    if (ch === '-' && next === '-') {
      current += '--';
      inLineComment = true;
      i += 2;
      continue;
    }

    if (ch === '/' && next === '*') {
      current += '/*';
      inBlockComment = true;
      i += 2;
      continue;
    }

    if (ch === '(') {
      depth += 1;
      current += ch;
      i += 1;
      continue;
    }

    if (ch === ')') {
      depth = Math.max(0, depth - 1);
      current += ch;
      i += 1;
      continue;
    }

    if (depth === 0) {
      const length = matchesKeywordAt(input, i, keyword);
      if (length > 0) {
        const value = current.trim();
        if (value.length > 0) {
          parts.push(value);
        }
        current = '';
        i += length;
        continue;
      }
    }

    current += ch;
    i += 1;
  }

  const tail = current.trim();
  if (tail.length > 0) {
    parts.push(tail);
  }
  return parts;
}

function splitSelectClauses(input: string): { source: string; clauses: ClauseMatch[] } {
  const matches = findTopLevelKeywordMatches(input, SELECT_CLAUSES);
  if (matches.length === 0) {
    return { source: input.trim(), clauses: [] };
  }

  return {
    source: input.slice(0, matches[0].index).trim(),
    clauses: matches,
  };
}

function formatWhereClause(input: string): string {
  const parts = splitTopLevelByKeyword(input, 'and');
  if (parts.length <= 1) {
    return `WHERE ${normalizeInlineSql(input)}`;
  }

  return [
    `WHERE ${normalizeInlineSql(parts[0])}`,
    ...parts.slice(1).map((part) => `  AND ${normalizeInlineSql(part)}`),
  ].join('\n');
}

function formatListBlock(keyword: string, input: string): string {
  const items = splitTopLevelByComma(input).map((item) => normalizeInlineSql(item)).filter((item) => item.length > 0);
  if (items.length <= 1) {
    return [
      keyword,
      `  ${items[0] ?? normalizeInlineSql(input)}`,
    ].join('\n');
  }

  return [
    keyword,
    ...items.map((item, index) => `  ${item}${index < items.length - 1 ? ',' : ''}`),
  ].join('\n');
}

function normalizeHead(input: string, replacements: Array<[RegExp, string]>): string {
  for (const [pattern, replacement] of replacements) {
    if (pattern.test(input)) {
      return input.replace(pattern, replacement);
    }
  }
  return input;
}

function formatSimpleStatement(statement: string): string {
  const normalized = normalizeInlineSql(statement);
  const headNormalized = normalizeHead(normalized, [
    [/^show\s+measurements\b/i, 'SHOW MEASUREMENTS'],
    [/^show\s+tables\b/i, 'SHOW TABLES'],
    [/^show\s+databases\b/i, 'SHOW DATABASES'],
    [/^show\s+users\b/i, 'SHOW USERS'],
    [/^show\s+grants\b/i, 'SHOW GRANTS'],
    [/^show\s+tokens\b/i, 'SHOW TOKENS'],
    [/^describe\s+measurement\b/i, 'DESCRIBE MEASUREMENT'],
    [/^describe\b/i, 'DESCRIBE'],
    [/^create\s+database\b/i, 'CREATE DATABASE'],
    [/^drop\s+database\b/i, 'DROP DATABASE'],
    [/^alter\s+database\b/i, 'ALTER DATABASE'],
    [/^create\s+user\b/i, 'CREATE USER'],
    [/^alter\s+user\b/i, 'ALTER USER'],
    [/^drop\s+user\b/i, 'DROP USER'],
    [/^grant\b/i, 'GRANT'],
    [/^revoke\b/i, 'REVOKE'],
    [/^issue\s+token\b/i, 'ISSUE TOKEN'],
    [/^revoke\s+token\b/i, 'REVOKE TOKEN'],
    [/^use\b/i, 'USE'],
  ]);

  return `${headNormalized};`;
}

function formatExplainStatement(statement: string): string {
  const inner = statement.replace(/^explain\b/i, '').trim();
  if (!inner) {
    return 'EXPLAIN;';
  }

  return [
    'EXPLAIN',
    indentBlock(formatSqlStatement(inner), 2),
  ].join('\n');
}

function formatSelectStatement(statement: string): string {
  const body = statement.replace(/^select\b/i, '').trim();
  if (!body) {
    return 'SELECT;';
  }

  const modifierMatch = /^(distinct|all)\b/i.exec(body);
  const modifier = modifierMatch ? modifierMatch[1].toUpperCase() : '';
  const projectionBody = modifierMatch ? body.slice(modifierMatch[0].length).trim() : body;
  const fromIndex = findTopLevelKeywordMatches(projectionBody, ['from'])[0]?.index ?? -1;

  if (fromIndex < 0) {
    const projections = splitTopLevelByComma(projectionBody).map((part) => normalizeInlineSql(part)).filter((part) => part.length > 0);
    if (projections.length <= 1) {
      return `SELECT${modifier ? ` ${modifier}` : ''} ${projections[0] ?? normalizeInlineSql(projectionBody)};`;
    }

    return `${[
      `SELECT${modifier ? ` ${modifier}` : ''}`,
      ...projections.map((part, index) => `  ${part}${index < projections.length - 1 ? ',' : ''}`),
    ].join('\n')};`;
  }

  const projectionPart = projectionBody.slice(0, fromIndex).trim();
  const remainder = projectionBody.slice(fromIndex + 4).trim();
  const { source, clauses } = splitSelectClauses(remainder);
  const projections = splitTopLevelByComma(projectionPart).map((part) => normalizeInlineSql(part)).filter((part) => part.length > 0);
  const lines: string[] = [`SELECT${modifier ? ` ${modifier}` : ''}`];
  const projectionLines = projections.length > 0 ? projections : [normalizeInlineSql(projectionPart)];
  lines.push(...projectionLines.map((part, index) => `  ${part}${index < projectionLines.length - 1 ? ',' : ''}`));

  lines.push(`FROM ${normalizeInlineSql(source)}`);

  for (let i = 0; i < clauses.length; i += 1) {
    const clause = clauses[i];
    const nextIndex = clauses[i + 1]?.index ?? remainder.length;
    const clauseText = remainder.slice(clause.index + clause.length, nextIndex).trim();
    switch (clause.keyword) {
      case 'where':
        lines.push(formatWhereClause(clauseText));
        break;
      case 'group by':
        lines.push(formatListBlock('GROUP BY', clauseText));
        break;
      case 'order by':
        lines.push(formatListBlock('ORDER BY', clauseText));
        break;
      case 'limit':
        lines.push(`LIMIT ${normalizeInlineSql(clauseText)}`);
        break;
      case 'offset':
        lines.push(`OFFSET ${normalizeInlineSql(clauseText)}`);
        break;
      case 'fetch':
        lines.push(`FETCH ${normalizeInlineSql(clauseText)}`);
        break;
      default:
        lines.push(`${clause.keyword.toUpperCase()} ${normalizeInlineSql(clauseText)}`);
        break;
    }
  }

  return `${lines.join('\n')};`;
}

function findMatchingParen(input: string, openIndex: number): number {
  let depth = 0;
  let i = openIndex;
  let inString = false;
  let inLineComment = false;
  let inBlockComment = false;

  while (i < input.length) {
    const ch = input[i];
    const next = input[i + 1];

    if (inString) {
      if (ch === "'" && next === "'") {
        i += 2;
        continue;
      }
      if (ch === "'") {
        inString = false;
      }
      i += 1;
      continue;
    }

    if (inLineComment) {
      if (ch === '\n' || ch === '\r') {
        inLineComment = false;
      }
      i += 1;
      continue;
    }

    if (inBlockComment) {
      if (ch === '*' && next === '/') {
        inBlockComment = false;
        i += 2;
        continue;
      }
      i += 1;
      continue;
    }

    if (ch === "'") {
      inString = true;
      i += 1;
      continue;
    }

    if (ch === '-' && next === '-') {
      inLineComment = true;
      i += 2;
      continue;
    }

    if (ch === '/' && next === '*') {
      inBlockComment = true;
      i += 2;
      continue;
    }

    if (ch === '(') {
      depth += 1;
    } else if (ch === ')') {
      depth -= 1;
      if (depth === 0) {
        return i;
      }
    }

    i += 1;
  }

  return -1;
}

function formatCreateMeasurementStatement(statement: string): string {
  const body = statement.replace(/^create\s+measurement\b/i, '').trim();
  if (!body) {
    return 'CREATE MEASUREMENT;';
  }

  const openIndex = body.indexOf('(');
  if (openIndex < 0) {
    return `${normalizeHead(normalizeInlineSql(statement), [[/^create\s+measurement\b/i, 'CREATE MEASUREMENT']])};`;
  }

  const closeIndex = findMatchingParen(body, openIndex);
  const name = normalizeInlineSql(body.slice(0, openIndex).trim());
  const columnBody = closeIndex >= 0 ? body.slice(openIndex + 1, closeIndex).trim() : body.slice(openIndex + 1).trim();
  const tail = closeIndex >= 0 ? body.slice(closeIndex + 1).trim() : '';
  const columns = splitTopLevelByComma(columnBody).map((part) => normalizeInlineSql(part)).filter((part) => part.length > 0);
  const lines = [`CREATE MEASUREMENT ${name} (`];
  lines.push(...columns.map((column, index) => `  ${column}${index < columns.length - 1 ? ',' : ''}`));
  lines.push(tail.length > 0 ? `) ${normalizeInlineSql(tail)}` : ')');
  return `${lines.join('\n')};`;
}

function formatInsertStatement(statement: string): string {
  const body = statement.replace(/^insert\s+into\b/i, '').trim();
  if (!body) {
    return 'INSERT INTO;';
  }

  const valuesIndex = findTopLevelKeywordMatches(body, ['values'])[0]?.index ?? -1;
  if (valuesIndex < 0) {
    return `${normalizeHead(normalizeInlineSql(statement), [[/^insert\s+into\b/i, 'INSERT INTO']])};`;
  }

  const targetPart = body.slice(0, valuesIndex).trim();
  const valuesPart = body.slice(valuesIndex + 6).trim();
  const openIndex = targetPart.indexOf('(');
  const hasColumns = openIndex >= 0;
  const tableName = hasColumns ? targetPart.slice(0, openIndex).trim() : targetPart;
  const columnsPart = hasColumns
    ? targetPart.slice(openIndex + 1, targetPart.lastIndexOf(')') >= openIndex ? targetPart.lastIndexOf(')') : targetPart.length).trim()
    : '';
  const columns = splitTopLevelByComma(columnsPart).map((part) => normalizeInlineSql(part)).filter((part) => part.length > 0);
  const rows = splitTopLevelByComma(valuesPart).map((part) => normalizeInlineSql(part)).filter((part) => part.length > 0);

  const lines = [`INSERT INTO ${normalizeInlineSql(tableName)}${columns.length > 0 ? ' (' : ''}`];
  if (columns.length > 0) {
    lines.push(...columns.map((column, index) => `  ${column}${index < columns.length - 1 ? ',' : ''}`));
    lines.push(')');
  }
  lines.push('VALUES');
  lines.push(...rows.map((row, index) => `  ${row}${index < rows.length - 1 ? ',' : ''}`));
  return `${lines.join('\n')};`;
}

function formatDeleteStatement(statement: string): string {
  const body = statement.replace(/^delete\s+from\b/i, '').trim();
  if (!body) {
    return 'DELETE FROM;';
  }

  const whereIndex = findTopLevelKeywordMatches(body, ['where'])[0]?.index ?? -1;
  if (whereIndex < 0) {
    return `DELETE FROM ${normalizeInlineSql(body)};`;
  }

  const target = body.slice(0, whereIndex).trim();
  const condition = body.slice(whereIndex + 5).trim();
  return `${[
    `DELETE FROM ${normalizeInlineSql(target)}`,
    formatWhereClause(condition),
  ].join('\n')};`;
}

export function formatSqlStatement(statement: string): string {
  const trimmed = statement.trim().replace(/;+\s*$/u, '');
  if (!trimmed) {
    return '';
  }

  if (/^show\s+current[\s_]+database$/i.test(trimmed)) {
    return 'SHOW CURRENT DATABASE;';
  }

  if (/^select\s+(current_database|database)\s*\(\s*\)$/i.test(trimmed)) {
    return 'SELECT current_database();';
  }

  const meta = parseSqlMetaCommand(trimmed);
  if (meta?.kind === 'use') {
    return `USE ${normalizeInlineSql(meta.database)};`;
  }

  if (/^explain\b/i.test(trimmed)) {
    return formatExplainStatement(trimmed);
  }

  if (/^select\b/i.test(trimmed)) {
    return formatSelectStatement(trimmed);
  }

  if (/^create\s+measurement\b/i.test(trimmed)) {
    return formatCreateMeasurementStatement(trimmed);
  }

  if (/^insert\s+into\b/i.test(trimmed)) {
    return formatInsertStatement(trimmed);
  }

  if (/^delete\s+from\b/i.test(trimmed)) {
    return formatDeleteStatement(trimmed);
  }

  return formatSimpleStatement(trimmed);
}

export function formatSqlDocument(input: string): string {
  return splitSqlStatements(input).map((statement) => formatSqlStatement(statement)).join('\n\n');
}
