import * as vscode from 'vscode';
import { scanSqlDiagnostics } from '../core/sqlDiagnostics';
import { LanguageServerDiagnostic, SqlLanguageServerClient } from '../core/sqlLanguageServerClient';

const Help = new Map<string, string>([
  ['measurement', 'Defines or references a SonnetDB time-series measurement.'],
  ['tag', 'A low-cardinality indexed dimension used to identify a time series.'],
  ['field', 'A typed measurement value. Fields may be sparse across timestamps.'],
  ['explain', 'Returns a read-only key/value query plan without executing the statement.'],
  ['knn', 'Runs top-K vector search using the configured vector metric and index.'],
  ['forecast', 'Produces a forecast from a time-series input.'],
  ['pid', 'Evaluates SonnetDB PID control helpers over a time-series input.'],
]);

export function registerSqlLanguageFeatures(
  context: vscode.ExtensionContext,
  languageServer?: SqlLanguageServerClient,
): void {
  const diagnostics = vscode.languages.createDiagnosticCollection('sonnetdb-sql');
  const selector: vscode.DocumentSelector = [{ language: 'sql' }, { language: 'sonnetdb-sql' }];
  const pending = new Map<string, NodeJS.Timeout>();

  const refresh = (document: vscode.TextDocument): void => {
    if (document.languageId !== 'sql' && document.languageId !== 'sonnetdb-sql') {
      return;
    }
    const localIssues = scanSqlDiagnostics(document.getText());
    diagnostics.set(document.uri, localIssues.map((issue) => {
      const start = document.positionAt(issue.offset);
      const end = document.positionAt(issue.offset + issue.length);
      const diagnostic = new vscode.Diagnostic(new vscode.Range(start, end), issue.message, vscode.DiagnosticSeverity.Error);
      diagnostic.source = 'SonnetDB SQL';
      return diagnostic;
    }));

    const key = document.uri.toString();
    const existing = pending.get(key);
    if (existing) {
      clearTimeout(existing);
    }
    if (languageServer) {
      const version = document.version;
      pending.set(key, setTimeout(() => {
        pending.delete(key);
        void languageServer.validate(document.getText()).then((serverIssues) => {
          if (document.isClosed || document.version !== version) {
            return;
          }
          const serverOffsets = new Set(serverIssues.map((issue) => issue.offset));
          const combined = [
            ...serverIssues,
            ...localIssues.filter((issue) => !serverOffsets.has(issue.offset)).map((issue) => ({
              ...issue,
              severity: 'error' as const,
            })),
          ];
          diagnostics.set(document.uri, combined.map((issue) => toDiagnostic(document, issue)));
        }).catch(() => {
          // Sidecar 不可用时保留轻量诊断，避免影响编辑体验。
        });
      }, 250));
    }
  };

  context.subscriptions.push(
    diagnostics,
    vscode.workspace.onDidOpenTextDocument(refresh),
    vscode.workspace.onDidChangeTextDocument((event) => refresh(event.document)),
    vscode.workspace.onDidCloseTextDocument((document) => {
      const key = document.uri.toString();
      const timeout = pending.get(key);
      if (timeout) {
        clearTimeout(timeout);
        pending.delete(key);
      }
      diagnostics.delete(document.uri);
    }),
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

function toDiagnostic(
  document: vscode.TextDocument,
  issue: LanguageServerDiagnostic,
): vscode.Diagnostic {
  const start = document.positionAt(issue.offset);
  const end = document.positionAt(issue.offset + issue.length);
  const severity = issue.severity === 'warning'
    ? vscode.DiagnosticSeverity.Warning
    : issue.severity === 'information'
      ? vscode.DiagnosticSeverity.Information
      : issue.severity === 'hint'
        ? vscode.DiagnosticSeverity.Hint
        : vscode.DiagnosticSeverity.Error;
  const diagnostic = new vscode.Diagnostic(new vscode.Range(start, end), issue.message, severity);
  diagnostic.source = 'SonnetDB SQL Parser';
  return diagnostic;
}
