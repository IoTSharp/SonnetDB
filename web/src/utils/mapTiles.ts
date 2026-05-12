import type { StyleSpecification } from 'maplibre-gl';
import type { GeoCoordinateSystem } from '@/utils/geoTransforms';

export type MapTileProviderId = 'osm' | 'amap' | 'tencent' | 'baidu';

export interface MapTileProvider {
  id: MapTileProviderId;
  label: string;
  tileUrl: string;
  attribution: string;
  projection: GeoCoordinateSystem;
}

export const MAP_TILE_PROVIDERS: readonly MapTileProvider[] = [
  {
    id: 'osm',
    label: 'OpenStreetMap (WGS84)',
    tileUrl: 'https://tile.openstreetmap.org/{z}/{x}/{y}.png',
    attribution: '© OpenStreetMap contributors',
    projection: 'wgs84',
  },
  {
    id: 'amap',
    label: '高德地图 (GCJ-02)',
    tileUrl: 'https://webrd02.is.autonavi.com/appmaptile?lang=zh_cn&size=1&scale=1&style=7&x={x}&y={y}&z={z}',
    attribution: '© 高德地图',
    projection: 'gcj02',
  },
  {
    id: 'tencent',
    label: '腾讯地图 (GCJ-02)',
    tileUrl: 'https://rt0.map.gtimg.com/tile?z={z}&x={x}&y={y}&styleid=1&version=297',
    attribution: '© 腾讯地图',
    projection: 'gcj02',
  },
  {
    id: 'baidu',
    label: '百度地图 (BD-09)',
    tileUrl: 'https://online0.map.bdimg.com/tile/?qt=vtile&x={x}&y={y}&z={z}&styles=pl&scaler=1&udt=20190528',
    attribution: '© 百度地图',
    projection: 'bd09',
  },
] as const;

export const MAP_TILE_PROVIDER_OPTIONS = MAP_TILE_PROVIDERS.map((provider) => ({
  label: provider.label,
  value: provider.id,
}));

export const MAP_SOURCE_PROJECTION_OPTIONS: Array<{ label: string; value: GeoCoordinateSystem }> = [
  { label: 'WGS84', value: 'wgs84' },
  { label: 'GCJ-02', value: 'gcj02' },
  { label: 'BD-09', value: 'bd09' },
];

export function resolveMapTileProvider(id: string): MapTileProvider {
  return MAP_TILE_PROVIDERS.find((provider) => provider.id === id) ?? MAP_TILE_PROVIDERS[0];
}

export function createRasterMapStyle(provider: MapTileProvider): StyleSpecification {
  return {
    version: 8,
    sources: {
      base: {
        type: 'raster',
        tiles: [provider.tileUrl],
        tileSize: 256,
        attribution: provider.attribution,
      },
    },
    layers: [{ id: 'base', type: 'raster', source: 'base' }],
  };
}
