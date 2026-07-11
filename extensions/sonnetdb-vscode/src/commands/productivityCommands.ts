import * as vscode from 'vscode';
import { SonnetDbClient } from '../core/sonnetdbClient';
import { SonnetDbConnectionProfile } from '../core/types';
import { QueryResultPanel } from '../panels/queryResultPanel';
import { TreeNode } from '../tree/sonnetdbTreeDataProvider';

export function registerProductivityCommands(
  context: vscode.ExtensionContext,
  getActiveProfile: () => SonnetDbConnectionProfile | undefined,
  createClient: (profile: SonnetDbConnectionProfile) => Promise<SonnetDbClient>,
  resultPanel: QueryResultPanel,
): void {
  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.openObjectQuery', async (node?: TreeNode) => {
      const target = queryTarget(node);
      if (target) {
        await openSql(`SELECT *\nFROM ${quoteIdentifier(target)}\nLIMIT 100;\n`);
      }
    }),
    vscode.commands.registerCommand('sonnetdb.copyObjectName', async (node?: TreeNode) => {
      const name = objectName(node);
      if (name) {
        await vscode.env.clipboard.writeText(name);
      }
    }),
    vscode.commands.registerCommand('sonnetdb.createMeasurement', async () => {
      const name = await vscode.window.showInputBox({ prompt: 'Measurement name', validateInput: identifierValidation });
      if (!name) {
        return;
      }
      const columns = await vscode.window.showInputBox({
        prompt: 'Columns (comma separated TAG / FIELD declarations)',
        value: 'device_id TAG STRING, value FIELD FLOAT',
        validateInput: (value) => /\bFIELD\b/iu.test(value) ? undefined : 'At least one FIELD column is required.',
      });
      if (!columns) {
        return;
      }
      const body = columns.split(',').map((column) => `  ${column.trim()}`).join(',\n');
      await openSql(`CREATE MEASUREMENT ${quoteIdentifier(name)} (\n${body}\n);\n`);
    }),
    vscode.commands.registerCommand('sonnetdb.bulkImport', async (node?: TreeNode) => {
      const profile = getActiveProfile();
      if (!profile?.defaultDatabase) {
        void vscode.window.showWarningMessage('Select an active SonnetDB database first.');
        return;
      }
      const nodeMeasurement = node?.kind === 'measurement' ? node.measurement.name : undefined;
      const measurement = nodeMeasurement ?? await vscode.window.showInputBox({
        prompt: 'Target measurement',
        validateInput: identifierValidation,
      });
      if (!measurement) {
        return;
      }
      const formatChoice = await vscode.window.showQuickPick([
        { label: 'Line Protocol', description: '*.lp, *.txt', format: 'lp' as const },
        { label: 'JSON points', description: '*.json, *.jsonl', format: 'json' as const },
        { label: 'Bulk VALUES', description: '*.sql, *.txt', format: 'bulk' as const },
      ], { placeHolder: 'Select import format' });
      if (!formatChoice) {
        return;
      }
      const files = await vscode.window.showOpenDialog({
        title: `Import ${formatChoice.label}`,
        canSelectMany: false,
        canSelectFiles: true,
        canSelectFolders: false,
        openLabel: 'Stage Import',
      });
      const file = files?.[0];
      if (!file) {
        return;
      }
      const payload = await vscode.workspace.fs.readFile(file);
      const approved = await vscode.window.showWarningMessage(
        `Import ${payload.byteLength.toLocaleString()} bytes into ${profile.label} / ${profile.defaultDatabase} / ${measurement}?`,
        { modal: true },
        'Confirm Import',
      );
      if (approved !== 'Confirm Import') {
        return;
      }
      try {
        const result = await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: `Importing ${file.path.split('/').at(-1)}` },
          async () => (await createClient(profile)).ingestBulk(
            profile.defaultDatabase!,
            measurement,
            formatChoice.format,
            payload,
          ),
        );
        resultPanel.showRows(
          `Import ${measurement}`,
          ['written', 'skipped', 'elapsedMilliseconds'],
          [[result.written, result.skipped, result.elapsedMilliseconds]],
          result,
          file.fsPath,
        );
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        void vscode.window.showErrorMessage(`SonnetDB import failed: ${message}`);
      }
    }),
    vscode.commands.registerCommand('sonnetdb.openSqlReference', () => vscode.env.openExternal(
      vscode.Uri.parse('https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md'),
    )),
  );
}

function queryTarget(node: TreeNode | undefined): string | undefined {
  if (node?.kind === 'measurement') return node.measurement.name;
  if (node?.kind === 'table') return node.table.name;
  return undefined;
}

function objectName(node: TreeNode | undefined): string | undefined {
  if (node?.kind === 'measurement') return node.measurement.name;
  if (node?.kind === 'table') return node.table.name;
  if (node?.kind === 'column') return node.name;
  return undefined;
}

function quoteIdentifier(value: string): string {
  return /^[A-Za-z_][A-Za-z0-9_]*$/u.test(value) ? value : `"${value.replace(/"/gu, '""')}"`;
}

function identifierValidation(value: string): string | undefined {
  return value.trim().length > 0 ? undefined : 'Name is required.';
}

async function openSql(sql: string): Promise<void> {
  const document = await vscode.workspace.openTextDocument({ language: 'sql', content: sql });
  await vscode.window.showTextDocument(document);
}
