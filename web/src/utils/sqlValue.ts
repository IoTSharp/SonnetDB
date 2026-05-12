export interface GeoPointLike {
  lat: number;
  lon: number;
}

const GEO_POINT_PATTERN_WITH_COMMA = /^point\s*\(\s*([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+)?)\s*,\s*([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+)?)\s*\)$/i;
const GEO_POINT_PATTERN_WITH_SPACE = /^point\s*\(\s*([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+)?)\s+([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+)?)\s*\)$/i;

export function parseGeoPointValue(value: unknown): GeoPointLike | null {
  if (value === null || value === undefined) return null;

  if (typeof value === 'string') {
    const text = value.trim();
    if (text === '') return null;

    if (text.startsWith('{') || text.startsWith('[')) {
      try {
        return parseGeoPointValue(JSON.parse(text));
      } catch {
        // fall through to POINT(...) parsing
      }
    }

    const commaMatch = text.match(GEO_POINT_PATTERN_WITH_COMMA);
    if (commaMatch) {
      return buildGeoPoint(Number(commaMatch[1]), Number(commaMatch[2]), 'latLon');
    }

    const spaceMatch = text.match(GEO_POINT_PATTERN_WITH_SPACE);
    if (spaceMatch) {
      return buildGeoPoint(Number(spaceMatch[1]), Number(spaceMatch[2]), 'lonLat');
    }

    return null;
  }

  if (Array.isArray(value)) {
    return parseCoordinateArray(value, false);
  }

  if (typeof value !== 'object') return null;

  const object = value as Record<string, unknown>;

  if (object.type === 'Feature') {
    const geometryPoint = parseGeoPointValue(object.geometry);
    if (geometryPoint) return geometryPoint;
  }

  if (object.type === 'Point') {
    const coordinates = object.coordinates;
    if (Array.isArray(coordinates)) {
      return parseCoordinateArray(coordinates, true);
    }
  }

  const lat = readNumber(
    object.lat ?? object.Lat ?? object.latitude ?? object.Latitude ?? object.y ?? object.Y,
  );
  const lon = readNumber(
    object.lon ?? object.Lon ?? object.lng ?? object.Lng ?? object.longitude ?? object.Longitude ?? object.x ?? object.X,
  );
  if (lat !== null && lon !== null) {
    return buildGeoPoint(lat, lon, 'latLon');
  }

  const coordinates = object.coordinates;
  if (Array.isArray(coordinates)) {
    return parseCoordinateArray(coordinates, false);
  }

  return null;
}

export function formatSqlValue(value: unknown): string {
  if (value === null || value === undefined) return '';

  const geoPoint = parseGeoPointValue(value);
  if (geoPoint) {
    return `POINT(${formatNumber(geoPoint.lat)}, ${formatNumber(geoPoint.lon)})`;
  }

  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'bigint' || typeof value === 'boolean') {
    return String(value);
  }
  if (value instanceof Date) return value.toISOString();

  if (Array.isArray(value)) {
    return `[${value.map((item) => formatSqlValue(item)).join(', ')}]`;
  }

  if (typeof value === 'object') {
    try {
      return JSON.stringify(value) ?? String(value);
    } catch {
      return String(value);
    }
  }

  return String(value);
}

function parseCoordinateArray(value: unknown[], allowExtraCoordinates: boolean): GeoPointLike | null {
  if (value.length < 2) return null;
  if (!allowExtraCoordinates && value.length !== 2) return null;

  const first = readNumber(value[0]);
  const second = readNumber(value[1]);
  if (first === null || second === null) return null;

  return buildGeoPoint(first, second, 'lonLat');
}

function buildGeoPoint(first: number, second: number, order: 'latLon' | 'lonLat'): GeoPointLike | null {
  const primary = order === 'latLon'
    ? validateGeoPoint(first, second)
    : validateGeoPoint(second, first);
  if (primary) return primary;

  return order === 'latLon'
    ? validateGeoPoint(second, first)
    : validateGeoPoint(first, second);
}

function validateGeoPoint(lat: number, lon: number): GeoPointLike | null {
  if (!Number.isFinite(lat) || !Number.isFinite(lon)) return null;
  if (lat < -90 || lat > 90) return null;
  if (lon < -180 || lon > 180) return null;
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

function formatNumber(value: number): string {
  if (Number.isInteger(value)) return String(value);
  const rounded = Number(value.toFixed(6));
  return Number.isInteger(rounded) ? String(rounded) : String(rounded);
}
