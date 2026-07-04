import type { AxiosInstance } from 'axios';

/** 一条 Prometheus 样本：name + label 集 + 数值。 */
export interface PromSample {
  name: string;
  labels: Record<string, string>;
  value: number;
}

/** 一次 /metrics 抓取的解析结果（按 metric 名分桶）。 */
export interface PromSnapshot {
  /** 抓取时刻（前端时钟，毫秒）。 */
  at: number;
  samples: Map<string, PromSample[]>;
  /** /metrics 是否为 OTel Prometheus exporter 输出（含 sonnetdb_* OTel 指标）。 */
  otel: boolean;
  error: string | null;
}

const LINE_RE = /^([a-zA-Z_:][a-zA-Z0-9_:]*)(?:\{([^}]*)\})?\s+(-?[0-9.eE+]+|NaN|[+-]Inf)/;
const LABEL_RE = /([a-zA-Z_][a-zA-Z0-9_]*)="((?:\\.|[^"\\])*)"/g;

/** 解析 Prometheus exposition 文本（只取样本行，忽略 HELP/TYPE/UNIT 注释）。 */
export function parsePrometheusText(text: string): Map<string, PromSample[]> {
  const out = new Map<string, PromSample[]>();
  for (const rawLine of text.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith('#')) continue;
    const m = LINE_RE.exec(line);
    if (!m) continue;
    const [, name, labelBlob, rawValue] = m;
    let value: number;
    if (rawValue === '+Inf') value = Number.POSITIVE_INFINITY;
    else if (rawValue === '-Inf') value = Number.NEGATIVE_INFINITY;
    else value = Number(rawValue);
    if (Number.isNaN(value) && rawValue !== 'NaN') continue;

    const labels: Record<string, string> = {};
    if (labelBlob) {
      for (const lm of labelBlob.matchAll(LABEL_RE)) {
        labels[lm[1]] = lm[2].replace(/\\"/g, '"').replace(/\\n/g, '\n').replace(/\\\\/g, '\\');
      }
    }

    let bucket = out.get(name);
    if (!bucket) out.set(name, bucket = []);
    bucket.push({ name, labels, value });
  }
  return out;
}

/** 抓取并解析 /metrics。 */
export async function fetchPromSnapshot(api: AxiosInstance): Promise<PromSnapshot> {
  const resp = await api.get('/metrics', {
    responseType: 'text',
    transformResponse: (v) => v,
    validateStatus: () => true,
  });
  if (resp.status >= 400 || typeof resp.data !== 'string') {
    return { at: Date.now(), samples: new Map(), otel: false, error: `HTTP ${resp.status}` };
  }
  const samples = parsePrometheusText(resp.data);
  const otel = samples.has('sonnetdb_write_points_total')
    || samples.has('sonnetdb_memtable_points')
    || resp.data.includes('otel_scope_name');
  return { at: Date.now(), samples, otel, error: null };
}

/** 对指定 metric 的所有系列样本求和（跨 label 汇总，如多 db）。 */
export function sumSamples(snapshot: PromSnapshot, name: string): number | null {
  const list = snapshot.samples.get(name);
  if (!list || list.length === 0) return null;
  let total = 0;
  for (const s of list) total += s.value;
  return total;
}

/** 按某 label 分组求和（如 sonnetdb_database）。 */
export function sumByLabel(snapshot: PromSnapshot, name: string, label: string): Record<string, number> {
  const out: Record<string, number> = {};
  for (const s of snapshot.samples.get(name) ?? []) {
    const key = s.labels[label] ?? '';
    out[key] = (out[key] ?? 0) + s.value;
  }
  return out;
}

interface HistogramBuckets {
  /** 升序 le → 累计计数（含 +Inf）。 */
  buckets: { le: number; count: number }[];
  count: number;
  sum: number;
}

/**
 * 聚合直方图（跨 label 汇总同名 _bucket / _count / _sum）。
 * `filter` 可按 label 过滤（如 db_operation=points）。
 */
export function collectHistogram(
  snapshot: PromSnapshot,
  baseName: string,
  filter?: Record<string, string>,
): HistogramBuckets | null {
  const match = (s: PromSample): boolean => {
    if (!filter) return true;
    for (const [k, v] of Object.entries(filter)) {
      if (s.labels[k] !== v) return false;
    }
    return true;
  };

  const byLe = new Map<number, number>();
  for (const s of snapshot.samples.get(`${baseName}_bucket`) ?? []) {
    if (!match(s)) continue;
    const le = s.labels.le === '+Inf' ? Number.POSITIVE_INFINITY : Number(s.labels.le);
    if (Number.isNaN(le)) continue;
    byLe.set(le, (byLe.get(le) ?? 0) + s.value);
  }
  if (byLe.size === 0) return null;

  let count = 0;
  for (const s of snapshot.samples.get(`${baseName}_count`) ?? []) {
    if (match(s)) count += s.value;
  }
  let sum = 0;
  for (const s of snapshot.samples.get(`${baseName}_sum`) ?? []) {
    if (match(s)) sum += s.value;
  }

  const buckets = [...byLe.entries()]
    .map(([le, c]) => ({ le, count: c }))
    .sort((a, b) => a.le - b.le);
  return { buckets, count, sum };
}

/**
 * 从两次抓取的直方图差分还原分位数（线性插值，Prometheus histogram_quantile 同款算法）。
 * `prev` 为 null 时用累计值（自启动以来）。
 */
export function histogramQuantile(
  quantile: number,
  curr: HistogramBuckets,
  prev: HistogramBuckets | null,
): number | null {
  const deltas: { le: number; count: number }[] = curr.buckets.map((b) => {
    const prevCount = prev?.buckets.find((p) => p.le === b.le)?.count ?? 0;
    return { le: b.le, count: Math.max(0, b.count - prevCount) };
  });
  const total = deltas.length > 0 ? deltas[deltas.length - 1].count : 0;
  if (total <= 0) return null;

  const rank = quantile * total;
  let prevLe = 0;
  let prevCount = 0;
  for (const d of deltas) {
    if (d.count >= rank) {
      if (!Number.isFinite(d.le)) return prevLe;
      const bucketCount = d.count - prevCount;
      if (bucketCount <= 0) return d.le;
      return prevLe + ((d.le - prevLe) * (rank - prevCount)) / bucketCount;
    }
    prevLe = Number.isFinite(d.le) ? d.le : prevLe;
    prevCount = d.count;
  }
  return prevLe;
}
