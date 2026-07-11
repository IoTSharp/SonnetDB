import { computed, h, ref, type WritableComputedRef } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import type { DropdownOption } from 'naive-ui';
import { Circle, CircleCheck, CircleX, LoaderCircle } from 'lucide-vue-next';
import type { useAuthStore } from '@/stores/auth';
import type { useConnectionsStore, ConnectionProfile } from '@/stores/connections';
import type { WorkbenchTool } from '@/utils/sqlWorkbench';
import { CONTROL_PLANE_KEY } from '@/stores/sqlConsole';

type AuthStore = ReturnType<typeof useAuthStore>;
type ConnectionsStore = ReturnType<typeof useConnectionsStore>;
const NativeDataRootStorageKey = 'sndb.studio.managed.data-root.v1';

export interface AccessBadge {
  label: string;
  type: 'default' | 'info' | 'success' | 'warning' | 'error';
}

export interface SqlWorkbenchChromeOptions {
  auth: AuthStore;
  connections: ConnectionsStore;
  targetDb: WritableComputedRef<string>;
}

export function useSqlWorkbenchChrome(options: SqlWorkbenchChromeOptions) {
  const { auth, connections, targetDb } = options;
  const route = useRoute();
  const router = useRouter();

  const showConnectionDialog = ref(false);
  const connectionForm = ref({ name: '', baseUrl: '', defaultDatabase: '' });
  const nativeServerBusy = ref(false);
  const nativeDataRoot = ref(readNativeDataRoot());
  const connectionHealthBusy = ref(false);

  const activeWorkbenchTool = computed<WorkbenchTool>(() => {
    if (route.query.tool === 'trajectory') return 'trajectory';
    if (route.query.tool === 'document' || route.query.model === 'document') return 'document';
    if (route.query.tool === 'kv' || route.query.model === 'kv') return 'kv';
    if (route.query.tool === 'mq' || route.query.model === 'mq') return 'mq';
    if (route.query.tool === 'vector' || route.query.model === 'vector') return 'vector';
    if (route.query.tool === 'fulltext' || route.query.model === 'fulltext') return 'fulltext';
    if (route.query.tool === 'bucket' || route.query.model === 'bucket') return 'bucket';
    if (route.query.tool === 'table' || route.query.model === 'table') return 'table';
    return 'sql';
  });

  const connectionLabel = computed(() => {
    const db = targetDb.value === CONTROL_PLANE_KEY ? 'system' : (targetDb.value || 'public');
    return `${connections.activeDisplayUrl}/${db}`;
  });

  const accessBadges = computed<AccessBadge[]>(() => {
    const badges: AccessBadge[] = [
      { label: 'explain', type: 'info' },
      { label: 'read', type: 'success' },
      { label: 'execute', type: 'warning' },
      { label: 'export', type: 'default' },
      { label: 'write', type: 'warning' },
    ];

    if (auth.isSuperuser) {
      badges.push(
        { label: 'ddl', type: 'warning' },
        { label: 'admin', type: 'info' },
      );
    }

    return badges;
  });

  const connectionOptions = computed<DropdownOption[]>(() => {
    const profileOptions = connections.profiles.map((profile) => {
      const health = connections.healthFor(profile.id);
      const icon = health.state === 'healthy'
        ? CircleCheck
        : health.state === 'unhealthy'
          ? CircleX
          : health.state === 'checking'
            ? LoaderCircle
            : Circle;
      const color = health.state === 'healthy'
        ? '#138a52'
        : health.state === 'unhealthy'
          ? '#c43832'
          : '#7a8493';
      return {
        label: `${profile.name} · ${displayConnectionProfile(profile)} · ${health.message}`,
        key: `connection:${profile.id}`,
        icon: () => h(icon, { size: 15, color, 'stroke-width': 2 }),
      };
    });

    return [
      ...profileOptions,
      { type: 'divider', key: 'connection-divider' },
      { label: 'New remote connection', key: 'connection:new' },
    ];
  });

  const canSaveConnection = computed(() => {
    const baseUrl = connectionForm.value.baseUrl.trim();
    return connectionForm.value.name.trim().length > 0
      && /^https?:\/\/[^\s/$.?#].[^\s]*$/i.test(baseUrl);
  });

  const studioBridgeAvailable = computed(() => connections.studioBridgeAvailable);
  const nativeServerStatus = computed(() => connections.studioManagedServerStatus);

  function setWorkbenchTool(tool: WorkbenchTool): void {
    if (activeWorkbenchTool.value === tool) return;
    void router.replace({
      name: 'sql',
      query: tool === 'trajectory'
        ? { tool: 'trajectory' }
        : tool === 'document'
          ? { tool: 'document' }
        : tool === 'kv'
          ? { tool: 'kv' }
        : tool === 'mq'
          ? { tool: 'mq' }
        : tool === 'vector'
          ? { tool: 'vector' }
        : tool === 'fulltext'
          ? { tool: 'fulltext' }
        : tool === 'bucket'
          ? { tool: 'bucket' }
        : tool === 'table'
          ? { tool: 'table' }
          : {},
    });
  }

  function openConnectionDialog(): void {
    connectionForm.value = {
      name: '',
      baseUrl: '',
      defaultDatabase: targetDb.value === CONTROL_PLANE_KEY ? '' : targetDb.value,
    };
    showConnectionDialog.value = true;
  }

  function saveConnection(): void {
    if (!canSaveConnection.value) return;
    const profile = connections.upsertRemoteProfile({
      name: connectionForm.value.name,
      baseUrl: connectionForm.value.baseUrl,
      defaultDatabase: connectionForm.value.defaultDatabase,
    });
    connections.setActiveProfile(profile.id);
    showConnectionDialog.value = false;
  }

  function onConnectionSelect(key: string | number): void {
    const value = String(key);
    if (value === 'connection:new') {
      openConnectionDialog();
      return;
    }
    if (!value.startsWith('connection:')) return;
    connections.setActiveProfile(value.slice('connection:'.length));
  }

  async function refreshNativeServerStatus(): Promise<void> {
    if (!connections.studioBridgeAvailable) return;
    nativeServerBusy.value = true;
    try {
      const status = await connections.refreshStudioServerStatus();
      if (status?.dataRoot && (status.startedByStudio || !nativeDataRoot.value)) setNativeDataRoot(status.dataRoot);
    } finally {
      nativeServerBusy.value = false;
    }
  }

  async function refreshConnectionHealth(): Promise<void> {
    connectionHealthBusy.value = true;
    try {
      await connections.checkAllProfilesHealth();
    } finally {
      connectionHealthBusy.value = false;
    }
  }

  async function startNativeServer(dataRoot?: string): Promise<void> {
    if (!connections.studioBridgeAvailable) return;
    nativeServerBusy.value = true;
    try {
      const selectedRoot = dataRoot?.trim() || nativeDataRoot.value.trim() || undefined;
      const status = await connections.startStudioManagedServer(selectedRoot);
      if (status?.dataRoot) setNativeDataRoot(status.dataRoot);
      if (status?.healthy) {
        connections.setActiveProfile('managed-local');
        auth.setApiBaseUrl(connections.activeBaseUrl);
      }
    } finally {
      nativeServerBusy.value = false;
    }
  }

  async function chooseNativeDataRoot(): Promise<void> {
    const selected = await connections.selectStudioDirectory('选择 SonnetDB data root', nativeDataRoot.value);
    if (selected) setNativeDataRoot(selected);
  }

  function setNativeDataRoot(value: string): void {
    nativeDataRoot.value = value;
    try {
      localStorage.setItem(NativeDataRootStorageKey, value);
    } catch {
      // Studio WebView 禁用存储时仍保留本次会话状态。
    }
  }

  async function stopNativeServer(): Promise<void> {
    if (!connections.studioBridgeAvailable) return;
    nativeServerBusy.value = true;
    try {
      await connections.stopStudioManagedServer();
    } finally {
      nativeServerBusy.value = false;
    }
  }

  return {
    showConnectionDialog,
    connectionForm,
    studioBridgeAvailable,
    nativeServerStatus,
    nativeServerBusy,
    nativeDataRoot,
    connectionHealthBusy,
    activeWorkbenchTool,
    connectionLabel,
    accessBadges,
    connectionOptions,
    canSaveConnection,
    setWorkbenchTool,
    openConnectionDialog,
    saveConnection,
    onConnectionSelect,
    refreshNativeServerStatus,
    refreshConnectionHealth,
    startNativeServer,
    chooseNativeDataRoot,
    setNativeDataRoot,
    stopNativeServer,
  };
}

function readNativeDataRoot(): string {
  try {
    return localStorage.getItem(NativeDataRootStorageKey) ?? '';
  } catch {
    return '';
  }
}

function displayConnectionProfile(profile: ConnectionProfile): string {
  return profile.baseUrl === '/'
    ? (typeof window === 'undefined' ? 'local' : window.location.host)
    : profile.baseUrl;
}
