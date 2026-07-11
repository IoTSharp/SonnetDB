import type { AxiosInstance } from 'axios';

export type HealthCheckStatus = 'healthy' | 'degraded' | 'unhealthy' | 'unknown';

export interface ReadinessEntry {
  status: HealthCheckStatus;
  description: string;
  duration: string;
  tags: string[];
}

export interface ReadinessReport {
  status: HealthCheckStatus;
  totalDuration: string;
  entries: Record<string, ReadinessEntry>;
}

export async function loadReadiness(api: AxiosInstance): Promise<ReadinessReport> {
  const response = await api.get('/healthz/ready', { validateStatus: () => true });
  const payload = response.data as {
    status?: unknown;
    totalDuration?: unknown;
    entries?: unknown;
  } | null;

  if (!payload || typeof payload !== 'object' || typeof payload.entries !== 'object' || payload.entries === null) {
    throw new Error(`Readiness 返回了无效响应（HTTP ${response.status}）。`);
  }

  const entries: Record<string, ReadinessEntry> = {};
  for (const [name, raw] of Object.entries(payload.entries)) {
    if (!raw || typeof raw !== 'object') continue;
    const entry = raw as Record<string, unknown>;
    entries[name] = {
      status: normalizeStatus(entry.status),
      description: typeof entry.description === 'string' ? entry.description : '',
      duration: typeof entry.duration === 'string' ? entry.duration : '',
      tags: Array.isArray(entry.tags) ? entry.tags.map(String) : [],
    };
  }

  return {
    status: normalizeStatus(payload.status),
    totalDuration: typeof payload.totalDuration === 'string' ? payload.totalDuration : '',
    entries,
  };
}

function normalizeStatus(value: unknown): HealthCheckStatus {
  const normalized = String(value ?? '').toLowerCase();
  if (normalized === 'healthy' || normalized === 'degraded' || normalized === 'unhealthy') {
    return normalized;
  }
  return 'unknown';
}
