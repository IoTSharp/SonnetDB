import * as vscode from 'vscode';
import { scanSqlDiagnostics } from '../core/sqlDiagnostics';

const Help = new Map<string, string>([
  ['measurement', 'Defines or references a SonnetDB time-series measurement.'],
  ['tag', 'A low-cardinality indexed dimension used to identify a time series.'],
  ['field', 'A typed measurement value. Fields may be sparse across timestamps.'],
  ['explain', 'Returns a read-only key/value query plan without executing the statement.'],
  ['knn', 'Runs top-K vector search using the configured vector metric and index.'],
  ['forecast', 'Produces a forecast from a time-series input.'],
  ['pid', 'Evaluates SonnetDB PID control helpers over a time-series input.'],
]);

export function registerSqlLanguageFeatures(context: vscode.ExtensionContext): void {
  const diagnostics = vscode.languages.createDiagnosticCollection('sonnetdb-sql');
  const selector: vscode.DocumentSelector = [{ language: 'sql' }, { language: 'sonnetdb-sql' }];

  const refresh = (document: vscode.TextDocument): void => {
    if (document.languageId !== 'sql' && document.languageId !== 'sonnetdb-sql') {
      return;
    }
    diagnostics.set(document.uri, scanSqlDiagnostics(document.getText()).map((issue) => {
      const start = document.positionAt(issue.offset);
      const end = document.positionAt(issue.offset + issue.length);
      const diagnostic = new vscode.Diagnostic(new vscode.Range(start, end), issue.message, vscode.DiagnosticSeverity.Error);
      diagnostic.source = 'SonnetDB SQL';
      return diagnostic;
    }));
  };

  context.subscriptions.push(
    diagnostics,
    vscode.workspace.onDidOpenTextDocument(refresh),
    vscode.workspace.onDidChangeTextDocument((event) => refresh(event.document)),
    vscode.workspace.onDidCloseTextDocument((document) => diagnostics.delete(document.uri)),
    vscode.languages.registerHoverProvider(selector, {
      provideHover(document, position) {
        const range = document.getWordRangeAtPosition(position);
        const word = range ? document.getText(range).toLowerCase() : '';
        const help = Help.get(word);
        if (!help) {
          return undefined;
        }
        return new vscode.Hover(new vscode.MarkdownString(`**SonnetDB ${word.toUpperCase()}**\n\n${help}`), range);
      },
    }),
  );
  for (const document of vscode.workspace.textDocuments) {
    refresh(document);
  }
}
