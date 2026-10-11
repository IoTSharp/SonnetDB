import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { digest, readJson, requireThat, validatePaths } from './package.mjs';

export function names(version) {
  requireThat(/^4\.5\.0-preview\.1\.[1-9][0-9]{0,5}$/u.test(version), 'Invalid Preview version');
  const base = `sonnetdb-workbench-web-${version}`;
  return { zip: `${base}-win-x64.zip`, manifest: `${base}-manifest.json`, notes: `${base}-notes.md`, sums: `${base}-SHA256SUMS.txt` };
}
export function identity(seal) {
  const n = names(seal.version);
  requireThat(seal.schemaVersion === 1 && seal.kind === 'immutable-preview-distribution-addendum', 'Unknown distribution schema');
  requireThat(seal.repository === 'IoTSharp/SonnetDB' && seal.profile === 'preview-1' && seal.scopeRevision === 'R1' && seal.rid === 'win-x64', 'Distribution scope mismatch');
  requireThat(seal.candidate === `candidate-${seal.version.split('.').at(-1)}` && seal.tag === `workbench-preview-1.${seal.version.split('.').at(-1)}`, 'Candidate/tag mismatch');
  requireThat(/^[a-f0-9]{40}$/u.test(seal.sourceCommit) && /^[a-f0-9]{40}$/u.test(seal.sourceTree), 'Invalid source SHA/tree');
  requireThat(/^\d+$/u.test(seal.build.runId) && seal.build.attempt === 1, 'Invalid immutable build attempt');
  exactList(seal.attachments, Object.values(n));
  return n;
}
function exactList(files, expected) {
  requireThat(Array.isArray(files) && files.length === expected.length, 'Attachment count mismatch');
  validatePaths(files);
  requireThat(JSON.stringify(files.map(f => f.path).sort()) === JSON.stringify([...expected].sort()), 'Unexpected/missing attachment');
}
function regular(file, maxBytes) {
  const s = fs.lstatSync(file);
  requireThat(s.isFile() && !s.isSymbolicLink() && s.size > 0 && s.size <= maxBytes, 'File type/size budget');
  return fs.readFileSync(file);
}
function checkedFile(file, expected, maxBytes = 256 * 1024 * 1024) {
  const b = regular(file, maxBytes);
  requireThat(b.length === expected.size && digest(b) === expected.sha256, `Hash/size mismatch: ${expected.path}`);
  return b;
}
export function checksums(files, n) {
  return [n.zip, n.manifest, n.notes].map(name => `${files.find(f => f.path === name).sha256}  ${name}\n`).join('');
}
export function validateManifest(m, seal) {
  const n = identity(seal);
  requireThat(m.schemaVersion === 1 && m.kind === 'workbench-preview-artifact-manifest', 'Manifest schema mismatch');
  requireThat(m.identity.candidate === seal.candidate && m.identity.version === seal.version && m.identity.profile === seal.profile && m.identity.scopeRevision === seal.scopeRevision && m.identity.rid === seal.rid, 'Manifest identity mismatch');
  requireThat(m.source.commit === seal.sourceCommit && m.source.tree === seal.sourceTree && m.identity.sourceCommit === seal.sourceCommit && m.identity.sourceTree === seal.sourceTree, 'Manifest source mismatch');
  requireThat(m.components.server.productVersion === `${seal.version}+${seal.sourceCommit}` && m.components.web.releaseVersion === seal.version && m.components.web.profile === seal.profile, 'Component mismatch');
  requireThat(m.build.ci.repository === seal.repository && m.build.ci.runId === seal.build.runId && Number(m.build.ci.attempt) === seal.build.attempt && m.build.ci.workflowSha === seal.sourceCommit, 'Manifest run/attempt mismatch');
  requireThat(m.build.nativeAot === 'PUBLISHED_WIN_X64_NO_CLR_DIRECTORY' && m.build.aotWarningCount === 0, 'NativeAOT proof missing');
  const z = seal.attachments.find(f => f.path === n.zip);
  requireThat(JSON.stringify(m.zip) === JSON.stringify(z), 'Manifest ZIP binding mismatch');
  validatePaths(m.files);
  requireThat(m.files.length === seal.archiveFiles, 'Archive count mismatch');
  requireThat(!m.files.some(f => /(?:^|\/)(?:SonnetDB\.(?:Studio|Cli)|.*\.(?:vsix|msi|nupkg|deb|rpm))(?:$|\/)/iu.test(f.path)), 'Deferred package entry');
  return m;
}
export function validateNotes(bytes, seal) {
  const text = bytes.toString('utf8');
  const match = text.match(/<!-- preview-identity: (\{[^\n]+\}) -->/u);
  requireThat(match, 'Notes identity missing');
  const i = JSON.parse(match[1]);
  for (const k of ['candidate', 'version', 'sourceCommit', 'profile', 'scopeRevision', 'rid']) requireThat(i[k] === seal[k], 'Notes identity mismatch');
  requireThat(text.includes('NOT_READY') && text.includes('unknown') && text.includes('P08') && text.includes('回退') && text.includes('反馈'), 'Notes boundary missing');
}
export function validateBindings(seal, candidateBytes, auditBytes) {
  identity(seal);
  requireThat(digest(candidateBytes) === seal.candidateRecord.sha256 && digest(auditBytes) === seal.acceptanceAudit.sha256, 'Frozen record drift');
  const c = JSON.parse(candidateBytes), a = JSON.parse(auditBytes);
  requireThat(c.candidate.id === seal.candidate && c.candidate.sourceCommit === seal.sourceCommit && c.candidate.sourceTree === seal.sourceTree && c.candidate.version === seal.version, 'Candidate source mismatch');
  requireThat(c.ci.runId === seal.build.runId && Number(c.ci.attempt) === seal.build.attempt && c.ci.conclusion === 'success', 'Candidate CI mismatch');
  requireThat(c.validation.installedUserJourneyP05 === 'PASS_R1_ON_RECORDED_WINDOWS_HOST' && c.installedAcceptance.summary.status === 'PASS', 'P05 not accepted');
  requireThat(a.version === seal.version && a.packageSource === seal.sourceCommit && a.packageUnchanged === true && a.originalEmptyWindow.effectiveStatus === 'FAIL_EMPTY_WINDOW' && a.originalEmptyWindow.notAcceptanceEvidence === true, 'Audit boundary missing');
  requireThat(a.correctedAudit.status === 'PASS' && a.correctedAudit.receiptCount >= 15 && a.correctedAudit.identityCount >= 100 && a.correctedAudit.liveMatches === 0 && a.correctedAudit.remainingListeners === 0, 'Nonempty corrected audit missing');
  exactList(seal.p04Assets, [names(seal.version).zip, names(seal.version).manifest, names(seal.version).sums]);
  requireThat(JSON.stringify([...seal.p04Assets].sort((a,b) => a.path.localeCompare(b.path))) === JSON.stringify([...c.candidate.builtArtifacts].sort((a,b) => a.path.localeCompare(b.path))), 'P04 asset drift');
}
export function verifyDirectory(root, seal) {
  const n = identity(seal);
  const entries = fs.readdirSync(root);
  requireThat(entries.length === 4 && JSON.stringify(entries.sort()) === JSON.stringify(Object.values(n).sort()), 'Exact four attachments required');
  const files = seal.attachments.map(f => ({ ...f, bytes: checkedFile(path.join(root, f.path), f) }));
  const bytes = name => files.find(f => f.path === name).bytes;
  requireThat(bytes(n.sums).toString('utf8') === checksums(seal.attachments, n), 'Checksum list must have exactly three non-self entries');
  validateNotes(bytes(n.notes), seal);
  return validateManifest(JSON.parse(bytes(n.manifest)), seal);
}
export function prepare(root, output, notesPath, seal) {
  const n = identity(seal);
  requireThat(!fs.existsSync(output) && path.resolve(root) !== path.resolve(output), 'Output must be a new directory');
  const entries = fs.readdirSync(root);
  requireThat(entries.length === 3 && JSON.stringify(entries.sort()) === JSON.stringify([n.zip, n.manifest, n.sums].sort()), 'Exact original P04 set required');
  for (const f of seal.p04Assets) checkedFile(path.join(root, f.path), f);
  const notes = checkedFile(notesPath, seal.attachments.find(f => f.path === n.notes), 1024 * 1024);
  validateNotes(notes, seal);
  validateManifest(readJson(path.join(root, n.manifest)), seal);
  fs.mkdirSync(output, { recursive: false });
  for (const file of [n.zip, n.manifest]) fs.copyFileSync(path.join(root, file), path.join(output, file), fs.constants.COPYFILE_EXCL);
  fs.writeFileSync(path.join(output, n.notes), notes, { flag: 'wx' });
  fs.writeFileSync(path.join(output, n.sums), checksums(seal.attachments, n), { flag: 'wx' });
  verifyDirectory(output, seal);
}
export function validateBuildRun(run, artifacts, seal) {
  requireThat(run.repository.full_name === seal.repository && run.head_sha === seal.sourceCommit && run.path === '.github/workflows/workbench-preview-build.yml', 'Wrong build source/workflow');
  requireThat(String(run.id) === seal.build.runId && run.run_attempt === seal.build.attempt && run.event === 'workflow_dispatch' && run.status === 'completed' && run.conclusion === 'success', 'Build attempt not latest successful dispatch');
  requireThat(artifacts.total_count <= 100 && artifacts.artifacts.length === artifacts.total_count, 'Incomplete artifact list');
  const matches = artifacts.artifacts.filter(a => a.name === `workbench-preview-${seal.version}`);
  requireThat(matches.length === 1 && matches[0].expired === false && matches[0].size_in_bytes > 0 && Date.parse(matches[0].created_at) >= Date.parse(run.run_started_at), 'Missing/stale/expired build artifact');
  requireThat(String(matches[0].id) === seal.build.artifactId && matches[0].digest === seal.build.artifactDigest, 'Artifact ID/digest mismatch');
}
export function validateP07(p, readiness, seal, sealBytes, evidenceRoot) {
  requireThat(p.schemaVersion === 1 && p.milestone === 'M47-P07' && p.status === 'READY' && p.source === 'github-and-installed-package', 'P07 not READY');
  for (const k of ['repository', 'candidate', 'version', 'sourceCommit']) requireThat(p[k] === seal[k], 'P07 identity mismatch');
  requireThat(p.distributionSha256 === digest(sealBytes), 'P07 distribution mismatch');
  requireThat(readiness.status === 'READY' && readiness.source === 'github' && readiness.repository === seal.repository && readiness.commitSha === seal.sourceCommit && readiness.version === seal.version, 'Live original verifier not READY');
  requireThat(readiness.workflows.length === 12 && readiness.workflows.every(w => w.ready === true && w.runId), 'Full policy missing');
  const required = ['C01','C02','C03','C04','C05','D01','P05-installed-R1','licenses'];
  requireThat(p.checks?.length === required.length, 'P07 required checks missing');
  const seen = new Set();
  for (const check of p.checks) {
    requireThat(required.includes(check.id) && !seen.has(check.id) && check.status === 'PASS', 'P07 rejected/missing/duplicate check');
    seen.add(check.id);
    requireThat(/^[a-zA-Z0-9_.-]+\.json$/u.test(check.file) && /^[a-f0-9]{64}$/u.test(check.sha256), 'Unsafe P07 evidence path/hash');
    const b = regular(path.join(evidenceRoot, check.file), 4 * 1024 * 1024);
    requireThat(digest(b) === check.sha256, 'P07 raw evidence drift');
    const e = JSON.parse(b);
    requireThat(e.status === 'PASS' && e.candidate === seal.candidate && e.version === seal.version && e.sourceCommit === seal.sourceCommit && e.check === check.id, 'P07 raw evidence identity/status mismatch');
  }
  // Scheduled seven-day Parity remains an observation; no status requirement here.
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const [command, sealFile, ...args] = process.argv.slice(2);
  const seal = readJson(sealFile);
  if (command === 'prepare') prepare(args[0], args[1], args[2], seal);
  else if (command === 'verify') verifyDirectory(args[0], seal);
  else throw new Error('Unknown distribution command');
  console.log(`PASS ${command}: ${seal.candidate}; NOT_READY; no publication`);
}
