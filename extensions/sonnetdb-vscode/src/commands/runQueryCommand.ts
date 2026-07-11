import * as vscode from 'vscode';
import { SonnetDbClient } from '../core/sonnetdbClient';
import { SonnetDbConnectionProfile } from '../core/types';
import { getEditorSql } from '../core/sqlContext';
import { QueryResultPanel } from '../panels/queryResultPanel';

export function registerRunQueryCommand(
  context: vscode.ExtensionContext,
  getActiveProfile: () => SonnetDbConnectionProfile | undefined,
  getToken: (profile: SonnetDbConnectionProfile) => Promise<string | undefined>,
  resultPanel: QueryResultPanel,
  setActiveDatabase?: (profile: SonnetDbConnectionProfile, database: string) => Promise<void>,
): void {
  const execute = async (mode: 'query' | 'selection' | 'explain'): Promise<void> => {
    const profile = getActiveProfile();
    if (!profile) {
      void vscode.window.showWarningMessage('No active SonnetDB connection is selected yet.');
      return;
    }

    const token = await getToken(profile);
    const client = new SonnetDbClient(profile.baseUrl, token);
    const database = await resolveDatabase(profile, client);
    if (!database) {
      return;
    }
    if (database !== profile.defaultDatabase) {
      await setActiveDatabase?.(profile, database);
    }

    const editor = vscode.window.activeTextEditor;
    const sql = editor ? getEditorSql(editor, mode === 'selection') : undefined;
    if (!sql) {
      if (mode === 'selection') {
        void vscode.window.showWarningMessage('Select the SQL text to run first.');
      } else {
        await openNewQuery();
        void vscode.window.showInformationMessage('Enter SQL, then run the current statement or selection.');
      }
      return;
    }

    const statement = mode === 'explain'
      ? `EXPLAIN ${sql.replace(/;+\s*$/u, '')};`
      : sql;

    try {
      await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Window, title: `SonnetDB: ${mode === 'explain' ? 'Explaining' : 'Running'} query` },
        async () => {
          const result = await client.executeSql(database, statement);
          resultPanel.show(result, statement, profile.label, database);
        },
      );
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      void vscode.window.showErrorMessage(`SonnetDB query failed: ${message}`);
    }
  };

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.runQuery', () => execute('query')),
    vscode.commands.registerCommand('sonnetdb.runSelection', () => execute('selection')),
    vscode.commands.registerCommand('sonnetdb.explainQuery', () => execute('explain')),
    vscode.commands.registerCommand('sonnetdb.newQuery', openNewQuery),
  );
}

async function resolveDatabase(
  profile: SonnetDbConnectionProfile,
  client: SonnetDbClient,
): Promise<string | undefined> {
  if (profile.defaultDatabase) {
    return profile.defaultDatabase;
  }
  const response = await client.listDatabases();
  return vscode.window.showQuickPick(response.databases, { placeHolder: 'Select the target SonnetDB database' });
}

async function openNewQuery(initialSql = '-- SonnetDB query\nSELECT * FROM measurement LIMIT 100;\n'): Promise<void> {
  const document = await vscode.workspace.openTextDocument({ language: 'sql', content: initialSql });
  await vscode.window.showTextDocument(document, vscode.ViewColumn.Active);
}
