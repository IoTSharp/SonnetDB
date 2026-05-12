import type { GeoPointLike } from '@/utils/sqlValue';

export type GeoCoordinateSystem = 'wgs84' | 'gcj02' | 'bd09';

const PI = Math.PI;
const EARTH_SEMI_MAJOR_AXIS = 6_378_245.0;
const EARTH_ECCENTRICITY_SQUARED = 0.006_693_421_622_965_943;
const BD_X_PI = 3000.0 * PI / 180.0;

export function transformGeoPoint(
  point: GeoPointLike,
  from: GeoCoordinateSystem,
  to: GeoCoordinateSystem,
): GeoPointLike {
  if (from === to) return point;

  switch (`${from}:${to}`) {
    case 'wgs84:gcj02':
      return wgs84ToGcj02(point);
    case 'gcj02:wgs84':
      return gcj02ToWgs84(point);
    case 'gcj02:bd09':
      return gcj02ToBd09(point);
    case 'bd09:gcj02':
      return bd09ToGcj02(point);
    case 'wgs84:bd09':
      return gcj02ToBd09(wgs84ToGcj02(point));
    case 'bd09:wgs84':
      return gcj02ToWgs84(bd09ToGcj02(point));
    default:
      return point;
  }
}

export function normalizeGeoCoordinateSystem(value: string): GeoCoordinateSystem {
  const text = value.trim().toLowerCase().replace(/[-_\s]/g, '');
  if (text === 'wgs84' || text === 'gps') return 'wgs84';
  if (text === 'gcj02' || text === 'gcj' || text === 'amap' || text === 'gaode' || text === 'tencent' || text === 'qq') {
    return 'gcj02';
  }
  if (text === 'bd09' || text === 'bd' || text === 'baidu') return 'bd09';
  return 'wgs84';
}

export function geoCoordinateSystemLabel(system: GeoCoordinateSystem): string {
  switch (system) {
    case 'gcj02':
      return 'GCJ-02';
    case 'bd09':
      return 'BD-09';
    default:
      return 'WGS84';
  }
}

function wgs84ToGcj02(point: GeoPointLike): GeoPointLike {
  if (isOutsideChina(point.lat, point.lon)) return point;
  const delta = deltaOf(point.lat, point.lon);
  return { lat: point.lat + delta.lat, lon: point.lon + delta.lon };
}

function gcj02ToWgs84(point: GeoPointLike): GeoPointLike {
  if (isOutsideChina(point.lat, point.lon)) return point;
  let minLat = point.lat - 0.01;
  let maxLat = point.lat + 0.01;
  let minLon = point.lon - 0.01;
  let maxLon = point.lon + 0.01;

  for (let i = 0; i < 30; i += 1) {
    const midLat = (minLat + maxLat) / 2;
    const midLon = (minLon + maxLon) / 2;
    const converted = wgs84ToGcj02({ lat: midLat, lon: midLon });

    if (converted.lat > point.lat) maxLat = midLat;
    else minLat = midLat;

    if (converted.lon > point.lon) maxLon = midLon;
    else minLon = midLon;

    if (Math.abs(converted.lat - point.lat) < 1e-7 && Math.abs(converted.lon - point.lon) < 1e-7) break;
  }

  return { lat: (minLat + maxLat) / 2, lon: (minLon + maxLon) / 2 };
}

function gcj02ToBd09(point: GeoPointLike): GeoPointLike {
  if (isOutsideChina(point.lat, point.lon)) return point;
  const x = point.lon;
  const y = point.lat;
  const z = Math.sqrt(x * x + y * y) + 0.00002 * Math.sin(y * BD_X_PI);
  const theta = Math.atan2(y, x) + 0.000003 * Math.cos(x * BD_X_PI);
  return {
    lat: z * Math.sin(theta) + 0.006,
    lon: z * Math.cos(theta) + 0.0065,
  };
}

function bd09ToGcj02(point: GeoPointLike): GeoPointLike {
  if (isOutsideChina(point.lat, point.lon)) return point;
  const x = point.lon - 0.0065;
  const y = point.lat - 0.006;
  const z = Math.sqrt(x * x + y * y) - 0.00002 * Math.sin(y * BD_X_PI);
  const theta = Math.atan2(y, x) - 0.000003 * Math.cos(x * BD_X_PI);
  return {
    lat: z * Math.sin(theta),
    lon: z * Math.cos(theta),
  };
}

function deltaOf(lat: number, lon: number): GeoPointLike {
  let dLat = transformLat(lon - 105.0, lat - 35.0);
  let dLon = transformLon(lon - 105.0, lat - 35.0);

  const radLat = lat / 180.0 * PI;
  let magic = Math.sin(radLat);
  magic = 1 - EARTH_ECCENTRICITY_SQUARED * magic * magic;
  const sqrtMagic = Math.sqrt(magic);
  dLat = (dLat * 180.0) / (((EARTH_SEMI_MAJOR_AXIS * (1 - EARTH_ECCENTRICITY_SQUARED)) / (magic * sqrtMagic)) * PI);
  dLon = (dLon * 180.0) / ((EARTH_SEMI_MAJOR_AXIS / sqrtMagic) * Math.cos(radLat) * PI);
  return { lat: dLat, lon: dLon };
}

function transformLat(x: number, y: number): number {
  let result = -100.0 + 2.0 * x + 3.0 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.sqrt(Math.abs(x));
  result += (20.0 * Math.sin(6.0 * x * PI) + 20.0 * Math.sin(2.0 * x * PI)) * 2.0 / 3.0;
  result += (20.0 * Math.sin(y * PI) + 40.0 * Math.sin(y / 3.0 * PI)) * 2.0 / 3.0;
  result += (160.0 * Math.sin(y / 12.0 * PI) + 320.0 * Math.sin(y * PI / 30.0)) * 2.0 / 3.0;
  return result;
}

function transformLon(x: number, y: number): number {
  let result = 300.0 + x + 2.0 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.sqrt(Math.abs(x));
  result += (20.0 * Math.sin(6.0 * x * PI) + 20.0 * Math.sin(2.0 * x * PI)) * 2.0 / 3.0;
  result += (20.0 * Math.sin(x * PI) + 40.0 * Math.sin(x / 3.0 * PI)) * 2.0 / 3.0;
  result += (150.0 * Math.sin(x / 12.0 * PI) + 300.0 * Math.sin(x / 30.0 * PI)) * 2.0 / 3.0;
  return result;
}

function isOutsideChina(lat: number, lon: number): boolean {
  return lon < 72.004 || lon > 137.8347 || lat < 0.8293 || lat > 55.8271;
}
