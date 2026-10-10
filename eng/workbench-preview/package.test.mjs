import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { captureSource, digest, inventory, readJson, validateNative, validateNetwork, validatePaths, validateProfile, validatePublishInventory, verifyInventory } from './package.mjs';

const profile = readJson(fileURLToPath(new URL('./profile.json', import.meta.url)));
const item = (name, bytes = 'data') => ({ path: name, size: Buffer.byteLength(bytes), sha256: digest(Buffer.from(bytes)) });
function removeOwned(root) {
  assert.ok(path.resolve(root).startsWith(path.resolve(os.tmpdir()) + path.sep)
    && path.basename(root).startsWith('sonnetdb-p04-'), 'Task-owned temporary path required');
  fs.rmSync(root, { recursive: true });
}
test('R1 profile accepts only its new version and keeps seven model deferrals', () => {
  validateProfile(profile, profile.version);
  for (const mutation of [p => p.version = '4.5.0-preview.1.4', p => p.profile = 'full', p => p.rid = 'linux-x64',
    p => p.deferredModels.pop(), p => p.releaseStatus = 'READY', p => p.budgets.timeoutMilliseconds = 30001,
    p => p.actions.find(a => a.status.startsWith('DEFERRED')).allow.push('read')]) {
    const changed = structuredClone(profile); mutation(changed); assert.throws(() => validateProfile(changed, profile.version));
  }
});
for (const name of ['Studio.zip', 'old.vsix', 'SDK.nupkg', 'setup.msi', 'appsettings.Development.json', 'users.json',
  'wwwroot/a.js.map', 'data/test.bin', '.system/mq/message', 'logs/start.log', '../evil', 'C:/escape', 'a\\b']) {
  test(`package rejects ${name}`, () => assert.throws(() => validatePaths([item(name)])));
}
test('inventory rejects case aliases, wrong digest and missing manifest data', () => {
  assert.throws(() => validatePaths([item('LICENSE'), item('license')]));
  assert.throws(() => validatePaths([{ ...item('LICENSE'), sha256: 'bad' }]));
  assert.throws(() => verifyInventory([item('SonnetDB.exe')], [item('SonnetDB.exe'), item('preview-1.json')]));
  assert.throws(() => verifyInventory([item('wwwroot/index.html', 'new')], [item('wwwroot/index.html', 'old')]));
});
test('publish allowlist excludes only debug/development files and binds fresh Web bytes', () => {
  const web = [item('index.html'), item('assets/app.js')];
  const publish = [item('SonnetDB.exe'), item('appsettings.json'), ...web.map(f => ({ ...f, path: `wwwroot/${f.path}` }))];
  const selected = validatePublishInventory([...publish, item('SonnetDB.pdb'), item('appsettings.Development.json')], web);
  assert.equal(selected.included.length, publish.length); assert.equal(selected.excluded.length, 2);
  for (const addition of ['SonnetDB.Cli.exe', 'SonnetDB.dll', 'Studio.zip', 'wwwroot/stale.js']) assert.throws(() => validatePublishInventory([...publish, item(addition)], web));
  assert.throws(() => validatePublishInventory(publish.filter(f => f.path !== 'SonnetDB.exe'), web));
  assert.throws(() => validatePublishInventory(publish, [item('index.html', 'new')]));
});
test('PE inspection rejects a managed apphost, other RID and malformed executable', () => {
  const pe = Buffer.alloc(1024); pe.writeUInt16LE(0x5a4d); pe.writeUInt32LE(128, 0x3c);
  pe.writeUInt32LE(0x4550, 128); pe.writeUInt16LE(0x8664, 132); pe.writeUInt16LE(0x20b, 152);
  validateNative(pe);
  const clr = Buffer.from(pe); clr.writeUInt32LE(1, 128 + 24 + 112 + 14 * 8); assert.throws(() => validateNative(clr));
  const arm = Buffer.from(pe); arm.writeUInt16LE(0xaa64, 132); assert.throws(() => validateNative(arm));
  assert.throws(() => validateNative(Buffer.from('managed')));
});
test('network template preserves loopback and rejects every optional protocol', () => {
  const settings = { Kestrel: { Endpoints: { Http: { Url: 'http://127.0.0.1:5080' } } }, SonnetDBServer: {
    DataRoot: '../data', Mqtt: { Enabled: false, ExternalClient: { Enabled: false }, Sparkplug: { Enabled: false } },
    Coap: { Enabled: false, Dtls: { Enabled: false } }, LineProtocolUdp: { Enabled: false }, Modbus: { Enabled: false } } };
  validateNetwork(settings);
  for (const mutation of [s => s.Kestrel.Endpoints.Http.Url = 'http://0.0.0.0:5080', s => s.SonnetDBServer.DataRoot = './data',
    ...['Mqtt', 'Coap', 'LineProtocolUdp', 'Modbus'].map(p => s => s.SonnetDBServer[p].Enabled = true),
    s => s.SonnetDBServer.Mqtt.ExternalClient.Enabled = true]) {
    const changed = structuredClone(settings); mutation(changed); assert.throws(() => validateNetwork(changed));
  }
});
test('inventory hashes physical files and detects missing or tampered content', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sonnetdb-p04-inventory-'));
  try {
    fs.mkdirSync(path.join(root, 'wwwroot')); fs.writeFileSync(path.join(root, 'wwwroot/index.html'), 'original');
    const before = inventory(root); assert.equal(before.length, 1);
    fs.writeFileSync(path.join(root, 'wwwroot/index.html'), 'tampered'); assert.throws(() => verifyInventory(inventory(root), before));
    fs.unlinkSync(path.join(root, 'wwwroot/index.html')); assert.throws(() => verifyInventory(inventory(root), before));
  } finally { removeOwned(root); }
});
test('source preflight rejects wrong SHA, dirty checkout and ignored old output before build', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sonnetdb-p04-source-'));
  const git = (...args) => execFileSync('git', ['-C', root, ...args], { encoding: 'utf8', timeout: 10000 });
  try {
    git('init', '--quiet'); fs.writeFileSync(path.join(root, '.gitignore'), 'web/dist/\n');
    git('add', '.gitignore'); git('-c', 'user.name=P04 test', '-c', 'user.email=p04@example.invalid', 'commit', '--quiet', '-m', 'fixture');
    const sha = git('rev-parse', 'HEAD').trim(); assert.throws(() => captureSource(root, '0'.repeat(40)), /SHA/u);
    fs.writeFileSync(path.join(root, 'dirty'), 'foreign'); assert.throws(() => captureSource(root, sha), /Dirty/u);
    fs.unlinkSync(path.join(root, 'dirty')); fs.mkdirSync(path.join(root, 'web/dist'), { recursive: true });
    fs.writeFileSync(path.join(root, 'web/dist/index.html'), 'old'); assert.throws(() => captureSource(root, sha), /ignored/u);
  } finally { removeOwned(root); }
});
