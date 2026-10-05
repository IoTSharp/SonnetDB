import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { resolve } from 'node:path';
import { SourceTextModule, SyntheticModule, createContext } from 'node:vm';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { computed, ref, shallowRef, watch, nextTick, h, effectScope } from 'vue';
import { createPinia, defineStore, disposePinia } from 'pinia';

const webRoot = fileURLToPath(new URL('../', import.meta.url));

async function loadBridgeContract() {
  const bridgePath = resolve(webRoot, 'src/api/studioNativeBridge.ts');
  const bridge = new SourceTextModule(stripTypeScriptTypes(readFileSync(bridgePath, 'utf8'), { mode: 'transform' }), {
    identifier: bridgePath,
  });
  const protocol = new SyntheticModule(['BrowserDirectContractVersion'], function () {
    this.setExport('BrowserDirectContractVersion', 'm27-browser-direct-v1');
  }, { identifier: 'publicRuntimeProtocol' });
  await bridge.link((specifier) => {
    assert.equal(specifier, '../copilot/publicRuntimeProtocol');
    return protocol;
  });
  await protocol.evaluate();
  await bridge.evaluate();
  return bridge.namespace;
}

function status(overrides = {}) {
  const value = {
    isRunning: true,
    startedByStudio: true,
    healthy: true,
    processId: 123,
    url: 'http://127.0.0.1:5080/api',
    dataRoot: 'C:/SonnetDB/data',
    error: null,
    processOwner: 'studio',
    lifecycleState: 'running',
    canStop: true,
  };
  return { ...value, ...overrides };
}

test('Studio identity keeps profile, endpoint and database spelling distinct', async () => {
  const { readStudioConnectionIdentity } = await loadBridgeContract();
  const profile = { id: 'prod-a', baseUrl: 'https://db.example/admin' };
  assert.deepEqual(readStudioConnectionIdentity({
    host: 'studio-desktop', profileId: 'prod-a', baseUrl: profile.baseUrl, database: 'Telemetry:Pump',
  }, profile, 'Telemetry:Pump'), {
    host: 'studio-desktop', profileId: 'prod-a', baseUrl: profile.baseUrl, database: 'Telemetry:Pump',
  });
  assert.equal(readStudioConnectionIdentity({
    host: 'studio-desktop', profileId: 'prod-b', baseUrl: profile.baseUrl, database: 'Telemetry:Pump',
  }, profile, 'Telemetry:Pump'), null);
  assert.equal(readStudioConnectionIdentity({
    host: 'studio-desktop', profileId: 'prod-a', baseUrl: profile.baseUrl, database: 'telemetry:pump',
  }, profile, 'Telemetry:Pump'), null);
});

test('Lifecycle presentation fails closed for missing or contradictory host contracts', async () => {
  const { studioManagedServerPresentation } = await loadBridgeContract();
  assert.deepEqual(studioManagedServerPresentation(status()), {
    confirmed: true, label: 'Studio 运行中', tagType: 'success', canStart: false, canStop: true,
  });
  assert.equal(studioManagedServerPresentation(status({ processId: null, canStop: false })).canStop, false);
  assert.equal(studioManagedServerPresentation(status({ processOwner: 'external', canStop: true })).confirmed, false);
  assert.equal(studioManagedServerPresentation({ isRunning: false, startedByStudio: false, healthy: false,
    processId: null, url: 'http://127.0.0.1:5080', dataRoot: 'data', error: null }).confirmed, false);
});

test('Lifecycle matrix keeps external, stopped and failed states actionable only when safe', async () => {
  const { studioManagedServerPresentation } = await loadBridgeContract();
  const external = studioManagedServerPresentation(status({ startedByStudio: false, processOwner: 'external', lifecycleState: 'external-running', canStop: false }));
  assert.deepEqual(external, { confirmed: true, label: '外部实例运行中', tagType: 'success', canStart: false, canStop: false });
  const stopped = studioManagedServerPresentation(status({ isRunning: false, healthy: false, processId: null, processOwner: 'none', lifecycleState: 'stopped', canStop: false }));
  assert.deepEqual(stopped, { confirmed: true, label: 'Studio 已停止', tagType: 'default', canStart: true, canStop: false });
  const failed = studioManagedServerPresentation(status({ isRunning: false, healthy: false, processId: null, error: 'launch failed', processOwner: 'none', lifecycleState: 'failed', canStop: false }));
  assert.deepEqual(failed, { confirmed: true, label: 'Studio 启动失败', tagType: 'error', canStart: true, canStop: false });
});

function deferred() {
  let resolvePromise;
  let reject;
  const promise = new Promise((resolve, rejectPromise) => { resolvePromise = resolve; reject = rejectPromise; });
  return { promise, resolve: resolvePromise, reject };
}

function profile(id = 'managed-local', database = 'Telemetry:Pump') {
  const baseUrl = id === 'managed-local' ? 'http://127.0.0.1:5080/api' : 'https://db.example/API/Admin';
  return { id, name: id, kind: id === 'managed-local' ? 'managed-local' : 'remote', baseUrl,
    defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1,
    identity: { host: 'studio-desktop', profileId: id, baseUrl, database } };
}

function snapshot(profiles, activeProfileId = profiles[0].id, activeDatabase = profiles[0].defaultDatabase) {
  const selected = profiles.find((item) => item.id === activeProfileId);
  return { profiles: profiles.map((item) => ({ ...item,
    identity: { host: 'studio-desktop', profileId: item.id, baseUrl: item.baseUrl, database: item.defaultDatabase } })),
    activeProfileId, activeDatabase,
    activeIdentity: { host: 'studio-desktop', profileId: selected.id, baseUrl: selected.baseUrl, database: activeDatabase } };
}

async function fixture(overrides = {}, bootstrap) {
  const contract = await loadBridgeContract();
  const initial = snapshot([profile(), profile('Remote-A', 'OriginalCase'), profile('Remote-B', 'OtherCase')]);
  const calls = { start: 0, stop: 0, open: 0, save: [] };
  const bridge = {
    refreshManifest: async () => ({ managedServer: status(), managedServerUrl: initial.profiles[0].baseUrl }),
    loadConnections: async () => initial,
    getServerStatus: async () => status(),
    startServer: async () => { calls.start++; return status(); },
    stopServer: async () => { calls.stop++; return status({ isRunning: false, healthy: false, processOwner: 'none', lifecycleState: 'stopped', canStop: false }); },
    openEmbeddedDatabase: async () => { calls.open++; return status(); },
    saveConnections: async (state) => { calls.save.push(state); return snapshot(state.profiles, state.activeProfileId, state.activeDatabase); },
    ...overrides,
  };
  const storage = new Map();
  const context = createContext({
    localStorage: { getItem: (key) => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    window: { setTimeout, clearTimeout, location: { origin: 'https://web.example' } },
    performance, AbortController, DOMException, Date, JSON, URL, console,
  });
  const vue = new SyntheticModule(['computed', 'ref', 'shallowRef', 'watch', 'h'], function () {
    this.setExport('computed', computed); this.setExport('ref', ref);
    this.setExport('shallowRef', shallowRef); this.setExport('watch', watch);
    this.setExport('h', h);
  }, { context });
  const piniaModule = new SyntheticModule(['defineStore'], function () { this.setExport('defineStore', defineStore); }, { context });
  const native = new SyntheticModule(['getStudioNativeBridge', 'readStudioConnectionIdentity', 'studioManagedServerPresentation'], function () {
    this.setExport('getStudioNativeBridge', () => bootstrap ? bootstrap.promise : Promise.resolve(bridge));
    this.setExport('readStudioConnectionIdentity', contract.readStudioConnectionIdentity);
    this.setExport('studioManagedServerPresentation', contract.studioManagedServerPresentation);
  }, { context });
  const source = readFileSync(resolve(webRoot, 'src/stores/connections.ts'), 'utf8');
  const module = new SourceTextModule(stripTypeScriptTypes(source, { mode: 'transform' }), { context });
  await module.link((specifier) => {
    if (specifier === 'vue') return vue;
    if (specifier === 'pinia') return piniaModule;
    assert.equal(specifier, '@/api/studioNativeBridge'); return native;
  });
  await module.evaluate({ timeout: 5000 });
  const pinia = createPinia();
  const store = module.namespace.useConnectionsStore(pinia);
  return { store, bridge, calls, initial, context, vue, native, close: () => disposePinia(pinia) };
}

async function chromeFixture(f, database) {
  const router = new SyntheticModule(['useRoute', 'useRouter'], function () {
    this.setExport('useRoute', () => ({ query: {} }));
    this.setExport('useRouter', () => ({ replace: async () => {} }));
  }, { context: f.context });
  const icons = new SyntheticModule(['Circle', 'CircleCheck', 'CircleX', 'LoaderCircle'], function () {
    this.setExport('Circle', 'circle'); this.setExport('CircleCheck', 'check');
    this.setExport('CircleX', 'error'); this.setExport('LoaderCircle', 'loader');
  }, { context: f.context });
  const consoleStore = new SyntheticModule(['CONTROL_PLANE_KEY'], function () {
    this.setExport('CONTROL_PLANE_KEY', '__control_plane__');
  }, { context: f.context });
  const source = readFileSync(resolve(webRoot, 'src/composables/useSqlWorkbenchChrome.ts'), 'utf8');
  const module = new SourceTextModule(stripTypeScriptTypes(source, { mode: 'transform' }), { context: f.context });
  await module.link((specifier) => {
    if (specifier === 'vue') return f.vue;
    if (specifier === 'vue-router') return router;
    if (specifier === 'lucide-vue-next') return icons;
    if (specifier === '@/stores/sqlConsole') return consoleStore;
    assert.equal(specifier, '@/api/studioNativeBridge'); return f.native;
  });
  await module.evaluate({ timeout: 5000 });
  const targetDb = ref(database);
  const scope = effectScope();
  const auth = { isSuperuser: true, setApiBaseUrl() {} };
  const chrome = scope.run(() => module.namespace.useSqlWorkbenchChrome({ auth, connections: f.store, targetDb }));
  return { chrome, targetDb, close: () => scope.stop() };
}

test('Production store consumes host identities, keeps same-endpoint profiles distinct and confirms new saves without a loop', { timeout: 5000 }, async (t) => {
  const f = await fixture(); t.after(f.close);
  await f.store.connectStudioBridge();
  assert.equal(f.store.studioActiveIdentity.database, 'Telemetry:Pump');
  assert.equal(f.calls.save.length, 0);
  f.store.setActiveProfile('Remote-A');
  f.store.setActiveDatabase('Mixed:Database');
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.ok(f.store.studioActiveIdentity, JSON.stringify({ saves: f.calls.save, active: f.store.activeProfile, database: f.store.activeDatabase }));
  assert.equal(f.store.studioActiveIdentity.profileId, 'Remote-A');
  assert.equal(f.store.studioActiveIdentity.database, 'Mixed:Database');
  assert.equal(f.store.studioActiveIdentity.baseUrl, 'https://db.example/API/Admin');
  const savedCount = f.calls.save.length;
  await nextTick(); await nextTick();
  assert.equal(f.calls.save.length, savedCount);
  f.store.setActiveProfile('Remote-B'); await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(f.store.studioActiveIdentity.profileId, 'Remote-B');
});

test('Production store denies direct lifecycle methods for unknown and external contracts and never adopts their URLs', { timeout: 5000 }, async (t) => {
  const f = await fixture(); t.after(f.close); await f.store.connectStudioBridge();
  f.store.studioManagedServerStatus = status({ processOwner: 'external' });
  await f.store.startStudioManagedServer(); await f.store.stopStudioManagedServer(); await f.store.openStudioEmbeddedDatabase('path');
  assert.equal(f.calls.start + f.calls.stop + f.calls.open, 0);
  f.bridge.getServerStatus = async () => status({ url: 'https://db.example/?token=bad' });
  await f.store.refreshStudioServerStatus();
  assert.equal(f.store.activeBaseUrl, 'http://127.0.0.1:5080/api');
  assert.equal(f.store.studioManagedServerPresentation.confirmed, false);
  f.bridge.getServerStatus = async () => { throw new Error('offline'); };
  await f.store.refreshStudioServerStatus();
  assert.equal(f.store.studioManagedServerStatus, null);
});

test('Production store rejects late status after profile ABA and same-ID endpoint changes', { timeout: 5000 }, async (t) => {
  const reply = deferred(); const f = await fixture({ getServerStatus: () => reply.promise }); t.after(f.close);
  await f.store.connectStudioBridge();
  const running = f.store.refreshStudioServerStatus();
  f.store.setActiveProfile('Remote-A'); f.store.setActiveProfile('managed-local');
  reply.resolve(status({ url: 'http://127.0.0.1:6000/late' }));
  assert.equal(await running, null);
  assert.equal(f.store.activeBaseUrl, 'http://127.0.0.1:5080/api');
  const endpointReply = deferred(); f.bridge.getServerStatus = () => endpointReply.promise;
  const endpointRunning = f.store.refreshStudioServerStatus();
  f.store.profiles[0].baseUrl = 'http://127.0.0.1:7000/new';
  endpointReply.resolve(status({ url: 'http://127.0.0.1:6000/late' }));
  assert.equal(await endpointRunning, null);
  assert.equal(f.store.activeBaseUrl, 'http://127.0.0.1:7000/new');
});

test('Production store does not apply an old connection library after selection changes during bootstrap', { timeout: 5000 }, async (t) => {
  const bootstrap = deferred(); const f = await fixture({}, bootstrap); t.after(f.close);
  const connect = f.store.connectStudioBridge();
  const local = f.store.profiles[0].id;
  f.store.setActiveDatabase('Current:Case');
  bootstrap.resolve(f.bridge);
  await connect;
  assert.equal(f.store.activeProfileId, local);
  assert.equal(f.store.activeDatabase, 'Current:Case');
  assert.equal(f.store.activeBaseUrl, '/');
});

test('Production store ignores delayed save identities after the current database changes', { timeout: 5000 }, async (t) => {
  const late = deferred(); let first;
  const f = await fixture({ saveConnections: async (state) => {
    if (!first) { first = state; return late.promise; }
    return snapshot(state.profiles, state.activeProfileId, state.activeDatabase);
  } }); t.after(f.close); await f.store.connectStudioBridge();
  f.store.setActiveDatabase('First:Case');
  await new Promise((resolve) => setTimeout(resolve, 0));
  f.store.setActiveDatabase('Second:Case');
  late.resolve(snapshot(first.profiles, first.activeProfileId, first.activeDatabase));
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(f.store.activeDatabase, 'Second:Case');
  assert.equal(f.store.studioActiveIdentity.database, 'Second:Case');
});

test('Same database selection and Health do not discard pending host identity confirmation', { timeout: 5000 }, async (t) => {
  const late = deferred(); let pending;
  const f = await fixture({ saveConnections: (state) => { pending = state; return late.promise; } });
  t.after(f.close); await f.store.connectStudioBridge();
  f.store.setActiveDatabase('Confirmed:Case');
  await new Promise((resolve) => setTimeout(resolve, 0));
  f.store.setActiveDatabase('Confirmed:Case');
  await f.store.refreshStudioServerStatus();
  late.resolve(snapshot(pending.profiles, pending.activeProfileId, pending.activeDatabase));
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(f.store.studioActiveIdentity.database, 'Confirmed:Case');
});

test('Embedded directory accepts a different workspace database and rejects workspace ABA before dispatch', { timeout: 5000 }, async (t) => {
  const stopped = status({ isRunning: false, healthy: false, processOwner: 'none', lifecycleState: 'stopped', canStop: false });
  const dialog = deferred();
  const f = await fixture({
    refreshManifest: async () => ({ managedServer: stopped, managedServerUrl: stopped.url }),
    selectDirectory: () => dialog.promise,
    openEmbeddedDatabase: async () => { f.calls.open++; return status({ mountedDatabaseName: 'Opened:Case' }); },
  }); t.after(f.close); await f.store.connectStudioBridge();
  const c = await chromeFixture(f, 'Workspace:Case'); t.after(c.close);
  const opened = c.chrome.openNativeEmbeddedDatabase();
  dialog.resolve({ canceled: false, path: 'C:/SonnetDB/Embedded', error: null });
  assert.equal(await opened, true);
  assert.equal(f.calls.open, 1);
  assert.equal(c.targetDb.value, 'Opened:Case');

  f.store.studioManagedServerStatus = stopped;
  const second = deferred(); f.bridge.selectDirectory = () => second.promise;
  const refused = c.chrome.openNativeEmbeddedDatabase();
  c.targetDb.value = 'Workspace:B'; c.targetDb.value = 'Opened:Case';
  second.resolve({ canceled: false, path: 'C:/SonnetDB/Stale', error: null });
  assert.equal(await refused, false);
  assert.equal(f.calls.open, 1);
});
