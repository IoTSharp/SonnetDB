import fs from 'node:fs';
import path from 'node:path';
import cp from 'node:child_process';
import { digest, readJson, requireThat } from './package.mjs';
import { validateBindings, verifyDirectory } from './distribution.mjs';
import { verifyOriginals, originalBytes, validateFrozenReceiptBindings, installedChecks, checkedRecord, evaluateReview, distributorPaths } from './p07.mjs';
const [root, number, prepareRun, output] = process.argv.slice(2);
requireThat(/^[1-9][0-9]{0,5}$/u.test(number) && /^[1-9][0-9]{0,18}$/u.test(prepareRun), 'P07 arguments');
const started = Date.now(), deadline = started + 600_000; let calls = 0;
const docs = path.join(root, 'docs/design/m47-unified-management-workbench');
const sealFile = path.join(docs, `preview-1-distribution-${number}.json`), sealBytes = fs.readFileSync(sealFile), seal = JSON.parse(sealBytes);
requireThat(process.env.GITHUB_EVENT_NAME === 'workflow_dispatch' && process.env.GITHUB_REPOSITORY === seal.repository, 'Only manual repository evidence collection');
fs.mkdirSync(output, { recursive: true });
function command(exe, args, timeout = 45_000) {
  requireThat(calls++ < 30 && Date.now() < deadline && !fs.existsSync(path.join(output, 'cancel')), 'P07 count/wall/cancel budget');
  return cp.execFileSync(exe, args, { timeout: Math.min(timeout, deadline - Date.now()), windowsHide: true, maxBuffer: 16 * 1024 * 1024 });
}
const proofDirectory = path.join(output, 'prepare-proof'); fs.mkdirSync(proofDirectory);
function api(file, endpoint) {
  const bytes = command('gh', ['api', `repos/${seal.repository}/${endpoint}`]);
  fs.writeFileSync(path.join(proofDirectory, file), bytes, { flag: 'wx' }); return JSON.parse(bytes);
}
const collection = { schemaVersion: 1, milestone: 'M47-P07', source: 'github-and-installed-package', repository: seal.repository, candidate: seal.candidate,
  version: seal.version, sourceCommit: seal.sourceCommit, toolingCommit: process.env.GITHUB_SHA, distributionSha256: digest(sealBytes), status: 'COLLECTING', published: false };
try {
  validateBindings(seal, fs.readFileSync(path.join(root, seal.candidateRecord.path)), fs.readFileSync(path.join(root, seal.acceptanceAudit.path)));
  for (const gate of seal.gateAuthority) requireThat(digest(fs.readFileSync(path.join(root, gate.path))) === gate.sha256, 'Original gate authority drift');
  const originalDirectory = path.join(docs, `preview-1-p07-evidence-${number}`), bundle = readJson(path.join(originalDirectory, 'inventory.json'));
  requireThat(bundle.candidate === seal.candidate && bundle.sourceCommit === seal.sourceCommit && bundle.version === seal.version && bundle.candidateRecordSha256 === seal.candidateRecord.sha256 && bundle.acceptanceAuditSha256 === seal.acceptanceAudit.sha256, 'Original candidate binding drift');
  validateFrozenReceiptBindings(bundle, readJson(path.join(root, seal.candidateRecord.path)), readJson(path.join(root, seal.acceptanceAudit.path)));
  const originals = verifyOriginals(bundle, originalDirectory);
  const archived = path.join(output, 'originals'); fs.mkdirSync(archived);
  for (const file of bundle.files) fs.writeFileSync(path.join(archived, file.file), originalBytes(file, originalDirectory), { flag: 'wx' });
  for (const file of bundle.supportingFiles ?? []) fs.writeFileSync(path.join(archived, file.file), originalBytes(file, originalDirectory), { flag: 'wx' });
  fs.copyFileSync(path.join(originalDirectory, 'inventory.json'), path.join(archived, 'source-storage-inventory.json'), fs.constants.COPYFILE_EXCL);
  const rawInventory = bundle.files.map(({ storageFile, encoding, ...file }) => file);
  fs.writeFileSync(path.join(archived, 'inventory.json'), `${JSON.stringify({ ...bundle, files: rawInventory, supportingFiles: (bundle.supportingFiles ?? []).map(({ storageFile, encoding, ...file }) => file), storage: 'decoded-original-bytes-verified-before-copy' }, null, 2)}\n`, { flag: 'wx' });
  const run = api('run.json', `actions/runs/${prepareRun}`), jobs = api('jobs.json', `actions/runs/${prepareRun}/jobs?filter=latest&per_page=100`), artifacts = api('artifacts.json', `actions/runs/${prepareRun}/artifacts?per_page=100`);
  requireThat(run.repository.full_name === seal.repository && String(run.id) === prepareRun && run.path === '.github/workflows/workbench-preview-publish.yml' && run.event === 'workflow_dispatch' && run.status === 'completed' && run.conclusion === 'success' && run.head_sha === collection.toolingCommit, 'Successful current-tooling prepare required');
  const job = jobs.jobs.filter(j => j.name === 'prepare');
  requireThat(jobs.total_count === jobs.jobs.length && jobs.total_count <= 100 && job.length === 1 && job[0].conclusion === 'success', 'Prepare job evidence missing');
  for (const name of ['Preserve immutable repository bytes on Windows', 'Prepare exact four attachments without publication', 'Retain sealed distribution for review']) requireThat(job[0].steps.filter(s => s.name === name && s.conclusion === 'success').length === 1, 'Prepare required step missing');
  requireThat(jobs.jobs.filter(j => j.name === 'publish').every(j => j.conclusion === 'skipped'), 'P07 must not perform publication');
  requireThat(artifacts.total_count === artifacts.artifacts.length && artifacts.total_count <= 100, 'Complete artifact inventory required');
  for (const name of [`preview-distribution-${number}`, `preview-distribution-evidence-${number}`]) {
    const matches = artifacts.artifacts.filter(a => a.name === name);
    requireThat(matches.length === 1 && !matches[0].expired && matches[0].size_in_bytes > 0 && /^sha256:[a-f0-9]{64}$/u.test(matches[0].digest) && Date.parse(matches[0].created_at) >= Date.parse(run.run_started_at), 'Prepare artifact invalid/stale');
  }
  const delivery = path.join(output, 'delivery'), prepareEvidence = path.join(proofDirectory, 'artifact');
  command('gh', ['run', 'download', prepareRun, '--repo', seal.repository, '--name', `preview-distribution-${number}`, '--dir', delivery], 90_000);
  command('gh', ['run', 'download', prepareRun, '--repo', seal.repository, '--name', `preview-distribution-evidence-${number}`, '--dir', prepareEvidence], 60_000);
  const manifest = verifyDirectory(delivery, seal);
  requireThat(manifest.files.find(file => file.path === 'dependency-licenses.json')?.sha256 === bundle.files.find(file => file.file === 'dependency-licenses-original.json')?.sha256, 'Dependency inventory is not the immutable package file');
  const prepare = readJson(path.join(prepareEvidence, 'prepare.json'));
  const owned = readJson(path.join(prepareEvidence, 'owned/prepare.result.json'));
  requireThat(prepare.mode === 'prepare' && prepare.status === 'PREPARED_NOT_READY' && prepare.mutationStarted === false && prepare.toolingCommit === collection.toolingCommit && prepare.sourceCommit === seal.sourceCommit && prepare.version === seal.version && prepare.candidate === seal.candidate && owned.status === 'PASS' && owned.cleanupComplete === true, 'Original prepare controller/Job receipt mismatch');
  fs.writeFileSync(path.join(output, 'zip-verification.log'), command(process.env.PREVIEW_PWSH, ['-NoProfile', '-File', path.join(root, 'eng/workbench-preview/Verify-Distribution.ps1'), '-Seal', sealFile, '-Delivery', delivery], 125_000), { flag: 'wx' });
  const readinessOutput = path.join(output, 'release-gates'); let verifierFailed = false;
  try { fs.writeFileSync(path.join(output, 'original-verifier.stdout.log'), command(process.env.PREVIEW_PWSH, ['-NoProfile', '-File', path.join(root, 'eng/workbench-preview/Invoke-P07Readiness.ps1'), '-SourceCommit', seal.sourceCommit, '-Version', seal.version, '-Output', readinessOutput], 400_000), { flag: 'wx' }); }
  catch (error) {
    verifierFailed = true;
    fs.writeFileSync(path.join(output, 'original-verifier.stdout.log'), error.stdout ?? Buffer.alloc(0), { flag: 'wx' });
    fs.writeFileSync(path.join(output, 'original-verifier.stderr.log'), error.stderr ?? Buffer.from(error.message), { flag: 'wx' });
  }
  const readiness = readJson(path.join(readinessOutput, 'readiness.json'));
  requireThat(readiness.commitSha === seal.sourceCommit && readiness.version === seal.version && readiness.repository === seal.repository && ['READY', 'NOT_READY'].includes(readiness.status), 'Original verifier identity/status invalid');
  requireThat(!verifierFailed || readiness.status === 'NOT_READY', 'Verifier error cannot be called READY');
  const checks = installedChecks(bundle, originals);
  const raw = [...rawInventory];
  const d01 = Buffer.from(`${JSON.stringify({ ...prepare, status: 'PASS', check: 'D01', originalControllerStatus: prepare.status, originalJobStatus: run.conclusion, originalRunId: prepareRun, source: 'normalized-from-original-prepare-receipts', artifactIds: artifacts.artifacts.map(a => ({ id: String(a.id), name: a.name, digest: a.digest })) }, null, 2)}\n`);
  // This normalization is specific to read-only four-attachment preparation; it never grants publication readiness.
  fs.writeFileSync(path.join(archived, 'd01-prepare-acceptance.json'), d01, { flag: 'wx' }); raw.push({ file: 'd01-prepare-acceptance.json', size: d01.length, sha256: digest(d01), originalPath: 'prepare-proof/ contains unmodified run/jobs/artifacts/controller/Job receipts' });
  checks.push({ id: 'D01', status: 'PASS', originalFiles: ['d01-prepare-acceptance.json'], boundary: 'Exact four attachments and unchanged ZIP only; no public tag/Release, upload or anonymous download.' },
    { id: 'P05-installed-R1', status: 'PASS', originalFiles: ['installed-suite.json', 'installed-audit.json', 'corrected-audit.json', 'first-journey.json', 'reopen-journey.json', 'rollback-journey.json', 'legacy-journey.json'], boundary: 'Recorded local Windows R1 only; original empty-window audit is FAIL_EMPTY_WINDOW and is excluded from acceptance.' });
  const license = originals.get('license-review.json');
  requireThat(license.sourceCommit === seal.sourceCommit && license.version === seal.version && license.candidate === seal.candidate
    && Array.isArray(license.gaps) && license.gaps.length <= 5000 && license.dependencyCount === manifest.dependencyInventory.length
    && license.gapCount === license.gaps.length, 'License review binding/count mismatch');
  checks.push({ id: 'licenses', status: license.status, originalFiles: ['license-review.json', 'dependency-licenses-original.json', 'license-primary-sources.json', 'microsoft-primary-documents.json'], issues: license.gaps.filter(g => g.status !== 'PASS_BUILD_TOOL_NOT_REDISTRIBUTED').map(g => `${g.id}: ${g.issues.join(' ')}`) });
  collection.checks = checks.map(check => {
    const file = `${check.id}.json`, bytes = Buffer.from(`${JSON.stringify(checkedRecord(check, seal, raw), null, 2)}\n`);
    fs.writeFileSync(path.join(output, file), bytes, { flag: 'wx' }); return { id: check.id, status: check.status, file, sha256: digest(bytes) };
  });
  Object.assign(collection, evaluateReview(readiness, checks));
  collection.distributorCommit = collection.toolingCommit;
  collection.distributorInputs = distributorPaths.map(file => ({ path: file, sha256: digest(fs.readFileSync(path.join(root, file))) }));
  collection.collectorInputs = ['.github/workflows/workbench-preview-p07.yml', 'eng/workbench-preview/p07.mjs', 'eng/workbench-preview/collect-p07.mjs', 'eng/workbench-preview/Invoke-P07.ps1', 'eng/workbench-preview/Invoke-P07Readiness.ps1'].map(file => ({ path: file, sha256: digest(fs.readFileSync(path.join(root, file))) }));
  collection.prepareRun = { repository: seal.repository, runId: prepareRun, attempt: run.run_attempt, toolingCommit: run.head_sha, artifacts: artifacts.artifacts.map(a => ({ id: String(a.id), name: a.name, digest: a.digest, expiresAt: a.expires_at })) };
  collection.collectionStatus = 'COMPLETE';
} catch (error) { collection.status = 'UNKNOWN'; collection.collectionStatus = 'FAILED'; collection.error = error.message; process.exitCode = 1; }
finally {
  collection.commandCount = calls; collection.endedUtc = new Date().toISOString(); collection.elapsedMilliseconds = Date.now() - started;
  collection.boundary = 'Successful evidence collection does not mean release readiness. Any non-PASS mandatory gate blocks publication; seven-day scheduled Parity remains nonblocking. Original package/manifest/checksum/FAIL/UNKNOWN remain immutable. P08 requires separate human authorization.';
  fs.writeFileSync(path.join(output, 'review.json'), `${JSON.stringify(collection, null, 2)}\n`, { flag: 'wx' });
  console.log(JSON.stringify({ status: collection.status, collectionStatus: collection.collectionStatus, blockers: collection.blockers?.length, error: collection.error, published: false }));
}
