export type WriteApprovalSeverity = 'read' | 'write' | 'danger';

export interface WriteApprovalItem {
  id: string;
  command: string;
  severity: WriteApprovalSeverity;
  label: string;
  detail?: string;
}

export interface WriteApprovalPlan {
  id: string;
  title: string;
  target: string;
  items: WriteApprovalItem[];
  queryCount: number;
  writeCount: number;
  dangerCount: number;
  dangerous: boolean;
  summary: string;
  createdAt: number;
  dryRunAvailable: boolean;
  dryRunLabel: string;
}

export function createWriteApprovalPlan(options: {
  id: string;
  title: string;
  target: string;
  items: WriteApprovalItem[];
  createdAt?: number;
  dryRunAvailable?: boolean;
  dryRunLabel?: string;
}): WriteApprovalPlan {
  const queryCount = options.items.filter((item) => item.severity === 'read').length;
  const writeCount = options.items.filter((item) => item.severity !== 'read').length;
  const dangerCount = options.items.filter((item) => item.severity === 'danger').length;
  const summary = [
    `${options.items.length} actions`,
    `${queryCount} read`,
    `${writeCount} write`,
    dangerCount > 0 ? `${dangerCount} danger` : '',
  ].filter(Boolean).join(' · ');

  return {
    id: options.id,
    title: options.title,
    target: options.target,
    items: options.items,
    queryCount,
    writeCount,
    dangerCount,
    dangerous: dangerCount > 0,
    summary,
    createdAt: options.createdAt ?? Date.now(),
    dryRunAvailable: options.dryRunAvailable ?? false,
    dryRunLabel: options.dryRunLabel ?? 'Dry-run',
  };
}

export function approvalSeverityTagType(
  severity: WriteApprovalSeverity,
): 'info' | 'warning' | 'error' {
  if (severity === 'danger') return 'error';
  return severity === 'write' ? 'warning' : 'info';
}
