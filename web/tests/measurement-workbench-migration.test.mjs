import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const source = readFileSync(new URL('../src/components/MeasurementWorkbench.vue', import.meta.url), 'utf8');

test('Measurement Workbench exposes the five-zone shell and database-local identity anchors', () => {
  assert.match(source, /data-testid="workbench-measurement"/);
  assert.match(source, /data-shell="five-zone"/);
  assert.match(source, /class="measurement-toolbar" data-zone="toolbar"/);
  assert.match(source, /data-zone="tabs"/);
  assert.match(source, /class="measurement-filterbar" data-zone="center"/);
  assert.match(source, /class="measurement-statusbar" data-zone="status"/);
  assert.match(source, /:data-state="measurementState"/);
  assert.match(source, /:data-database="targetDb"/);
  assert.match(source, /:data-resource-key="measurement\?\.name \?\? ''"/);
  assert.match(source, /data-zone="center" data-slot="measurement-data"/);
  assert.match(source, /data-zone="context" data-slot="measurement-inspector"/);
  assert.match(source, /active-database="targetDb"/);
  assert.match(source, /watch\(\(\) => `\$\{props\.targetDb\}\\u0000\$\{props\.measurement\?\.name \?\? ''\}`/);
});

test('Measurement Workbench defines and derives all six production states', () => {
  for (const state of ['normal', 'empty', 'error', 'permission', 'readonly', 'longContent']) {
    assert.match(source, new RegExp(`\\b${state}: \\{`), `${state} state contract`);
  }
  assert.match(source, /type MeasurementWorkbenchState = 'normal' \| 'empty' \| 'error' \| 'permission' \| 'readonly' \| 'longContent'/);
  assert.match(source, /if \(!props\.measurement\) return 'empty'/);
  assert.match(source, /if \(permissionDenied\.value\) return 'permission'/);
  assert.match(source, /const permissionDenied = computed\(\(\) => props\.permissionDenied \|\| permissionError\.value\)/);
  assert.match(source, /if \(readOnly\.value\) return 'readonly'/);
  assert.match(source, /return \/permission\|forbidden\|unauthori/);
  assert.match(source, /if \(pointResult\.value\?\.end && pointRows\.value\.length === 0\) return 'empty'/);
  assert.match(source, /pointResult\.value\?\.end\?\.truncated/);
  assert.match(source, /pointRows\.value\.length >= pointLimit\.value/);
});

test('Measurement query, refresh, export and monitor actions remain wired', () => {
  assert.match(source, /@click="loadPoints"/);
  assert.match(source, /exportVisiblePoints\('csv'\)/);
  assert.match(source, /exportVisiblePoints\('json'\)/);
  assert.match(source, /@click="refreshMonitor\(true\)"/);
  assert.match(source, /function loadPoints\(\)/);
  assert.match(source, /function exportVisiblePoints\(format: 'csv' \| 'json'\)/);
  assert.match(source, /function refreshMonitor\(allowOverlap = false\)/);
  assert.match(source, /:disabled="permissionDenied \|\| pointRows\.length === 0"/);
  assert.match(source, /monitorResult\.value = null;/);
  assert.match(source, /catch \(error\) \{\n    if \(requestId !== monitorRequestId\) return;/);
});

test('Measurement writes and deletes stay behind WriteApprovalPanel and readonly guards', () => {
  assert.match(source, /<WriteApprovalPanel/);
  assert.match(source, /v-if="approvalPlan && !readOnly && !permissionDenied"/);
  assert.match(source, /function stagePoint\(\): void \{\n  if \(!props\.measurement \|\| readOnly\.value \|\| permissionDenied\.value\) return/);
  assert.match(source, /function stageDelete\(row: PointGridRow\): void \{\n  if \(!props\.measurement \|\| readOnly\.value \|\| permissionDenied\.value\) return/);
  assert.match(source, /async function confirmPendingOperations\(\): Promise<void> \{\n  if \(!props\.measurement \|\| readOnly\.value \|\| permissionDenied\.value/);
  assert.match(source, /disabled: readOnly\.value \|\| permissionDenied\.value/);
  assert.match(source, /readOnly\?: boolean/);
  assert.match(source, /permissionDenied\?: boolean/);
  assert.match(source, /watch\(permissionDenied, \(denied\) => \{/);
  assert.match(source, /if \(!denied\) return;[\s\S]{0,180}monitorResult\.value = null;/);
});

test('Measurement preserves original database and measurement spelling in history and SQL', () => {
  assert.match(source, /target: `\$\{props\.targetDb\}\.\$\{props\.measurement\.name\}`/);
  assert.match(source, /database: props\.targetDb/);
  assert.match(source, /formatSqlIdentifier\(props\.measurement\.name\)/);
  assert.match(source, /targetDb \|\| 'database'/);
});
