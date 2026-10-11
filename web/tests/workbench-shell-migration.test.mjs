import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const shellSource = readFileSync(new URL('../src/views/AppShell.vue', import.meta.url), 'utf8');
const routerSource = readFileSync(new URL('../src/router/index.ts', import.meta.url), 'utf8');

function moduleNavigationBlock() {
  const start = shellSource.indexOf('const moduleNavigation: NavigationItem[] = [');
  assert.notEqual(start, -1, 'M47 module navigation should be declared');
  const end = shellSource.indexOf('];', start);
  assert.ok(end > start, 'M47 module navigation should have a bounded declaration');
  return shellSource.slice(start, end);
}

test('primary rail exposes the confirmed seven modules exactly once', () => {
  const block = moduleNavigationBlock();
  const labels = ['概览', '工作台', '观测', '数据流', 'AI 与 MCP', '治理', '设置'];
  for (const label of labels) {
    assert.match(block, new RegExp(`label: '${label}'`));
  }
  assert.equal((block.match(/label:/g) ?? []).length, labels.length);
  for (const legacyLabel of ['查询', '数据', 'Studio', '事件', '监控', 'RAG']) {
    assert.doesNotMatch(block, new RegExp(`label: '${legacyLabel}'`));
  }
  assert.match(shellSource, /const primaryNavigation = computed\(\(\) => moduleNavigation\.filter\(\(item\) => item\.key !== 'settings'/);
  assert.match(shellSource, /!item\.requiresAdmin \|\| auth\.isSuperuser/);
  const secondaryStart = shellSource.indexOf('const secondaryNavigation = computed(() => [');
  const secondaryEnd = shellSource.indexOf(']);', secondaryStart);
  assert.ok(secondaryEnd > secondaryStart);
  const secondary = shellSource.slice(secondaryStart, secondaryEnd);
  assert.match(secondary, /moduleNavigation\.filter\(\(item\) => item\.key === 'settings'\)/);
  assert.doesNotMatch(secondary, /key: auth\.isSuperuser \? 'ai-settings' : 'dashboard'/);
  assert.match(shellSource, /function isSecondaryActive\(key: string\)/);
  assert.match(shellSource, /const routeToModule: Record<string, string> = \{/);
  for (const [route, module] of [
    ['dashboard', 'overview'],
    ['sql', 'workbench'],
    ['events', 'observe'],
    ['monitoring', 'observe'],
    ['modbus', 'flows'],
    ['rag', 'ai'],
    ['ai-settings', 'ai'],
    ['users', 'govern'],
    ['grants', 'govern'],
    ['tokens', 'govern'],
    ['about', 'settings'],
  ]) {
    const key = route.includes('-') ? `'${route}'` : route;
    assert.match(shellSource, new RegExp(`${key}: '${module}'`));
  }
  assert.match(shellSource, /key: 'flows',[\s\S]{0,120}requiresAdmin: true/);
  assert.match(shellSource, /key === 'settings'\) return activeModuleKey\.value === 'settings' && activeKey\.value !== 'about'/);
  assert.match(shellSource, /activeModuleKey === item\.key/);
});

test('module entries map to implemented pages without claiming planned pages', () => {
  const block = moduleNavigationBlock();
  for (const [key, route] of [
    ['overview', 'dashboard'],
    ['workbench', 'sql'],
    ['observe', 'monitoring'],
    ['flows', 'modbus'],
    ['ai', 'rag'],
    ['govern', 'users'],
    ['settings', 'about'],
  ]) {
    assert.match(block, new RegExp(`key: '${key}', routeName: '${route}'`));
  }
  assert.match(block, /key: 'flows',[\s\S]{0,120}requiresAdmin: true/);
  assert.match(block, /key: 'govern',[\s\S]{0,100}requiresAdmin: true/);
  assert.match(routerSource, /Data-flow pages are still represented by the existing Modbus view/);
  assert.match(routerSource, /path: 'flows',[\s\S]{0,100}meta: \{ admin: true \}/);
});

test('module aliases and legacy paths retain stable routing contracts', () => {
  for (const [path, name] of [
    ['overview', 'dashboard'],
    ['workbench', 'sql'],
    ['observe', 'monitoring'],
    ['flows', 'modbus'],
    ['ai', 'rag'],
    ['govern', 'users'],
    ['settings', 'about'],
  ]) {
    assert.match(routerSource, new RegExp(`path: '${path}', name: '${path}', redirect: \\{ name: '${name}' \\}`));
  }
  assert.match(routerSource, /path: 'studio', redirect: \{ name: 'sql' \}/);
  assert.match(routerSource, /path: 'databases',\s*(?:name: 'databases',\s*)?redirect: \{ name: 'sql' \}/);
  assert.match(routerSource, /path: 'trajectory-map',[\s\S]{0,180}query: \{ tool: 'trajectory' \}/);
  assert.match(routerSource, /if \(to\.meta\.app && !auth\.isAuthenticated\)/);
  assert.match(routerSource, /if \(to\.meta\.admin && !auth\.isSuperuser\)/);
  assert.match(shellSource, /\.\.\.\(auth\.isSuperuser \? adminNavigation : \[\]\)/);
});
