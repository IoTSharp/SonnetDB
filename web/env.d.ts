/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** 固定 Copilot 数据路径；省略时保持兼容的 ServerRelay。 */
  readonly VITE_COPILOT_RUNTIME_MODE?: 'ServerRelay' | 'BrowserDirect' | 'StudioNative' | 'Disabled';
  /** BrowserDirect 的版本化公网 runtime 地址；不得包含凭据。 */
  readonly VITE_COPILOT_BROWSER_DIRECT_PUBLIC_BASE_URL?: string;
  /** BrowserDirect 允许访问的 HTTPS origin，多个值用逗号分隔。 */
  readonly VITE_COPILOT_BROWSER_DIRECT_APPROVED_ORIGINS?: string;
  /** 只有精确为 true 时才允许本地 MCP 成功结果发送到批准的公网 origin。 */
  readonly VITE_COPILOT_BROWSER_DIRECT_ALLOW_DATA_EGRESS?: string;
  /** 允许出域的只读 MCP 工具名，多个值用逗号分隔；空集合拒绝全部工具。 */
  readonly VITE_COPILOT_BROWSER_DIRECT_ALLOWED_TOOLS?: string;
  /** 单个 typed MCP 结果允许发送到公网的最大 UTF-8 字节数。 */
  readonly VITE_COPILOT_BROWSER_DIRECT_MAX_RESULT_BYTES?: string;
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
