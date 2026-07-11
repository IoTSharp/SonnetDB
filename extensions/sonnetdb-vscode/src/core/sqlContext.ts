import * as vscode from 'vscode';
import { extractStatementAtOffset } from './sqlText';

export { extractStatementAtOffset } from './sqlText';

export function getEditorSql(editor: vscode.TextEditor, requireSelection = false): string | undefined {
  if (!editor.selection.isEmpty) {
    return editor.document.getText(editor.selection).trim() || undefined;
  }
  if (requireSelection) {
    return undefined;
  }
  return extractStatementAtOffset(
    editor.document.getText(),
    editor.document.offsetAt(editor.selection.active),
  );
}
