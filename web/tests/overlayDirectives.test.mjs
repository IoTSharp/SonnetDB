import assert from 'node:assert/strict';
import test from 'node:test';
import { clickoutside, mousemoveoutside, zindexable } from '../src/utils/overlayDirectives.mjs';

function fixture() {
  const listeners = new Map();
  const document = {
    addEventListener(name, callback, capture) { const list = listeners.get(name) ?? []; list.push({ callback, capture }); listeners.set(name, list); },
    removeEventListener(name, callback, capture) { listeners.set(name, (listeners.get(name) ?? []).filter(item => item.callback !== callback || item.capture !== capture)); },
    dispatch(name, event) { for (const { callback } of [...(listeners.get(name) ?? [])]) callback(event); },
  };
  const element = { ownerDocument: document, style: { zIndex: '' }, contains(target) { return target === this; } };
  const event = (target = {}, path = [], detail = 1) => ({ target, composedPath: () => path, detail });
  return { document, element, event, listeners };
}

test('outside clicks respect both drag endpoints, shadow paths, and unmount cleanup', () => {
  const f = fixture(); const notified = [];
  clickoutside.mounted(f.element, { value: e => notified.push(e), modifiers: { capture: true } });
  f.document.dispatch('mousedown', f.event(f.element)); f.document.dispatch('mouseup', f.event());
  f.document.dispatch('mousedown', f.event()); f.document.dispatch('mouseup', f.event(f.element));
  f.document.dispatch('mousedown', f.event({}, [f.element])); f.document.dispatch('mouseup', f.event());
  assert.equal(notified.length, 0);
  f.document.dispatch('mousedown', f.event()); f.document.dispatch('mouseup', f.event());
  assert.equal(notified.length, 1);
  f.document.dispatch('click', f.event()); assert.equal(notified.length, 1);
  f.document.dispatch('click', f.event({}, [], 0)); assert.equal(notified.length, 2);
  clickoutside.beforeUnmount(f.element); clickoutside.unmounted(f.element);
  assert.equal([...f.listeners.values()].flat().length, 0);
});

test('updated callback and capture mode replace stale listeners without leaks', () => {
  const f = fixture(); let old = 0; let current = 0;
  clickoutside.mounted(f.element, { value: () => old++, modifiers: {} });
  clickoutside.updated(f.element, { value: () => current++, modifiers: { capture: true } });
  f.document.dispatch('mouseup', f.event()); assert.equal(old, 0); assert.equal(current, 1);
  clickoutside.updated(f.element, { value: undefined, modifiers: { capture: true } });
  f.document.dispatch('mouseup', f.event()); assert.equal(current, 1);
  assert.equal([...f.listeners.values()].flat().length, 3);
  clickoutside.unmounted(f.element); assert.equal([...f.listeners.values()].flat().length, 0);
});

test('mouse movement excludes descendants and applies current handler', () => {
  const f = fixture(); let count = 0;
  mousemoveoutside.mounted(f.element, { value: () => count++, modifiers: {} });
  f.document.dispatch('mousemove', f.event({}, [f.element])); assert.equal(count, 0);
  f.document.dispatch('mousemove', f.event()); assert.equal(count, 1);
  mousemoveoutside.updated(f.element, { value: () => count += 2, modifiers: {} });
  f.document.dispatch('mousemove', f.event()); assert.equal(count, 3);
  mousemoveoutside.unmounted(f.element); assert.equal([...f.listeners.values()].flat().length, 0);
});

test('overlay ordering survives toggles, fixed layers, nested disposal and restores style', () => {
  const f = fixture(); const a = f.element; a.style.zIndex = '12';
  const b = { ...a, style: { zIndex: '' } }; const fixed = { ...a, style: { zIndex: '' } };
  zindexable.mounted(a, { value: { enabled: false } }); assert.equal(a.style.zIndex, '12');
  zindexable.updated(a, { value: { enabled: true } }); assert.equal(a.style.zIndex, '2001');
  zindexable.mounted(fixed, { value: { zIndex: 7000 } }); assert.equal(fixed.style.zIndex, '7000');
  zindexable.mounted(b, { value: {} }); assert.equal(b.style.zIndex, '7001');
  zindexable.updated(a, { value: { enabled: true } }); assert.equal(a.style.zIndex, '2001');
  zindexable.updated(a, { value: { enabled: false } }); assert.equal(a.style.zIndex, '12');
  zindexable.updated(a, { value: { enabled: true } }); assert.equal(a.style.zIndex, '7002');
  zindexable.unmounted(b); zindexable.unmounted(fixed); zindexable.unmounted(a);
  assert.equal(a.style.zIndex, '12'); assert.equal(b.style.zIndex, '');
  zindexable.mounted(a, { value: {} }); assert.equal(a.style.zIndex, '2001');
  zindexable.unmounted(a);
});
