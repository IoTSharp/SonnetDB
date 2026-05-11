<template>
  <div class="trajectory-page" :class="{ 'trajectory-page--embedded': embedded }">
    <n-card :bordered="false" class="filter-card">
      <n-space vertical :size="14">
        <div v-if="!embedded">
          <h2>轨迹地图</h2>
          <p>选择数据库、Measurement 和 TAG 后，从 GeoJSON 轨迹端点加载 LineString 并回放。</p>
        </div>

        <n-form label-placement="top" size="small">
          <n-form-item label="数据库">
            <n-select v-model:value="selectedDb" :options="dbOptions" filterable placeholder="选择数据库" />
          </n-form-item>
          <n-form-item label="Measurement">
            <n-select
              v-model:value="selectedMeasurement"
              :options="measurementOptions"
              filterable
              placeholder="选择含 GEOPOINT 字段的 Measurement"
            />
          </n-form-item>
          <n-form-item label="GEOPOINT 字段">
            <n-select v-model:value="selectedField" :options="geoFieldOptions" placeholder="自动选择第一个 GEOPOINT 字段" />
          </n-form-item>
          <n-form-item label="时间范围（Unix 毫秒）">
            <n-space>
              <n-input-number v-model:value="fromMs" :min="0" placeholder="from" clearable />
              <n-input-number v-model:value="toMs" :min="0" placeholder="to" clearable />
            </n-space>
          </n-form-item>
          <n-form-item label="TAG 过滤">
            <n-space vertical :size="8" class="tag-list">
              <n-input
                v-for="tag in tagColumns"
                :key="tag.name"
                v-model:value="tagFilters[tag.name]"
                :placeholder="`${tag.name} = ...`"
                clearable
              />
              <n-text v-if="tagColumns.length === 0" depth="3">当前 Measurement 没有 TAG 列。</n-text>
            </n-space>
          </n-form-item>
        </n-form>

        <n-space>
          <n-button type="primary" :loading="loading" :disabled="!canLoad" @click="loadTrajectory">加载轨迹</n-button>
          <n-button @click="reloadSchema" :disabled="!selectedDb">刷新 Schema</n-button>
        </n-space>

        <n-alert v-if="errorMsg" type="error" :title="errorMsg" closable @close="errorMsg = ''" />

        <n-statistic label="轨迹数" :value="trackCount" />
        <n-statistic label="轨迹点" :value="pointCount" />
      </n-space>
    </n-card>

    <n-card :bordered="false" class="map-card" content-style="padding:0;">
      <div ref="mapEl" class="map-canvas"></div>
      <div class="map-empty" v-if="!hasTracks && !loading">暂无轨迹数据</div>
    </n-card>

    <n-card :bordered="false" class="timeline-card">
      <n-space vertical :size="10">
        <n-space align="center">
          <n-button size="small" :disabled="timelinePoints.length === 0" @click="togglePlayback">
            {{ playing ? '暂停' : '播放' }}
          </n-button>
          <n-button size="small" :disabled="timelinePoints.length === 0" @click="resetPlayback">重置</n-button>
          <n-text depth="3">{{ currentPointLabel }}</n-text>
        </n-space>
        <n-slider v-model:value="frameIndex" :min="0" :max="frameMax" :step="1" :disabled="timelinePoints.length === 0" />
        <div ref="chartEl" class="chart-canvas"></div>
      </n-space>
    </n-card>
  </div>
</template>

<script setup lang="ts">
import 'maplibre-gl/dist/maplibre-gl.css';
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import {
  NAlert,
  NButton,
  NCard,
  NForm,
  NFormItem,
  NInput,
  NInputNumber,
  NSelect,
  NSlider,
  NSpace,
  NStatistic,
  NText,
  type SelectOption,
} from 'naive-ui';
import maplibregl, { type GeoJSONSource, type LngLatBoundsLike, type Map as MapLibreMap } from 'maplibre-gl';
import * as echarts from 'echarts/core';
import { GridComponent, LegendComponent, TooltipComponent, type GridComponentOption } from 'echarts/components';
import { LineChart, type LineSeriesOption } from 'echarts/charts';
import { CanvasRenderer } from 'echarts/renderers';
import { useAuthStore } from '@/stores/auth';
import { listDatabases } from '@/api/server';
import { fetchSchema, type ColumnInfo, type MeasurementInfo } from '@/api/schema';
import { fetchTrajectory, type GeoJsonFeatureCollection } from '@/api/geo';

echarts.use([GridComponent, LegendComponent, TooltipComponent, LineChart, CanvasRenderer]);

type EChartsOption = echarts.ComposeOption<GridComponentOption | LineSeriesOption>;

const props = defineProps<{
  embedded?: boolean;
  initialDb?: string;
  initialMeasurement?: string;
}>();

const embedded = computed(() => Boolean(props.embedded));

interface TrackPoint {
  trackId: string;
  time: number;
  lon: number;
  lat: number;
  properties: Record<string, unknown>;
}

interface TrackLine {
  id: string;
  color: string;
  properties: Record<string, unknown>;
  points: TrackPoint[];
}

const colors = ['#18a058', '#2080f0', '#f0a020', '#d03050', '#8a2be2', '#00a2ae', '#ad7a00'];
const auth = useAuthStore();

const mapEl = ref<HTMLDivElement | null>(null);
const chartEl = ref<HTMLDivElement | null>(null);
const selectedDb = ref('');
const selectedMeasurement = ref('');
const selectedField = ref('');
const fromMs = ref<number | null>(null);
const toMs = ref<number | null>(null);
const databases = ref<string[]>([]);
const schema = ref<MeasurementInfo[]>([]);
const loading = ref(false);
const errorMsg = ref('');
const collection = ref<GeoJsonFeatureCollection | null>(null);
const tracks = ref<TrackLine[]>([]);
const frameIndex = ref(0);
const playing = ref(false);
const tagFilters = reactive<Record<string, string>>({});

let map: MapLibreMap | null = null;
let chart: echarts.ECharts | null = null;
let timer: number | null = null;
let resizeObserver: ResizeObserver | null = null;

const dbOptions = computed<SelectOption[]>(() => databases.value.map((db) => ({ label: db, value: db })));
const geoMeasurements = computed(() => schema.value.filter((m) => m.columns.some(isGeoField)));
const measurementOptions = computed<SelectOption[]>(() => geoMeasurements.value.map((m) => ({ label: m.name, value: m.name })));
const currentMeasurement = computed(() => schema.value.find((m) => m.name === selectedMeasurement.value) ?? null);
const geoFieldOptions = computed<SelectOption[]>(() => currentMeasurement.value?.columns.filter(isGeoField).map((c) => ({ label: c.name, value: c.name })) ?? []);
const tagColumns = computed(() => currentMeasurement.value?.columns.filter((c) => c.role.toLowerCase() === 'tag') ?? []);
const canLoad = computed(() => selectedDb.value.length > 0 && selectedMeasurement.value.length > 0);
const hasTracks = computed(() => tracks.value.length > 0);
const trackCount = computed(() => tracks.value.length);
const pointCount = computed(() => tracks.value.reduce((sum, track) => sum + track.points.length, 0));
const timelinePoints = computed(() => tracks.value.flatMap((track) => track.points).sort((a, b) => a.time - b.time));
const frameMax = computed(() => Math.max(0, timelinePoints.value.length - 1));
const currentPoint = computed(() => timelinePoints.value[Math.min(frameIndex.value, frameMax.value)] ?? null);
const currentPointLabel = computed(() => {
  const point = currentPoint.value;
  if (!point) return '等待轨迹数据';
  return `${new Date(point.time).toLocaleString()} · ${point.trackId} · ${point.lat.toFixed(6)}, ${point.lon.toFixed(6)}`;
});

function isGeoField(column: ColumnInfo): boolean {
  return column.role.toLowerCase() === 'field' && column.dataType.toLowerCase() === 'geopoint';
}

async function reloadDbs(): Promise<void> {
  const result = await listDatabases(auth.api);
  if (result.error) {
    errorMsg.value = result.error.message;
    return;
  }
  databases.value = result.databases;
  if (props.initialDb && result.databases.includes(props.initialDb)) {
    selectedDb.value = props.initialDb;
  } else {
    selectedDb.value ||= result.databases[0] ?? '';
  }
}

async function reloadSchema(): Promise<void> {
  if (!selectedDb.value) return;
  try {
    schema.value = (await fetchSchema(auth.api, selectedDb.value)).measurements;
    const firstGeoMeasurement = geoMeasurements.value[0]?.name ?? '';
    const preferredMeasurement = props.initialMeasurement && geoMeasurements.value.some((m) => m.name === props.initialMeasurement)
      ? props.initialMeasurement
      : '';
    if (preferredMeasurement) {
      selectedMeasurement.value = preferredMeasurement;
    } else if (!geoMeasurements.value.some((m) => m.name === selectedMeasurement.value)) {
      selectedMeasurement.value = firstGeoMeasurement;
    }
  } catch (error) {
    errorMsg.value = error instanceof Error ? error.message : '加载 Schema 失败';
  }
}

async function loadTrajectory(): Promise<void> {
  if (!canLoad.value) return;
  loading.value = true;
  errorMsg.value = '';
  try {
    const result = await fetchTrajectory(auth.api, {
      db: selectedDb.value,
      measurement: selectedMeasurement.value,
      field: selectedField.value,
      from: fromMs.value ?? undefined,
      to: toMs.value ?? undefined,
      tags: tagFilters,
      format: 'linestring',
    });
    if (result.error) {
      errorMsg.value = result.error.message;
      return;
    }
    collection.value = result.collection;
    tracks.value = buildTracks(result.collection);
    frameIndex.value = 0;
    await nextTick();
    renderMap();
    renderChart();
  } finally {
    loading.value = false;
  }
}

function buildTracks(source: GeoJsonFeatureCollection | null): TrackLine[] {
  if (!source) return [];
  return source.features.flatMap((feature, index) => {
    const coordinates = feature.geometry?.coordinates;
    if (feature.geometry?.type !== 'LineString' || !Array.isArray(coordinates)) return [];
    const properties = feature.properties ?? {};
    const id = trackId(properties, index);
    const from = toNumber(properties.from);
    const to = toNumber(properties.to);
    const points = coordinates.flatMap((coordinate, coordinateIndex) => {
      if (!Array.isArray(coordinate) || coordinate.length < 2) return [];
      const lon = Number(coordinate[0]);
      const lat = Number(coordinate[1]);
      if (!Number.isFinite(lon) || !Number.isFinite(lat)) return [];
      const time = interpolateTime(from, to, coordinateIndex, coordinates.length);
      return [{ trackId: id, time, lon, lat, properties }];
    });
    return points.length > 0 ? [{ id, color: colors[index % colors.length], properties, points }] : [];
  });
}

function trackId(properties: Record<string, unknown>, index: number): string {
  const preferred = properties.device ?? properties.deviceId ?? properties.id ?? properties.name;
  return typeof preferred === 'string' && preferred.length > 0 ? preferred : `轨迹 ${index + 1}`;
}

function toNumber(value: unknown): number | null {
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

function interpolateTime(from: number | null, to: number | null, index: number, count: number): number {
  if (from === null && to === null) return index;
  if (from !== null && to !== null && count > 1) return Math.round(from + ((to - from) * index) / (count - 1));
  return from ?? to ?? index;
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
    zoom: 4,
  });
  map.addControl(new maplibregl.NavigationControl({ visualizePitch: true }), 'top-right');
  map.once('style.load', renderMap);
  map.on('load', renderMap);
}

function renderMap(): void {
  if (!map || !map.isStyleLoaded()) return;
  const features = tracks.value.map((track) => ({
    type: 'Feature' as const,
    properties: { id: track.id, color: track.color },
    geometry: {
      type: 'LineString' as const,
      coordinates: track.points.map((point) => [point.lon, point.lat]),
    },
  }));
  const startEndFeatures = tracks.value.flatMap((track) => {
    const start = track.points[0];
    const end = track.points[track.points.length - 1];
    return [start, end].filter(Boolean).map((point, index) => ({
      type: 'Feature' as const,
      properties: { id: track.id, kind: index === 0 ? '起点' : '终点', color: track.color },
      geometry: { type: 'Point' as const, coordinates: [point.lon, point.lat] },
    }));
  });
  const current = currentPoint.value;
  const cursorFeatures = current ? [{
    type: 'Feature' as const,
    properties: { id: current.trackId },
    geometry: { type: 'Point' as const, coordinates: [current.lon, current.lat] },
  }] : [];

  upsertSource('trajectory-lines', { type: 'FeatureCollection', features });
  upsertSource('trajectory-markers', { type: 'FeatureCollection', features: startEndFeatures });
  upsertSource('trajectory-cursor', { type: 'FeatureCollection', features: cursorFeatures });

  if (!map.getLayer('trajectory-lines')) {
    map.addLayer({
      id: 'trajectory-lines',
      type: 'line',
      source: 'trajectory-lines',
      paint: { 'line-color': ['get', 'color'], 'line-width': 4, 'line-opacity': 0.9 },
    });
  }
  if (!map.getLayer('trajectory-markers')) {
    map.addLayer({
      id: 'trajectory-markers',
      type: 'circle',
      source: 'trajectory-markers',
      paint: {
        'circle-color': ['get', 'color'],
        'circle-radius': 6,
        'circle-stroke-color': '#fff',
        'circle-stroke-width': 2,
      },
    });
  }
  if (!map.getLayer('trajectory-cursor')) {
    map.addLayer({
      id: 'trajectory-cursor',
      type: 'circle',
      source: 'trajectory-cursor',
      paint: { 'circle-color': '#d03050', 'circle-radius': 8, 'circle-stroke-color': '#fff', 'circle-stroke-width': 3 },
    });
  }

  fitTracks();
}

function upsertSource(id: string, data: GeoJSON.FeatureCollection): void {
  const source = map?.getSource(id) as GeoJSONSource | undefined;
  if (source) source.setData(data);
  else map?.addSource(id, { type: 'geojson', data });
}

function fitTracks(): void {
  if (!map || pointCount.value === 0) return;
  const bounds = new maplibregl.LngLatBounds();
  for (const track of tracks.value) {
    for (const point of track.points) bounds.extend([point.lon, point.lat]);
  }
  map.fitBounds(bounds as LngLatBoundsLike, { padding: 56, maxZoom: 15, duration: 500 });
}

function renderChart(): void {
  if (!chartEl.value) return;
  chart ??= echarts.init(chartEl.value);
  const series: LineSeriesOption[] = tracks.value.map((track) => ({
    name: track.id,
    type: 'line',
    showSymbol: false,
    data: buildSpeedSeries(track),
    lineStyle: { color: track.color },
  }));
  const option: EChartsOption = {
    tooltip: { trigger: 'axis' },
    legend: { top: 0 },
    grid: { left: 44, right: 20, top: 34, bottom: 28 },
    xAxis: { type: 'category', boundaryGap: false },
    yAxis: { type: 'value', name: '速度 m/s' },
    series,
  };
  chart.setOption(option, true);
}

function buildSpeedSeries(track: TrackLine): Array<[string, number]> {
  return track.points.map((point, index) => {
    if (index === 0) return [formatTime(point.time), 0];
    const previous = track.points[index - 1];
    const elapsed = Math.max(1, point.time - previous.time) / 1000;
    return [formatTime(point.time), distanceMeters(previous, point) / elapsed];
  });
}

function distanceMeters(a: TrackPoint, b: TrackPoint): number {
  const radius = 6371008.8;
  const lat1 = radians(a.lat);
  const lat2 = radians(b.lat);
  const dLat = radians(b.lat - a.lat);
  const dLon = radians(b.lon - a.lon);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(lat1) * Math.cos(lat2) * Math.sin(dLon / 2) ** 2;
  return 2 * radius * Math.asin(Math.min(1, Math.sqrt(h)));
}

function radians(degrees: number): number {
  return degrees * Math.PI / 180;
}

function formatTime(value: number): string {
  return value > 10_000_000 ? new Date(value).toLocaleTimeString() : String(value);
}

function togglePlayback(): void {
  playing.value = !playing.value;
}

function resetPlayback(): void {
  playing.value = false;
  frameIndex.value = 0;
}

function startTimer(): void {
  stopTimer();
  timer = window.setInterval(() => {
    if (frameIndex.value >= frameMax.value) {
      playing.value = false;
      return;
    }
    frameIndex.value += 1;
  }, 450);
}

function stopTimer(): void {
  if (timer !== null) {
    window.clearInterval(timer);
    timer = null;
  }
}

watch(selectedDb, async () => {
  schema.value = [];
  selectedMeasurement.value = '';
  selectedField.value = '';
  await reloadSchema();
});

watch(selectedMeasurement, () => {
  for (const key of Object.keys(tagFilters)) delete tagFilters[key];
  for (const tag of tagColumns.value) tagFilters[tag.name] = '';
  selectedField.value = String(geoFieldOptions.value[0]?.value ?? '');
});

watch(frameIndex, () => renderMap());
watch(playing, (value) => { if (value) startTimer(); else stopTimer(); });
watch(frameMax, () => { if (frameIndex.value > frameMax.value) frameIndex.value = frameMax.value; });

onMounted(async () => {
  initMap();
  if (chartEl.value) {
    resizeObserver = new ResizeObserver(() => chart?.resize());
    resizeObserver.observe(chartEl.value);
  }
  await reloadDbs();
  await reloadSchema();
});

onBeforeUnmount(() => {
  stopTimer();
  resizeObserver?.disconnect();
  chart?.dispose();
  map?.remove();
});
</script>

<style scoped>
.trajectory-page {
  display: grid;
  grid-template-columns: 360px minmax(0, 1fr);
  grid-template-rows: minmax(520px, calc(100vh - 230px)) 220px;
  gap: 16px;
}

.trajectory-page--embedded {
  height: 100%;
  min-height: 0;
  grid-template-rows: minmax(0, 1fr) 220px;
}

.filter-card {
  grid-row: 1 / span 2;
  overflow: auto;
}

.filter-card h2 {
  margin: 0 0 6px;
}

.filter-card p {
  margin: 0;
  color: var(--sndb-ink-soft);
  line-height: 1.6;
}

.tag-list {
  width: 100%;
}

.map-card {
  position: relative;
  overflow: hidden;
}

.map-canvas {
  width: 100%;
  height: 100%;
  min-height: 520px;
}

.map-empty {
  position: absolute;
  inset: 50% auto auto 50%;
  transform: translate(-50%, -50%);
  padding: 10px 14px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.9);
  color: var(--sndb-ink-soft);
  box-shadow: var(--sndb-shadow);
}

.timeline-card {
  min-width: 0;
}

.chart-canvas {
  width: 100%;
  height: 150px;
}

@media (max-width: 1100px) {
  .trajectory-page {
    grid-template-columns: 1fr;
    grid-template-rows: auto 520px 240px;
  }

  .filter-card {
    grid-row: auto;
  }
}

.trajectory-page--embedded .filter-card {
  max-height: none;
}
</style>

