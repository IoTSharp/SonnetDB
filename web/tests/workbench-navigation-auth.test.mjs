import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import fs from 'node:fs';
import path from 'node:path';
import childProcess from 'node:child_process';
import { fileURLToPath, URL } from 'node:url';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule, createContext } from 'node:vm';
import test from 'node:test';
import axios from 'axios';
import * as vue from 'vue';
import { createPinia, defineStore, disposePinia } from 'pinia';
import { createRouter, createMemoryHistory } from 'vue-router';

const webRoot = new URL('../', import.meta.url);
const deepLink = '/admin/app/sql?database=Telemetry%3APump&tool=mq&model=mq&node=Mixed%3ATopic';

function synthetic(context, exports) {
  return new SyntheticModule(Object.keys(exports), function () {
    for (const [key, value] of Object.entries(exports)) this.setExport(key, value);
  }, { context });
}

function sourceModule(context, file, base = '/', override) {
  const source = override ?? readFileSync(new URL(file, webRoot), 'utf8');
  return new SourceTextModule(stripTypeScriptTypes(source, { mode: 'transform' }), {
    context,
    identifier: file,
    initializeImportMeta(meta) { meta.env = { BASE_URL: base }; meta.url = new URL(file, webRoot).href; },
    importModuleDynamically: async () => {
      const component = synthetic(context, { default: {} });
      await component.link(() => {}); await component.evaluate({ timeout: 1000 });
      return component;
    },
  });
}

async function navigationFixture({ base = '/', authenticated = false, setupError = false } = {}) {
  const context = createContext({ console });
  const helper = sourceModule(context, 'src/utils/workbenchNavigation.ts', base);
  await helper.link(() => {}); await helper.evaluate({ timeout: 1000 });
  const auth = { isAuthenticated: authenticated, isSuperuser: true, apply() {} };
  const setup = { needsSetup: false, ensureLoaded: async () => {
    if (setupError) throw new Error('setup unavailable');
    return { needsSetup: setup.needsSetup };
  } };
  const imports = {
    '@/preview/policy': synthetic(context, { previewEnabled: false, previewRouteAllowed: () => true }),
    'vue-router': synthetic(context, { createRouter, createWebHistory: createMemoryHistory }),
    '@/views/WelcomeView.vue': synthetic(context, { default: {} }),
    '@/stores/auth': synthetic(context, { useAuthStore: () => auth }),
    '@/stores/setup': synthetic(context, { useSetupStore: () => setup }),
    '@/utils/workbenchNavigation': helper,
  };
  const module = sourceModule(context, 'src/router/index.ts', base);
  await module.link((specifier) => {
    assert.ok(imports[specifier], specifier); return imports[specifier];
  });
  await module.evaluate({ timeout: 1000 });
  return { context, helper: helper.namespace, module: helper, router: module.namespace.default, auth, setup };
}

test('Explicit deployment base preserves spelling and rejects guessed or unsafe URL forms', { timeout: 5000 }, async () => {
  const f = await navigationFixture();
  assert.equal(f.helper.normalizeWebBasePath(undefined), '/');
  assert.equal(f.helper.normalizeWebBasePath('/Gateway/SonnetDB'), '/Gateway/SonnetDB/');
  assert.equal(f.helper.normalizeWebBasePath('/Gateway/SonnetDB/'), '/Gateway/SonnetDB/');
  for (const value of ['', 'https://host/prefix/', '//host/', 'prefix/', '/a/../b', '/a/./b', '/a?token=x', '/a#fragment', '/a%2fb', '/a\\b', '/a b', '/a\u0000b']) {
    assert.throws(() => f.helper.normalizeWebBasePath(value), /SONNETDB_WEB_BASE_PATH/);
  }
});

test('Authenticated login honors internal resource redirect and rejects unsafe, duplicate or anonymous targets', { timeout: 5000 }, async () => {
  const f = await navigationFixture({ authenticated: true, base: '/Gateway/SonnetDB/' });
  await f.router.push({ name: 'login', query: { redirect: deepLink } });
  assert.equal(f.router.currentRoute.value.name, 'sql');
  assert.equal(f.router.currentRoute.value.query.database, 'Telemetry:Pump');
  assert.equal(f.router.currentRoute.value.query.node, 'Mixed:Topic');
  assert.equal(f.router.resolve(deepLink).href, `/Gateway/SonnetDB${deepLink}`);
  for (const redirect of ['https://evil.example', '//evil.example/admin/app/sql', '/\\evil.example', '/admin/login', '/admin/setup', '/not-registered', '/admin/app/../login', '/admin/app/sql\n', ['/admin/app/sql', '/admin/app/dashboard'], null]) {
    assert.equal(f.helper.validatedLoginRedirect(redirect, f.router), null);
  }
  assert.equal(f.helper.validatedLoginRedirect('/admin/app/sql?sql=SELECT%201', f.router), '/admin/app/sql?sql=SELECT%201');
});

test('Anonymous deep links retain the original database and resource across login', { timeout: 5000 }, async () => {
  const f = await navigationFixture();
  await f.router.push(deepLink);
  assert.equal(f.router.currentRoute.value.name, 'login');
  assert.equal(f.router.currentRoute.value.query.redirect, deepLink);
  f.auth.isAuthenticated = true;
  await f.router.replace({ name: 'login', query: { redirect: deepLink }, force: true });
  assert.equal(f.router.currentRoute.value.name, 'sql');
  assert.equal(f.router.currentRoute.value.query.database, 'Telemetry:Pump');
  assert.equal(f.router.currentRoute.value.query.node, 'Mixed:Topic');
});

test('Setup status failure preserves an app target on the login redirect', { timeout: 5000 }, async () => {
  const f = await navigationFixture({ setupError: true });
  await f.router.push(deepLink);
  assert.equal(f.router.currentRoute.value.name, 'login');
  assert.equal(f.router.currentRoute.value.query.redirect, deepLink);
});

test('Actual Login submit applies the same route validation and keeps legacy SQL drafts', { timeout: 5000 }, async () => {
  for (const target of [deepLink, '/admin/app/sql?sql=SELECT%201', ['/admin/app/sql', '/admin/app/dashboard'], '//evil.example']) {
    const f = await navigationFixture();
    await f.router.push({ name: 'login', query: { redirect: target } });
    f.auth.login = async () => { f.auth.isAuthenticated = true; };
    const imports = {
      vue: synthetic(f.context, { ref: vue.ref, onMounted() {} }),
      'vue-router': synthetic(f.context, { useRouter: () => f.router, useRoute: () => f.router.currentRoute.value }),
      'naive-ui': synthetic(f.context, Object.fromEntries(['NButton', 'NForm', 'NFormItem', 'NInput', 'NText'].map((key) => [key, {}]))),
      '@/components/BrandLogo.vue': synthetic(f.context, { default: {} }),
      '@/stores/auth': synthetic(f.context, { useAuthStore: () => f.auth }),
      '@/stores/setup': synthetic(f.context, { useSetupStore: () => f.setup }),
      '@/utils/workbenchNavigation': f.module,
    };
    const script = readFileSync(new URL('src/views/LoginView.vue', webRoot), 'utf8').match(/<script setup lang="ts">([\s\S]*?)<\/script>/)[1];
    imports['@/preview/policy'] = synthetic(f.context, { previewEnabled: false });
    const component = sourceModule(f.context, 'LoginView', '/', `${script}\nexport { onSubmit, username, password };`);
    await component.link((specifier) => imports[specifier]); await component.evaluate({ timeout: 1000 });
    component.namespace.username.value = 'admin'; component.namespace.password.value = 'password';
    await component.namespace.onSubmit();
    const expected = typeof target === 'string' && target.startsWith('/admin/app/') ? f.router.resolve(target) : f.router.resolve('/admin/app/dashboard');
    assert.equal(f.router.currentRoute.value.path, expected.path);
    assert.deepEqual(f.router.currentRoute.value.query, expected.query);
  }
});

test('Initial setup/login API requests share the explicit deployment base', { timeout: 5000 }, async () => {
  for (const base of ['/', '/Gateway/SonnetDB/']) {
    const context = createContext({ console });
    const module = sourceModule(context, 'src/api/client.ts', base);
    const dependency = synthetic(context, { default: axios });
    await module.link(() => dependency); await module.evaluate({ timeout: 1000 });
    const api = module.namespace.createApiClient(() => null);
    assert.equal(api.getUri({ url: '/v1/auth/login' }), `${base}v1/auth/login`);
    assert.equal(api.getUri({ url: '/v1/setup/status' }), `${base}v1/setup/status`);
  }
});

test('Actual SSE subscriptions stay under deployment base, encode token/channels and remove handlers on close', { timeout: 5000 }, async () => {
  for (const base of ['/', '/Gateway/SonnetDB/']) {
    const subscriptions = [];
    class EventSourceFixture {
      static CLOSED = 2;
      listeners = new Map();
      closed = false;
      constructor(url) { this.url = url; subscriptions.push(this); }
      addEventListener(type, handler) { this.listeners.set(type, handler); }
      removeEventListener(type, handler) {
        assert.equal(this.listeners.get(type), handler);
        this.listeners.delete(type);
      }
      close() { this.closed = true; }
    }
    const context = createContext({ console, URLSearchParams, EventSource: EventSourceFixture });
    const module = sourceModule(context, 'src/api/events.ts', base);
    await module.link(() => {}); await module.evaluate({ timeout: 1000 });
    for (const channels of [undefined, ['metrics', 'db']]) {
      const token = 'token+/= :&?#';
      const close = module.namespace.subscribeServerEvents(token, { channels, onEvent() {} });
      const es = subscriptions.at(-1);
      const url = new URL(es.url, 'https://web.example');
      assert.equal(url.pathname, `${base}v1/events`);
      assert.equal(url.searchParams.get('access_token'), token);
      assert.equal(url.searchParams.get('stream'), channels ? channels.join(',') : null);
      assert.deepEqual([...url.searchParams.keys()], channels ? ['access_token', 'stream'] : ['access_token']);
      assert.equal(es.listeners.size, 4);
      assert.equal(es.closed, false);
      close();
      assert.equal(es.listeners.size, 0);
      assert.equal(es.closed, true);
    }
  }
});

test('Actual Vite config applies the explicit asset base and strips only its configured development proxy prefix', { timeout: 5000 }, async () => {
  for (const base of ['/', '/Gateway/SonnetDB/', '/Gateway.v1/SonnetDB+Admin/']) {
    const context = createContext({ console });
    const helper = sourceModule(context, 'src/utils/workbenchNavigation.ts');
    await helper.link(() => {}); await helper.evaluate({ timeout: 1000 });
    const imports = {
      'node:url': synthetic(context, { fileURLToPath, URL }),
      'node:fs': synthetic(context, { default: fs }),
      'node:path': synthetic(context, { default: path }),
      'node:child_process': synthetic(context, { default: childProcess }),
      'node:process': synthetic(context, { env: { SONNETDB_WEB_BASE_PATH: base, SONNETDB_PROXY_TARGET: 'http://localhost:5080' } }),
      vite: synthetic(context, { defineConfig: (config) => config }),
      '@vitejs/plugin-vue': synthetic(context, { default: () => ({ name: 'vue-test' }) }),
      './src/utils/workbenchNavigation': helper,
    };
    const module = sourceModule(context, 'vite.config.ts');
    await module.link((specifier) => imports[specifier]); await module.evaluate({ timeout: 1000 });
    const config = module.namespace.default({ mode: 'e2e' });
    assert.equal(config.base, base);
    const [pattern, proxy] = Object.entries(config.server.proxy)[0];
    assert.match(`${base}v1/auth/login`, new RegExp(pattern));
    assert.equal(proxy.rewrite(`${base}v1/auth/login?preserve=query`), '/v1/auth/login?preserve=query');
    if (base !== '/') assert.doesNotMatch('/v1/auth/login', new RegExp(pattern));
  }
});

test('Actual connection store uses configured local path, migrates only old built-in root, and health stays under base', { timeout: 5000 }, async (t) => {
  for (const stored of [null, { profiles: [
    { id: 'managed-local', kind: 'managed-local', baseUrl: '/' },
    { id: 'remote-a', kind: 'remote', baseUrl: 'https://remote.example/API' },
  ], activeProfileId: 'managed-local', activeDatabase: 'Telemetry:Pump' }, { profiles: [
    { id: 'managed-local', kind: 'remote', baseUrl: '/' },
  ], activeProfileId: 'managed-local', activeDatabase: '' }]) {
    const requests = [];
    const context = createContext({
      console, Date, JSON, performance, AbortController, DOMException,
      window: { setTimeout, clearTimeout, location: { origin: 'https://web.example' } },
      localStorage: { getItem: () => stored ? JSON.stringify(stored) : null, setItem() {} },
      fetch: async (url) => { requests.push(url); return { ok: true, json: async () => ({ status: 'ok' }) }; },
    });
    const imports = {
      vue: synthetic(context, { computed: vue.computed, ref: vue.ref, shallowRef: vue.shallowRef, watch: vue.watch }),
      pinia: synthetic(context, { defineStore }),
      '@/api/studioNativeBridge': synthetic(context, { getStudioNativeBridge: async () => null, readStudioConnectionIdentity: () => null, studioManagedServerPresentation: () => ({}) }),
      '@/preview/policy': synthetic(context, { previewEnabled: false }),
    };
    const module = sourceModule(context, 'src/stores/connections.ts', '/Gateway/SonnetDB/');
    await module.link((specifier) => imports[specifier]); await module.evaluate({ timeout: 1000 });
    const pinia = createPinia(); t.after(() => disposePinia(pinia));
    const store = module.namespace.useConnectionsStore(pinia);
    const explicitRemote = stored?.profiles[0].kind === 'remote';
    assert.equal(store.activeBaseUrl, explicitRemote ? '/' : '/Gateway/SonnetDB');
    assert.equal(store.activeDisplayUrl, explicitRemote ? 'https://web.example' : 'https://web.example/Gateway/SonnetDB');
    if (stored?.profiles.length === 2) assert.equal(store.profiles.find((p) => p.id === 'remote-a').baseUrl, 'https://remote.example/API');
    await store.checkProfileHealth('managed-local');
    assert.deepEqual(requests, [explicitRemote ? '/healthz' : '/Gateway/SonnetDB/healthz']);
  }
});
