import { defineStore } from 'pinia';
import { computed, ref, watch } from 'vue';

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

function localProfile(): ConnectionProfile {
  const ts = now();
  return {
    id: LocalProfileId,
    name: 'Managed Local',
    kind: 'managed-local',
    baseUrl: '/',
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

  const activeProfile = computed(() =>
    profiles.value.find((profile) => profile.id === activeProfileId.value) ?? profiles.value[0] ?? localProfile());

  const activeBaseUrl = computed(() => normalizeBaseUrl(activeProfile.value.baseUrl));

  const activeDisplayUrl = computed(() => {
    const baseUrl = activeBaseUrl.value;
    return baseUrl === '/' ? currentOrigin() : baseUrl;
  });

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

  watch(
    [profiles, activeProfileId, activeDatabase],
    () => saveState({
      profiles: profiles.value,
      activeProfileId: activeProfileId.value,
      activeDatabase: activeDatabase.value,
    }),
    { deep: true },
  );

  return {
    profiles,
    activeProfileId,
    activeDatabase,
    activeProfile,
    activeBaseUrl,
    activeDisplayUrl,
    setActiveProfile,
    setActiveDatabase,
    upsertRemoteProfile,
    removeProfile,
  };
});
