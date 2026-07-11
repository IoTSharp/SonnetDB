const BridgeUrlQueryKey = 'studioBridgeUrl';
const BridgeTokenQueryKey = 'studioBridgeToken';
const BridgeUrlStorageKey = 'sndb.studio.bridge.url';
const BridgeTokenStorageKey = 'sndb.studio.bridge.token';

export interface StudioBridgeManifest {
  mode: string;
  version: string;
  serverUrl: string;
  managedServerUrl: string;
  dataRoot: string;
  capabilities: string[];
  menu: StudioMenuItem[];
  managedServer: StudioManagedServerStatus;
}

export interface StudioMenuItem {
  id: string;
  label: string;
  command: string;
}

export interface StudioConnectionLibrarySnapshot {
  profiles: StudioConnectionProfile[];
  activeProfileId: string;
  activeDatabase: string;
}

export interface StudioConnectionProfile {
  id: string;
  name: string;
  kind: 'managed-local' | 'remote';
  baseUrl: string;
  defaultDatabase: string;
  tokenMode: 'current-session';
  createdAt: number;
  updatedAt: number;
}

export interface StudioFileDialogFilter {
  name: string;
  extensions: string[];
}

export interface StudioOpenFileResult {
  canceled: boolean;
  fileName: string | null;
  content: string | null;
  error: string | null;
}

export interface StudioSaveFileResult {
  canceled: boolean;
  fileName: string | null;
  error: string | null;
}

export interface StudioOpenBinaryFileResult {
  canceled: boolean;
  fileName: string | null;
  content: Blob | null;
}

export interface StudioSelectDirectoryResult {
  canceled: boolean;
  path: string | null;
  error: string | null;
}

export interface StudioManagedServerStatus {
  isRunning: boolean;
  startedByStudio: boolean;
  healthy: boolean;
  processId: number | null;
  url: string;
  dataRoot: string;
  error: string | null;
}

export interface StudioNativeBridgeClient {
  manifest: StudioBridgeManifest;
  refreshManifest(): Promise<StudioBridgeManifest>;
  loadConnections(): Promise<StudioConnectionLibrarySnapshot>;
  saveConnections(snapshot: StudioConnectionLibrarySnapshot): Promise<StudioConnectionLibrarySnapshot>;
  openTextFile(options: {
    title?: string;
    filters?: StudioFileDialogFilter[];
    maxBytes?: number;
  }): Promise<StudioOpenFileResult>;
  saveTextFile(options: {
    title?: string;
    suggestedName?: string;
    content: string;
    contentType?: string;
    filters?: StudioFileDialogFilter[];
  }): Promise<StudioSaveFileResult>;
  openBinaryFile(options?: { title?: string; filters?: StudioFileDialogFilter[] }): Promise<StudioOpenBinaryFileResult>;
  saveBinaryFile(options: { title?: string; suggestedName?: string; content: Blob }): Promise<StudioSaveFileResult>;
  selectDirectory(options?: { title?: string; initialPath?: string }): Promise<StudioSelectDirectoryResult>;
  getServerStatus(): Promise<StudioManagedServerStatus>;
  startServer(options?: { dataRoot?: string; url?: string }): Promise<StudioManagedServerStatus>;
  stopServer(options?: { dataRoot?: string; url?: string }): Promise<StudioManagedServerStatus>;
}

let cachedClient: StudioNativeBridgeClient | null | undefined;

export async function getStudioNativeBridge(): Promise<StudioNativeBridgeClient | null> {
  if (cachedClient !== undefined) return cachedClient;

  const config = readBridgeConfig();
  if (!config) {
    cachedClient = null;
    return null;
  }

  try {
    const client = createClient(config);
    const manifest = await client.refreshManifest();
    cachedClient = { ...client, manifest };
    return cachedClient;
  } catch {
    cachedClient = null;
    return null;
  }
}

export function currentStudioNativeBridge(): StudioNativeBridgeClient | null {
  return cachedClient ?? null;
}

function createClient(config: { baseUrl: string; token: string }): StudioNativeBridgeClient {
  const request = async <T>(path: string, init: RequestInit = {}, timeoutMs = 5000): Promise<T> => {
    const controller = new AbortController();
    const timer = window.setTimeout(() => controller.abort(), timeoutMs);
    try {
      const headers = new Headers(init.headers);
      headers.set('X-SonnetDB-Studio-Bridge-Token', config.token);
      if (init.body && !headers.has('Content-Type')) {
        headers.set('Content-Type', 'application/json');
      }

      const response = await fetch(`${config.baseUrl}${path}`, {
        ...init,
        headers,
        signal: controller.signal,
      });
      if (!response.ok) {
        throw new Error(`Studio bridge request failed: ${response.status}`);
      }
      return await response.json() as T;
    } finally {
      window.clearTimeout(timer);
    }
  };

  const rawRequest = async (path: string, init: RequestInit = {}, timeoutMs = 120000): Promise<Response> => {
    const controller = new AbortController();
    const timer = window.setTimeout(() => controller.abort(), timeoutMs);
    try {
      const headers = new Headers(init.headers);
      headers.set('X-SonnetDB-Studio-Bridge-Token', config.token);
      const response = await fetch(`${config.baseUrl}${path}`, { ...init, headers, signal: controller.signal });
      if (!response.ok && response.status !== 204) {
        throw new Error(`Studio bridge request failed: ${response.status}`);
      }
      return response;
    } finally {
      window.clearTimeout(timer);
    }
  };

  return {
    manifest: placeholderManifest(),
    refreshManifest: () => request<StudioBridgeManifest>('/manifest', {}, 3000),
    loadConnections: () => request<StudioConnectionLibrarySnapshot>('/connections'),
    saveConnections: (snapshot) => request<StudioConnectionLibrarySnapshot>('/connections', {
      method: 'PUT',
      body: JSON.stringify(snapshot),
    }),
    openTextFile: (options) => request<StudioOpenFileResult>('/dialogs/open-file', {
      method: 'POST',
      body: JSON.stringify({
        title: options.title ?? null,
        filters: options.filters ?? null,
        maxBytes: options.maxBytes ?? null,
      }),
    }, 120000),
    saveTextFile: (options) => request<StudioSaveFileResult>('/dialogs/save-file', {
      method: 'POST',
      body: JSON.stringify({
        title: options.title ?? null,
        suggestedName: options.suggestedName ?? null,
        content: options.content,
        contentType: options.contentType ?? null,
        filters: options.filters ?? null,
      }),
    }, 120000),
    openBinaryFile: async (options = {}) => {
      const response = await rawRequest('/dialogs/open-binary', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ title: options.title ?? null, filters: options.filters ?? null }),
      });
      if (response.status === 204) return { canceled: true, fileName: null, content: null };
      return {
        canceled: false,
        fileName: fileNameFromContentDisposition(response.headers.get('content-disposition')),
        content: await response.blob(),
      };
    },
    saveBinaryFile: (options) => {
      const params = new URLSearchParams();
      if (options.title) params.set('title', options.title);
      if (options.suggestedName) params.set('suggestedName', options.suggestedName);
      return request<StudioSaveFileResult>(`/dialogs/save-binary?${params.toString()}`, {
        method: 'POST',
        headers: { 'Content-Type': options.content.type || 'application/octet-stream' },
        body: options.content,
      }, 300000);
    },
    selectDirectory: (options = {}) => request<StudioSelectDirectoryResult>('/dialogs/select-directory', {
      method: 'POST',
      body: JSON.stringify({ title: options.title ?? null, initialPath: options.initialPath ?? null }),
    }, 120000),
    getServerStatus: () => request<StudioManagedServerStatus>('/server/status'),
    startServer: (options = {}) => request<StudioManagedServerStatus>('/server/start', {
      method: 'POST',
      body: JSON.stringify({
        dataRoot: options.dataRoot ?? null,
        url: options.url ?? null,
      }),
    }, 15000),
    stopServer: (options = {}) => request<StudioManagedServerStatus>('/server/stop', {
      method: 'POST',
      body: JSON.stringify({
        dataRoot: options.dataRoot ?? null,
        url: options.url ?? null,
      }),
    }, 10000),
  };
}

function fileNameFromContentDisposition(value: string | null): string | null {
  if (!value) return null;
  const encoded = /filename\*=UTF-8''([^;]+)/iu.exec(value)?.[1];
  if (encoded) return decodeURIComponent(encoded);
  const plain = /filename="?([^";]+)"?/iu.exec(value)?.[1];
  return plain ?? null;
}

function readBridgeConfig(): { baseUrl: string; token: string } | null {
  if (typeof window === 'undefined') return null;

  const url = new URL(window.location.href);
  const urlBridge = url.searchParams.get(BridgeUrlQueryKey);
  const urlToken = url.searchParams.get(BridgeTokenQueryKey);
  if (urlBridge && urlToken) {
    sessionStorage.setItem(BridgeUrlStorageKey, urlBridge.replace(/\/+$/u, ''));
    sessionStorage.setItem(BridgeTokenStorageKey, urlToken);
    url.searchParams.delete(BridgeUrlQueryKey);
    url.searchParams.delete(BridgeTokenQueryKey);
    window.history.replaceState(window.history.state, document.title, `${url.pathname}${url.search}${url.hash}`);
  }

  const baseUrl = sessionStorage.getItem(BridgeUrlStorageKey);
  const token = sessionStorage.getItem(BridgeTokenStorageKey);
  return baseUrl && token ? { baseUrl, token } : null;
}

function placeholderManifest(): StudioBridgeManifest {
  return {
    mode: 'browser',
    version: '0.0.0',
    serverUrl: '',
    managedServerUrl: '',
    dataRoot: '',
    capabilities: [],
    menu: [],
    managedServer: {
      isRunning: false,
      startedByStudio: false,
      healthy: false,
      processId: null,
      url: '',
      dataRoot: '',
      error: null,
    },
  };
}
