/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** 固定 Copilot 数据路径；省略时保持兼容的 ServerRelay。 */
  readonly VITE_COPILOT_RUNTIME_MODE?: 'ServerRelay' | 'BrowserDirect' | 'StudioNative' | 'Disabled';
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}

declare module '*.vue' {
  import type { DefineComponent } from 'vue';
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const component: DefineComponent<{}, {}, any>;
  export default component;
}
