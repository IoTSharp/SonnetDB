import * as vscode from 'vscode';
import { SonnetDbClient } from './sonnetdbClient';
import {
  ConnectionProbeResult,
  SonnetDbConnectionProfile,
} from './types';

const ProfilesStorageKey = 'sonnetdb.connectionProfiles';
const ActiveProfileStorageKey = 'sonnetdb.activeProfileId';

export class ConnectionService implements vscode.Disposable {
  private readonly profiles: SonnetDbConnectionProfile[];
  private readonly probes = new Map<string, ConnectionProbeResult>();
  private readonly emitter = new vscode.EventEmitter<void>();
  private activeProfileId: string | undefined;

  public readonly onDidChange = this.emitter.event;

  public constructor(private readonly context: vscode.ExtensionContext) {
    this.profiles = loadProfiles(context);
    this.activeProfileId = context.globalState.get<string>(ActiveProfileStorageKey) ?? this.profiles[0]?.id;
  }

  public dispose(): void {
    this.emitter.dispose();
  }

  public getProfiles(): SonnetDbConnectionProfile[] {
    return this.profiles;
  }

  public getActiveProfileId(): string | undefined {
    return this.activeProfileId;
  }

  public getActiveProfile(): SonnetDbConnectionProfile | undefined {
    return this.profiles.find((profile) => profile.id === this.activeProfileId);
  }

  public getProbe(profile: SonnetDbConnectionProfile): ConnectionProbeResult | undefined {
    return this.probes.get(profile.id);
  }

  public async getToken(profile: SonnetDbConnectionProfile): Promise<string | undefined> {
    return this.context.secrets.get(secretKey(profile.id));
  }

  public async createClient(profile: SonnetDbConnectionProfile): Promise<SonnetDbClient> {
    return new SonnetDbClient(profile.baseUrl, await this.getToken(profile));
  }

  public async add(profile: SonnetDbConnectionProfile, token?: string): Promise<void> {
    this.profiles.push(profile);
    if (token) {
      await this.context.secrets.store(secretKey(profile.id), token);
    }
    await this.setActive(profile);
  }

  public async update(
    profile: SonnetDbConnectionProfile,
    values: Partial<Pick<SonnetDbConnectionProfile, 'label' | 'baseUrl' | 'dataRoot'>>,
    token?: string,
  ): Promise<void> {
    Object.assign(profile, values);
    this.probes.delete(profile.id);
    if (token !== undefined) {
      if (token) {
        await this.context.secrets.store(secretKey(profile.id), token);
      } else {
        await this.context.secrets.delete(secretKey(profile.id));
      }
    }
    await this.persist();
    this.emitter.fire();
  }

  public async remove(profile: SonnetDbConnectionProfile): Promise<void> {
    const index = this.profiles.findIndex((candidate) => candidate.id === profile.id);
    if (index < 0) {
      return;
    }
    this.profiles.splice(index, 1);
    this.probes.delete(profile.id);
    await this.context.secrets.delete(secretKey(profile.id));
    if (this.activeProfileId === profile.id) {
      this.activeProfileId = this.profiles[0]?.id;
      await this.context.globalState.update(ActiveProfileStorageKey, this.activeProfileId);
    }
    await this.persist();
    this.emitter.fire();
  }

  public async setActive(profile: SonnetDbConnectionProfile, database?: string): Promise<void> {
    this.activeProfileId = profile.id;
    if (database) {
      profile.defaultDatabase = database;
    }
    await this.persist();
    await this.context.globalState.update(ActiveProfileStorageKey, this.activeProfileId);
    this.emitter.fire();
  }

  public async probe(profile: SonnetDbConnectionProfile): Promise<ConnectionProbeResult> {
    const client = await this.createClient(profile);
    const [health, setup] = await Promise.all([
      client.checkHealth(),
      client.fetchSetupStatus(),
    ]);
    const result = { health, setup, checkedAt: Date.now() };
    this.probes.set(profile.id, result);
    this.emitter.fire();
    return result;
  }

  private async persist(): Promise<void> {
    await this.context.globalState.update(ProfilesStorageKey, this.profiles.map(cloneProfile));
  }
}

function loadProfiles(context: vscode.ExtensionContext): SonnetDbConnectionProfile[] {
  const stored = context.globalState.get<SonnetDbConnectionProfile[]>(ProfilesStorageKey, []);
  return Array.isArray(stored) ? stored.filter(isProfile).map(cloneProfile) : [];
}

function isProfile(value: unknown): value is SonnetDbConnectionProfile {
  if (!value || typeof value !== 'object') {
    return false;
  }
  const record = value as Record<string, unknown>;
  return typeof record.id === 'string'
    && typeof record.label === 'string'
    && typeof record.baseUrl === 'string'
    && (record.kind === 'remote' || record.kind === 'managed-local');
}

function cloneProfile(profile: SonnetDbConnectionProfile): SonnetDbConnectionProfile {
  return {
    id: profile.id,
    label: profile.label,
    kind: profile.kind,
    baseUrl: profile.baseUrl,
    defaultDatabase: profile.defaultDatabase,
    dataRoot: profile.dataRoot,
  };
}

function secretKey(profileId: string): string {
  return `sonnetdb.connection.${profileId}.token`;
}
