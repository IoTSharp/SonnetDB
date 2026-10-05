import * as assert from 'node:assert/strict';
import * as vscode from 'vscode';
import type { SonnetDbConnectionProfile } from '../../core/types';
import type { TreeNode } from '../../tree/sonnetdbTreeDataProvider';

const RequiredCommands = [
  'sonnetdb.addConnection',
  'sonnetdb.runQuery',
  'sonnetdb.previewObjectBucket',
  'sonnetdb.showRuntimeMonitor',
  'sonnetdb.openWorkbench',
];

/**
 * 在真实 VS Code Extension Host 中验证扩展激活、命令和 SQL 语言功能。
 */
export async function run(): Promise<void> {
  const extension = vscode.extensions.getExtension('iotsharp.sonnetdb-vscode');
  assert.ok(extension, 'SonnetDB extension should be discoverable in the Extension Host.');
  await extension.activate();
  assert.equal(extension.isActive, true, 'SonnetDB extension should activate successfully.');

  const commands = new Set(await vscode.commands.getCommands(true));
  for (const command of RequiredCommands) {
    assert.ok(commands.has(command), `Expected command ${command} to be registered.`);
  }

  await verifyWorkbenchCommand();

  const resultPanelModule = await vscode.workspace.fs.readFile(vscode.Uri.joinPath(
    extension.extensionUri,
    'out',
    'panels',
    'queryResultPanel.js',
  ));
  assert.match(
    Buffer.from(resultPanelModule).toString('utf8'),
    /data-tab="trajectory"/u,
    'Packaged Query Result panel should include the Trajectory tab.',
  );

  const document = await vscode.workspace.openTextDocument({
    language: 'sql',
    content: 'SELECT knn([0.1, 0.2], ',
  });
  const editor = await vscode.window.showTextDocument(document);
  const position = document.positionAt(document.getText().length);
  editor.selection = new vscode.Selection(position, position);
  await delay(600);

  const diagnostics = vscode.languages.getDiagnostics(document.uri);
  assert.ok(
    diagnostics.some((diagnostic) => diagnostic.message === 'Unmatched opening parenthesis.'),
    'SQL diagnostics provider should report the unmatched function call.',
  );

  const signature = await vscode.commands.executeCommand<vscode.SignatureHelp>(
    'vscode.executeSignatureHelpProvider',
    document.uri,
    position,
    ',',
  );
  assert.equal(signature?.signatures[0]?.label, 'knn(vector_column, query_vector, top_k)');
  assert.equal(signature?.activeParameter, 2);

  const actions = await vscode.commands.executeCommand<Array<vscode.CodeAction | vscode.Command>>(
    'vscode.executeCodeActionProvider',
    document.uri,
    new vscode.Range(new vscode.Position(0, 0), position),
    vscode.CodeActionKind.QuickFix.value,
  );
  assert.ok(
    actions?.some((action) => 'title' in action && action.title === 'Insert closing parenthesis'),
    'SQL quick-fix provider should offer a closing parenthesis repair.',
  );

  await vscode.commands.executeCommand('workbench.action.closeActiveEditor');
}

/** 在真实 Extension Host 调用导航命令，拦截外部开启以避免浏览器或网络副作用。 */
async function verifyWorkbenchCommand(): Promise<void> {
  const profile: SonnetDbConnectionProfile = {
    id: 'wb16-host', label: 'Workbench Host fixture', kind: 'remote',
    baseUrl: 'https://unused.invalid/proxy/sonnet/?token=must-not-forward-token&sql=must-not-forward-sql#secret',
    defaultDatabase: 'DefaultDB:Other', tokenSecretKey: 'must-not-forward-secret-key',
  };
  const database = 'FactoryDB:East';
  const name = 'DeviceID:Main';
  const base = { profile, database };
  const fixtures: Array<{ node: TreeNode; model?: string; routeNode?: string }> = [
    { node: { ...base, kind: 'measurement', measurement: { name, columns: [] } }, model: 'measurement', routeNode: name },
    { node: { ...base, kind: 'table', table: { name, columns: [], primaryKey: [], indexes: [], createdUtc: '' } }, model: 'table', routeNode: name },
    { node: { ...base, kind: 'document', collection: { name, jsonIndexes: [], fullTextIndexes: [], createdUtc: '' } }, model: 'document', routeNode: name },
    { node: { ...base, kind: 'kvKeyspace', keyspace: name }, model: 'kv', routeNode: name },
    { node: { ...base, kind: 'vectorIndex', index: { measurement: name, column: 'Embedding:V2', kind: 'flat', metric: 'cosine', params: [] } }, model: 'vector', routeNode: `vector:${name}:Embedding:V2` },
    { node: { ...base, kind: 'fullTextIndex', index: { collection: name, name: 'Text:V2', fields: [], tokenizer: 'standard', documentCount: 0 } }, model: 'fulltext', routeNode: `fulltext:${name}:Text:V2` },
    { node: { ...base, kind: 'mqTopic', topic: { topic: name, messageCount: 0, nextOffset: 0 } }, model: 'mq', routeNode: name },
    { node: { ...base, database: 'FactoryDB:West', kind: 'mqTopic', topic: { topic: name, messageCount: 0, nextOffset: 0 } }, model: 'mq', routeNode: name },
    { node: { ...base, kind: 'objectBucket', bucket: { name, purpose: '', createdUtc: '', updatedUtc: '' } }, model: 'bucket', routeNode: name },
    { node: { ...base, kind: 'graph', graph: { name, storageId: 'host-fixture', recordFormatVersion: 1 } }, model: 'graph', routeNode: name },
    { node: { ...base, kind: 'index', index: { id: `table:${name}:IX:Device`, model: 'table', owner: name, name: 'IX:Device', kind: 'btree', state: 'ready', includedInBackup: true, rebuildable: true, columns: [] } }, model: 'index', routeNode: `table:${name}:IX:Device` },
    { node: { ...base, kind: 'backup', backupStatus: null }, model: 'backup', routeNode: 'backup-status' },
    { node: { kind: 'database', profile, name: database, active: false } },
  ];
  const descriptor = Object.getOwnPropertyDescriptor(vscode.env, 'openExternal');
  assert.ok(descriptor?.configurable || descriptor?.writable, 'Extension Host openExternal must be replaceable for side-effect-free command evidence.');
  const original = vscode.env.openExternal;
  const captured: string[] = [];
  const replacement = async (uri: vscode.Uri): Promise<boolean> => {
    captured.push(uri.toString(true));
    return true;
  };
  const deadline = Date.now() + 30_000;
  try {
    if (descriptor.configurable) {
      Object.defineProperty(vscode.env, 'openExternal', { configurable: true, enumerable: descriptor.enumerable, writable: true, value: replacement });
    } else {
      (vscode.env as { openExternal: typeof original }).openExternal = replacement;
    }
    assert.equal(fixtures.length, 13);
    for (const fixture of fixtures) {
      assert.ok(Date.now() < deadline, 'Workbench command fixtures exceeded the 30-second bound.');
      const before = captured.length;
      await withTimeout(vscode.commands.executeCommand('sonnetdb.openWorkbench', fixture.node), Math.max(1, deadline - Date.now()));
      assert.equal(captured.length, before + 1, 'Selected resource must call openExternal exactly once.');
      const value = captured[before];
      const url = new URL(value);
      const selectedDatabase = fixture.node.kind === 'database' ? fixture.node.name : 'database' in fixture.node ? fixture.node.database : undefined;
      assert.equal(url.pathname, '/proxy/sonnet/admin/app/sql');
      assert.equal(url.searchParams.get('database'), selectedDatabase);
      assert.notEqual(url.searchParams.get('database'), profile.defaultDatabase);
      assert.equal(url.searchParams.get('model'), fixture.model ?? null);
      assert.equal(url.searchParams.get('node'), fixture.routeNode ?? null);
      assert.equal(url.searchParams.get('tool'), fixture.model && !['index', 'backup'].includes(fixture.model) ? fixture.model : null);
      assert.equal(url.hash, '');
      assert.deepEqual([...url.searchParams.keys()].sort(), fixture.model
        ? (['index', 'backup'].includes(fixture.model) ? ['database', 'model', 'node'] : ['database', 'model', 'node', 'tool'])
        : ['database']);
      assert.equal(value.includes('must-not-forward'), false, 'Credentials, SQL and hash from the connection must not escape.');
    }
  } finally {
    if (descriptor.configurable) Object.defineProperty(vscode.env, 'openExternal', descriptor);
    else (vscode.env as { openExternal: typeof original }).openExternal = original;
  }
  assert.equal(vscode.env.openExternal, original, 'openExternal must be restored after command evidence.');
}

async function withTimeout<T>(promise: Thenable<T>, milliseconds: number): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      Promise.resolve(promise),
      new Promise<never>((_resolve, reject) => { timer = setTimeout(() => reject(new Error('Workbench command timed out.')), milliseconds); }),
    ]);
  } finally {
    if (timer) clearTimeout(timer);
  }
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
