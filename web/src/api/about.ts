import type { AxiosInstance } from 'axios';

export interface CpuAboutInfo {
  name: string;
  logicalProcessors: number;
  speedMHz: number | null;
}

export interface MemoryAboutInfo {
  totalBytes: number;
  availableBytes: number;
  source: string;
}

export interface DiskAboutInfo {
  name: string;
  format: string;
  totalBytes: number;
  availableBytes: number;
}

export interface GpuAboutInfo {
  name: string;
  driver: string | null;
  pciId: string | null;
}

export interface SystemAboutResponse {
  serverVersion: string;
  hostName: string;
  osDescription: string;
  osVersion: string;
  osArchitecture: string;
  processArchitecture: string;
  runtimeDescription: string;
  cpu: CpuAboutInfo;
  memory: MemoryAboutInfo;
  disks: DiskAboutInfo[];
  gpus: GpuAboutInfo[];
  capturedAtUtc: string;
}

export async function fetchSystemAbout(api: AxiosInstance): Promise<SystemAboutResponse> {
  const response = await api.get<SystemAboutResponse>('/v1/system/about');
  return response.data;
}
