import { computed, ref, watch } from 'vue';
import { MAP_SOURCE_PROJECTION_OPTIONS, MAP_TILE_PROVIDER_OPTIONS, createRasterMapStyle, resolveMapTileProvider } from '@/utils/mapTiles';
import type { GeoCoordinateSystem } from '@/utils/geoTransforms';

const STORAGE_KEY = 'sndb.map.tiles.v1';

interface StoredState {
  providerId: string;
  sourceProjection: GeoCoordinateSystem;
}

function loadState(): StoredState {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return { providerId: 'osm', sourceProjection: 'wgs84' };
    const parsed = JSON.parse(raw) as Partial<StoredState>;
    return {
      providerId: typeof parsed.providerId === 'string' ? parsed.providerId : 'osm',
      sourceProjection: parsed.sourceProjection === 'gcj02' || parsed.sourceProjection === 'bd09' ? parsed.sourceProjection : 'wgs84',
    };
  } catch {
    return { providerId: 'osm', sourceProjection: 'wgs84' };
  }
}

function saveState(state: StoredState): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  } catch {
    // 忽略 localStorage 不可用
  }
}

export function useMapTileSettings() {
  const initial = loadState();
  const providerId = ref(initial.providerId);
  const sourceProjection = ref<GeoCoordinateSystem>(initial.sourceProjection);

  const provider = computed(() => resolveMapTileProvider(providerId.value));
  const mapStyle = computed(() => createRasterMapStyle(provider.value));

  watch([providerId, sourceProjection], () => {
    saveState({ providerId: providerId.value, sourceProjection: sourceProjection.value });
  }, { deep: true });

  return {
    providerId,
    sourceProjection,
    provider,
    providerOptions: MAP_TILE_PROVIDER_OPTIONS,
    sourceProjectionOptions: MAP_SOURCE_PROJECTION_OPTIONS,
    mapStyle,
  };
}
