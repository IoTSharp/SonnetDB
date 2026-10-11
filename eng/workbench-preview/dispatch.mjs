import fs from 'node:fs';
import path from 'node:path';
import cp from 'node:child_process';
import { readJson, requireThat, digest } from './package.mjs';
import { identity, validateBindings, validateBuildRun, prepare, verifyDirectory, validateP07 } from './distribution.mjs';
import { publish } from './release.mjs';
import { target, response, expectedMissing, binaryBodyHeaders } from './github-api.mjs';

const [mode, number, root, output] = process.argv.slice(2);
requireThat(['prepare','gate','publish'].includes(mode) && /^[1-9][0-9]{0,5}$/u.test(number), 'Invalid operation/candidate');
const clock = Date.now(), deadline = clock + 600_000;
let calls = 0;
const directory = path.join(root, 'docs/design/m47-unified-management-workbench');
const sealFile = path.join(directory, `preview-1-distribution-${number}.json`), sealBytes = fs.readFileSync(sealFile), seal = JSON.parse(sealBytes);
const n = identity(seal), repository = seal.repository;
requireThat(process.env.GITHUB_REPOSITORY === repository && process.env.GITHUB_EVENT_NAME === 'workflow_dispatch', 'Only the repository manual workflow may execute this controller');
fs.mkdirSync(output, { recursive: true });
const resultPath = path.join(output, `${mode}.json`);
requireThat(!fs.existsSync(resultPath), 'Immutable result exists');
const record = { mode, candidate: seal.candidate, version: seal.version, sourceCommit: seal.sourceCommit, toolingCommit: process.env.GITHUB_SHA,
  tag: seal.tag, distributionSha256: digest(sealBytes), startedUtc: new Date().toISOString(), status: 'RUNNING', phase: 'preflight', mutationStarted: false };
let checkpointCount = 0;
function checkpoint(value) {
  requireThat(checkpointCount < 24, 'Publication journal item budget');
  const snapshot = { ...value, checkpoint: ++checkpointCount, observedUtc: new Date().toISOString() };
  const file = fs.openSync(path.join(output, `${mode}-journal-${String(checkpointCount).padStart(3, '0')}.json`), 'wx');
  try { fs.writeFileSync(file, `${JSON.stringify(snapshot, null, 2)}\n`); fs.fsyncSync(file); }
  finally { fs.closeSync(file); }
}
function budget() {
  requireThat(calls++ < 64 && Date.now() < deadline && !fs.existsSync(path.join(output, 'cancel')), 'Command/cancel/wall budget exhausted');
}
function command(exe, args, input, maxBuffer = 256 * 1024 * 1024, timeout = 45_000) {
  budget();
  return cp.execFileSync(exe, args, { input, timeout: Math.min(timeout, deadline - Date.now()), maxBuffer, windowsHide: true, stdio: ['pipe','pipe','pipe'] });
}
async function api(method, endpoint, body, accept = 'application/vnd.github+json') {
  requireThat(mode === 'publish' || method === 'GET', 'Read-only controller refuses mutation');
  const binaryResponse = method === 'GET' && accept === 'application/octet-stream';
  const args = ['api', target(repository, endpoint), '--include', '--method', method, '-H', `Accept: ${binaryResponse ? accept : 'application/vnd.github+json'}`, '-H', 'X-GitHub-Api-Version: 2022-11-28'];
  if (body !== undefined) {
    for (const header of Buffer.isBuffer(body) ? binaryBodyHeaders(body) : ['Content-Type: application/json']) args.push('-H', header);
    args.push('--input', '-');
  }
  let raw;
  try { raw = command('gh', args, body === undefined ? undefined : Buffer.isBuffer(body) ? body : JSON.stringify(body)); }
  catch (e) {
    const status = e.stdout?.toString('utf8', 0, 150).match(/^HTTP\/\S+ (\d{3})/u)?.[1];
    if (expectedMissing(method, endpoint, e.stdout)) return null;
    throw new Error(status ? `GitHub HTTP ${status}` : 'GitHub transport effect unknown');
  }
  return response(raw, binaryResponse);
}
try {
  validateBindings(seal, fs.readFileSync(path.join(root, seal.candidateRecord.path)), fs.readFileSync(path.join(root, seal.acceptanceAudit.path)));
  for (const gate of seal.gateAuthority) requireThat(digest(fs.readFileSync(path.join(root, gate.path))) === gate.sha256, 'Original release authority drift');
  const run = await api('GET', `actions/runs/${seal.build.runId}`);
  const artifacts = await api('GET', `actions/runs/${seal.build.runId}/artifacts?per_page=100`);
  validateBuildRun(run, artifacts, seal);
  const source = path.join(root, 'candidate-source');
  requireThat(command('git', ['-C', source, 'rev-parse', 'HEAD']).toString().trim() === seal.sourceCommit, 'Checkout source mismatch');
  requireThat(command('git', ['-C', source, 'rev-parse', 'HEAD^{tree}']).toString().trim() === seal.sourceTree, 'Checkout tree mismatch');
  requireThat(command('git', ['-C', source, 'status', '--porcelain']).length === 0, 'Candidate checkout dirty');
  const candidate = readJson(path.join(root, seal.candidateRecord.path));
  for (const sub of candidate.candidate.submodules) requireThat(command('git', ['-C', path.join(source, sub.path), 'rev-parse', 'HEAD']).toString().trim() === sub.commit, 'Gitlink mismatch');
  const delivery = path.join(output, 'delivery');
  if (mode === 'prepare') {
    const incoming = path.join(output, 'incoming');
    requireThat(!fs.existsSync(incoming), 'Download directory exists');
    command('gh', ['run','download',seal.build.runId,'--repo',repository,'--name',`workbench-preview-${seal.version}`,'--dir',incoming]);
    prepare(incoming, delivery, path.join(directory, n.notes), seal);
  } else verifyDirectory(delivery, seal);
  // Verify the original archive contents on every mode, including immediately before mutation.
  command(process.env.PREVIEW_PWSH, ['-NoProfile','-File',path.join(root,'eng/workbench-preview/Verify-Distribution.ps1'),'-Seal',sealFile,'-Delivery',delivery], undefined, 1024 * 1024, 125_000);
  if (mode !== 'prepare') {
    const p07 = readJson(path.join(directory, `preview-1-p07-${number}.json`));
    requireThat(/^[a-f0-9]{40}$/u.test(p07.distributorCommit), 'P07 distributor source SHA missing');
    const distributorPaths = ['.github/workflows/workbench-preview-publish.yml', 'eng/workbench-preview/distribution.mjs', 'eng/workbench-preview/release.mjs',
      'eng/workbench-preview/dispatch.mjs', 'eng/workbench-preview/github-api.mjs', 'eng/workbench-preview/Invoke-Distribution.ps1', 'eng/workbench-preview/Verify-Distribution.ps1',
      'eng/workbench-preview/Invoke-Owned.ps1', 'eng/workbench-preview/JobProcess.cs', 'eng/workbench-preview/package.mjs'];
    requireThat(p07.distributorInputs?.length === distributorPaths.length && new Set(p07.distributorInputs.map(f => f.path)).size === distributorPaths.length, 'P07 distributor input inventory incomplete');
    for (const input of p07.distributorInputs) requireThat(distributorPaths.includes(input.path) && digest(fs.readFileSync(path.join(root, input.path))) === input.sha256, 'P07 reviewed distribution implementation drift');
    record.reviewedDistributorCommit = p07.distributorCommit;
    const proof = p07.evidenceArtifact;
    requireThat(proof?.repository === repository && /^\d+$/u.test(proof.runId) && Number.isSafeInteger(proof.attempt) && proof.attempt > 0 && /^\d+$/u.test(proof.id) && /^[a-zA-Z0-9_.-]{1,100}$/u.test(proof.name), 'P07 artifact binding missing');
    const evidenceRun = await api('GET', `actions/runs/${proof.runId}`);
    const evidenceArtifact = await api('GET', `actions/artifacts/${proof.id}`);
    requireThat(evidenceRun.repository.full_name === repository && evidenceRun.status === 'completed' && evidenceRun.conclusion === 'success' && evidenceRun.run_attempt === proof.attempt && evidenceRun.head_sha === p07.toolingCommit, 'P07 evidence run mismatch');
    requireThat(evidenceArtifact.name === proof.name && evidenceArtifact.expired === false && evidenceArtifact.size_in_bytes > 0 && evidenceArtifact.workflow_run.id === Number(proof.runId) && evidenceArtifact.digest === proof.digest && Date.parse(evidenceArtifact.created_at) >= Date.parse(evidenceRun.run_started_at), 'P07 evidence artifact stale/mismatched');
    const evidence = path.join(output, `p07-evidence-${mode}`);
    command('gh', ['run','download',proof.runId,'--repo',repository,'--name',proof.name,'--dir',evidence]);
    const readiness = path.join(output, `readiness-${mode}.json`);
    command(process.env.PREVIEW_PWSH, ['-NoProfile','-File',path.join(root,'eng/verify-release-readiness.ps1'),'-Repository',repository,'-CommitSha',seal.sourceCommit,'-Version',seal.version,'-OutputPath',readiness], undefined, 4 * 1024 * 1024, 400_000);
    validateP07(p07, readJson(readiness), seal, sealBytes, evidence);
    if (mode === 'publish') {
      const environment = await api('GET', 'environments/workbench-preview-p08');
      const recoveryBytes = process.env.P08_RECOVERY_AUTHORIZATION ? fs.readFileSync(path.join(directory, `preview-1-p08-recovery-${number}.json`)) : undefined;
      if (recoveryBytes) record.recoveryOriginalRunId = JSON.parse(recoveryBytes).originalRunId;
      await publish(api, { seal, sealBytes, delivery, environment, record, checkpoint, context: { event:process.env.GITHUB_EVENT_NAME, operation:mode, repository:process.env.GITHUB_REPOSITORY, authorization:process.env.P08_AUTHORIZATION,
        recovery: recoveryBytes ? JSON.parse(recoveryBytes) : undefined, recoveryBytes, recoveryAuthorization:process.env.P08_RECOVERY_AUTHORIZATION } });
    }
  }
  if (record.status === 'RUNNING') record.status = mode === 'prepare' ? 'PREPARED_NOT_READY' : 'P07_RECHECK_PASS_NO_PUBLICATION';
} catch (e) {
  record.status = record.mutationStarted ? 'UNKNOWN_RECONCILE_ORIGINAL_TAG_RELEASE_IDS_NO_RETRY' : 'REFUSED';
  record.error = e.message; process.exitCode = 1;
} finally {
  record.apiAndCommandCount = calls; record.elapsedMilliseconds = Date.now() - clock; record.endedUtc = new Date().toISOString();
  fs.writeFileSync(resultPath, `${JSON.stringify(record,null,2)}\n`, { flag:'wx' });
  console.log(JSON.stringify(record));
}
