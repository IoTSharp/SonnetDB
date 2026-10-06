import type { AxiosInstance, AxiosResponse } from 'axios';

export interface ObjectBucketResponse {
  name: string;
  purpose: string;
  createdUtc: string;
  updatedUtc: string;
}

export interface ObjectInfoResponse {
  bucket: string;
  key: string;
  versionId: string;
  contentType: string;
  sizeBytes: number;
  eTag: string;
  sha256: string;
  isDeleteMarker: boolean;
  createdUtc: string;
  updatedUtc: string;
  metadata: Record<string, string>;
  tags: Record<string, string>;
}

export interface ObjectListResponse {
  bucket: string;
  prefix: string;
  maxKeys: number;
  continuationToken?: string | null;
  nextContinuationToken?: string | null;
  isTruncated: boolean;
  objects: ObjectInfoResponse[];
}

export interface ObjectDeleteResultResponse {
  key: string;
  versionId: string;
  deleteMarker: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
}

export interface ObjectDeleteManyResponse {
  bucket: string;
  deleted: ObjectDeleteResultResponse[];
}

export interface ObjectVersionListResponse {
  bucket: string;
  key?: string | null;
  versions: ObjectInfoResponse[];
}

export interface ObjectCopyResponse {
  eTag: string;
  sha256: string;
  versionId: string;
}

export interface ObjectTagsResponse {
  tags: Record<string, string>;
}

export interface MultipartUploadCreateResponse {
  bucket: string;
  key: string;
  uploadId: string;
  contentType: string;
  initiatedUtc: string;
  expiresUtc: string;
  metadata: Record<string, string>;
  tags: Record<string, string>;
}

export interface MultipartPartResponse {
  partNumber: number;
  sizeBytes: number;
  eTag: string;
  sha256: string;
}

export interface MultipartUploadSessionResponse {
  upload: MultipartUploadCreateResponse;
  status: 'active' | 'expired' | 'completed' | 'aborted';
  parts: MultipartPartResponse[];
}

export interface MultipartUploadListResponse {
  bucket: string;
  maxUploads: number;
  continuationToken?: string | null;
  nextContinuationToken?: string | null;
  isTruncated: boolean;
  uploads: MultipartUploadSessionResponse[];
}

export interface PresignedObjectUrlResponse {
  url: string;
  method: string;
  bucket: string;
  key: string;
  expiresUtc: string;
}

export interface ObjectBucketPolicyResponse {
  bucket: string;
  policyJson?: string | null;
  updatedUtc: string;
}

export interface ObjectLifecycleResponse {
  bucket: string;
  expireCurrentAfterDays?: number | null;
  expireNoncurrentAfterDays?: number | null;
  expireDeleteMarkerAfterDays?: number | null;
  updatedUtc: string;
}

export interface ObjectLifecycleApplyResponse {
  bucket: string;
  expiredCurrentObjects: number;
  removedNoncurrentVersions: number;
  removedDeleteMarkers: number;
  expiredObjects?: Array<{
    key: string;
    versionId: string;
    contentType: string;
  }> | null;
  semanticCleanupJobs: number;
}

export interface ObjectBucketSemanticOptionsResponse {
  bucket: string;
  asyncIngestionEnabled: boolean;
  thumbnailEnabled: boolean;
  thumbnailMaxWidth: number;
  thumbnailMaxHeight: number;
  thumbnailQuality: number;
  updatedUtc: string;
}

export interface ObjectBucketSemanticBackfillResponse {
  bucket: string;
  scannedObjects: number;
  queuedObjects: number;
  skippedObjects: number;
  hasMore: boolean;
  completed: boolean;
  continuationToken?: string | null;
}

export interface ObjectProcessingStatusResponse {
  jobId: string;
  bucket: string;
  key: string;
  versionId: string;
  operation: string;
  status: string;
  semanticRequested: boolean;
  thumbnailRequested: boolean;
  attempts: number;
  error?: string | null;
  semanticImageId?: string | null;
  thumbnailUrl?: string | null;
  createdUtc: string;
  updatedUtc: string;
  nextAttemptUtc?: string | null;
}

export interface ObjectRetentionResponse {
  bucket: string;
  retainCurrentForDays?: number | null;
  retainNoncurrentForDays?: number | null;
  updatedUtc: string;
}

export interface ObjectLegalHoldResponse {
  bucket: string;
  key: string;
  versionId: string;
  enabled: boolean;
  reason?: string | null;
  updatedUtc: string;
}

export interface ObjectQuotaResponse {
  bucket: string;
  maxSizeBytes?: number | null;
  maxObjectVersions?: number | null;
  updatedUtc: string;
}

export interface ObjectStatsResponse {
  bucket: string;
  currentObjectCount: number;
  currentSizeBytes: number;
  objectVersionCount: number;
  objectVersionSizeBytes: number;
  deleteMarkerCount: number;
  multipartUploadCount: number;
  multipartPartCount: number;
  multipartPartSizeBytes: number;
  quotaMaxSizeBytes?: number | null;
  quotaMaxObjectVersions?: number | null;
  quotaRemainingSizeBytes?: number | null;
  quotaRemainingObjectVersions?: number | null;
}

export interface ObjectAuditEntryResponse {
  id: string;
  action: string;
  bucket: string;
  key?: string | null;
  versionId?: string | null;
  timestampUtc: string;
  details: Record<string, string>;
}

export interface ObjectAuditListResponse {
  bucket: string;
  entries: ObjectAuditEntryResponse[];
}

export interface ObjectHeadInfo {
  bucket: string;
  key: string;
  versionId: string;
  contentType: string;
  sizeBytes: number;
  eTag: string;
  sha256: string;
  isDeleteMarker: boolean;
}

export interface ObjectListRequest {
  prefix?: string | null;
  maxKeys?: number | null;
  continuationToken?: string | null;
}

export interface PutObjectOptions {
  contentType?: string | null;
  metadata?: Record<string, string> | null;
  tags?: Record<string, string> | null;
}

export interface InitiateMultipartOptions extends PutObjectOptions {
  expiresHours?: number | null;
}

function bucketUrl(db: string, bucket?: string): string {
  const base = `/v1/db/${encodeURIComponent(db)}/s3`;
  return bucket ? `${base}/${encodeURIComponent(bucket)}` : base;
}

function objectUrl(db: string, bucket: string, key: string): string {
  return `${bucketUrl(db, bucket)}/${encodeKeyPath(key)}`;
}

function encodeKeyPath(key: string): string {
  return key.split('/').map((part) => encodeURIComponent(part)).join('/');
}

function objectHeaders(options?: PutObjectOptions): Record<string, string> {
  const headers: Record<string, string> = {};
  if (options?.contentType) headers['Content-Type'] = options.contentType;
  for (const [key, value] of Object.entries(options?.metadata ?? {})) {
    if (key.trim()) headers[`x-amz-meta-${key.trim()}`] = value;
  }
  const tags = Object.entries(options?.tags ?? {}).filter(([key]) => key.trim());
  if (tags.length > 0) {
    headers['x-amz-tagging'] = tags
      .map(([key, value]) => `${encodeURIComponent(key.trim())}=${encodeURIComponent(value)}`)
      .join('&');
  }
  return headers;
}

function unwrapArray<T>(value: unknown): T[] {
  return Array.isArray(value) ? value as T[] : [];
}

function normalizeObjectList(data: ObjectListResponse): ObjectListResponse {
  return {
    ...data,
    objects: unwrapArray<ObjectInfoResponse>(data.objects),
  };
}

export async function listObjectBuckets(api: AxiosInstance, db: string, signal?: AbortSignal): Promise<ObjectBucketResponse[]> {
  const resp = await api.get<ObjectBucketResponse[]>(bucketUrl(db), { signal });
  return unwrapArray<ObjectBucketResponse>(resp.data);
}

export async function createObjectBucket(
  api: AxiosInstance,
  db: string,
  bucket: string,
  purpose?: string | null,
): Promise<ObjectBucketResponse> {
  const resp = await api.put<ObjectBucketResponse>(bucketUrl(db, bucket), { purpose: purpose || null });
  return resp.data;
}

export async function deleteObjectBucket(api: AxiosInstance, db: string, bucket: string): Promise<void> {
  const response = await api.delete(bucketUrl(db, bucket));
  validateNoContentTerminal(response);
}

export async function getObjectBucket(api: AxiosInstance, db: string, bucket: string, signal?: AbortSignal): Promise<ObjectBucketResponse> {
  const resp = await api.get<ObjectBucketResponse>(bucketUrl(db, bucket), { signal });
  return resp.data;
}

export async function listObjects(
  api: AxiosInstance,
  db: string,
  bucket: string,
  request: ObjectListRequest,
  signal?: AbortSignal,
): Promise<ObjectListResponse> {
  const params = new URLSearchParams();
  params.set('list-type', '2');
  if (request.prefix) params.set('prefix', request.prefix);
  if (request.maxKeys) params.set('max-keys', String(request.maxKeys));
  if (request.continuationToken) params.set('continuation-token', request.continuationToken);
  const resp = await api.get<ObjectListResponse>(`${bucketUrl(db, bucket)}?${params.toString()}`, { signal });
  return normalizeObjectList(resp.data);
}

export async function putObject(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  body: Blob | ArrayBuffer | Uint8Array | string,
  options?: PutObjectOptions,
): Promise<ObjectInfoResponse> {
  const resp = await api.put<ObjectInfoResponse>(objectUrl(db, bucket, key), body, {
    headers: objectHeaders(options),
  });
  return resp.data;
}

export async function headObject(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  versionId?: string | null,
  signal?: AbortSignal,
): Promise<ObjectHeadInfo> {
  const url = versionId
    ? `${objectUrl(db, bucket, key)}?versionId=${encodeURIComponent(versionId)}`
    : objectUrl(db, bucket, key);
  const resp = await api.head(url, { signal });
  return headInfoFromResponse(resp, bucket, key);
}

export async function getObjectBlob(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  options?: { versionId?: string | null; range?: { start: number; end?: number | null } | null },
  signal?: AbortSignal,
): Promise<{ blob: Blob; head: ObjectHeadInfo; status?: number; contentRange?: string }> {
  const params = options?.versionId ? `?versionId=${encodeURIComponent(options.versionId)}` : '';
  const headers: Record<string, string> = {};
  if (options?.range) {
    const end = typeof options.range.end === 'number' ? options.range.end : '';
    headers.Range = `bytes=${Math.max(0, options.range.start)}-${end}`;
  }
  const resp = await api.get<Blob>(`${objectUrl(db, bucket, key)}${params}`, {
    signal,
    headers,
    responseType: 'blob',
  });
  return {
    blob: resp.data,
    status: resp.status,
    contentRange: resp.headers['content-range'],
    head: headInfoFromResponse(resp, bucket, key),
  };
}

export async function deleteObject(api: AxiosInstance, db: string, bucket: string, key: string): Promise<void> {
  const response = await api.delete(objectUrl(db, bucket, key));
  validateNoContentTerminal(response);
  const headers = response.headers;
  if (headers['x-amz-delete-marker'] !== 'true' || typeof headers['x-amz-version-id'] !== 'string'
    || !headers['x-amz-version-id'].trim() || typeof headers.etag !== 'string' || !headers.etag.trim()) throw unknownWriteTerminal();
}

export async function deleteManyObjects(
  api: AxiosInstance,
  db: string,
  bucket: string,
  keys: string[],
): Promise<ObjectDeleteManyResponse> {
  const resp = await api.post<ObjectDeleteManyResponse>(`${bucketUrl(db, bucket)}?delete`, { keys });
  return {
    ...resp.data,
    deleted: unwrapArray<ObjectDeleteResultResponse>(resp.data.deleted),
  };
}

export async function copyObject(
  api: AxiosInstance,
  db: string,
  sourceBucket: string,
  sourceKey: string,
  targetBucket: string,
  targetKey: string,
  options?: PutObjectOptions,
): Promise<ObjectCopyResponse> {
  const headers = objectHeaders(options);
  headers['x-amz-copy-source'] = `/${encodeURIComponent(sourceBucket)}/${encodeKeyPath(sourceKey)}`;
  const resp = await api.put<ObjectCopyResponse>(objectUrl(db, targetBucket, targetKey), null, { headers });
  return resp.data;
}

export async function getObjectTags(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  signal?: AbortSignal,
): Promise<Record<string, string>> {
  const resp = await api.get<ObjectTagsResponse>(`${objectUrl(db, bucket, key)}?tagging`, { signal });
  return resp.data.tags ?? {};
}

export async function setObjectTags(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  tags: Record<string, string>,
): Promise<ObjectInfoResponse> {
  const resp = await api.put<ObjectInfoResponse>(`${objectUrl(db, bucket, key)}?tagging`, { tags });
  return resp.data;
}

export async function listObjectVersions(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key?: string | null,
  signal?: AbortSignal,
): Promise<ObjectVersionListResponse> {
  const suffix = key ? `&key=${encodeURIComponent(key)}` : '';
  const resp = await api.get<ObjectVersionListResponse>(`${bucketUrl(db, bucket)}?versions${suffix}`, { signal });
  return {
    ...resp.data,
    versions: unwrapArray<ObjectInfoResponse>(resp.data.versions),
  };
}

export async function createPresignedObjectUrl(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  method: string,
  expiresMinutes: number,
): Promise<PresignedObjectUrlResponse> {
  const resp = await api.post<PresignedObjectUrlResponse>(`${objectUrl(db, bucket, key)}?presign`, {
    method,
    expiresMinutes,
  });
  return resp.data;
}

export async function getBucketPolicy(
  api: AxiosInstance,
  db: string,
  bucket: string,
  signal?: AbortSignal,
): Promise<ObjectBucketPolicyResponse> {
  const resp = await api.get<ObjectBucketPolicyResponse>(`${bucketUrl(db, bucket)}?policy`, { signal });
  return resp.data;
}

export async function setBucketPolicy(
  api: AxiosInstance,
  db: string,
  bucket: string,
  policyJson?: string | null,
): Promise<ObjectBucketPolicyResponse> {
  const resp = await api.put<ObjectBucketPolicyResponse>(`${bucketUrl(db, bucket)}?policy`, { policyJson: policyJson || null });
  return resp.data;
}

export async function getBucketLifecycle(
  api: AxiosInstance,
  db: string,
  bucket: string,
  signal?: AbortSignal,
): Promise<ObjectLifecycleResponse> {
  const resp = await api.get<ObjectLifecycleResponse>(`${bucketUrl(db, bucket)}?lifecycle`, { signal });
  return resp.data;
}

export async function setBucketLifecycle(
  api: AxiosInstance,
  db: string,
  bucket: string,
  request: Omit<ObjectLifecycleResponse, 'bucket' | 'updatedUtc'>,
): Promise<ObjectLifecycleResponse> {
  const resp = await api.put<ObjectLifecycleResponse>(`${bucketUrl(db, bucket)}?lifecycle`, request);
  return resp.data;
}

export async function applyBucketLifecycle(
  api: AxiosInstance,
  db: string,
  bucket: string,
): Promise<ObjectLifecycleApplyResponse> {
  const resp = await api.post<ObjectLifecycleApplyResponse>(`${bucketUrl(db, bucket)}?lifecycle`);
  return resp.data;
}

export async function getBucketSemanticOptions(
  api: AxiosInstance,
  db: string,
  bucket: string,
  signal?: AbortSignal,
): Promise<ObjectBucketSemanticOptionsResponse> {
  const resp = await api.get<ObjectBucketSemanticOptionsResponse>(`${bucketUrl(db, bucket)}?semantic`, { signal });
  return resp.data;
}

export async function setBucketSemanticOptions(
  api: AxiosInstance,
  db: string,
  bucket: string,
  request: Omit<ObjectBucketSemanticOptionsResponse, 'bucket' | 'updatedUtc'>,
): Promise<ObjectBucketSemanticOptionsResponse> {
  const resp = await api.put<ObjectBucketSemanticOptionsResponse>(`${bucketUrl(db, bucket)}?semantic`, request);
  return resp.data;
}

export async function backfillBucketSemanticObjects(
  api: AxiosInstance,
  db: string,
  bucket: string,
): Promise<ObjectBucketSemanticBackfillResponse> {
  const resp = await api.post<ObjectBucketSemanticBackfillResponse>(`${bucketUrl(db, bucket)}?semantic`);
  return resp.data;
}

export async function getObjectProcessingStatus(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  signal?: AbortSignal,
): Promise<ObjectProcessingStatusResponse> {
  const resp = await api.get<ObjectProcessingStatusResponse>(`${objectUrl(db, bucket, key)}?processing`, { signal });
  return resp.data;
}

export async function enqueueObjectProcessing(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
): Promise<ObjectProcessingStatusResponse> {
  const resp = await api.post<ObjectProcessingStatusResponse>(`${objectUrl(db, bucket, key)}?processing`);
  return resp.data;
}

export async function getObjectThumbnailBlob(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  signal?: AbortSignal,
): Promise<Blob> {
  const resp = await api.get<Blob>(`${objectUrl(db, bucket, key)}?thumbnail`, { signal, responseType: 'blob' });
  return resp.data;
}

export async function getBucketRetention(
  api: AxiosInstance,
  db: string,
  bucket: string,
  signal?: AbortSignal,
): Promise<ObjectRetentionResponse> {
  const resp = await api.get<ObjectRetentionResponse>(`${bucketUrl(db, bucket)}?retention`, { signal });
  return resp.data;
}

export async function setBucketRetention(
  api: AxiosInstance,
  db: string,
  bucket: string,
  request: Omit<ObjectRetentionResponse, 'bucket' | 'updatedUtc'>,
): Promise<ObjectRetentionResponse> {
  const resp = await api.put<ObjectRetentionResponse>(`${bucketUrl(db, bucket)}?retention`, request);
  return resp.data;
}

export async function getObjectLegalHold(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  versionId?: string | null,
  signal?: AbortSignal,
): Promise<ObjectLegalHoldResponse> {
  const suffix = versionId ? `&versionId=${encodeURIComponent(versionId)}` : '';
  const resp = await api.get<ObjectLegalHoldResponse>(`${objectUrl(db, bucket, key)}?legal-hold${suffix}`, { signal });
  return resp.data;
}

export async function setObjectLegalHold(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  enabled: boolean,
  reason?: string | null,
  versionId?: string | null,
): Promise<ObjectLegalHoldResponse> {
  const suffix = versionId ? `&versionId=${encodeURIComponent(versionId)}` : '';
  const resp = await api.put<ObjectLegalHoldResponse>(`${objectUrl(db, bucket, key)}?legal-hold${suffix}`, {
    enabled,
    reason: reason || null,
  });
  return resp.data;
}

export async function getBucketQuota(
  api: AxiosInstance,
  db: string,
  bucket: string,
  signal?: AbortSignal,
): Promise<ObjectQuotaResponse> {
  const resp = await api.get<ObjectQuotaResponse>(`${bucketUrl(db, bucket)}?quota`, { signal });
  return resp.data;
}

export async function setBucketQuota(
  api: AxiosInstance,
  db: string,
  bucket: string,
  request: Omit<ObjectQuotaResponse, 'bucket' | 'updatedUtc'>,
): Promise<ObjectQuotaResponse> {
  const resp = await api.put<ObjectQuotaResponse>(`${bucketUrl(db, bucket)}?quota`, request);
  return resp.data;
}

export async function getBucketStats(
  api: AxiosInstance,
  db: string,
  bucket: string,
  signal?: AbortSignal,
): Promise<ObjectStatsResponse> {
  const resp = await api.get<ObjectStatsResponse>(`${bucketUrl(db, bucket)}?stats`, { signal });
  return resp.data;
}

export async function listBucketAudit(
  api: AxiosInstance,
  db: string,
  bucket: string,
  prefix?: string | null,
  maxEntries?: number | null,
  signal?: AbortSignal,
): Promise<ObjectAuditListResponse> {
  const params = new URLSearchParams();
  params.set('audit', '');
  if (prefix) params.set('prefix', prefix);
  if (maxEntries) params.set('max-entries', String(maxEntries));
  const resp = await api.get<ObjectAuditListResponse>(`${bucketUrl(db, bucket)}?${params.toString()}`, { signal });
  return {
    ...resp.data,
    entries: unwrapArray<ObjectAuditEntryResponse>(resp.data.entries),
  };
}

export async function initiateMultipartUpload(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  options?: InitiateMultipartOptions,
): Promise<MultipartUploadCreateResponse> {
  const resp = await api.post<MultipartUploadCreateResponse>(`${objectUrl(db, bucket, key)}?uploads`, {
    contentType: options?.contentType ?? null,
    metadata: options?.metadata ?? null,
    tags: options?.tags ?? null,
    expiresHours: options?.expiresHours ?? null,
  });
  return resp.data;
}

export async function listMultipartUploads(
  api: AxiosInstance,
  db: string,
  bucket: string,
  maxUploads = 100,
  continuationToken?: string | null,
  signal?: AbortSignal,
): Promise<MultipartUploadListResponse> {
  const params = new URLSearchParams({ uploads: '', 'max-uploads': String(maxUploads) });
  if (continuationToken) params.set('continuation-token', continuationToken);
  const resp = await api.get<MultipartUploadListResponse>(`${bucketUrl(db, bucket)}?${params.toString()}`, { signal });
  return {
    ...resp.data,
    uploads: unwrapArray<MultipartUploadSessionResponse>(resp.data.uploads).map((session) => ({
      ...session,
      parts: unwrapArray<MultipartPartResponse>(session.parts),
    })),
  };
}

export async function getMultipartUpload(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  uploadId: string,
  signal?: AbortSignal,
): Promise<MultipartUploadSessionResponse> {
  const resp = await api.get<MultipartUploadSessionResponse>(`${objectUrl(db, bucket, key)}?uploadId=${encodeURIComponent(uploadId)}`, { signal });
  return { ...resp.data, parts: unwrapArray<MultipartPartResponse>(resp.data.parts) };
}

export async function uploadMultipartPart(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  uploadId: string,
  partNumber: number,
  body: Blob | ArrayBuffer | Uint8Array | string,
): Promise<MultipartPartResponse> {
  const params = new URLSearchParams({
    uploadId,
    partNumber: String(partNumber),
  });
  const resp = await api.put<MultipartPartResponse>(`${objectUrl(db, bucket, key)}?${params.toString()}`, body);
  return resp.data;
}

export async function completeMultipartUpload(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  uploadId: string,
  partNumbers: number[],
): Promise<ObjectInfoResponse> {
  const resp = await api.post<ObjectInfoResponse>(`${objectUrl(db, bucket, key)}?uploadId=${encodeURIComponent(uploadId)}`, {
    partNumbers,
  });
  return resp.data;
}

export async function abortMultipartUpload(
  api: AxiosInstance,
  db: string,
  bucket: string,
  key: string,
  uploadId: string,
): Promise<void> {
  const response = await api.delete(`${objectUrl(db, bucket, key)}?uploadId=${encodeURIComponent(uploadId)}`);
  validateNoContentTerminal(response);
}

function unknownWriteTerminal(): Error & { code: string } {
  return Object.assign(new Error('Object write terminal is unknown.'), { code: 'OBJECT_UNKNOWN_TERMINAL' });
}

function validateNoContentTerminal(response: AxiosResponse<unknown>): void {
  if (!response || response.status !== 204) throw unknownWriteTerminal();
}

function headInfoFromResponse(resp: AxiosResponse<unknown>, bucket: string, key: string): ObjectHeadInfo {
  const headers = resp.headers as Record<string, string | undefined>;
  return {
    bucket,
    key,
    versionId: headers['x-amz-version-id'] ?? '',
    contentType: headers['content-type'] ?? 'application/octet-stream',
    sizeBytes: Number.parseInt(headers['content-length'] ?? '0', 10) || 0,
    eTag: headers.etag ?? '',
    sha256: headers['x-amz-meta-sha256'] ?? '',
    isDeleteMarker: headers['x-amz-delete-marker'] === 'true',
  };
}
