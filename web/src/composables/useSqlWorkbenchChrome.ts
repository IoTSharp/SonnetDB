import { computed, ref, type WritableComputedRef } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import type { DropdownOption } from 'naive-ui';
import type { useAuthStore } from '@/stores/auth';
import type { useConnectionsStore, ConnectionProfile } from '@/stores/connections';
import type { WorkbenchTool } from '@/utils/sqlWorkbench';
import { CONTROL_PLANE_KEY } from '@/stores/sqlConsole';

type AuthStore = ReturnType<typeof useAuthStore>;
type ConnectionsStore = ReturnType<typeof useConnectionsStore>;

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

  const activeWorkbenchTool = computed<WorkbenchTool>(() => {
    if (route.query.tool === 'trajectory') return 'trajectory';
    if (route.query.tool === 'kv' || route.query.model === 'kv') return 'kv';
    if (route.query.tool === 'mq' || route.query.model === 'mq') return 'mq';
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
    const profileOptions = connections.profiles.map((profile) => ({
      label: `${profile.name} · ${displayConnectionProfile(profile)}`,
      key: `connection:${profile.id}`,
    }));

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

  function setWorkbenchTool(tool: WorkbenchTool): void {
    if (activeWorkbenchTool.value === tool) return;
    void router.replace({
      name: 'sql',
      query: tool === 'trajectory'
        ? { tool: 'trajectory' }
        : tool === 'kv'
          ? { tool: 'kv' }
        : tool === 'mq'
          ? { tool: 'mq' }
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

  return {
    showConnectionDialog,
    connectionForm,
    activeWorkbenchTool,
    connectionLabel,
    accessBadges,
    connectionOptions,
    canSaveConnection,
    setWorkbenchTool,
    openConnectionDialog,
    saveConnection,
    onConnectionSelect,
  };
}

function displayConnectionProfile(profile: ConnectionProfile): string {
  return profile.baseUrl === '/'
    ? (typeof window === 'undefined' ? 'local' : window.location.host)
    : profile.baseUrl;
}
