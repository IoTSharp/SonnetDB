import fs from 'node:fs';
import path from 'node:path';
import { gunzipSync } from 'node:zlib';
import { digest, requireThat } from './package.mjs';

export const requiredChecks = ['C01', 'C02', 'C03', 'C04', 'C05', 'D01', 'P05-installed-R1', 'licenses'];
export const distributorPaths = ['.github/workflows/workbench-preview-publish.yml', 'eng/workbench-preview/distribution.mjs', 'eng/workbench-preview/release.mjs',
  'eng/workbench-preview/dispatch.mjs', 'eng/workbench-preview/github-api.mjs', 'eng/workbench-preview/Invoke-Distribution.ps1', 'eng/workbench-preview/Verify-Distribution.ps1',
  'eng/workbench-preview/Invoke-Owned.ps1', 'eng/workbench-preview/JobProcess.cs', 'eng/workbench-preview/package.mjs'];

export function originalBytes(file, directory) {
  const storage = file.storageFile ?? file.file;
  requireThat(/^[a-zA-Z0-9_.-]+\.(?:json|txt)(?:\.gz)?$/u.test(storage), 'Unsafe original storage filename');
  requireThat(!file.storageFile || (storage === `${file.file}.gz` && file.encoding === 'gzip'), 'Unknown original storage encoding');
  const full = path.join(directory, storage), stat = fs.lstatSync(full);
  requireThat(stat.isFile() && !stat.isSymbolicLink() && stat.size > 0 && stat.size <= 2 * 1024 * 1024, 'Original storage size/type budget');
  const stored = fs.readFileSync(full), bytes = file.encoding === 'gzip' ? gunzipSync(stored, { maxOutputLength: 2 * 1024 * 1024 }) : stored;
  requireThat(bytes.length === file.size && digest(bytes) === file.sha256, 'Original receipt hash/size drift');
  return bytes;
}

export function verifyOriginals(bundle, directory) {
  requireThat(bundle.schemaVersion === 1 && bundle.source === 'preserved-installed-package-receipts', 'Original evidence schema');
  requireThat(Array.isArray(bundle.files) && bundle.files.length >= 10 && bundle.files.length <= 80, 'Nonempty original evidence inventory required');
  const seen = new Set(), values = new Map();
  for (const file of bundle.files) {
    requireThat(/^[a-zA-Z0-9_.-]+\.json$/u.test(file.file) && !seen.has(file.file), 'Unsafe/duplicate original filename');
    seen.add(file.file);
    const bytes = originalBytes(file, directory);
    values.set(file.file, JSON.parse(bytes));
  }
  for (const phase of ['first', 'reopen', 'rollback', 'legacy']) {
    const journey = values.get(`${phase}-journey.json`);
    requireThat(journey?.phase === phase && journey.status === 'PASS' && journey.source === bundle.sourceCommit && journey.version === bundle.version, 'Installed journey source/status mismatch');
    requireThat(journey.records.length > 0 && journey.records.every(r => r.status === 'PASS'), 'Installed journey incomplete');
  }
  const suite = values.get('installed-suite.json');
  requireThat(suite?.status === 'PASS' && suite.records.length === 10, 'Installed suite incomplete');
  const audit = values.get('corrected-audit.json');
  requireThat(audit?.status === 'PASS' && audit.receiptCount >= 15 && audit.recordedIdentityCount >= 100 && audit.liveCompletedTaskIdentities === 0 && audit.remainingListeners === 0, 'Nonempty corrected audit required');
  requireThat((bundle.supportingFiles ?? []).length <= 20, 'Supporting original count budget');
  for (const file of bundle.supportingFiles ?? []) {
    requireThat(/^[a-zA-Z0-9_.-]+\.txt$/u.test(file.file) && !seen.has(file.file), 'Unsafe/duplicate supporting filename');
    seen.add(file.file);
    originalBytes(file, directory);
  }
  return values;
}

export function validateFrozenReceiptBindings(bundle, candidate, acceptanceAudit) {
  const installed = candidate.installedAcceptance;
  requireThat(installed?.phases?.length === 4, 'Frozen installed phases missing');
  const expected = installed.phases.flatMap(phase => [
    { file: `${phase.phase}-journey.json`, sha256: phase.journeySha256 },
    { file: `${phase.phase}-result.json`, sha256: phase.resultSha256 },
  ]);
  expected.push({ file: 'installed-suite.json', sha256: installed.suiteSha256 }, { file: 'installed-audit.json', sha256: installed.independentAuditSha256 },
    { file: 'corrected-audit.json', sha256: acceptanceAudit.correctedAudit.sha256 }, { file: 'original-empty-window-audit.json', sha256: acceptanceAudit.originalEmptyWindow.sha256 });
  for (const original of expected) requireThat(/^[a-f0-9]{64}$/u.test(original.sha256) && bundle.files.filter(file => file.file === original.file && file.sha256 === original.sha256).length === 1, 'Receipt inventory does not match frozen candidate/audit');
}

const selectors = {
  C01: ['deferred-models-and-direct-routes'],
  C02: ['authorized-select-query-truncation-export', 'relation-and-measurement-reads', 'deferred-models-and-direct-routes'],
  C03: ['approval-cancel-then-once-confirm', 'readonly-database-grant', 'revoked-grant-clears-approval', 'unknown-write-does-not-replay'],
  C04: ['readonly-database-grant', 'revoked-grant-clears-approval', 'offline-and-revoked-token-clearing', 'cancel-and-database-isolation', 'authorized-select-query-truncation-export'],
  C05: ['password-login-and-about', 'actual-server-rejects-stale-preview-headers', 'cached-web-identity-mismatch-clears-state'],
};
export function installedChecks(bundle, values) {
  const first = values.get('first-journey.json');
  return Object.entries(selectors).map(([id, names]) => {
    const selected = names.flatMap(name => ['chrome', 'edge'].map(browser => `${browser}-${name}`));
    const records = selected.map(name => first.records.filter(r => r.name === name));
    requireThat(records.every(matches => matches.length === 1 && matches[0].status === 'PASS'), `Missing installed ${id} step`);
    return { id, status: 'PASS', selectedRecords: selected, originalFiles: ['first-journey.json', ...(id === 'C05' ? ['legacy-journey.json'] : [])],
      boundary: 'Recorded Windows R1 acceptance only; synthetic fixtures and browser fault injections retain their original evidenceKind. No clean-machine, production or server scan-stop inference.' };
  });
}

export function evaluateReview(readiness, checks) {
  requireThat(readiness.source === 'github' && readiness.workflows?.length === 12, 'Original twelve-workflow report required');
  requireThat(checks.length === requiredChecks.length && new Set(checks.map(c => c.id)).size === requiredChecks.length && requiredChecks.every(id => checks.some(c => c.id === id)), 'Exact eight checks required');
  const blockers = [...readiness.workflows.filter(w => !w.ready).map(w => ({ gate: w.workflow, issues: w.issues })),
    ...checks.filter(c => c.status !== 'PASS').map(c => ({ gate: c.id, issues: c.issues ?? ['Check is not PASS.'] })),
    ...(readiness.issues ?? []).map(issue => ({ gate: 'original-verifier', issues: [issue] }))];
  // READY from the original verifier remains mandatory even if all booleans were altered.
  if (readiness.status !== 'READY' && blockers.length === 0) blockers.push({ gate: 'original-verifier', issues: ['Original verifier is not READY.'] });
  return { status: blockers.length ? 'NOT_READY' : 'READY', blockers, observations: readiness.observations ?? [] };
}

export function checkedRecord(check, seal, rawInventory) {
  return { schemaVersion: 1, check: check.id, status: check.status, candidate: seal.candidate, version: seal.version, sourceCommit: seal.sourceCommit,
    source: 'review-of-preserved-original-receipts', ...check, rawEvidence: check.originalFiles.map(file => {
      const entry = rawInventory.find(e => e.file === file); requireThat(entry, 'Unbound original reference'); return entry;
    }) };
}
