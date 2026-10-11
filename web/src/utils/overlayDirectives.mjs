// SonnetDB's own Vue directive implementation; covered by the repository MIT license.
// The public binding shape follows Naive UI/vueuc consumers, without vendored vdirs code.
const outsideStates = new WeakMap();

function containsEvent(element, event) {
  return event.composedPath?.().includes(element) || (event.target && element.contains(event.target));
}

function installOutside(element, binding, moving) {
  const document = element.ownerDocument;
  if (!document) return;
  const capture = Boolean(binding.modifiers?.capture);
  const state = { binding, document, capture, pressedInside: false, listeners: [] };
  const listen = (name, handler) => {
    document.addEventListener(name, handler, capture);
    state.listeners.push([name, handler]);
  };
  const notify = event => {
    if (!containsEvent(element, event) && typeof state.binding.value === 'function') state.binding.value(event);
  };
  if (moving) {
    listen('mousemove', notify);
  } else {
    listen('mousedown', event => { state.pressedInside = Boolean(containsEvent(element, event)); });
    listen('mouseup', event => {
      if (!state.pressedInside) notify(event);
      state.pressedInside = false;
    });
    // Keyboard and programmatic clicks have no mouse down/up pair.
    listen('click', event => { if (event.detail === 0) notify(event); });
  }
  outsideStates.set(element, state);
}

function uninstallOutside(element) {
  const state = outsideStates.get(element);
  if (!state) return;
  for (const [name, handler] of state.listeners) state.document.removeEventListener(name, handler, state.capture);
  outsideStates.delete(element);
}

function outsideDirective(moving) {
  return {
    mounted(element, binding) { installOutside(element, binding, moving); },
    updated(element, binding) {
      const state = outsideStates.get(element);
      if (!state || state.capture !== Boolean(binding.modifiers?.capture)) {
        uninstallOutside(element);
        installOutside(element, binding, moving);
      } else state.binding = binding;
    },
    beforeUnmount: uninstallOutside,
    unmounted: uninstallOutside,
  };
}

export const clickoutside = outsideDirective(false);
export const mousemoveoutside = outsideDirective(true);

const layers = new WeakMap();
const documents = new WeakMap();

function releaseLayer(element) {
  const state = layers.get(element);
  if (!state) return;
  state.stack.active.delete(element);
  element.style.zIndex = state.original;
  if (state.stack.active.size === 0) state.stack.next = 2000;
  layers.delete(element);
}

function updateLayer(element, binding) {
  const value = binding.value ?? {};
  if (value.enabled === false) { releaseLayer(element); return; }
  const explicit = Number.isFinite(value.zIndex) ? value.zIndex : undefined;
  let state = layers.get(element);
  if (state && state.explicit === explicit) return;
  if (!state) {
    let stack = documents.get(element.ownerDocument);
    if (!stack) { stack = { next: 2000, active: new Map() }; documents.set(element.ownerDocument, stack); }
    state = { stack, original: element.style.zIndex, explicit };
    layers.set(element, state);
  }
  const index = explicit ?? Math.max(state.stack.next, ...state.stack.active.values()) + 1;
  state.explicit = explicit;
  state.stack.active.set(element, index);
  if (explicit === undefined) state.stack.next = index;
  element.style.zIndex = String(index);
}

export const zindexable = {
  mounted: updateLayer,
  updated: updateLayer,
  beforeUnmount: releaseLayer,
  unmounted: releaseLayer,
};
