import type { AxiosInstance, AxiosResponse } from 'axios';

export interface ModbusSource {
  name: string;
  host: string;
  port: number;
  unitId: number;
  configuredEnabled: boolean;
  runtimeEnabled: boolean;
  health: string;
  lastErrorCode?: string | null;
  catalogRevision: number;
}

export interface ModbusEndpoint {
  name: string;
  bindAddress: string;
  port: number;
  unitId: number;
  writePolicy: string;
  allowlist: string[];
  maxConnections: number;
  configuredEnabled: boolean;
  runtimeEnabled: boolean;
  health: string;
  lastErrorCode?: string | null;
  catalogRevision: number;
}

export interface ModbusMapping {
  column: string;
  area: string;
  declaredAddress: number;
  pduAddress: number;
  registerCount: number;
  wireType: string;
  access: string;
}

export interface ModbusBinding {
  table: string;
  direction: string;
  target: string;
  rowKey?: number | null;
  tableMode: string;
  approvedWriteAction: string;
  mappings: ModbusMapping[];
}

export interface ModbusOverview {
  runtimeEnabled: boolean;
  sources: ModbusSource[];
  endpoints: ModbusEndpoint[];
  bindings: ModbusBinding[];
}

export interface ModbusEndpointWrite {
  requestId: string;
  occurredAtUtc: string;
  eventType: string;
  state: string;
  principal: string;
  endpoint: string;
  remoteEndpoint: string;
  unitId: number;
  transactionId: number;
  functionCode: string;
  area: string;
  declaredAddress: number;
  pduAddress: number;
  rawValues: string[];
  decodedValue?: string | null;
  table?: string | null;
  column?: string | null;
  rowKey?: number | null;
  catalogRevision: number;
  approvedWriteAction: string;
  expiresAtUtc?: string | null;
  errorCode?: string | null;
  reason?: string | null;
}

interface ModbusEndpointWriteList {
  items: ModbusEndpointWrite[];
}

export async function getModbusOverview(api: AxiosInstance, database: string): Promise<ModbusOverview> {
  const response = await api.get<ModbusOverview>(path(database, ''));
  return response.data;
}

export async function listModbusWrites(
  api: AxiosInstance,
  database: string,
  state = '',
  limit = 200,
): Promise<ModbusEndpointWrite[]> {
  const response = await api.get<ModbusEndpointWriteList>(path(database, '/writes'), {
    params: { state: state || undefined, limit },
  });
  return response.data.items ?? [];
}

export async function listModbusWriteAudit(
  api: AxiosInstance,
  database: string,
  limit = 200,
): Promise<ModbusEndpointWrite[]> {
  const response = await api.get<ModbusEndpointWriteList>(path(database, '/write-audit'), {
    params: { limit },
  });
  return response.data.items ?? [];
}

export async function approveModbusWrite(
  api: AxiosInstance,
  database: string,
  requestId: string,
): Promise<ModbusEndpointWrite> {
  const response = await api.post<ModbusEndpointWrite>(
    path(database, `/writes/${encodeURIComponent(requestId)}/approve`),
  );
  return response.data;
}

export async function rejectModbusWrite(
  api: AxiosInstance,
  database: string,
  requestId: string,
  reason?: string,
): Promise<ModbusEndpointWrite> {
  const response = await api.post<ModbusEndpointWrite>(
    path(database, `/writes/${encodeURIComponent(requestId)}/reject`),
    reason ? { reason } : {},
  );
  return response.data;
}

export function modbusApiError(error: unknown): string {
  const response = (error as { response?: AxiosResponse<{ message?: string }> })?.response;
  return response?.data?.message ?? (error instanceof Error ? error.message : 'Modbus 请求失败');
}

function path(database: string, suffix: string): string {
  return `/v1/db/${encodeURIComponent(database)}/modbus${suffix}`;
}
