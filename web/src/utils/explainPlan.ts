import type { SqlResultSet } from '@/api/sql';
import { formatSqlValue } from '@/utils/sqlValue';

export type ExplainNodeKind =
  | 'root'
  | 'catalog'
  | 'scan'
  | 'index'
  | 'filter'
  | 'join'
  | 'sort'
  | 'projection'
  | 'vector'
  | 'candidate'
  | 'generic';

export type ExplainTone = 'default' | 'good' | 'warn' | 'danger' | 'muted';

export interface ExplainMetric {
  label: string;
  value: string;
  tone: ExplainTone;
}

export interface ExplainPlanNode {
  id: string;
  title: string;
  subtitle: string;
  kind: ExplainNodeKind;
  badges: string[];
  metrics: ExplainMetric[];
  children: ExplainPlanNode[];
}

export interface ExplainPlanCandidate {
  id: string;
  selected: boolean;
  accessPath: string;
  accessLabel: string;
  indexName: string;
  rows: string;
  cost: string;
  pushdownFields: string[];
  rejectReason: string;
}

export interface VisualExplainPlan {
  title: string;
  subtitle: string;
  statementType: string;
  target: string;
  database: string;
  accessPath: string;
  indexName: string;
  totalRows: string;
  root: ExplainPlanNode;
  metrics: ExplainMetric[];
  candidates: ExplainPlanCandidate[];
  notes: string[];
}

type ExplainValueMap = Record<string, unknown>;

const KeyColumn = 'key';
const ValueColumn = 'value';

const AccessPathLabels: Record<string, string> = {
  catalog: 'Catalog lookup',
  measurement_scan: 'Measurement scan',
  tag_index: 'Tag index',
  table_scan: 'Table scan',
  secondary_index: 'Secondary index',
  secondary_index_prefix: 'Secondary index prefix',
  secondary_index_range: 'Secondary index range',
  json_path_index: 'JSON path index',
  primary_key: 'Primary key lookup',
  document_id: 'Document id lookup',
  document_id_set: 'Document id set',
  document_index: 'Document index',
  document_index_prefix: 'Document index prefix',
  document_scan: 'Document scan',
  fulltext_index: 'Full-text index',
  hybrid_search: 'Hybrid search',
  hybrid_search_measurement_knn_documents: 'Measurement KNN + documents',
  document_vector_scan: 'Document vector scan',
  document_vector_index: 'Document vector index',
  json_file_virtual_table: 'JSON virtual table',
};

const StatementLabels: Record<string, string> = {
  select: 'SELECT plan',
  select_join: 'JOIN SELECT plan',
  select_table: 'Table SELECT plan',
  select_document_collection: 'Document SELECT plan',
  vector_search: 'Vector search plan',
  hybrid_search: 'Hybrid search plan',
  json_file_virtual_table: 'JSON virtual table plan',
  show_measurements: 'SHOW MEASUREMENTS plan',
  show_tables: 'SHOW TABLES plan',
  show_document_collections: 'SHOW DOCUMENT COLLECTIONS plan',
  show_indexes: 'SHOW INDEXES plan',
  show_json_indexes: 'SHOW JSON INDEXES plan',
  show_fulltext_indexes: 'SHOW FULLTEXT INDEXES plan',
  describe_measurement: 'DESCRIBE MEASUREMENT plan',
  describe_table: 'DESCRIBE TABLE plan',
  describe_document_collection: 'DESCRIBE DOCUMENT COLLECTION plan',
};

export function parseVisualExplainPlan(result: SqlResultSet | null): VisualExplainPlan | null {
  const values = readExplainValues(result);
  if (!values) return null;

  const statementType = valueText(values.statement_type) || 'explain';
  const database = valueText(values.database);
  const target = valueText(values.measurement);
  const accessPath = valueText(values.access_path);
  const indexName = valueText(values.index_name);
  const totalRows = valueText(values.estimated_scanned_rows) || '0';
  const candidates = parseCandidatePlans(valueText(values.candidate_plans));
  const root = buildPlanTree(values, statementType, target, database, accessPath, indexName, candidates);
  const notes = buildPlanNotes(values, accessPath, candidates);
  const metrics = compactMetrics([
    metric('Estimated rows', totalRows, rowsTone(totalRows)),
    metric('Access path', labelForAccessPath(accessPath), accessTone(accessPath)),
    metric('Index', indexName || 'none', indexName ? 'good' : 'muted'),
    metric('Series', valueText(values.matched_series_count), 'default'),
    metric('Blocks', valueText(values.estimated_block_count), 'default'),
    metric('Segments', valueText(values.estimated_segment_count), 'default'),
  ]);

  return {
    title: StatementLabels[statementType] ?? `${statementType.replace(/_/g, ' ')} plan`,
    subtitle: [database && `db ${database}`, target && `target ${target}`].filter(Boolean).join(' · '),
    statementType,
    target,
    database,
    accessPath,
    indexName,
    totalRows,
    root,
    metrics,
    candidates,
    notes,
  };
}

function readExplainValues(result: SqlResultSet | null): ExplainValueMap | null {
  if (!result || result.error || result.columns.length < 2) return null;
  const keyIndex = result.columns.findIndex((column) => column.toLowerCase() === KeyColumn);
  const valueIndex = result.columns.findIndex((column) => column.toLowerCase() === ValueColumn);
  if (keyIndex < 0 || valueIndex < 0) return null;

  const values: ExplainValueMap = {};
  for (const row of result.rows) {
    const key = row[keyIndex];
    if (typeof key !== 'string' || !key) continue;
    values[key] = row[valueIndex];
  }

  return typeof values.statement_type === 'string' || values.access_path !== undefined
    ? values
    : null;
}

function buildPlanTree(
  values: ExplainValueMap,
  statementType: string,
  target: string,
  database: string,
  accessPath: string,
  indexName: string,
  candidates: ExplainPlanCandidate[],
): ExplainPlanNode {
  const root: ExplainPlanNode = {
    id: 'root',
    title: StatementLabels[statementType] ?? 'SQL plan',
    subtitle: [target, database].filter(Boolean).join(' in '),
    kind: 'root',
    badges: [statementType],
    metrics: compactMetrics([
      metric('Rows', valueText(values.estimated_scanned_rows), rowsTone(valueText(values.estimated_scanned_rows))),
      metric('MemTable', valueText(values.estimated_memtable_rows), 'default'),
      metric('Segments', valueText(values.estimated_segment_rows), 'default'),
    ]),
    children: [],
  };

  root.children.push(...buildAccessNodes(values, accessPath, indexName));
  root.children.push(...buildPredicateNodes(values));
  root.children.push(...buildDocumentPlannerNodes(values, candidates));
  return root;
}

function buildAccessNodes(values: ExplainValueMap, accessPath: string, indexName: string): ExplainPlanNode[] {
  if (!accessPath) {
    return [
      makeNode('access', 'Access path', 'No access path reported', 'generic', [], []),
    ];
  }

  const segments = splitAccessSegments(accessPath);
  if (segments.length > 1) {
    return segments.map((segment, index) => {
      const kind = kindForAccessPath(segment.path);
      const title = labelForAccessStage(segment.stage, segment.path);
      const scopedIndex = segment.stage === 'table' || segment.stage === 'relation_filter' ? indexName : '';
      return makeNode(
        `access-${index}`,
        title,
        labelForAccessPath(segment.path),
        kind,
        [segment.stage],
        compactMetrics([
          metric('Path', segment.path, accessTone(segment.path)),
          metric('Index', scopedIndex, scopedIndex ? 'good' : 'muted'),
          metric('Rows', rowsForStage(values, segment.stage), rowsTone(rowsForStage(values, segment.stage))),
        ]),
      );
    });
  }

  return [
    makeNode(
      'access',
      labelForAccessPath(accessPath),
      accessSubtitle(accessPath),
      kindForAccessPath(accessPath),
      accessPath === 'catalog' ? ['metadata'] : [],
      compactMetrics([
        metric('Path', accessPath, accessTone(accessPath)),
        metric('Index', indexName, indexName ? 'good' : 'muted'),
        metric('Rows', valueText(values.estimated_scanned_rows), rowsTone(valueText(values.estimated_scanned_rows))),
      ]),
    ),
  ];
}

function buildPredicateNodes(values: ExplainValueMap): ExplainPlanNode[] {
  const nodes: ExplainPlanNode[] = [];
  const hasTimeFilter = valueBool(values.has_time_filter);
  const tagFilterCount = valueText(values.tag_filter_count);
  const filterPushdown = valueBool(values.filter_pushdown);
  const pushdownFields = splitList(valueText(values.filter_pushdown_fields));
  const residualFields = splitList(valueText(values.residual_filter_fields));

  if (hasTimeFilter || tagFilterCount !== '' && tagFilterCount !== '0') {
    nodes.push(makeNode(
      'time-tag-filter',
      'Predicate pushdown',
      'Time and tag predicates are applied before row materialization',
      'filter',
      [
        hasTimeFilter ? 'time' : '',
        tagFilterCount && tagFilterCount !== '0' ? `${tagFilterCount} tag filters` : '',
      ].filter(Boolean),
      compactMetrics([
        metric('Time filter', yesNo(hasTimeFilter), hasTimeFilter ? 'good' : 'muted'),
        metric('Tag filters', tagFilterCount || '0', tagFilterCount && tagFilterCount !== '0' ? 'good' : 'muted'),
      ]),
    ));
  }

  if (filterPushdown || pushdownFields.length > 0 || residualFields.length > 0) {
    nodes.push(makeNode(
      'document-filter',
      'Document filter',
      residualFields.length > 0 ? 'Some predicates remain as residual filters' : 'Predicates are covered by access planning',
      'filter',
      [
        filterPushdown ? 'pushdown' : '',
        residualFields.length > 0 ? 'residual' : '',
      ].filter(Boolean),
      compactMetrics([
        metric('Pushdown fields', pushdownFields.join(', ') || 'none', pushdownFields.length > 0 ? 'good' : 'muted'),
        metric('Residual fields', residualFields.join(', ') || 'none', residualFields.length > 0 ? 'warn' : 'good'),
      ]),
    ));
  }

  return nodes;
}

function buildDocumentPlannerNodes(values: ExplainValueMap, candidates: ExplainPlanCandidate[]): ExplainPlanNode[] {
  const nodes: ExplainPlanNode[] = [];
  const hasDocumentPlanner = values.estimated_candidate_rows !== undefined
    || values.estimated_output_rows !== undefined
    || candidates.length > 0;

  if (!hasDocumentPlanner) return nodes;

  nodes.push(makeNode(
    'candidate-selection',
    'Candidate selection',
    candidates.length > 0
      ? `${candidates.length} access paths compared`
      : 'Document planner returned candidate estimates',
    'candidate',
    candidates.some((candidate) => candidate.selected) ? ['selected path marked'] : [],
    compactMetrics([
      metric('Candidate rows', valueText(values.estimated_candidate_rows), 'default'),
      metric('Output rows', valueText(values.estimated_output_rows), 'default'),
      metric('Gap reason', valueText(values.gap_reason), valueText(values.gap_reason) ? 'warn' : 'muted'),
    ]),
  ));

  if (values.sort_uses_index !== undefined) {
    const sortUsesIndex = valueBool(values.sort_uses_index);
    nodes.push(makeNode(
      'sort',
      sortUsesIndex ? 'Index order' : 'In-memory sort',
      sortUsesIndex ? 'ORDER BY is satisfied by the selected path' : 'ORDER BY needs a sort after access',
      'sort',
      [sortUsesIndex ? 'covered' : 'sort'],
      compactMetrics([
        metric('Uses index', yesNo(sortUsesIndex), sortUsesIndex ? 'good' : 'warn'),
      ]),
    ));
  }

  if (values.projection_covered_by_index !== undefined) {
    const covered = valueBool(values.projection_covered_by_index);
    nodes.push(makeNode(
      'projection',
      covered ? 'Covered projection' : 'Fetch projection',
      covered ? 'Projected fields can be resolved from the access path' : 'Rows must be fetched for projection',
      'projection',
      [covered ? 'covered' : 'fetch'],
      compactMetrics([
        metric('Covered', yesNo(covered), covered ? 'good' : 'warn'),
      ]),
    ));
  }

  return nodes;
}

function buildPlanNotes(values: ExplainValueMap, accessPath: string, candidates: ExplainPlanCandidate[]): string[] {
  const notes: string[] = [];
  const gapReason = valueText(values.gap_reason);
  if (gapReason) {
    notes.push(`Planner gap: ${gapReason.replace(/_/g, ' ')}.`);
  }
  if (accessPath.includes('scan')) {
    notes.push('The selected path includes a scan; adding or aligning an index may reduce candidate rows.');
  }
  if (accessPath.includes('index')) {
    notes.push('The selected path uses an index before materializing rows.');
  }
  if (candidates.some((candidate) => candidate.rejectReason)) {
    notes.push('Some candidate paths were rejected; inspect their reason labels before changing indexes.');
  }
  if (valueBool(values.has_time_filter)) {
    notes.push('A time predicate is present and can limit time-series block reads.');
  }
  return notes;
}

export function parseCandidatePlans(value: string): ExplainPlanCandidate[] {
  if (!value.trim()) return [];

  return value
    .split(';')
    .map((part) => part.trim())
    .filter(Boolean)
    .map((part, index) => {
      const tokens = part.split(/\s+/u).filter(Boolean);
      let head = tokens.shift() ?? '';
      const selected = head.startsWith('*');
      if (selected) head = head.slice(1);

      const firstColon = head.indexOf(':');
      const accessPath = firstColon >= 0 ? head.slice(0, firstColon) : head;
      const indexName = firstColon >= 0 ? head.slice(firstColon + 1) : '';
      let rows = '';
      let cost = '';
      let rejectReason = '';
      let pushdownFields: string[] = [];

      for (const token of tokens) {
        if (token.startsWith('rows=')) {
          rows = token.slice('rows='.length);
        } else if (token.startsWith('cost=')) {
          cost = token.slice('cost='.length);
        } else if (token.startsWith('pushdown=')) {
          pushdownFields = token.slice('pushdown='.length).split('|').filter(Boolean);
        } else if (token.startsWith('reason=')) {
          rejectReason = token.slice('reason='.length);
        }
      }

      return {
        id: `candidate-${index}`,
        selected,
        accessPath,
        accessLabel: labelForAccessPath(accessPath),
        indexName,
        rows,
        cost,
        pushdownFields,
        rejectReason,
      };
    });
}

function splitAccessSegments(accessPath: string): Array<{ stage: string; path: string }> {
  const parts = accessPath.split(';').map((part) => part.trim()).filter(Boolean);
  const segments = parts.map((part) => {
    const colon = part.indexOf(':');
    if (colon < 0) {
      return { stage: 'access', path: part };
    }
    return {
      stage: part.slice(0, colon),
      path: part.slice(colon + 1),
    };
  });

  return segments.length > 0 ? segments : [{ stage: 'access', path: accessPath }];
}

function labelForAccessStage(stage: string, path: string): string {
  if (stage === 'measurement') return 'Measurement access';
  if (stage === 'table') return 'Relation table access';
  if (stage === 'join') return 'Join';
  if (stage === 'relation_filter') return 'Relation filter';
  return labelForAccessPath(path);
}

function accessSubtitle(accessPath: string): string {
  if (accessPath.includes('scan')) return 'Rows are read by scanning the selected source.';
  if (accessPath.includes('index')) return 'Rows are narrowed through an index.';
  if (accessPath === 'catalog') return 'Metadata is read from the catalog.';
  if (accessPath.includes('hybrid')) return 'Hybrid retrieval combines multiple scoring paths.';
  return 'Access path reported by the SQL planner.';
}

function labelForAccessPath(accessPath: string): string {
  if (!accessPath) return 'Not reported';
  return AccessPathLabels[accessPath] ?? accessPath.replace(/_/g, ' ');
}

function kindForAccessPath(accessPath: string): ExplainNodeKind {
  if (accessPath === 'catalog') return 'catalog';
  if (accessPath.includes('join')) return 'join';
  if (accessPath.includes('vector') || accessPath.includes('hybrid') || accessPath.includes('knn')) return 'vector';
  if (accessPath.includes('index') || accessPath === 'primary_key' || accessPath.includes('document_id')) return 'index';
  if (accessPath.includes('scan')) return 'scan';
  return 'generic';
}

function accessTone(accessPath: string): ExplainTone {
  if (!accessPath) return 'muted';
  if (accessPath.includes('scan')) return 'warn';
  if (accessPath.includes('index') || accessPath === 'primary_key' || accessPath.includes('document_id')) return 'good';
  return 'default';
}

function rowsForStage(values: ExplainValueMap, stage: string): string {
  if (stage === 'measurement') return valueText(values.matched_series_count);
  if (stage === 'table' || stage === 'relation_filter') return valueText(values.estimated_scanned_rows);
  return '';
}

function splitList(value: string): string[] {
  return value.split(',').map((item) => item.trim()).filter(Boolean);
}

function yesNo(value: boolean): string {
  return value ? 'yes' : 'no';
}

function valueBool(value: unknown): boolean {
  if (typeof value === 'boolean') return value;
  if (typeof value === 'number') return value !== 0;
  if (typeof value === 'string') return /^(true|1|yes)$/i.test(value.trim());
  return false;
}

function valueText(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'string') return value;
  return formatSqlValue(value);
}

function rowsTone(value: string): ExplainTone {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return 'default';
  if (parsed === 0) return 'muted';
  if (parsed > 100_000) return 'warn';
  return 'default';
}

function compactMetrics(metrics: Array<ExplainMetric | null>): ExplainMetric[] {
  return metrics.filter((item): item is ExplainMetric => item !== null && item.value !== '');
}

function metric(label: string, value: string, tone: ExplainTone): ExplainMetric | null {
  if (value === '') return null;
  return { label, value, tone };
}

function makeNode(
  id: string,
  title: string,
  subtitle: string,
  kind: ExplainNodeKind,
  badges: string[],
  metrics: ExplainMetric[],
): ExplainPlanNode {
  return {
    id,
    title,
    subtitle,
    kind,
    badges,
    metrics,
    children: [],
  };
}
