import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

const deadline = Date.now() + 120_000;
const maximumFiles = 5000;
export function requireThat(value, message) { if (!value) throw new Error(message); }
export const digest = (bytes, algorithm = 'sha256') => crypto.createHash(algorithm).update(bytes).digest('hex');
export const readJson = (file) => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/u, ''));
export const writeJson = (file, value) => fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`, { flag: 'wx' });
function checkBudget(count) { requireThat(count <= maximumFiles && Date.now() < deadline, 'File/time budget exceeded'); }
export function inventory(root) {
  const files = [], directories = [root];
  for (let index = 0; index < directories.length; index++) {
    checkBudget(files.length + directories.length);
    requireThat(!fs.existsSync(path.join(root, 'cancel')), 'Cancelled');
    for (const entry of fs.readdirSync(directories[index], { withFileTypes: true })) {
      checkBudget(files.length + directories.length);
      const file = path.join(directories[index], entry.name);
      requireThat(!entry.isSymbolicLink(), `Symlink forbidden: ${file}`);
      if (entry.isDirectory()) directories.push(file);
      else {
        requireThat(entry.isFile(), `Unsupported file: ${file}`);
        const size = fs.statSync(file).size;
        requireThat(size <= 512 * 1024 * 1024, 'Individual file budget exceeded');
        files.push({ path: path.relative(root, file).replaceAll('\\', '/'), size, sha256: digest(fs.readFileSync(file)) });
      }
    }
  }
  return files.sort((a, b) => a.path.localeCompare(b.path, 'en'));
}
export function validatePaths(files) {
  requireThat(files.length > 0 && files.length <= maximumFiles, 'Invalid inventory size');
  const names = new Set();
  for (const file of files) {
    const name = file.path;
    requireThat(typeof name === 'string' && !name.includes('\\') && !name.startsWith('/')
      && name.split('/').every(part => part && part !== '.' && part !== '..' && !part.includes(':')), `Unsafe path: ${name}`);
    requireThat(!names.has(name.toLowerCase()), `Duplicate/case collision: ${name}`);
    names.add(name.toLowerCase());
    requireThat(!/(?:^|\/)(?:node_modules|bin|obj|data|sonnetdb-data|\.system|\.git|logs|test-results)(?:\/|$)|(?:(?:^|\/)(?:sonnetdb[.-])?studio[^/]*|\.vsix$|\.nupkg$|\.msi$|\.pdb$|\.map$|appsettings\.(?:Development|Container)|\.env|\.log$|\.trx$|(?:^|\/)(?:tokens|users|credentials)\.json$)/iu.test(name), `Forbidden package file: ${name}`);
    requireThat(Number.isSafeInteger(file.size) && file.size >= 0 && /^[a-f0-9]{64}$/u.test(file.sha256), 'Invalid file hash/size');
  }
  return names;
}
export function verifyInventory(actual, expected) {
  validatePaths(actual); validatePaths(expected);
  requireThat(JSON.stringify(actual) === JSON.stringify(expected), 'Exact inventory/hash mismatch');
}
export function validateProfile(profile, version) {
  requireThat(/^4\.5\.0-preview\.1\.[1-9]\d*$/u.test(version) && profile.version === version, 'Version mismatch');
  requireThat(profile.profile === 'preview-1' && profile.scopeRevision === 'R1' && profile.rid === 'win-x64', 'Profile/RID mismatch');
  requireThat(profile.releaseStatus === 'NOT_READY', 'Premature release approval');
  requireThat(JSON.stringify(profile.targets?.requiredOperatingSystems) === JSON.stringify(['Windows 11 x64'])
    && profile.targets.compatibility === 'EXACT_SAME_CANDIDATE_AND_VERSION_WEB_SERVER'
    && profile.targets.legacyServerCompatibility === 'NOT_ACCEPTED', 'Target compatibility mismatch');
  requireThat(JSON.stringify(profile.deferredModels) === JSON.stringify(['document', 'kv', 'fulltext', 'vector', 'object', 'mq', 'graph']), 'R1 deferrals changed');
  requireThat(profile.budgets.timeoutMilliseconds === 30000 && profile.budgets.maximumResponseBodyBytes === 4194304
    && profile.budgets.maximumSqlOrInsertInputUtf8Bytes === 65536, 'Budget mismatch');
  for (const action of profile.actions.filter(item => item.status.startsWith('DEFERRED'))) requireThat(action.allow.length === 0, 'Deferred action reopened');
}
export function validateNative(bytes) {
  requireThat(bytes.length > 512 && bytes.readUInt16LE(0) === 0x5a4d, 'Missing PE executable');
  const offset = bytes.readUInt32LE(0x3c);
  requireThat(offset + 256 < bytes.length && bytes.readUInt32LE(offset) === 0x4550, 'Invalid PE header');
  requireThat(bytes.readUInt16LE(offset + 4) === 0x8664 && bytes.readUInt16LE(offset + 24) === 0x20b, 'Expected win-x64 PE32+');
  const clrDirectory = offset + 24 + 112 + 14 * 8;
  requireThat(bytes.readUInt32LE(clrDirectory) === 0 && bytes.readUInt32LE(clrDirectory + 4) === 0, 'Managed apphost/CLR payload forbidden');
}
function git(root, args) { return execFileSync('git', ['-C', root, ...args], { encoding: 'utf8', timeout: 30000, maxBuffer: 16 * 1024 * 1024 }).trim(); }
export function captureSource(root, commit) {
  requireThat(/^[a-f0-9]{40}$/u.test(commit) && git(root, ['rev-parse', 'HEAD']) === commit, 'Full source SHA mismatch');
  requireThat(git(root, ['status', '--porcelain', '--untracked-files=all']) === '', 'Dirty checkout forbidden');
  const ignored = git(root, ['ls-files', '--others', '--ignored', '--exclude-standard']);
  requireThat(ignored === '', 'Pre-existing ignored build/runtime inputs forbidden');
  const gitlinks = git(root, ['ls-tree', '-r', commit]).split('\n').filter(line => line.startsWith('160000 ')).map(line => {
    const [metadata, name] = line.split('\t');
    const sha = metadata.split(' ')[2];
    requireThat(git(path.join(root, name), ['rev-parse', 'HEAD']) === sha, `Gitlink mismatch: ${name}`);
    requireThat(git(path.join(root, name), ['status', '--porcelain', '--untracked-files=all']) === '', `Dirty gitlink: ${name}`);
    requireThat(git(path.join(root, name), ['ls-files', '--others', '--ignored', '--exclude-standard']) === '', `Old gitlink build inputs: ${name}`);
    return { path: name, commit: sha };
  });
  requireThat(gitlinks.length === 2, 'Unexpected gitlink inventory');
  const tracked = git(root, ['ls-files']).split('\n');
  requireThat(tracked.length <= 15000, 'Source file budget exceeded');
  const inputs = tracked.filter(file => /^(?:src\/|web\/|eng\/workbench-preview\/|licenses\/|extensions\/)|^(?:Directory\.|global.json|NuGet.Config|LICENSE|THIRD_PARTY_NOTICES|llms)/u.test(file))
    .filter(file => fs.existsSync(path.join(root, file)) && fs.statSync(path.join(root, file)).isFile())
    .map(file => ({ path: file, sha256: digest(fs.readFileSync(path.join(root, file))) }));
  return { commit, tree: git(root, ['rev-parse', `${commit}^{tree}`]), gitlinks, inputs, cleanCheckout: true };
}
export function validateNetwork(settings) {
  for (const endpoint of Object.values(settings.Kestrel.Endpoints)) requireThat(new URL(endpoint.Url).hostname === '127.0.0.1', 'Non-loopback listener');
  requireThat(settings.SonnetDBServer.DataRoot === '../data', 'Data directory must be separate from binaries');
  for (const protocol of ['Mqtt', 'Coap', 'LineProtocolUdp', 'Modbus']) requireThat(settings.SonnetDBServer[protocol].Enabled === false, `Enabled protocol: ${protocol}`);
  requireThat(!settings.SonnetDBServer.Mqtt.ExternalClient.Enabled && !settings.SonnetDBServer.Mqtt.Sparkplug.Enabled && !settings.SonnetDBServer.Coap.Dtls.Enabled, 'Enabled nested listener/client');
}
export function validatePublishInventory(files, webFiles) {
  const nativeNames = ['SonnetDB.exe', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll', 'onnxruntime_providers_dml.dll', 'DirectML.dll', 'libSkiaSharp.dll', 'usearch_c.dll', 'libusearch_c.dll', 'numkong.dll'];
  const webNames = new Set(webFiles.map(file => `wwwroot/${file.path}`));
  const excluded = [];
  const included = files.filter(file => {
    if (/\.pdb$/iu.test(file.path) || ['appsettings.Development.json', 'appsettings.Container.json', 'onnxruntime.lib', 'onnxruntime_providers_shared.lib',
      'SonnetDB.staticwebassets.endpoints.json', 'SonnetDB.staticwebassets.runtime.json'].includes(file.path)) { excluded.push(file); return false; }
    return true;
  });
  validatePaths(included);
  for (const file of included) requireThat(nativeNames.includes(file.path) || file.path === 'appsettings.json'
    || webNames.has(file.path) || ['wwwroot/llms.txt', 'wwwroot/llms-full.txt'].includes(file.path)
    || /^licenses\/image-codecs\/[\w.-]+$/u.test(file.path), `Unlisted publish file: ${file.path}`);
  for (const file of webFiles) {
    const actual = included.find(item => item.path === `wwwroot/${file.path}`);
    requireThat(actual?.sha256 === file.sha256 && actual?.size === file.size, `Stale/missing Web file: ${file.path}`);
  }
  requireThat(included.some(file => file.path === 'SonnetDB.exe') && included.some(file => file.path === 'wwwroot/index.html'), 'Required Server/Web missing');
  return { included, excluded };
}
function prepare(root, output, commit, version) {
  requireThat(process.env.GITHUB_ACTIONS === 'true' && process.env.GITHUB_REPOSITORY === 'IoTSharp/SonnetDB', 'Build requires the dedicated GitHub CI checkout');
  const profile = readJson(path.join(root, 'eng/workbench-preview/profile.json'));
  validateProfile(profile, version);
  requireThat(!fs.existsSync(output) && !path.resolve(output).startsWith(`${path.resolve(root)}${path.sep}`), 'Fresh output must be outside checkout');
  const source = captureSource(root, commit);
  fs.mkdirSync(output, { recursive: true });
  fs.mkdirSync(path.join(output, 'evidence'));
  writeJson(path.join(output, 'source.json'), source);
  writeJson(path.join(output, 'identity.json'), { ...profile, sourceCommit: commit, sourceTree: source.tree });
}
function stage(root, output) {
  const identity = readJson(path.join(output, 'identity.json'));
  const web = inventory(path.join(root, 'web/dist'));
  const html = fs.readFileSync(path.join(root, 'web/dist/index.html'), 'utf8');
  requireThat(/src="\/assets\//u.test(html) && !html.includes('/admin/assets/'), 'Root static resource path mismatch');
  const published = inventory(path.join(output, 'publish'));
  writeJson(path.join(output, 'evidence/native-publish-files.json'), { published, web });
  const selection = validatePublishInventory(published, web);
  const packaged = path.join(output, 'package'); fs.mkdirSync(packaged);
  for (const file of selection.included) {
    const target = path.join(packaged, file.path); fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(path.join(output, 'publish', file.path), target, fs.constants.COPYFILE_EXCL);
  }
  validateNative(fs.readFileSync(path.join(packaged, 'SonnetDB.exe')));
  validateNetwork(readJson(path.join(packaged, 'appsettings.json')));
  requireThat(git(root, ['status', '--porcelain', '--untracked-files=all']) === '', 'Tracked inputs changed during build');
  const original = readJson(path.join(output, 'source.json'));
  for (const file of original.inputs) requireThat(digest(fs.readFileSync(path.join(root, file.path))) === file.sha256, `Build changed source: ${file.path}`);
  for (const link of original.gitlinks) requireThat(git(path.join(root, link.path), ['rev-parse', 'HEAD']) === link.commit
    && git(path.join(root, link.path), ['status', '--porcelain', '--untracked-files=all']) === '', 'Build changed gitlink');
  for (const file of ['LICENSE', 'THIRD_PARTY_NOTICES.md']) fs.copyFileSync(path.join(root, file), path.join(packaged, file));
  for (const file of ['Start-Preview.ps1', 'START-HERE.md']) fs.copyFileSync(path.join(root, 'eng/workbench-preview', file), path.join(packaged, file));
  fs.copyFileSync(path.join(output, 'identity.json'), path.join(packaged, 'preview-1.json'));
  const webIdentity = readJson(path.join(packaged, 'wwwroot/workbench-preview.json'));
  requireThat(webIdentity.version === identity.version && webIdentity.sourceCommit === identity.sourceCommit && webIdentity.profile === identity.profile, 'Embedded Web identity mismatch');
  writeJson(path.join(output, 'evidence/publish-inventory.json'), selection);
}
function seal(output) {
  const identity = readJson(path.join(output, 'identity.json'));
  const files = inventory(path.join(output, 'package'));
  validatePaths(files);
  writeJson(path.join(output, 'package/package-inventory.json'), { schemaVersion: 1, version: identity.version, sourceCommit: identity.sourceCommit, files });
}
function manifest(output) {
  const identity = readJson(path.join(output, 'identity.json'));
  const source = readJson(path.join(output, 'source.json'));
  const files = inventory(path.join(output, 'package')); validatePaths(files);
  const base = `sonnetdb-workbench-web-${identity.version}`;
  const zipName = `${base}-win-x64.zip`, zipPath = path.join(output, 'delivery', zipName);
  const tools = readJson(path.join(output, 'evidence/tools.json'));
  requireThat(tools.server.productVersion === `${identity.version}+${source.commit}`, 'Actual Server informational version mismatch');
  for (const name of ['node-contracts', 'npm-ci', 'web-build', 'native-publish']) {
    const receipt = readJson(path.join(output, `evidence/${name}.result.json`));
    requireThat(receipt.status === 'PASS' && receipt.cleanupComplete === true, `Missing successful owned execution: ${name}`);
  }
  const nativeLog = fs.readFileSync(path.join(output, 'evidence/native-publish.stdout.log'), 'utf8')
    + fs.readFileSync(path.join(output, 'evidence/native-publish.stderr.log'), 'utf8');
  requireThat(!/\b(?:warning|error)\s+(?:IL\d{4}|NETSDK\d+)\b/iu.test(nativeLog), 'NativeAOT/trim warning');
  requireThat(/Generating native code/iu.test(nativeLog), 'NativeAOT publish proof missing');
  const result = { schemaVersion: 1, kind: 'workbench-preview-artifact-manifest', milestone: 'M47-P04', releaseStatus: 'NOT_READY',
    identity, source, components: { server: tools.server, web: { sourceVersion: readJson(path.join(output, 'web-package.json')).version,
      releaseVersion: identity.version, profile: 'preview-1', embeddedIdentity: 'wwwroot/workbench-preview.json', inventory: files.filter(file => file.path.startsWith('wwwroot/')) } },
    build: { nativeAot: 'PUBLISHED_WIN_X64_NO_CLR_DIRECTORY', aotWarningCount: 0, tools,
      ci: { repository: process.env.GITHUB_REPOSITORY, workflow: process.env.GITHUB_WORKFLOW, runId: process.env.GITHUB_RUN_ID,
        attempt: process.env.GITHUB_RUN_ATTEMPT, workflowRef: process.env.GITHUB_WORKFLOW_REF, workflowSha: process.env.GITHUB_SHA },
      dependencyInventory: readJson(path.join(output, 'package/dependency-licenses.json')) },
    files, zip: { path: zipName, size: fs.statSync(zipPath).size, sha256: digest(fs.readFileSync(zipPath)) },
    evidence: { sourceAndPackaging: 'PASS', actualNativeAotPublish: 'PASS', archiveInventory: 'PASS',
      installedUserJourneyP05: 'NOT_RUN', releaseNotesP06: 'NOT_RUN', releaseGatesP07: 'NOT_RUN', publicationP08: 'NOT_RUN',
      historicalFailuresPreserved: true }, publicAttachmentSetComplete: false, releaseNotesOwner: 'M47-P06' };
  const name = `${base}-manifest.json`; writeJson(path.join(output, 'delivery', name), result);
  const checksums = [zipName, name].map(file => `${digest(fs.readFileSync(path.join(output, 'delivery', file)))}  ${file}`).join('\n');
  fs.writeFileSync(path.join(output, 'delivery', `${base}-SHA256SUMS.txt`), `${checksums}\n`, { flag: 'wx' });
}
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'prepare') prepare(...args);
  else if (command === 'stage') stage(...args);
  else if (command === 'seal') seal(...args);
  else if (command === 'manifest') manifest(...args);
  else throw new Error('Unknown packaging command');
}
