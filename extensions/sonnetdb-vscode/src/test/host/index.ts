import * as assert from 'node:assert/strict';
import * as vscode from 'vscode';

const RequiredCommands = [
  'sonnetdb.addConnection',
  'sonnetdb.runQuery',
  'sonnetdb.previewObjectBucket',
  'sonnetdb.showRuntimeMonitor',
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

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
