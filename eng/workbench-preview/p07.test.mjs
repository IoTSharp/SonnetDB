import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { gzipSync } from 'node:zlib';
import { digest } from './package.mjs';
import { requiredChecks, evaluateReview, installedChecks, originalBytes, validateFrozenReceiptBindings } from './p07.mjs';
const reports = () => ({ source: 'github', status: 'READY', workflows: Array.from({ length: 12 }, (_, i) => ({ workflow: `workflow-${i}`, ready: true, runId: i + 1, issues: [] })), issues: [], observations: [{ blocking: false, status: 'NOT_READY' }] });
const checks = () => requiredChecks.map(id => ({ id, status: 'PASS' }));
test('missing workflow or license evidence prevents READY', () => {
  const r = reports(); r.workflows[4].ready = false; r.workflows[4].issues = ['No eligible run'];
  const c = checks(); c.at(-1).status = 'NOT_READY';
  assert.equal(evaluateReview(r, c).status, 'NOT_READY'); assert.equal(evaluateReview(r, c).blockers.length, 2);
});
test('UNKNOWN, NOT_RUN and FAIL each block', () => {
  for (const status of ['UNKNOWN', 'NOT_RUN', 'FAIL']) { const c = checks(); c[0].status = status; assert.equal(evaluateReview(reports(), c).status, 'NOT_READY'); }
});
test('exact check inventory and all twelve workflows are mandatory', () => {
  assert.throws(() => evaluateReview(reports(), checks().slice(1)));
  const c = checks(); c[0].id = c[1].id; assert.throws(() => evaluateReview(reports(), c));
  const r = reports(); r.workflows.pop(); assert.throws(() => evaluateReview(r, checks()));
});
test('original NOT_READY cannot be upgraded by booleans', () => { const r = reports(); r.status = 'NOT_READY'; assert.equal(evaluateReview(r, checks()).status, 'NOT_READY'); });
test('seven-day observation remains nonblocking', () => { assert.equal(evaluateReview(reports(), checks()).status, 'READY'); });
test('missing installed step cannot be replaced with a wrapper PASS', () => { assert.throws(() => installedChecks({}, new Map([['first-journey.json', { status: 'PASS', records: [] }]]))); });
test('gzip archive preserves original CRLF and rejects substituted receipt bytes', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sonnetdb-p07-receipt-'));
  try {
    const bytes = Buffer.from('{\r\n  "status": "FAIL"\r\n}\r\n');
    const file = { file: 'receipt.json', storageFile: 'receipt.json.gz', encoding: 'gzip', size: bytes.length, sha256: digest(bytes) };
    const full = path.join(directory, file.storageFile);
    fs.writeFileSync(full, gzipSync(bytes)); assert.deepEqual(originalBytes(file, directory), bytes);
    fs.writeFileSync(full, gzipSync(Buffer.from(bytes.toString().replace('FAIL', 'PASS'))));
    assert.throws(() => originalBytes(file, directory), /hash\/size drift/u);
  } finally {
    assert.equal(path.dirname(directory), path.resolve(os.tmpdir())); assert.ok(path.basename(directory).startsWith('sonnetdb-p07-receipt-'));
    fs.rmSync(directory, { recursive: true });
  }
});
test('compressed original cannot escape archive or use unknown encoding', () => {
  assert.throws(() => originalBytes({ file: 'receipt.json', storageFile: '../receipt.json.gz', encoding: 'gzip' }, '.'));
  assert.throws(() => originalBytes({ file: 'receipt.json', storageFile: 'other.json.gz', encoding: 'gzip' }, '.'));
});
test('a replaced original hash cannot be authorized by a rewritten inventory', () => {
  const h = 'a'.repeat(64), phases = ['first', 'reopen', 'rollback', 'legacy'].map(phase => ({ phase, journeySha256: h, resultSha256: h }));
  const candidate = { installedAcceptance: { phases, suiteSha256: h, independentAuditSha256: h } };
  const audit = { correctedAudit: { sha256: h }, originalEmptyWindow: { sha256: h } };
  const files = phases.flatMap(p => [{ file: `${p.phase}-journey.json`, sha256: h }, { file: `${p.phase}-result.json`, sha256: h }]);
  files.push(...['installed-suite.json', 'installed-audit.json', 'corrected-audit.json', 'original-empty-window-audit.json'].map(file => ({ file, sha256: h })));
  validateFrozenReceiptBindings({ files }, candidate, audit);
  files[0].sha256 = 'b'.repeat(64); assert.throws(() => validateFrozenReceiptBindings({ files }, candidate, audit));
});
