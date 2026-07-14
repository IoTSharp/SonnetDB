export interface GeoPointLike {
  lat: number;
  lon: number;
}

export interface TrajectoryPoint extends GeoPointLike {
  rowIndex: number;
  timeLabel?: string;
}

export interface TrajectorySeries {
  name: string;
  points: TrajectoryPoint[];
}

export interface TrajectoryModel {
  geoColumn: string;
  timeColumn?: string;
  groupColumn?: string;
  series: TrajectorySeries[];
  bounds: {
    minLat: number;
    maxLat: number;
    minLon: number;
    maxLon: number;
  };
  pointCount: number;
}

const GeoPointPatternWithComma = /^point\s*\(\s*([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+)?)\s*,\s*([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+)?)\s*\)$/iu;
const GeoPointPatternWithSpace = /^point\s*\(\s*([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+)?)\s+([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+)?)\s*\)$/iu;
const GeoColumnPattern = /(^|_)(geo(point)?|position|location|coordinates?|point)($|_)/iu;
const TimeColumnPattern = /(^time$|timestamp|_at$|utc$)/iu;

/**
 * 从 SQL 单元格值中解析 SonnetDB GEOPOINT 或常见 GeoJSON Point 表示。
 */
export function parseGeoPointValue(value: unknown): GeoPointLike | null {
  if (value === null || value === undefined) {
    return null;
  }

  if (typeof value === 'string') {
    const text = value.trim();
    if (text === '') {
      return null;
    }

    if (text.startsWith('{') || text.startsWith('[')) {
      try {
        return parseGeoPointValue(JSON.parse(text));
      } catch {
        // 继续尝试 POINT(...) 文本格式。
      }
    }

    const commaMatch = text.match(GeoPointPatternWithComma);
    if (commaMatch) {
      return buildGeoPoint(Number(commaMatch[1]), Number(commaMatch[2]), 'latLon');
    }

    const spaceMatch = text.match(GeoPointPatternWithSpace);
    if (spaceMatch) {
      return buildGeoPoint(Number(spaceMatch[1]), Number(spaceMatch[2]), 'lonLat');
    }

    return null;
  }

  if (Array.isArray(value)) {
    return parseCoordinateArray(value, false);
  }

  if (typeof value !== 'object') {
    return null;
  }

  const record = value as Record<string, unknown>;
  if (record.type === 'Feature') {
    const geometryPoint = parseGeoPointValue(record.geometry);
    if (geometryPoint) {
      return geometryPoint;
    }
  }

  if (record.type === 'Point' && Array.isArray(record.coordinates)) {
    return parseCoordinateArray(record.coordinates, true);
  }

  const lat = readNumber(record.lat ?? record.Lat ?? record.latitude ?? record.Latitude ?? record.y ?? record.Y);
  const lon = readNumber(record.lon ?? record.Lon ?? record.lng ?? record.Lng
    ?? record.longitude ?? record.Longitude ?? record.x ?? record.X);
  if (lat !== null && lon !== null) {
    return buildGeoPoint(lat, lon, 'latLon');
  }

  if (Array.isArray(record.coordinates)) {
    return parseCoordinateArray(record.coordinates, false);
  }

  return null;
}

/**
 * 从查询结果推断轨迹列、时间顺序和可选分组，未检测到明确地理列时返回 null。
 */
export function inferTrajectory(columns: string[], rows: unknown[][]): TrajectoryModel | null {
  if (columns.length === 0 || rows.length === 0) {
    return null;
  }

  const geoIndex = columns.findIndex((column, index) => {
    const values = rows.map((row) => row[index]);
    const hasCoordinate = values.some((value) => parseGeoPointValue(value) !== null);
    return hasCoordinate && (GeoColumnPattern.test(column) || values.some(isExplicitGeoValue));
  });
  if (geoIndex < 0) {
    return null;
  }

  const timeIndex = columns.findIndex((column, index) =>
    index !== geoIndex
    && TimeColumnPattern.test(column)
    && rows.some((row) => readSortTime(row[index]) !== null));
  const groupIndex = findGroupColumn(columns, rows, geoIndex, timeIndex);
  const grouped = new Map<string, Array<TrajectoryPoint & { sortTime: number }>>();

  rows.forEach((row, rowIndex) => {
    const point = parseGeoPointValue(row[geoIndex]);
    if (!point) {
      return;
    }

    const groupName = groupIndex >= 0 ? formatGroupValue(row[groupIndex]) : columns[geoIndex];
    const points = grouped.get(groupName) ?? [];
    points.push({
      ...point,
      rowIndex,
      sortTime: timeIndex >= 0 ? readSortTime(row[timeIndex]) ?? rowIndex : rowIndex,
      timeLabel: timeIndex >= 0 ? formatTimeLabel(row[timeIndex]) : undefined,
    });
    grouped.set(groupName, points);
  });

  const allPoints = Array.from(grouped.values()).flat();
  if (allPoints.length === 0) {
    return null;
  }

  const series = Array.from(grouped.entries()).map(([name, points]) => ({
    name,
    points: points
      .sort((left, right) => left.sortTime - right.sortTime || left.rowIndex - right.rowIndex)
      .map(({ sortTime: _sortTime, ...point }) => point),
  }));

  return {
    geoColumn: columns[geoIndex],
    timeColumn: timeIndex >= 0 ? columns[timeIndex] : undefined,
    groupColumn: groupIndex >= 0 ? columns[groupIndex] : undefined,
    series,
    bounds: {
      minLat: Math.min(...allPoints.map((point) => point.lat)),
      maxLat: Math.max(...allPoints.map((point) => point.lat)),
      minLon: Math.min(...allPoints.map((point) => point.lon)),
      maxLon: Math.max(...allPoints.map((point) => point.lon)),
    },
    pointCount: allPoints.length,
  };
}

function findGroupColumn(columns: string[], rows: unknown[][], geoIndex: number, timeIndex: number): number {
  const candidates = columns
    .map((_, index) => index)
    .filter((index) => index !== geoIndex && index !== timeIndex)
    .map((index) => ({
      index,
      values: rows
        .map((row) => row[index])
        .filter((value): value is string | number | boolean =>
          typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean'),
    }))
    .map((candidate) => ({
      ...candidate,
      unique: new Set(candidate.values.map(formatGroupValue)),
    }))
    .filter((candidate) => candidate.values.length === rows.length
      && candidate.unique.size > 0
      && candidate.unique.size <= 12
      && candidate.unique.size <= Math.max(1, Math.ceil(rows.length / 2)))
    .sort((left, right) => left.unique.size - right.unique.size || left.index - right.index);

  return candidates[0]?.index ?? -1;
}

function isExplicitGeoValue(value: unknown): boolean {
  if (typeof value === 'string') {
    const text = value.trim();
    if (/^point\s*\(/iu.test(text)) {
      return true;
    }
    if (!text.startsWith('{')) {
      return false;
    }
    try {
      return isExplicitGeoValue(JSON.parse(text));
    } catch {
      return false;
    }
  }

  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return false;
  }

  const record = value as Record<string, unknown>;
  return record.type === 'Point'
    || record.type === 'Feature'
    || ['lat', 'Lat', 'latitude', 'Latitude'].some((key) => key in record);
}

function parseCoordinateArray(value: unknown[], allowExtraCoordinates: boolean): GeoPointLike | null {
  if (value.length < 2 || (!allowExtraCoordinates && value.length !== 2)) {
    return null;
  }
  const first = readNumber(value[0]);
  const second = readNumber(value[1]);
  if (first === null || second === null) {
    return null;
  }
  return buildGeoPoint(first, second, 'lonLat');
}

function buildGeoPoint(first: number, second: number, order: 'latLon' | 'lonLat'): GeoPointLike | null {
  const primary = order === 'latLon'
    ? validateGeoPoint(first, second)
    : validateGeoPoint(second, first);
  if (primary) {
    return primary;
  }
  return order === 'latLon'
    ? validateGeoPoint(second, first)
    : validateGeoPoint(first, second);
}

function validateGeoPoint(lat: number, lon: number): GeoPointLike | null {
  if (!Number.isFinite(lat) || !Number.isFinite(lon)
    || lat < -90 || lat > 90 || lon < -180 || lon > 180) {
    return null;
  }
  return { lat, lon };
}

function readNumber(value: unknown): number | null {
  if (typeof value === 'number') {
    return Number.isFinite(value) ? value : null;
  }
  if (typeof value === 'string' && value.trim() !== '') {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

function readSortTime(value: unknown): number | null {
  if (typeof value === 'number') {
    return Number.isFinite(value) ? value : null;
  }
  if (typeof value !== 'string' || value.trim() === '') {
    return null;
  }
  const numeric = Number(value);
  if (Number.isFinite(numeric)) {
    return numeric;
  }
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function formatTimeLabel(value: unknown): string | undefined {
  if (value === null || value === undefined) {
    return undefined;
  }
  return String(value);
}

function formatGroupValue(value: unknown): string {
  if (value === null || value === undefined || value === '') {
    return '(empty)';
  }
  return String(value);
}
