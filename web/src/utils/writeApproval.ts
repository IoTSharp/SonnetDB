export type WriteApprovalSeverity = 'read' | 'write' | 'danger';

/** 写审批从草稿到服务器终态的共享状态。 */
export type WriteApprovalState =
  | 'draft'
  | 'staged'
  | 'preview'
  | 'dry-run'
  | 'awaiting-confirmation'
  | 'executing'
  | 'succeeded'
  | 'failed'
  | 'partial'
  | 'cancelled'
  | 'pending'
  | 'unknown'
  | 'stale';

/** 用于判断预览是否仍绑定原始连接、目标和输入的上下文快照。 */
export interface WriteApprovalContext {
  connectionId?: string;
  endpoint?: string;
  database?: string;
  credentialsFingerprint?: string;
  host?: string;
  objectVersion?: string | number;
  draftFingerprint?: string;
  filterFingerprint?: string;
  inputFingerprint?: string;
  impactFingerprint?: string;
}

/** 服务器写请求的可追溯终态。 */
export interface WriteApprovalOutcome {
  state: Extract<WriteApprovalState, 'succeeded' | 'failed' | 'partial' | 'cancelled' | 'pending' | 'unknown'>;
  requestId: string;
  operationId?: string;
  serverState?: string;
  auditSource?: string;
  completedCount?: number;
  failedCount?: number;
  message?: string;
  /** 未知、待核对及失败结果都必须先重新预览/审批，不能盲目重试。 */
  requiresReapproval: boolean;
  /** 始终为 false；重试只能由宿主在重新预览/审批后显式发起。 */
  canAutoRetry: false;
}

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
  expiresAt?: number;
  dryRunAvailable: boolean;
  dryRunLabel: string;
  /** 保留审批时的连接、目标、凭据和输入绑定，变更后必须重新预览。 */
  context?: Readonly<WriteApprovalContext>;
  /** 原始影响范围和风险摘要，不以动作条数冒充数据数量。 */
  impact?: string;
  risk?: string;
  requestId: string;
  serverState?: string;
  auditSource?: string;
  state: WriteApprovalState;
  staleReason?: string;
}

export function createWriteApprovalPlan(options: {
  id: string;
  title: string;
  target: string;
  items: WriteApprovalItem[];
  createdAt?: number;
  expiresAt?: number;
  dryRunAvailable?: boolean;
  dryRunLabel?: string;
  context?: WriteApprovalContext;
  impact?: string;
  risk?: string;
  requestId?: string;
  serverState?: string;
  auditSource?: string;
  state?: WriteApprovalState;
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
    ...(options.expiresAt !== undefined ? { expiresAt: options.expiresAt } : {}),
    dryRunAvailable: options.dryRunAvailable ?? false,
    dryRunLabel: options.dryRunLabel ?? 'Dry-run',
    ...(options.context ? { context: Object.freeze({ ...options.context }) } : {}),
    ...(options.impact ? { impact: options.impact } : {}),
    ...(options.risk ? { risk: options.risk } : {}),
    requestId: options.requestId ?? options.id,
    ...(options.serverState ? { serverState: options.serverState } : {}),
    ...(options.auditSource ? { auditSource: options.auditSource } : {}),
    state: options.state ?? 'awaiting-confirmation',
  };
}

const contextLabels: Readonly<Record<keyof WriteApprovalContext, string>> = {
  connectionId: '连接',
  endpoint: '端点',
  database: '数据库',
  credentialsFingerprint: '凭据/会话',
  host: '宿主',
  objectVersion: '对象版本',
  draftFingerprint: '草稿',
  filterFingerprint: '筛选条件',
  inputFingerprint: '输入文件/映射',
  impactFingerprint: '影响范围',
};

/** 返回首个使审批失效的上下文差异；缺失值表示当前无法验证原绑定。 */
export function writeApprovalStaleReason(
  plan: WriteApprovalPlan,
  current?: WriteApprovalContext,
): string | null {
  if (plan.state === 'stale') return plan.staleReason ?? '预览已失效，请重新生成。';
  if (plan.expiresAt !== undefined && Date.now() >= plan.expiresAt) return '审批已过期，请重新预览。';
  if (!plan.context) return null;
  if (!current) return '审批上下文无法验证，请重新预览。';
  for (const key of Object.keys(plan.context) as Array<keyof WriteApprovalContext>) {
    const expected = plan.context[key];
    const actual = current[key];
    if (actual === undefined) return `${contextLabels[key]}无法验证，请重新预览。`;
    if (expected !== actual) return `${contextLabels[key]}已变化，请重新预览。`;
  }
  return null;
}

/** 审批只有在仍绑定原始上下文且处于可确认状态时才可执行。 */
export function canConfirmWriteApproval(
  plan: WriteApprovalPlan,
  current?: WriteApprovalContext,
): boolean {
  if (plan.state !== 'awaiting-confirmation' && plan.state !== 'preview') return false;
  return writeApprovalStaleReason(plan, current) === null;
}

/** 将计划标记为失效，保留原始目标、影响范围、请求 ID 和审计来源。 */
export function markWriteApprovalStale(
  plan: WriteApprovalPlan,
  reason?: string,
): WriteApprovalPlan {
  return {
    ...plan,
    state: 'stale',
    staleReason: reason ?? '预览已失效，请重新生成。',
  };
}

/** 将取消、断连或超时后的写请求记录为待核对，禁止客户端自动重试。 */
export function createWriteApprovalOutcome(options: {
  requestId: string;
  state?: WriteApprovalOutcome['state'];
  operationId?: string;
  serverState?: string;
  auditSource?: string;
  completedCount?: number;
  failedCount?: number;
  message?: string;
}): WriteApprovalOutcome {
  const state = options.state ?? 'unknown';
  return {
    state,
    requestId: options.requestId,
    ...(options.operationId ? { operationId: options.operationId } : {}),
    ...(options.serverState ? { serverState: options.serverState } : {}),
    ...(options.auditSource ? { auditSource: options.auditSource } : {}),
    ...(options.completedCount !== undefined ? { completedCount: options.completedCount } : {}),
    ...(options.failedCount !== undefined ? { failedCount: options.failedCount } : {}),
    ...(options.message ? { message: options.message } : {}),
    requiresReapproval: state !== 'succeeded',
    canAutoRetry: false,
  };
}

/** 未知/待核对结果不能恢复为自动重放；宿主必须重新预览并审批。 */
export function requiresWriteReapproval(outcome: WriteApprovalOutcome): boolean {
  return outcome.requiresReapproval;
}

export function approvalSeverityTagType(
  severity: WriteApprovalSeverity,
): 'info' | 'warning' | 'error' {
  if (severity === 'danger') return 'error';
  return severity === 'write' ? 'warning' : 'info';
}
