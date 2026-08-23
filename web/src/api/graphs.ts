import type { AxiosInstance } from 'axios';

export interface GraphInfo {
  name: string;
  storageId: string;
  recordFormatVersion: number;
}

export interface GraphValue {
  kind: number;
  int64?: number | null;
  float64?: number | null;
  boolean?: boolean | null;
  string?: string | null;
  dateTime?: string | null;
  blobBase64?: string | null;
  json?: string | null;
}

export interface GraphProperty {
  propertyId: number;
  value: GraphValue;
}

export interface GraphVertex {
  id: number;
  elementVersion: number;
  labels: number[];
  properties: GraphProperty[];
}

export interface GraphEdge {
  id: number;
  elementVersion: number;
  sourceId: number;
  targetId: number;
  labelId: number;
  properties: GraphProperty[];
}

export interface GraphLabelStatistic {
  labelId: number;
  elementCount: number;
}

export interface GraphIndexStatistic {
  elementType: string;
  labelId: number;
  propertyId: number;
  valueKind: string;
  entryCount: number;
}

export interface GraphDegreeBucket {
  degree: number;
  vertexCount: number;
}

export interface GraphSlowTraversal {
  timestampMs: number;
  fingerprint: string;
  elapsedMs: number;
  rowCount: number;
  accessPath?: string | null;
  fallbackReason?: string | null;
  sql: string;
}

export interface GraphOperationsCapabilities {
  schemaAndIndexes: boolean;
  degreeHistogram: boolean;
  slowTraversalDiagnostics: boolean;
  boundedVisualization: boolean;
  restrictedEditing: boolean;
  jsonImportExport: boolean;
  stagedMaintenance: boolean;
  audit: boolean;
}

export interface GraphOperationsOverview {
  graph: GraphInfo;
  snapshotSequence: number;
  vertexCount: number;
  edgeCount: number;
  labels: GraphLabelStatistic[];
  indexes: GraphIndexStatistic[];
  degreeHistogram: GraphDegreeBucket[];
  slowTraversals: GraphSlowTraversal[];
  slowTraversalSource: string;
  capabilities: GraphOperationsCapabilities;
}

export interface GraphVisualization {
  snapshotSequence: number;
  truncated: boolean;
  vertices: GraphVertex[];
  edges: GraphEdge[];
}

export interface GraphUpsertVertexRequest {
  id: number;
  expectedElementVersion: number;
  labels: number[];
  properties: GraphProperty[];
  uniquePropertyIds: number[];
  requestId: string;
}

export interface GraphUpsertEdgeRequest {
  id: number;
  expectedElementVersion: number;
  sourceId: number;
  targetId: number;
  labelId: number;
  properties: GraphProperty[];
  uniquePropertyIds: number[];
  requestId: string;
}

export interface GraphMutationResponse {
  sequence: number;
  isDuplicate: boolean;
}

export interface GraphImportRequest {
  requestId: string;
  vertices: Array<Omit<GraphUpsertVertexRequest, 'requestId'>>;
  edges: Array<Omit<GraphUpsertEdgeRequest, 'requestId'>>;
  nodes?: Array<Omit<GraphUpsertVertexRequest, 'requestId'>>;
  relationships?: Array<Omit<GraphUpsertEdgeRequest, 'requestId'>>;
}

export interface GraphImportResponse extends GraphMutationResponse {
  vertexCount: number;
  edgeCount: number;
}

export type GraphMaintenanceAction = 'RepairRebuild' | 'Checkpoint' | 'Compact';

export interface GraphMaintenanceStageRequest {
  action: GraphMaintenanceAction;
  compactOnCompletion: boolean;
  maxWorkUnits: number;
}

export interface GraphMaintenanceExecution {
  action: GraphMaintenanceAction;
  isComplete: boolean;
  operationId?: string | null;
  phase?: string | null;
  sequence: number;
  scannedRecords: number;
  repairedEntries: number;
  removedEntries: number;
  workUnits: number;
}

export interface GraphMaintenanceApproval {
  approvalId: string;
  occurredAtUtc: string;
  database: string;
  graph: string;
  action: GraphMaintenanceAction;
  state: string;
  principal: string;
  expiresAtUtc: string;
  compactOnCompletion: boolean;
  maxWorkUnits: number;
  result?: GraphMaintenanceExecution | null;
  errorCode?: string | null;
  reason?: string | null;
}

export interface GraphExportDocument {
  snapshotSequence: number;
  vertices: GraphVertex[];
  edges: GraphEdge[];
  truncated: boolean;
  elementCount: number;
}

function graphBase(db: string, graph?: string): string {
  const root = `/v1/db/${encodeURIComponent(db)}/graphs`;
  return graph ? `${root}/${encodeURIComponent(graph)}` : root;
}

export async function fetchGraphs(api: AxiosInstance, db: string): Promise<GraphInfo[]> {
  const response = await api.get<GraphInfo[]>(graphBase(db));
  return Array.isArray(response.data) ? response.data : [];
}

export async function fetchGraphOperationsOverview(
  api: AxiosInstance,
  db: string,
  graph: string,
): Promise<GraphOperationsOverview> {
  const response = await api.get<GraphOperationsOverview>(`${graphBase(db, graph)}/operations/overview`);
  return response.data;
}

export async function fetchGraphVisualization(
  api: AxiosInstance,
  db: string,
  graph: string,
  limit: number,
): Promise<GraphVisualization> {
  const response = await api.get<GraphVisualization>(`${graphBase(db, graph)}/operations/visualization`, {
    params: { limit },
  });
  return response.data;
}

export async function fetchGraphVertex(api: AxiosInstance, db: string, graph: string, id: number): Promise<GraphVertex> {
  const response = await api.get<GraphVertex>(`${graphBase(db, graph)}/vertices/${id}`);
  return response.data;
}

export async function fetchGraphEdge(api: AxiosInstance, db: string, graph: string, id: number): Promise<GraphEdge> {
  const response = await api.get<GraphEdge>(`${graphBase(db, graph)}/edges/${id}`);
  return response.data;
}

export async function upsertGraphVertex(
  api: AxiosInstance,
  db: string,
  graph: string,
  request: GraphUpsertVertexRequest,
): Promise<GraphMutationResponse> {
  const response = await api.put<GraphMutationResponse>(`${graphBase(db, graph)}/vertices/${request.id}`, request);
  return response.data;
}

export async function upsertGraphEdge(
  api: AxiosInstance,
  db: string,
  graph: string,
  request: GraphUpsertEdgeRequest,
): Promise<GraphMutationResponse> {
  const response = await api.put<GraphMutationResponse>(`${graphBase(db, graph)}/edges/${request.id}`, request);
  return response.data;
}

export async function deleteGraphElement(
  api: AxiosInstance,
  db: string,
  graph: string,
  kind: 'vertex' | 'edge',
  id: number,
  expectedElementVersion: number,
): Promise<GraphMutationResponse> {
  const path = kind === 'vertex' ? 'vertices' : 'edges';
  const response = await api.delete<GraphMutationResponse>(`${graphBase(db, graph)}/${path}/${id}`, {
    data: { expectedElementVersion, requestId: crypto.randomUUID() },
  });
  return response.data;
}

export async function importGraphJson(
  api: AxiosInstance,
  db: string,
  graph: string,
  request: GraphImportRequest,
): Promise<GraphImportResponse> {
  const response = await api.post<GraphImportResponse>(`${graphBase(db, graph)}/import`, request);
  return response.data;
}

export async function downloadGraphExport(
  api: AxiosInstance,
  db: string,
  graph: string,
  maxElements: number,
): Promise<Blob> {
  const response = await api.get<Blob>(`${graphBase(db, graph)}/operations/export`, {
    params: { maxElements },
    responseType: 'blob',
  });
  return response.data;
}

export async function stageGraphMaintenance(
  api: AxiosInstance,
  db: string,
  graph: string,
  request: GraphMaintenanceStageRequest,
): Promise<GraphMaintenanceApproval> {
  const response = await api.post<GraphMaintenanceApproval>(`${graphBase(db, graph)}/maintenance/stage`, request);
  return response.data;
}

export async function approveGraphMaintenance(
  api: AxiosInstance,
  db: string,
  graph: string,
  approvalId: string,
): Promise<GraphMaintenanceApproval> {
  const response = await api.post<GraphMaintenanceApproval>(`${graphBase(db, graph)}/maintenance/${encodeURIComponent(approvalId)}/approve`);
  return response.data;
}

export async function rejectGraphMaintenance(
  api: AxiosInstance,
  db: string,
  graph: string,
  approvalId: string,
  reason?: string,
): Promise<GraphMaintenanceApproval> {
  const response = await api.post<GraphMaintenanceApproval>(
    `${graphBase(db, graph)}/maintenance/${encodeURIComponent(approvalId)}/reject`,
    { reason: reason?.trim() || null },
  );
  return response.data;
}

export async function fetchGraphMaintenanceAudit(
  api: AxiosInstance,
  db: string,
  graph: string,
  limit = 200,
): Promise<GraphMaintenanceApproval[]> {
  const response = await api.get<{ items: GraphMaintenanceApproval[] }>(`${graphBase(db, graph)}/maintenance/audit`, {
    params: { limit },
  });
  return Array.isArray(response.data.items) ? response.data.items : [];
}
