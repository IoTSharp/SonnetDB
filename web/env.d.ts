/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** 固定 Copilot 数据路径；省略时保持兼容的 ServerRelay。 */
  readonly VITE_COPILOT_RUNTIME_MODE?: 'ServerRelay' | 'BrowserDirect' | 'StudioNative' | 'Disabled';
  /** BrowserDirect 的版本化公网 runtime 地址；不得包含凭据。 */
  readonly VITE_COPILOT_BROWSER_DIRECT_PUBLIC_BASE_URL?: string;
  /** BrowserDirect 允许访问的 HTTPS origin，多个值用逗号分隔。 */
  readonly VITE_COPILOT_BROWSER_DIRECT_APPROVED_ORIGINS?: string;
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
