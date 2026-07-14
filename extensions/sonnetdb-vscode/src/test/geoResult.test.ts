import assert from 'node:assert/strict';
import { test } from 'node:test';
import { inferTrajectory, parseGeoPointValue } from '../core/geoResult';

test('parseGeoPointValue accepts SonnetDB and GeoJSON point values', () => {
  assert.deepEqual(parseGeoPointValue('POINT(39.9042, 116.4074)'), { lat: 39.9042, lon: 116.4074 });
  assert.deepEqual(parseGeoPointValue({ type: 'Point', coordinates: [121.4737, 31.2304] }), {
    lat: 31.2304,
    lon: 121.4737,
  });
  assert.deepEqual(parseGeoPointValue({ Latitude: '22.5431', Longitude: '114.0579' }), {
    lat: 22.5431,
    lon: 114.0579,
  });
  assert.equal(parseGeoPointValue('POINT(200, 300)'), null);
});

test('inferTrajectory groups low-cardinality devices and sorts points by time', () => {
  const model = inferTrajectory(
    ['time', 'device', 'position', 'temperature'],
    [
      [3000, 'car-1', { type: 'Point', coordinates: [121.5, 31.3] }, 24],
      [1000, 'car-1', { type: 'Point', coordinates: [121.4, 31.2] }, 22],
      [2000, 'car-2', 'POINT(22.5431, 114.0579)', 23],
      [4000, 'car-2', [114.1, 22.6], 25],
    ],
  );

  assert.ok(model);
  assert.equal(model.geoColumn, 'position');
  assert.equal(model.timeColumn, 'time');
  assert.equal(model.groupColumn, 'device');
  assert.equal(model.pointCount, 4);
  assert.deepEqual(model.series.map((series) => series.name), ['car-1', 'car-2']);
  assert.deepEqual(model.series[0]?.points.map((point) => point.timeLabel), ['1000', '3000']);
  assert.equal(model.bounds.minLon, 114.0579);
  assert.equal(model.bounds.maxLat, 31.3);
});

test('inferTrajectory does not mistake an unlabelled vector column for GEOPOINT', () => {
  assert.equal(inferTrajectory(
    ['time', 'embedding'],
    [[1000, [0.1, 0.2]], [2000, [0.2, 0.3]]],
  ), null);
});
