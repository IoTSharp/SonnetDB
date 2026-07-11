import * as vscode from 'vscode';

export const extensionId = 'iotsharp.sonnetdb-vscode';
export const configurationSection = 'sonnetdb';

export function getDefaultBaseUrl(): string {
  return vscode.workspace
    .getConfiguration(configurationSection)
    .get<string>('defaultBaseUrl', 'http://127.0.0.1:5080');
}

export function getDefaultQueryMaxRows(): number {
  return vscode.workspace
    .getConfiguration(configurationSection)
    .get<number>('query.maxRows', 1000);
}
