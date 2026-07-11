import * as vscode from 'vscode';
import { ConnectionService } from '../core/connectionService';

export class ConnectionStatus implements vscode.Disposable {
  private readonly connectionItem: vscode.StatusBarItem;
  private readonly databaseItem: vscode.StatusBarItem;
  private readonly modeItem: vscode.StatusBarItem;
  private readonly subscription: vscode.Disposable;

  public constructor(private readonly connections: ConnectionService) {
    this.connectionItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    this.connectionItem.command = 'sonnetdb.selectConnection';
    this.connectionItem.name = 'SonnetDB connection';

    this.databaseItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 99);
    this.databaseItem.command = 'sonnetdb.selectDatabase';
    this.databaseItem.name = 'SonnetDB database';

    this.modeItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 98);
    this.modeItem.text = '$(lock) Read-only';
    this.modeItem.tooltip = 'SonnetDB developer surfaces default to read-only';
    this.modeItem.show();

    this.subscription = connections.onDidChange(() => this.render());
    this.render();
  }

  public dispose(): void {
    this.subscription.dispose();
    this.connectionItem.dispose();
    this.databaseItem.dispose();
    this.modeItem.dispose();
  }

  private render(): void {
    const profile = this.connections.getActiveProfile();
    if (!profile) {
      this.connectionItem.text = '$(database) SonnetDB: Connect';
      this.connectionItem.tooltip = 'Add or select a SonnetDB connection';
      this.connectionItem.show();
      this.databaseItem.hide();
      return;
    }

    const probe = this.connections.getProbe(profile);
    const healthy = probe?.health.status === 'ok';
    this.connectionItem.text = `${healthy ? '$(pass-filled)' : '$(server)'} ${profile.label}`;
    this.connectionItem.tooltip = probe
      ? `${profile.baseUrl}\n${healthy ? 'Healthy' : probe.health.status}\nLast checked ${new Date(probe.checkedAt).toLocaleTimeString()}`
      : `${profile.baseUrl}\nConnection has not been checked yet`;
    this.connectionItem.show();

    this.databaseItem.text = `$(database) ${profile.defaultDatabase ?? 'Select database'}`;
    this.databaseItem.tooltip = 'Active SonnetDB database';
    this.databaseItem.show();
  }
}
