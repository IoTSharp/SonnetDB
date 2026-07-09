import { defineStore } from 'pinia';
import { computed, ref, shallowRef, watch } from 'vue';
import {
  getStudioNativeBridge,
  type StudioBridgeManifest,
  type StudioConnectionLibrarySnapshot,
  type StudioManagedServerStatus,
  type StudioNativeBridgeClient,
} from '@/api/studioNativeBridge';

export type ConnectionKind = 'managed-local' | 'remote';

export interface ConnectionProfile {
  id: string;
  name: string;
  kind: ConnectionKind;
  baseUrl: string;
  defaultDatabase: string;
  tokenMode: 'current-session';
  createdAt: number;
  updatedAt: number;
}

interface StoredConnectionsState {
  profiles: ConnectionProfile[];
  activeProfileId: string;
  activeDatabase: string;
}

const StorageKey = 'sndb.connection.library.v1';
const LocalProfileId = 'managed-local';

function now(): number {
  return Date.now();
}

function makeId(prefix: string): string {
  return `${prefix}_${now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function currentOrigin(): string {
  if (typeof window === 'undefined') return '/';
  return window.location.origin;
}

function normalizeBaseUrl(value: string): string {
  const trimmed = value.trim();
  if (!trimmed || trimmed === '/') return '/';
  return trimmed.replace(/\/+$/u, '');
}

function localProfile(baseUrl = '/'): ConnectionProfile {
  const ts = now();
  return {
    id: LocalProfileId,
    name: 'Managed Local',
    kind: 'managed-local',
    baseUrl,
    defaultDatabase: '',
    tokenMode: 'current-session',
    createdAt: ts,
    updatedAt: ts,
  };
}

function normalizeProfile(input: Partial<ConnectionProfile>, index: number): ConnectionProfile {
  const fallback = localProfile();
  const ts = now();
  return {
    id: typeof input.id === 'string' && input.id ? input.id : makeId('remote'),
    name: typeof input.name === 'string' && input.name ? input.name : `Remote ${index + 1}`,
    kind: input.kind === 'remote' ? 'remote' : 'managed-local',
    baseUrl: normalizeBaseUrl(typeof input.baseUrl === 'string' ? input.baseUrl : fallback.baseUrl),
    defaultDatabase: typeof input.defaultDatabase === 'string' ? input.defaultDatabase : '',
    tokenMode: 'current-session',
    createdAt: typeof input.createdAt === 'number' ? input.createdAt : ts,
    updatedAt: typeof input.updatedAt === 'number' ? input.updatedAt : ts,
  };
}

function loadState(): StoredConnectionsState {
  try {
    const raw = localStorage.getItem(StorageKey);
    if (!raw) {
      const profile = localProfile();
      return { profiles: [profile], activeProfileId: profile.id, activeDatabase: '' };
    }

    const parsed = JSON.parse(raw) as Partial<StoredConnectionsState>;
    const loaded = Array.isArray(parsed.profiles)
      ? parsed.profiles.map((profile, index) => normalizeProfile(profile, index))
      : [];
    const profiles = loaded.some((profile) => profile.id === LocalProfileId)
      ? loaded
      : [localProfile(), ...loaded];
    const activeProfileId = typeof parsed.activeProfileId === 'string'
      && profiles.some((profile) => profile.id === parsed.activeProfileId)
      ? parsed.activeProfileId
      : profiles[0].id;
    return {
      profiles,
      activeProfileId,
      activeDatabase: typeof parsed.activeDatabase === 'string' ? parsed.activeDatabase : '',
    };
  } catch {
    const profile = localProfile();
    return { profiles: [profile], activeProfileId: profile.id, activeDatabase: '' };
  }
}

function saveState(state: StoredConnectionsState): void {
  try {
    localStorage.setItem(StorageKey, JSON.stringify(state));
  } catch {
    // 浏览器可能禁用本地存储，内存态仍可继续工作。
  }
}

export const useConnectionsStore = defineStore('connections', () => {
  const initial = loadState();
  const profiles = ref<ConnectionProfile[]>(initial.profiles);
  const activeProfileId = ref(initial.activeProfileId);
  const activeDatabase = ref(initial.activeDatabase);
  const studioBridge = shallowRef<StudioNativeBridgeClient | null>(null);
  const studioBridgeManifest = ref<StudioBridgeManifest | null>(null);
  const studioManagedServerStatus = ref<StudioManagedServerStatus | null>(null);
  let syncingFromStudioBridge = false;

  const activeProfile = computed(() =>
    profiles.value.find((profile) => profile.id === activeProfileId.value) ?? profiles.value[0] ?? localProfile());

  const activeBaseUrl = computed(() => normalizeBaseUrl(activeProfile.value.baseUrl));

  const activeDisplayUrl = computed(() => {
    const baseUrl = activeBaseUrl.value;
    return baseUrl === '/' ? currentOrigin() : baseUrl;
  });

  const studioBridgeAvailable = computed(() => studioBridge.value !== null);

  function setActiveProfile(id: string): void {
    const profile = profiles.value.find((item) => item.id === id);
    if (!profile) return;
    activeProfileId.value = profile.id;
    activeDatabase.value = profile.defaultDatabase;
  }

  function setActiveDatabase(db: string): void {
    activeDatabase.value = db;
    const profile = activeProfile.value;
    const index = profiles.value.findIndex((item) => item.id === profile.id);
    if (index >= 0 && db && db !== '__control_plane__') {
      profiles.value[index] = {
        ...profiles.value[index],
        defaultDatabase: db,
        updatedAt: now(),
      };
    }
  }

  function upsertRemoteProfile(input: { id?: string; name: string; baseUrl: string; defaultDatabase?: string }): ConnectionProfile {
    const ts = now();
    const id = input.id && profiles.value.some((profile) => profile.id === input.id)
      ? input.id
      : makeId('remote');
    const next: ConnectionProfile = {
      id,
      name: input.name.trim() || 'Remote',
      kind: 'remote',
      baseUrl: normalizeBaseUrl(input.baseUrl),
      defaultDatabase: input.defaultDatabase?.trim() ?? '',
      tokenMode: 'current-session',
      createdAt: profiles.value.find((profile) => profile.id === id)?.createdAt ?? ts,
      updatedAt: ts,
    };

    const index = profiles.value.findIndex((profile) => profile.id === id);
    if (index >= 0) {
      profiles.value[index] = next;
    } else {
      profiles.value.push(next);
    }
    return next;
  }

  function removeProfile(id: string): void {
    if (id === LocalProfileId) return;
    profiles.value = profiles.value.filter((profile) => profile.id !== id);
    if (activeProfileId.value === id) {
      activeProfileId.value = profiles.value[0]?.id ?? LocalProfileId;
      activeDatabase.value = profiles.value[0]?.defaultDatabase ?? '';
    }
  }

  function setManagedLocalBaseUrl(baseUrl: string): void {
    const normalized = normalizeBaseUrl(baseUrl);
    const ts = now();
    const index = profiles.value.findIndex((profile) => profile.id === LocalProfileId);
    const next: ConnectionProfile = {
      ...(index >= 0 ? profiles.value[index] : localProfile(normalized)),
      id: LocalProfileId,
      name: 'Managed Local',
      kind: 'managed-local',
      baseUrl: normalized,
      tokenMode: 'current-session',
      updatedAt: ts,
    };
    if (index >= 0) {
      profiles.value[index] = next;
    } else {
      profiles.value.unshift(next);
    }
  }

  async function connectStudioBridge(): Promise<boolean> {
    const bridge = await getStudioNativeBridge();
    if (!bridge) return false;

    studioBridge.value = bridge;
    const manifest = await bridge.refreshManifest();
    bridge.manifest = manifest;
    studioBridgeManifest.value = manifest;
    studioManagedServerStatus.value = manifest.managedServer;

    const snapshot = await bridge.loadConnections();
    applyStudioSnapshot(snapshot);
    return true;
  }

  async function refreshStudioServerStatus(): Promise<StudioManagedServerStatus | null> {
    if (!studioBridge.value) return null;
    const status = await studioBridge.value.getServerStatus();
    studioManagedServerStatus.value = status;
    if (status.url) setManagedLocalBaseUrl(status.url);
    return status;
  }

  async function startStudioManagedServer(): Promise<StudioManagedServerStatus | null> {
    if (!studioBridge.value) return null;
    const status = await studioBridge.value.startServer();
    studioManagedServerStatus.value = status;
    if (status.url) setManagedLocalBaseUrl(status.url);
    return status;
  }

  async function stopStudioManagedServer(): Promise<StudioManagedServerStatus | null> {
    if (!studioBridge.value) return null;
    const status = await studioBridge.value.stopServer();
    studioManagedServerStatus.value = status;
    return status;
  }

  function applyStudioSnapshot(snapshot: StudioConnectionLibrarySnapshot): void {
    syncingFromStudioBridge = true;
    try {
      const normalizedProfiles = snapshot.profiles.map((profile, index) => normalizeProfile(profile, index));
      profiles.value = normalizedProfiles.length ? normalizedProfiles : [localProfile(studioBridgeManifest.value?.managedServerUrl ?? '/')];
      activeProfileId.value = profiles.value.some((profile) => profile.id === snapshot.activeProfileId)
        ? snapshot.activeProfileId
        : profiles.value[0].id;
      activeDatabase.value = snapshot.activeDatabase ?? '';
      saveState(currentState());
    } finally {
      syncingFromStudioBridge = false;
    }
  }

  function currentState(): StoredConnectionsState {
    return {
      profiles: profiles.value,
      activeProfileId: activeProfileId.value,
      activeDatabase: activeDatabase.value,
    };
  }

  async function saveStudioSnapshot(state: StoredConnectionsState): Promise<void> {
    if (!studioBridge.value || syncingFromStudioBridge) return;
    try {
      await studioBridge.value.saveConnections({
        profiles: state.profiles,
        activeProfileId: state.activeProfileId,
        activeDatabase: state.activeDatabase,
      });
    } catch {
      // 磁盘连接库同步失败时保留浏览器态，避免阻断当前工作流。
    }
  }

  watch(
    [profiles, activeProfileId, activeDatabase],
    () => {
      const state = currentState();
      saveState(state);
      void saveStudioSnapshot(state);
    },
    { deep: true },
  );

  return {
    profiles,
    activeProfileId,
    activeDatabase,
    activeProfile,
    activeBaseUrl,
    activeDisplayUrl,
    studioBridgeAvailable,
    studioBridgeManifest,
    studioManagedServerStatus,
    setActiveProfile,
    setActiveDatabase,
    upsertRemoteProfile,
    removeProfile,
    setManagedLocalBaseUrl,
    connectStudioBridge,
    refreshStudioServerStatus,
    startStudioManagedServer,
    stopStudioManagedServer,
  };
});
