import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const explorerSource = readFileSync(new URL('../src/utils/managementExplorer.ts', import.meta.url), 'utf8');
const routingSource = readFileSync(new URL('../src/composables/useSqlExplorerRouting.ts', import.meta.url), 'utf8');
const consoleSource = readFileSync(new URL('../src/views/SqlConsoleView.vue', import.meta.url), 'utf8');
const tabsSource = readFileSync(new URL('../src/components/StudioWorkspaceTabs.vue', import.meta.url), 'utf8');

test('Explorer items expose a canonical descriptor for every database resource model', () => {
  assert.match(explorerSource, /resource:\s*ResourceDescriptor/);
  for (const factory of [
    'createResourceDescriptor',
    'createMqResourceDescriptor',
    'createGraphResourceDescriptor',
  ]) {
    assert.match(explorerSource, new RegExp(`\\b${factory}\\b`));
  }
  assert.match(explorerSource, /model:\s*'index'[\s\S]{0,220}key:\s*index\.id/);
  assert.match(explorerSource, /model:\s*'backup'[\s\S]{0,240}key:\s*'backup-status'/);
});

test('MQ and Graph descriptors retain the confirmed boundary semantics', () => {
  assert.match(explorerSource, /resource:\s*createMqResourceDescriptor\(dbNode\.name, topic\.topic\)/);
  assert.match(explorerSource, /resource:\s*createGraphResourceDescriptor\(dbNode\.name, graph\.name, `graph:\$\{graph\.name\}`\)/);
  assert.match(consoleSource, /createMqResourceDescriptor\(database, topic\)/);
  assert.match(consoleSource, /createGraphResourceDescriptor\(/);
});

test('Object workspace tabs carry database, descriptor and legacy key together', () => {
  assert.match(tabsSource, /export interface StudioWorkspaceResourceIdentity/);
  assert.match(tabsSource, /database:\s*string;/);
  assert.match(tabsSource, /resource:\s*ResourceDescriptor;/);
  assert.match(tabsSource, /legacyKey:\s*string;/);
  assert.match(consoleSource, /resourceIdentity:\s*identity\.resource\s*\?/);
  assert.match(consoleSource, /database:\s*identity\.resource\.database/);
  assert.match(consoleSource, /resource:\s*identity\.resource/);
  assert.match(consoleSource, /legacyKey:\s*identity\.resource\.legacyKey/);
});

test('Explorer deep links keep the legacy tool/model/node query contract', () => {
  assert.match(routingSource, /name:\s*'sql'/);
  assert.match(routingSource, /tool:\s*'table'/);
  assert.match(routingSource, /model:\s*item\.model/);
  assert.match(routingSource, /node:\s*item\.name/);
  assert.match(routingSource, /activeExplorerKey\.value\s*=\s*item\.resource\.legacyKey/);
  assert.doesNotMatch(routingSource, /executeStatements|run\(/);
});

test('Initial or control-plane object states do not synthesize empty database identities', () => {
  assert.match(consoleSource, /if \(!targetDb\.value \|\| targetDb\.value === CONTROL_PLANE_KEY\) return null/);
  assert.match(consoleSource, /if \(!selectedMeasurement\.value\) return null/);
  assert.match(consoleSource, /if \(!selectedMqTopic\.value\) return null/);
});
