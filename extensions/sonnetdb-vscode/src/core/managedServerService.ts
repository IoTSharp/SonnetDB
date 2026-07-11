import { ChildProcess, spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { getDefaultBaseUrl } from './config';
import { ConnectionService } from './connectionService';
import { SonnetDbClient } from './sonnetdbClient';
import { SonnetDbConnectionProfile } from './types';

interface ManagedProcess {
  process: ChildProcess;
  profile: SonnetDbConnectionProfile;
}

export class ManagedServerService implements vscode.Disposable {
  private readonly processes = new Map<string, ManagedProcess>();
  private readonly output = vscode.window.createOutputChannel('SonnetDB Managed Server');

  public constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly connections: ConnectionService,
  ) {}

  public dispose(): void {
    const keepRunning = vscode.workspace.getConfiguration('sonnetdb').get<boolean>('managedLocal.keepRunningOnExit', false);
    if (!keepRunning) {
      for (const managed of this.processes.values()) {
        managed.process.kill();
      }
    }
    this.output.dispose();
  }

  public async start(): Promise<void> {
    const roots = await vscode.window.showOpenDialog({
      title: 'Select SonnetDB data root',
      canSelectFiles: false,
      canSelectFolders: true,
      canSelectMany: false,
      openLabel: 'Use as Data Root',
    });
    const dataRoot = roots?.[0]?.fsPath;
    if (!dataRoot) {
      return;
    }

    const baseUrl = normalizeBaseUrl(
      vscode.workspace.getConfiguration('sonnetdb').get<string>('managedLocal.baseUrl', getDefaultBaseUrl()),
    );
    if (await isHealthy(baseUrl)) {
      const attach = await vscode.window.showWarningMessage(
        `${baseUrl} is already healthy. Attach this data-root profile without starting another process?`,
        { modal: true },
        'Attach',
      );
      if (attach === 'Attach') {
        await this.upsertProfile(dataRoot, baseUrl);
      }
      return;
    }

    const target = await this.resolveLaunchTarget();
    if (!target) {
      return;
    }

    const environment = {
      ...process.env,
      SONNETDB_Kestrel__Endpoints__Http__Url: baseUrl,
      SONNETDB_Kestrel__Endpoints__FrameH2__Url: frameUrl(baseUrl),
      SONNETDB_SonnetDBServer__DataRoot: dataRoot,
      SONNETDB_SonnetDBServer__Mqtt__Enabled: 'false',
      SONNETDB_SonnetDBServer__Coap__Enabled: 'false',
      SONNETDB_SonnetDBServer__LineProtocolUdp__Enabled: 'false',
    };
    this.output.appendLine(`[start] ${target.command} ${target.args.join(' ')}`);
    this.output.appendLine(`[data] ${dataRoot}`);
    this.output.appendLine(`[url] ${baseUrl}`);

    const child = spawn(target.command, target.args, {
      cwd: target.cwd,
      env: environment,
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    child.stdout?.on('data', (chunk: Buffer) => this.output.append(chunk.toString('utf8')));
    child.stderr?.on('data', (chunk: Buffer) => this.output.append(chunk.toString('utf8')));
    child.on('error', (error) => this.output.appendLine(`[error] ${error.message}`));

    const healthy = await vscode.window.withProgress(
      { location: vscode.ProgressLocation.Notification, title: 'Starting managed SonnetDB Server', cancellable: true },
      (_, token) => waitForHealth(baseUrl, child, token),
    );
    if (!healthy) {
      child.kill();
      this.output.show(true);
      void vscode.window.showErrorMessage('Managed SonnetDB Server did not become healthy. See the SonnetDB Managed Server output.');
      return;
    }

    const profile = await this.upsertProfile(dataRoot, baseUrl);
    this.processes.set(profile.id, { process: child, profile });
    child.once('exit', (code) => {
      this.processes.delete(profile.id);
      this.output.appendLine(`[exit] ${profile.label} exited with code ${code ?? 'unknown'}`);
    });
    void this.connections.probe(profile).catch(() => undefined);
    void vscode.window.showInformationMessage(`Managed SonnetDB Server is healthy at ${baseUrl}.`);
  }

  public async stop(profile = this.connections.getActiveProfile()): Promise<void> {
    if (!profile || profile.kind !== 'managed-local') {
      void vscode.window.showWarningMessage('The active SonnetDB connection is not a managed local server.');
      return;
    }
    const managed = this.processes.get(profile.id);
    if (!managed) {
      void vscode.window.showInformationMessage(`${profile.label} is attached, but it was not started by this extension session.`);
      return;
    }
    managed.process.kill();
    this.processes.delete(profile.id);
    void vscode.window.showInformationMessage(`Stopped ${profile.label}.`);
  }

  public showOutput(): void {
    this.output.show();
  }

  private async upsertProfile(dataRoot: string, baseUrl: string): Promise<SonnetDbConnectionProfile> {
    const existing = this.connections.getProfiles().find((profile) =>
      profile.kind === 'managed-local' && path.resolve(profile.dataRoot ?? '') === path.resolve(dataRoot));
    if (existing) {
      await this.connections.update(existing, { baseUrl, dataRoot });
      await this.connections.setActive(existing);
      return existing;
    }
    const profile: SonnetDbConnectionProfile = {
      id: `managed-${Date.now()}`,
      label: `Local · ${path.basename(dataRoot)}`,
      kind: 'managed-local',
      baseUrl,
      dataRoot,
    };
    await this.connections.add(profile);
    return profile;
  }

  private async resolveLaunchTarget(): Promise<{ command: string; args: string[]; cwd: string } | undefined> {
    const configured = vscode.workspace.getConfiguration('sonnetdb').get<string>('managedLocal.serverPath', '').trim();
    const candidates = [configured, ...workspaceCandidates()].filter(Boolean);
    let selected = candidates.find((candidate) => existsSync(candidate));
    if (!selected) {
      const files = await vscode.window.showOpenDialog({
        title: 'Select SonnetDB Server executable, DLL, or project',
        canSelectFiles: true,
        canSelectFolders: false,
        canSelectMany: false,
        filters: { 'SonnetDB Server': ['exe', 'dll', 'csproj'] },
        openLabel: 'Use Server',
      });
      selected = files?.[0]?.fsPath;
    }
    if (!selected) {
      return undefined;
    }
    const cwd = path.dirname(selected);
    switch (path.extname(selected).toLowerCase()) {
      case '.dll': return { command: 'dotnet', args: [selected], cwd };
      case '.csproj': return { command: 'dotnet', args: ['run', '--project', selected, '--no-launch-profile'], cwd };
      default: return { command: selected, args: [], cwd };
    }
  }
}

function workspaceCandidates(): string[] {
  return (vscode.workspace.workspaceFolders ?? []).flatMap((folder) => {
    const root = folder.uri.fsPath;
    return [
      path.join(root, 'src', 'SonnetDB', 'bin', 'Release', 'net10.0', 'SonnetDB.exe'),
      path.join(root, 'src', 'SonnetDB', 'bin', 'Release', 'net10.0', 'SonnetDB.dll'),
      path.join(root, 'src', 'SonnetDB', 'bin', 'Debug', 'net10.0', 'SonnetDB.exe'),
      path.join(root, 'src', 'SonnetDB', 'bin', 'Debug', 'net10.0', 'SonnetDB.dll'),
      path.join(root, 'src', 'SonnetDB', 'SonnetDB.csproj'),
      path.join(root, 'SonnetDB', 'src', 'SonnetDB', 'SonnetDB.csproj'),
    ];
  });
}

async function waitForHealth(
  baseUrl: string,
  child: ChildProcess,
  token: vscode.CancellationToken,
): Promise<boolean> {
  for (let attempt = 0; attempt < 40 && !token.isCancellationRequested; attempt += 1) {
    if (child.exitCode !== null || child.killed) {
      return false;
    }
    if (await isHealthy(baseUrl)) {
      return true;
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  return false;
}

async function isHealthy(baseUrl: string): Promise<boolean> {
  try {
    return (await new SonnetDbClient(baseUrl).checkHealth()).status === 'ok';
  } catch {
    return false;
  }
}

function normalizeBaseUrl(value: string): string {
  return value.trim().replace(/\/+$/u, '');
}

function frameUrl(baseUrl: string): string {
  const url = new URL(baseUrl);
  const port = Number(url.port || (url.protocol === 'https:' ? 443 : 80));
  url.port = String(port + 1);
  return url.toString().replace(/\/+$/u, '');
}
