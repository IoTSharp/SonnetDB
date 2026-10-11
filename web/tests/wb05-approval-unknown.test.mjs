import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule } from 'node:vm';
import test from 'node:test';

const source = readFileSync(new URL('../src/utils/writeApproval.ts', import.meta.url), 'utf8');
const module = new SourceTextModule(stripTypeScriptTypes(source, { mode: 'transform' }));
await module.link(() => { throw new Error('writeApproval.ts must remain dependency free'); });
await module.evaluate({ timeout: 1000 });
const {
  canConfirmWriteApproval,
  createWriteApprovalOutcome,
  createWriteApprovalPlan,
  markWriteApprovalStale,
  requiresWriteReapproval,
  writeApprovalStaleReason,
} = module.namespace;

const plan = (overrides = {}) => createWriteApprovalPlan({
  id: 'approval-1',
  title: '删除事件',
  target: 'Telemetry.Events',
  items: [{ id: 'drop-1', command: 'DELETE ...', severity: 'danger', label: '删除' }],
  context: {
    connectionId: 'conn-a', endpoint: 'https://db-a.invalid', database: 'Telemetry',
    credentialsFingerprint: 'token-a', host: 'web', objectVersion: 'v7',
    draftFingerprint: 'draft-a', filterFingerprint: 'filter-a', impactFingerprint: 'impact-a',
  },
  impact: '筛选命中 24 个对象',
  risk: '不可逆删除',
  requestId: 'req-7',
  auditSource: 'workbench',
  ...overrides,
});

test('WB-05B plan keeps identity evidence and rejects changed approval context', () => {
  const current = plan();
  assert.equal(current.requestId, 'req-7');
  assert.equal(current.target, 'Telemetry.Events');
  assert.equal(current.impact, '筛选命中 24 个对象');
  assert.equal(current.auditSource, 'workbench');
  assert.equal(canConfirmWriteApproval(current, current.context), true);
  assert.equal(writeApprovalStaleReason(current), '审批上下文无法验证，请重新预览。');
  assert.equal(writeApprovalStaleReason(current, { ...current.context, endpoint: 'https://db-b.invalid' }), '端点已变化，请重新预览。');
  assert.equal(writeApprovalStaleReason(current, { ...current.context, objectVersion: undefined }), '对象版本无法验证，请重新预览。');
  assert.equal(canConfirmWriteApproval(current, { ...current.context, database: 'Other' }), false);
  assert.equal(Object.isFrozen(current.context), true);
});

test('WB-05B stale plans preserve request identity while disabling confirmation', () => {
  const stale = markWriteApprovalStale(plan(), '权限已变化，请重新预览。');
  assert.equal(stale.state, 'stale');
  assert.equal(stale.staleReason, '权限已变化，请重新预览。');
  assert.equal(stale.requestId, 'req-7');
  assert.equal(canConfirmWriteApproval(stale), false);
  const expired = plan({ expiresAt: 0 });
  assert.equal(writeApprovalStaleReason(expired, expired.context), '审批已过期，请重新预览。');
  assert.equal(canConfirmWriteApproval(expired, expired.context), false);
});

test('WB-05B cancellation and disconnect outcomes stay unknown/pending without auto replay', () => {
  const unknown = createWriteApprovalOutcome({
    requestId: 'req-8', operationId: 'op-8', state: 'unknown',
    serverState: 'not-confirmed', auditSource: 'http-timeout', message: '连接中断',
  });
  assert.equal(unknown.state, 'unknown');
  assert.equal(unknown.requestId, 'req-8');
  assert.equal(unknown.serverState, 'not-confirmed');
  assert.equal(unknown.canAutoRetry, false);
  assert.equal(requiresWriteReapproval(unknown), true);

  const pending = createWriteApprovalOutcome({ requestId: 'req-9', state: 'pending' });
  assert.equal(pending.requiresReapproval, true);
  assert.equal(pending.canAutoRetry, false);

  const succeeded = createWriteApprovalOutcome({ requestId: 'req-10', state: 'succeeded', serverState: 'committed' });
  assert.equal(succeeded.requiresReapproval, false);
  assert.equal(succeeded.canAutoRetry, false);
});
