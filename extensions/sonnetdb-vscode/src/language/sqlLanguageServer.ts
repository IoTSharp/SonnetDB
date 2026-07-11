import { existsSync } from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { LanguageServerLaunch, SqlLanguageServerClient } from '../core/sqlLanguageServerClient';

export function createSqlLanguageServer(
  context: vscode.ExtensionContext,
): SqlLanguageServerClient | undefined {
  const configuration = vscode.workspace.getConfiguration('sonnetdb.languageServer');
  if (!configuration.get<boolean>('enabled', true)) {
    return undefined;
  }

  // 可执行路径只接受用户级设置，避免仓库设置在打开工作区时触发任意进程。
  const configuredPath = configuration.inspect<string>('path')?.globalValue?.trim() ?? '';
  const launch = configuredPath
    ? launchForPath(configuredPath)
    : discoverLaunch(context.extensionPath);
  if (!launch) {
    return undefined;
  }

  const output = vscode.window.createOutputChannel('SonnetDB Language Server', { log: true });
  const client = new SqlLanguageServerClient(launch, (message) => output.info(message));
  context.subscriptions.push(output, client);
  return client;
}

function discoverLaunch(extensionPath: string): LanguageServerLaunch | undefined {
  const candidates = [
    path.join(extensionPath, 'language-server', executableName()),
    path.join(extensionPath, 'language-server', 'SonnetDB.LanguageServer.dll'),
    path.resolve(extensionPath, '..', '..', 'src', 'SonnetDB.LanguageServer', 'bin', 'Debug', 'net10.0', executableName()),
    path.resolve(extensionPath, '..', '..', 'src', 'SonnetDB.LanguageServer', 'bin', 'Debug', 'net10.0', 'SonnetDB.LanguageServer.dll'),
  ];
  const candidate = candidates.find(existsSync);
  return candidate ? launchForPath(candidate) : undefined;
}

function launchForPath(serverPath: string): LanguageServerLaunch {
  const resolved = path.resolve(serverPath);
  if (resolved.toLowerCase().endsWith('.dll')) {
    return { command: 'dotnet', args: [resolved] };
  }
  if (resolved.toLowerCase().endsWith('.csproj')) {
    return { command: 'dotnet', args: ['run', '--project', resolved, '--no-launch-profile'] };
  }
  return { command: resolved, args: [] };
}

function executableName(): string {
  return process.platform === 'win32' ? 'SonnetDB.LanguageServer.exe' : 'SonnetDB.LanguageServer';
}
