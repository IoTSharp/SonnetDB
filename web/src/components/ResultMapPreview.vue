<template>
  <div class="result-map" v-if="mapReady">
    <div class="result-map__toolbar">
      <n-space size="small" align="center" :wrap="true">
        <span class="result-map__label">坐标列：</span>
        <n-select size="small" style="min-width: 160px" v-model:value="geoColumn" :options="geoOptions" />
        <span class="result-map__label" v-if="timeOptions.length > 0">时间列：</span>
        <n-select
          v-if="timeOptions.length > 0"
          size="small"
          style="min-width: 140px"
          v-model:value="timeColumn"
          :options="timeOptions"
          clearable
          placeholder="不连线"
        />
        <span class="result-map__label" v-if="groupOptions.length > 0">分组：</span>
        <n-select
          v-if="groupOptions.length > 0"
          size="small"
          style="min-width: 150px"
          v-model:value="groupColumn"
          :options="groupOptions"
          clearable
          placeholder="单轨迹"
        />
        <n-tag size="small" :bordered="false">{{ points.length }} 点</n-tag>
      </n-space>
    </div>
    <div ref="mapEl" class="result-map__canvas"></div>
  </div>
  <n-text v-else depth="3" style="font-size: 12px">
    {{ mapHint }}
  </n-text>
</template>

<script setup lang="ts">
import 'maplibre-gl/dist/maplibre-gl.css';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { NSelect, NSpace, NTag, NText, type SelectOption } from 'naive-ui';
import maplibregl, { type GeoJSONSource, type LngLatBoundsLike, type Map as MapLibreMap } from 'maplibre-gl';
import { parseGeoPointValue } from '@/utils/sqlValue';

interface Props {
  columns: string[];
  rows: Record<string, unknown>[];
}

interface MapPoint {
  lon: number;
  lat: number;
  time: number | null;
  group: string;
}

const props = defineProps<Props>();
const palette = ['#2080f0', '#18a058', '#f0a020', '#d03050', '#8a2be2', '#00a2ae', '#ad7a00', '#7c3aed'];

const mapEl = ref<HTMLDivElement | null>(null);
const geoColumn = ref('');
const timeColumn = ref<string | null>(null);
const groupColumn = ref<string | null>(null);
let map: MapLibreMap | null = null;
let resizeObserver: ResizeObserver | null = null;

const geoColumns = computed(() => props.columns.filter((column) => props.rows.some((row) => parseGeoPointValue(row[column]) !== null)));
const geoOptions = computed<SelectOption[]>(() => geoColumns.value.map((column) => ({ label: column, value: column })));
const timeOptions = computed<SelectOption[]>(() => props.columns.filter((column) => props.rows.some((row) => parseTime(row[column]) !== null)).map((column) => ({ label: column, value: column })));
const groupOptions = computed<SelectOption[]>(() => props.columns.filter(isGroupColumn).map((column) => ({ label: column, value: column })));
const points = computed<MapPoint[]>(() => buildPoints());
const mapReady = computed(() => props.rows.length > 0 && geoColumns.value.length > 0 && points.value.length > 0);
const mapHint = computed(() => {
  if (props.rows.length === 0) return '没有可绘制的数据。';
  if (geoColumns.value.length === 0) return '未检测到 GEOPOINT / GeoJSON Point 列。';
  return '没有有效经纬度坐标。';
});

function parseTime(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value === 'string') {
    const parsedDate = Date.parse(value);
    if (!Number.isNaN(parsedDate)) return parsedDate;
    const parsedNumber = Number(value);
    if (Number.isFinite(parsedNumber)) return parsedNumber;
  }
  return null;
}

function isGroupColumn(column: string): boolean {
  if (column === geoColumn.value || column === timeColumn.value) return false;
  const values = new Set<string>();
  let stringCount = 0;
  for (const row of props.rows) {
    const value = row[column];
    if (typeof value === 'string' || typeof value === 'number') {
      stringCount += 1;
      values.add(String(value));
    }
  }
  return stringCount > props.rows.length / 2 && values.size > 1 && values.size <= 16;
}

function buildPoints(): MapPoint[] {
  if (!geoColumn.value) return [];
  return props.rows.flatMap((row, index) => {
    const point = parseGeoPointValue(row[geoColumn.value]);
    if (!point) return [];
    return [{
      ...point,
      time: timeColumn.value ? parseTime(row[timeColumn.value]) : index,
      group: groupColumn.value ? String(row[groupColumn.value] ?? 'null') : '结果轨迹',
    }];
  });
}

function initMap(): void {
  if (!mapEl.value || map) return;
  map = new maplibregl.Map({
    container: mapEl.value,
    style: {
      version: 8,
      sources: {
        osm: {
          type: 'raster',
          tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],
          tileSize: 256,
          attribution: '© OpenStreetMap contributors',
        },
      },
      layers: [{ id: 'osm', type: 'raster', source: 'osm' }],
    },
    center: [116.397, 39.908],
    zoom: 3,
  });
  map.addControl(new maplibregl.NavigationControl({ visualizePitch: true }), 'top-right');
  map.once('style.load', renderMap);
  map.on('load', renderMap);
}

function renderMap(): void {
  if (!map || !map.isStyleLoaded()) return;
  const grouped = groupPoints();
  const lineFeatures = Array.from(grouped.entries()).flatMap(([group, groupPoints], index) => {
    if (!timeColumn.value || groupPoints.length < 2) return [];
    return [{
      type: 'Feature' as const,
      properties: { group, color: palette[index % palette.length] },
      geometry: { type: 'LineString' as const, coordinates: groupPoints.map((point) => [point.lon, point.lat]) },
    }];
  });
  const pointFeatures = Array.from(grouped.entries()).flatMap(([group, groupPoints], index) => groupPoints.map((point) => ({
    type: 'Feature' as const,
    properties: { group, color: palette[index % palette.length] },
    geometry: { type: 'Point' as const, coordinates: [point.lon, point.lat] },
  })));

  upsertSource('result-lines', { type: 'FeatureCollection', features: lineFeatures });
  upsertSource('result-points', { type: 'FeatureCollection', features: pointFeatures });

  if (!map.getLayer('result-lines')) {
    map.addLayer({
      id: 'result-lines',
      type: 'line',
      source: 'result-lines',
      paint: { 'line-color': ['get', 'color'], 'line-width': 3, 'line-opacity': 0.85 },
    });
  }
  if (!map.getLayer('result-points')) {
    map.addLayer({
      id: 'result-points',
      type: 'circle',
      source: 'result-points',
      paint: {
        'circle-color': ['get', 'color'],
        'circle-radius': 5,
        'circle-stroke-color': '#fff',
        'circle-stroke-width': 1.5,
      },
    });
  }

  fitBounds();
}

function groupPoints(): Map<string, MapPoint[]> {
  const grouped = new Map<string, MapPoint[]>();
  for (const point of points.value) {
    let bucket = grouped.get(point.group);
    if (!bucket) {
      bucket = [];
      grouped.set(point.group, bucket);
    }
    bucket.push(point);
  }
  for (const bucket of grouped.values()) {
    bucket.sort((a, b) => (a.time ?? 0) - (b.time ?? 0));
  }
  return grouped;
}

function upsertSource(id: string, data: GeoJSON.FeatureCollection): void {
  const source = map?.getSource(id) as GeoJSONSource | undefined;
  if (source) source.setData(data);
  else map?.addSource(id, { type: 'geojson', data });
}

function fitBounds(): void {
  if (!map || points.value.length === 0) return;
  const bounds = new maplibregl.LngLatBounds();
  for (const point of points.value) bounds.extend([point.lon, point.lat]);
  map.fitBounds(bounds as LngLatBoundsLike, { padding: 40, maxZoom: 15, duration: 350 });
}

watch(() => [props.columns, props.rows], async () => {
  geoColumn.value = geoColumns.value[0] ?? '';
  timeColumn.value = timeOptions.value.find((option) => /^(time|ts|timestamp)$/i.test(String(option.value)))?.value as string | undefined ?? null;
  groupColumn.value = groupOptions.value[0]?.value as string | undefined ?? null;
  await nextTick();
  if (mapReady.value) initMap();
  renderMap();
}, { immediate: true, deep: true });

watch([geoColumn, timeColumn, groupColumn, points], async () => {
  await nextTick();
  if (mapReady.value) initMap();
  renderMap();
});

onMounted(() => {
  if (mapReady.value) initMap();
  if (mapEl.value) {
    resizeObserver = new ResizeObserver(() => map?.resize());
    resizeObserver.observe(mapEl.value);
  }
});

onBeforeUnmount(() => {
  resizeObserver?.disconnect();
  map?.remove();
});
</script>

<style scoped>
.result-map {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.result-map__toolbar {
  font-size: 12px;
}

.result-map__label {
  color: #678;
  font-size: 12px;
}

.result-map__canvas {
  width: 100%;
  height: 360px;
  border: 1px solid rgba(0, 0, 0, 0.06);
  border-radius: 8px;
  overflow: hidden;
  background: #eef5f9;
}
</style>
