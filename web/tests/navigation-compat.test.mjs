import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const routerSource = readFileSync(new URL('../src/router/index.ts', import.meta.url), 'utf8');
const shellSource = readFileSync(new URL('../src/views/AppShell.vue', import.meta.url), 'utf8');

function routeSnippet(name) {
  const marker = `name: '${name}'`;
  const start = routerSource.indexOf(marker);
  assert.notEqual(start, -1, `router should keep route ${name}`);
  return routerSource.slice(start, start + 220);
}

test('legacy app entry redirects remain stable', () => {
  assert.match(routerSource, /path:\s*'\/admin',\s*redirect:\s*\{\s*name:\s*'dashboard'\s*\}/);
  assert.match(routerSource, /path:\s*'\/admin\/app',[\s\S]{0,120}redirect:\s*\{\s*name:\s*'dashboard'\s*\}/);
  assert.match(routerSource, /path:\s*'studio',\s*redirect:\s*\{\s*name:\s*'sql'\s*\}/);
  assert.match(routerSource, /path:\s*'databases',\s*(?:name:\s*'databases',\s*)?redirect:\s*\{\s*name:\s*'sql'\s*\}/);
  assert.match(routerSource, /path:\s*'trajectory-map',\s*name:\s*'trajectory-map',\s*redirect:\s*\{\s*name:\s*'sql',\s*query:\s*\{\s*tool:\s*'trajectory'\s*\}\s*\}/);
});

test('current route names and administrator metadata remain discoverable', () => {
  for (const name of ['dashboard', 'sql', 'events', 'monitoring', 'rag', 'about']) {
    assert.match(routerSource, new RegExp(`name:\\s*'${name}'`));
  }
  for (const name of ['modbus', 'users', 'grants', 'tokens', 'ai-settings', 'copilot-test']) {
    assert.match(routeSnippet(name), /meta:\s*\{\s*admin:\s*true\s*\}/);
  }
});

test('AppShell preserves the existing base and administrator navigation surfaces', () => {
  const baseStart = shellSource.indexOf('const baseNavigation: NavigationItem[] = [');
  const baseEnd = shellSource.indexOf('];', baseStart);
  assert.notEqual(baseStart, -1);
  assert.ok(baseEnd > baseStart);
  const baseNavigation = shellSource.slice(baseStart, baseEnd);
  for (const label of ['概览', '查询', '数据', 'Studio', '事件', '监控', 'RAG']) {
    assert.match(baseNavigation, new RegExp(`label: '${label}'`));
  }
  assert.equal((baseNavigation.match(/label:/g) ?? []).length, 7);

  const adminStart = shellSource.indexOf('const adminNavigation: NavigationItem[] = [');
  const adminEnd = shellSource.indexOf('];', adminStart);
  assert.notEqual(adminStart, -1);
  assert.ok(adminEnd > adminStart);
  const adminNavigation = shellSource.slice(adminStart, adminEnd);
  for (const label of ['Modbus', '用户', '权限', 'Token', 'Copilot']) {
    assert.match(adminNavigation, new RegExp(`label: '${label}'`));
  }
  assert.equal((adminNavigation.match(/label:/g) ?? []).length, 5);
  assert.match(shellSource, /\.\.\.\(auth\.isSuperuser \? adminNavigation : \[\]\)/);
  assert.match(shellSource, /\{ label: '设置', key: auth\.isSuperuser \? 'ai-settings' : 'dashboard'/);
  assert.match(shellSource, /\{ label: '关于', key: 'about'/);
});

test('router guards retain setup, authentication, redirect, and admin boundaries', () => {
  assert.match(routerSource, /await setup\.ensureLoaded\(\)/);
  assert.match(routerSource, /if \(setup\.needsSetup\)/);
  assert.match(routerSource, /auth\.apply\(null\)/);
  assert.match(routerSource, /if \(to\.name === 'setup'\)/);
  assert.match(routerSource, /return \{ name: 'setup' \}/);
  assert.match(routerSource, /if \(to\.name === 'setup'\)[\s\S]{0,180}return auth\.isAuthenticated \? \{ name: 'dashboard' \} : \{ name: 'login' \}/);
  assert.match(routerSource, /if \(to\.name === 'login' && auth\.isAuthenticated\)/);
  assert.match(routerSource, /if \(to\.meta\.app && !auth\.isAuthenticated\)/);
  assert.match(routerSource, /query: \{ redirect: to\.fullPath \}/);
  assert.match(routerSource, /if \(to\.meta\.admin && !auth\.isSuperuser\)/);
  assert.match(routerSource, /return \{ name: 'dashboard' \}/);
});

test('trajectory legacy query remains mapped to the shared SQL workspace', () => {
  assert.match(routerSource, /path:\s*'trajectory-map',[\s\S]{0,180}query:\s*\{\s*tool:\s*'trajectory'\s*\}/);
  assert.match(shellSource, /trajectory:\s*'轨迹分析'/);
  assert.match(shellSource, /toolLabels\[String\(route\.query\.tool \?\? 'sql'\)\]/);
});
