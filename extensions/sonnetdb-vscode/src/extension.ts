import * as vscode from 'vscode';
import { getDefaultBaseUrl } from './core/config';
import { SonnetDbConnectionProfile } from './core/types';
import { registerRunQueryCommand } from './commands/runQueryCommand';
import { CopilotPanel } from './panels/copilotPanel';
import { QueryResultPanel } from './panels/queryResultPanel';
import { SonnetDbTreeDataProvider } from './tree/sonnetdbTreeDataProvider';

export function activate(context: vscode.ExtensionContext): void {
  const profiles: SonnetDbConnectionProfile[] = [];
  let activeProfileId: string | undefined;

  const tree = new SonnetDbTreeDataProvider(
    () => profiles,
    async (profile) => context.secrets.get(getSecretKey(profile.id)),
  );
  const resultPanel = new QueryResultPanel();
  const copilotPanel = new CopilotPanel();

  context.subscriptions.push(
    vscode.window.registerTreeDataProvider('sonnetdb.explorer', tree),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.addConnection', async () => {
      const label = await vscode.window.showInputBox({
        prompt: 'Connection label',
        value: 'Local SonnetDB',
        ignoreFocusOut: true,
      });

      if (!label) {
        return;
      }

      const baseUrl = await vscode.window.showInputBox({
        prompt: 'SonnetDB base URL',
        value: getDefaultBaseUrl(),
        ignoreFocusOut: true,
      });

      if (!baseUrl) {
        return;
      }

      const profile: SonnetDbConnectionProfile = {
        id: `profile-${Date.now()}`,
        label,
        kind: 'remote',
        baseUrl,
      };

      profiles.push(profile);
      activeProfileId = profile.id;
      tree.refresh();

      const token = await vscode.window.showInputBox({
        prompt: 'Bearer token (stored in SecretStorage)',
        password: true,
        ignoreFocusOut: true,
      });

      if (token) {
        await context.secrets.store(getSecretKey(profile.id), token);
      }

      void vscode.window.showInformationMessage(`Added SonnetDB connection "${label}".`);
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.refreshExplorer', () => {
      tree.refresh();
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.openCopilot', () => {
      copilotPanel.show();
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.startManagedLocalServer', () => {
      void vscode.window.showInformationMessage(
        'Managed local server mode is planned for PR #105.',
      );
    }),
  );

  registerRunQueryCommand(
    context,
    () => profiles.find((profile) => profile.id === activeProfileId),
    async (profile) => context.secrets.get(getSecretKey(profile.id)),
    resultPanel,
  );
}

export function deactivate(): void {}

function getSecretKey(profileId: string): string {
  return `sonnetdb.connection.${profileId}.token`;
}
