import type { AxiosInstance } from 'axios';

export interface MqConsumerLag {
  consumerGroup: string;
  committedOffset: number;
  lag: number;
}

export interface MqOffsetsResponse {
  topic: string;
  nextOffset: number;
  consumers: MqConsumerLag[];
}

export interface MqStatsResponse {
  topic: string;
  messageCount: number;
  nextOffset: number;
  consumerOffsets: Record<string, number>;
}

export interface MqRetentionResponse {
  topic: string;
  retainedStartOffset: number;
  retainedEndOffset: number;
  retainedMessages: number;
  trimmedBeforeOffset: number;
  retentionMaxAgeSeconds?: number | null;
  retentionMaxBytes?: number | null;
  retentionIntervalSeconds: number;
  trimAcknowledgedMessages: boolean;
  ackRetentionMinOffsetDelta: number;
  segmentMaxBytes: number;
  hotTailMaxBytes: number;
  segmentCacheSize: number;
}

export interface MqMessageResponse {
  topic: string;
  offset: number;
  timestampUtc: string;
  headers: Record<string, string>;
  payload: string;
}

export interface MqBrowseRequest {
  fromOffset?: number | null;
  maxCount?: number | null;
}

export interface MqBrowseResponse {
  messages: MqMessageResponse[];
}

export interface MqPublishRequest {
  payload: string;
  headers?: Record<string, string> | null;
}

export interface MqPublishResponse {
  topic: string;
  offset: number;
}

export interface MqAckRequest {
  consumerGroup: string;
  offset: number;
}

export interface MqAckResponse {
  topic: string;
  consumerGroup: string;
  nextOffset: number;
}

function mqUrl(db: string, topic: string, action: string): string {
  return `/v1/db/${encodeURIComponent(db)}/mq/${encodeURIComponent(topic)}/${action}`;
}

export async function fetchMqOffsets(
  api: AxiosInstance,
  db: string,
  topic: string,
): Promise<MqOffsetsResponse> {
  const resp = await api.post<MqOffsetsResponse>(mqUrl(db, topic, 'offsets'));
  return {
    topic: resp.data.topic,
    nextOffset: resp.data.nextOffset,
    consumers: Array.isArray(resp.data.consumers) ? resp.data.consumers : [],
  };
}

export async function fetchMqStats(
  api: AxiosInstance,
  db: string,
  topic: string,
): Promise<MqStatsResponse> {
  const resp = await api.post<MqStatsResponse>(mqUrl(db, topic, 'stats'));
  return {
    topic: resp.data.topic,
    messageCount: resp.data.messageCount,
    nextOffset: resp.data.nextOffset,
    consumerOffsets: resp.data.consumerOffsets ?? {},
  };
}

export async function fetchMqRetention(
  api: AxiosInstance,
  db: string,
  topic: string,
): Promise<MqRetentionResponse> {
  const resp = await api.post<MqRetentionResponse>(mqUrl(db, topic, 'retention'));
  return resp.data;
}

export async function browseMqMessages(
  api: AxiosInstance,
  db: string,
  topic: string,
  request: MqBrowseRequest,
): Promise<MqBrowseResponse> {
  const resp = await api.post<MqBrowseResponse>(mqUrl(db, topic, 'browse'), request);
  return {
    messages: Array.isArray(resp.data.messages) ? resp.data.messages : [],
  };
}

export async function publishMqMessage(
  api: AxiosInstance,
  db: string,
  topic: string,
  request: MqPublishRequest,
): Promise<MqPublishResponse> {
  const resp = await api.post<MqPublishResponse>(mqUrl(db, topic, 'publish'), request);
  return resp.data;
}

export async function ackMqConsumer(
  api: AxiosInstance,
  db: string,
  topic: string,
  request: MqAckRequest,
): Promise<MqAckResponse> {
  const resp = await api.post<MqAckResponse>(mqUrl(db, topic, 'ack'), request);
  return resp.data;
}
