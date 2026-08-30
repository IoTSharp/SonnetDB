import type { AxiosInstance } from 'axios';
import { CopilotRuntimeContractError } from './runtime';

export const BrowserDirectMcpProtocolVersion = '2025-11-25' as const;
export const SonnetDbMcpContractMajor = 1;

const DefaultMaximumResultBytes = 64 * 1024;
const MaximumToolPages = 16;
const MaximumDiscoveredTools = 128;

export interface BrowserDirectMcpEgressPolicy {
  allowDataEgress: boolean;
  allowedToolNames: readonly string[];
  maximumResultBytes?: number;
}

export interface BrowserDirectLocalToolCall {
  cursor: string;
  toolCallId: string;
  toolName: string;
  toolArguments?: string;
}

export interface BrowserDirectLocalToolLoop<TRequest> {
  callTool(request: TRequest, call: BrowserDirectLocalToolCall, signal: AbortSignal): Promise<string>;
}

export interface BrowserDirectMcpToolLoopOptions {
  policy: BrowserDirectMcpEgressPolicy;
  fetchImpl?: typeof fetch;
  locationHref?: string;
}

interface DatabaseBoundRequest {
  db?: string;
}

interface JsonRpcResponse {
  jsonrpc: string;
  id?: unknown;
  result?: unknown;
  error?: unknown;
}

interface McpTool {
  name: string;
  inputSchema: Record<string, unknown>;
  outputSchema: Record<string, unknown>;
  annotations: Record<string, unknown>;
}

interface McpToolCallResult {
  isError?: boolean;
  structuredContent?: unknown;
  content?: unknown;
}

/**
 * Browser-side client for SonnetDB's fixed, stateless Streamable HTTP MCP endpoint.
 * It discovers the current permission-filtered typed tools before every first call.
 */
export class BrowserDirectMcpToolLoop<TRequest extends DatabaseBoundRequest>
implements BrowserDirectLocalToolLoop<TRequest> {
  private readonly fetchImpl: typeof fetch;
  private readonly locationHref: string;
  private readonly allowedToolNames: ReadonlySet<string>;
  private readonly maximumResultBytes: number;
  private readonly sessions = new Map<string, BrowserDirectMcpSession>();

  constructor(
    private readonly api: AxiosInstance,
    private readonly databaseToken: string,
    private readonly options: BrowserDirectMcpToolLoopOptions,
  ) {
    this.fetchImpl = options.fetchImpl ?? globalThis.fetch.bind(globalThis);
    this.locationHref = options.locationHref ?? currentLocationHref();
    this.allowedToolNames = new Set(options.policy.allowedToolNames.map((name) => name.trim()).filter(Boolean));
    this.maximumResultBytes = options.policy.maximumResultBytes ?? DefaultMaximumResultBytes;
    if (!Number.isSafeInteger(this.maximumResultBytes) || this.maximumResultBytes <= 0) {
      throw contractError(
        'browser_direct_mcp_result_budget_invalid',
        'BrowserDirect MCP 结果字节预算必须是正安全整数。',
      );
    }
  }

  async callTool(
    request: TRequest,
    call: BrowserDirectLocalToolCall,
    signal: AbortSignal,
  ): Promise<string> {
    throwIfAborted(signal);
    if (!this.options.policy.allowDataEgress) {
      throw contractError(
        'browser_direct_mcp_egress_disabled',
        'BrowserDirect 本地工具结果出域未显式启用。',
      );
    }

    const database = request.db?.trim() ?? '';
    if (!database) {
      throw contractError(
        'browser_direct_mcp_database_missing',
        'BrowserDirect 本地工具调用必须绑定当前数据库。',
      );
    }

    const toolName = call.toolName.trim();
    if (!toolName || !this.allowedToolNames.has(toolName)) {
      throw contractError(
        'browser_direct_mcp_tool_unapproved',
        `BrowserDirect 本地工具未获出域批准：${toolName || '(missing)'}。`,
      );
    }

    let argumentsValue: unknown;
    try {
      argumentsValue = JSON.parse(call.toolArguments?.trim() || '{}');
    } catch {
      throw contractError(
        'browser_direct_mcp_arguments_invalid',
        `BrowserDirect 本地工具 ${toolName} 的参数不是有效 JSON。`,
      );
    }
    if (!isRecord(argumentsValue)) {
      throw contractError(
        'browser_direct_mcp_arguments_invalid',
        `BrowserDirect 本地工具 ${toolName} 的参数必须是 JSON object。`,
      );
    }

    let session = this.sessions.get(database);
    if (!session) {
      session = new BrowserDirectMcpSession(
        resolveMcpEndpoint(this.api, database, this.locationHref),
        this.databaseToken,
        this.fetchImpl,
      );
      this.sessions.set(database, session);
    }

    const result = await session.callTool(toolName, argumentsValue, signal);
    const resultJson = JSON.stringify(result);
    if (new TextEncoder().encode(resultJson).byteLength > this.maximumResultBytes) {
      throw contractError(
        'browser_direct_mcp_result_too_large',
        `BrowserDirect 本地工具 ${toolName} 的结果超过出域字节预算。`,
      );
    }
    return resultJson;
  }
}

class BrowserDirectMcpSession {
  private nextRequestId = 1;
  private initialized = false;
  private sessionId: string | null = null;
  private readonly tools = new Map<string, McpTool>();

  constructor(
    private readonly endpoint: string,
    private readonly databaseToken: string,
    private readonly fetchImpl: typeof fetch,
  ) {}

  async callTool(
    toolName: string,
    argumentsValue: Record<string, unknown>,
    signal: AbortSignal,
  ): Promise<{ contractVersion: string; isError: false; structuredContent: Record<string, unknown> }> {
    await this.ensureInitialized(signal);
    const tool = this.tools.get(toolName);
    if (!tool) {
      throw contractError(
        'browser_direct_mcp_tool_unknown',
        `当前 SonnetDB MCP endpoint 未发布工具 ${toolName}。`,
      );
    }
    requireReadOnlyTool(tool);
    if (!matchesJsonSchema(argumentsValue, tool.inputSchema)) {
      throw contractError(
        'browser_direct_mcp_arguments_schema_mismatch',
        `BrowserDirect 本地工具 ${toolName} 的参数不符合 inputSchema。`,
      );
    }

    const rawResult = await this.request('tools/call', {
      name: toolName,
      arguments: argumentsValue,
    }, signal);
    if (!isRecord(rawResult)) {
      throw contractError('browser_direct_mcp_result_invalid', 'SonnetDB MCP 返回了无效工具结果。');
    }
    const result = rawResult as McpToolCallResult;
    if (result.isError === true) {
      requireTypedErrorContract(result.content);
      throw contractError(
        'browser_direct_mcp_tool_error',
        `SonnetDB MCP 工具 ${toolName} 返回错误，已停止公网 continuation。`,
      );
    }
    if (!isRecord(result.structuredContent)
      || !hasCompatibleContractVersion(result.structuredContent.contractVersion)
      || !matchesJsonSchema(result.structuredContent, tool.outputSchema)) {
      throw contractError(
        'browser_direct_mcp_result_contract_mismatch',
        `SonnetDB MCP 工具 ${toolName} 未返回兼容的 typed contract v1 结果。`,
      );
    }

    return {
      contractVersion: String(result.structuredContent.contractVersion),
      isError: false,
      structuredContent: result.structuredContent,
    };
  }

  private async ensureInitialized(signal: AbortSignal): Promise<void> {
    if (this.initialized) return;
    const initialize = await this.request('initialize', {
      protocolVersion: BrowserDirectMcpProtocolVersion,
      capabilities: {},
      clientInfo: { name: 'SonnetDB BrowserDirect', version: '1.0' },
    }, signal, false);
    if (!isRecord(initialize) || initialize.protocolVersion !== BrowserDirectMcpProtocolVersion) {
      const actualVersion = isRecord(initialize) ? initialize.protocolVersion : undefined;
      throw contractError(
        'browser_direct_mcp_protocol_mismatch',
        `SonnetDB MCP protocol 版本不匹配：${String(actualVersion ?? '(missing)')}。`,
      );
    }

    await this.notify('notifications/initialized', {}, signal);
    await this.discoverTools(signal);
    this.initialized = true;
  }

  private async discoverTools(signal: AbortSignal): Promise<void> {
    let cursor: string | undefined;
    for (let page = 0; page < MaximumToolPages; page += 1) {
      const result = await this.request('tools/list', cursor ? { cursor } : {}, signal);
      if (!isRecord(result) || !Array.isArray(result.tools)) {
        throw contractError('browser_direct_mcp_tools_invalid', 'SonnetDB MCP tools/list 返回无效。');
      }

      for (const value of result.tools) {
        const tool = parseTool(value);
        if (this.tools.has(tool.name)) {
          throw contractError(
            'browser_direct_mcp_tool_duplicate',
            `SonnetDB MCP tools/list 重复发布工具 ${tool.name}。`,
          );
        }
        this.tools.set(tool.name, tool);
        if (this.tools.size > MaximumDiscoveredTools) {
          throw contractError('browser_direct_mcp_tools_too_many', 'SonnetDB MCP 发布的工具数量超过客户端上限。');
        }
      }

      cursor = typeof result.nextCursor === 'string' && result.nextCursor.trim()
        ? result.nextCursor
        : undefined;
      if (!cursor) return;
    }
    throw contractError('browser_direct_mcp_tools_pagination_exceeded', 'SonnetDB MCP tools/list 分页超过客户端上限。');
  }

  private async request(
    method: string,
    params: Record<string, unknown>,
    signal: AbortSignal,
    includeProtocolVersion = true,
  ): Promise<unknown> {
    const id = this.nextRequestId;
    this.nextRequestId += 1;
    const response = await this.post({ jsonrpc: '2.0', id, method, params }, signal, includeProtocolVersion);
    const envelope = await readJsonRpcResponse(response, signal);
    if (envelope.jsonrpc !== '2.0' || envelope.id !== id || envelope.error !== undefined) {
      throw contractError(
        envelope.error !== undefined ? 'browser_direct_mcp_rpc_error' : 'browser_direct_mcp_rpc_invalid',
        `SonnetDB MCP ${method} 返回无效 JSON-RPC 响应。`,
      );
    }
    return envelope.result;
  }

  private async notify(
    method: string,
    params: Record<string, unknown>,
    signal: AbortSignal,
  ): Promise<void> {
    const response = await this.post({ jsonrpc: '2.0', method, params }, signal, true);
    if (!response.ok) {
      throw contractError(
        'browser_direct_mcp_http_error',
        `SonnetDB MCP ${method} 失败（HTTP ${response.status}）。`,
      );
    }
  }

  private async post(
    body: Record<string, unknown>,
    signal: AbortSignal,
    includeProtocolVersion: boolean,
  ): Promise<Response> {
    if (!this.databaseToken.trim()) {
      throw contractError('browser_direct_mcp_token_missing', 'SonnetDB 数据库登录状态已失效。');
    }
    const headers = new Headers({
      Accept: 'application/json, text/event-stream',
      'Content-Type': 'application/json',
      Authorization: `Bearer ${this.databaseToken}`,
    });
    if (includeProtocolVersion) headers.set('MCP-Protocol-Version', BrowserDirectMcpProtocolVersion);
    if (this.sessionId) headers.set('Mcp-Session-Id', this.sessionId);

    let response: Response;
    try {
      response = await this.fetchImpl(this.endpoint, {
        method: 'POST',
        headers,
        body: JSON.stringify(body),
        signal,
        credentials: 'omit',
        redirect: 'error',
      });
    } catch (error) {
      rethrowIfAborted(signal, error);
      throw contractError('browser_direct_mcp_unavailable', '无法连接当前 SonnetDB MCP endpoint。');
    }
    if (response.redirected) {
      throw contractError('browser_direct_mcp_redirect', 'BrowserDirect 拒绝跟随 SonnetDB MCP 重定向。');
    }
    if (!response.ok) {
      throw contractError(
        'browser_direct_mcp_http_error',
        `SonnetDB MCP 请求失败（HTTP ${response.status}）。`,
      );
    }

    const receivedSessionId = response.headers.get('Mcp-Session-Id')?.trim();
    if (receivedSessionId) {
      if (this.sessionId && this.sessionId !== receivedSessionId) {
        throw contractError('browser_direct_mcp_session_mismatch', 'SonnetDB MCP session id 在运行中发生变化。');
      }
      this.sessionId = receivedSessionId;
    }
    return response;
  }
}

function resolveMcpEndpoint(api: AxiosInstance, database: string, locationHref: string): string {
  let endpoint: URL;
  try {
    endpoint = new URL(api.getUri({ url: `/mcp/${encodeURIComponent(database)}` }), locationHref);
  } catch {
    throw contractError('browser_direct_mcp_url_invalid', '当前 SonnetDB MCP 地址无效。');
  }
  if ((endpoint.protocol !== 'http:' && endpoint.protocol !== 'https:')
    || endpoint.username
    || endpoint.password
    || endpoint.search
    || endpoint.hash) {
    throw contractError(
      'browser_direct_mcp_url_invalid',
      'BrowserDirect MCP 只允许当前 SonnetDB 连接上的固定 HTTP(S) 地址。',
    );
  }
  return endpoint.href;
}

async function readJsonRpcResponse(response: Response, signal: AbortSignal): Promise<JsonRpcResponse> {
  const contentType = response.headers.get('content-type')?.toLowerCase() ?? '';
  let value: unknown;
  if (contentType.startsWith('application/json')) {
    try {
      value = await response.json();
    } catch {
      throw contractError('browser_direct_mcp_json_invalid', 'SonnetDB MCP 返回了无效 JSON。');
    }
  } else if (contentType.startsWith('text/event-stream')) {
    value = await readSingleSseValue(response, signal);
  } else {
    throw contractError(
      'browser_direct_mcp_content_type_invalid',
      `SonnetDB MCP 返回了无效 Content-Type：${contentType || '(missing)'}。`,
    );
  }
  if (!isRecord(value)) {
    throw contractError('browser_direct_mcp_rpc_invalid', 'SonnetDB MCP 返回了无效 JSON-RPC envelope。');
  }
  return value as unknown as JsonRpcResponse;
}

async function readSingleSseValue(response: Response, signal: AbortSignal): Promise<unknown> {
  const text = await response.text();
  throwIfAborted(signal);
  const values: unknown[] = [];
  let dataLines: string[] = [];
  for (const line of `${text}\n`.split(/\r\n|\n|\r/u)) {
    if (!line) {
      if (dataLines.length > 0) {
        try {
          values.push(JSON.parse(dataLines.join('\n')));
        } catch {
          throw contractError('browser_direct_mcp_json_invalid', 'SonnetDB MCP SSE 返回了无效 JSON。');
        }
        dataLines = [];
      }
    } else if (line.startsWith('data:')) {
      dataLines.push(line.slice('data:'.length).trimStart());
    }
  }
  if (values.length !== 1) {
    throw contractError('browser_direct_mcp_rpc_invalid', 'SonnetDB MCP SSE 必须包含一个 JSON-RPC 响应。');
  }
  return values[0];
}

function parseTool(value: unknown): McpTool {
  if (!isRecord(value)
    || typeof value.name !== 'string'
    || !value.name.trim()
    || !isRecord(value.inputSchema)
    || !isRecord(value.outputSchema)
    || !isRecord(value.annotations)) {
    throw contractError('browser_direct_mcp_tool_contract_invalid', 'SonnetDB MCP 发布了无效 typed tool 合同。');
  }
  return {
    name: value.name,
    inputSchema: value.inputSchema,
    outputSchema: value.outputSchema,
    annotations: value.annotations,
  };
}

function requireReadOnlyTool(tool: McpTool): void {
  if (tool.annotations.readOnlyHint !== true
    || tool.annotations.destructiveHint !== false
    || tool.annotations.idempotentHint !== true
    || tool.annotations.openWorldHint !== false) {
    throw contractError(
      'browser_direct_mcp_tool_not_read_only',
      `BrowserDirect 拒绝调用未完整声明只读边界的 MCP 工具 ${tool.name}。`,
    );
  }
}

function requireTypedErrorContract(content: unknown): void {
  if (!Array.isArray(content)) {
    throw contractError('browser_direct_mcp_error_contract_invalid', 'SonnetDB MCP 错误缺少 typed content。');
  }
  for (const block of content) {
    if (!isRecord(block) || block.type !== 'text' || typeof block.text !== 'string') continue;
    try {
      const value: unknown = JSON.parse(block.text);
      if (isRecord(value) && hasCompatibleContractVersion(value.contractVersion)) return;
    } catch {
      // Human-readable compatibility text is not the typed error block.
    }
  }
  throw contractError('browser_direct_mcp_error_contract_invalid', 'SonnetDB MCP 错误缺少兼容的 typed contract v1。');
}

function hasCompatibleContractVersion(value: unknown): boolean {
  if (typeof value !== 'string') return false;
  const match = /^(\d+)\.(\d+)$/u.exec(value);
  return match !== null && Number.parseInt(match[1], 10) === SonnetDbMcpContractMajor;
}

function matchesJsonSchema(value: unknown, schema: Record<string, unknown>): boolean {
  if (Array.isArray(schema.anyOf)) {
    return schema.anyOf.some((candidate) => isRecord(candidate) && matchesJsonSchema(value, candidate));
  }
  if (Array.isArray(schema.enum) && !schema.enum.some((candidate) => Object.is(candidate, value))) return false;

  const types = typeof schema.type === 'string'
    ? [schema.type]
    : Array.isArray(schema.type) ? schema.type.filter((item): item is string => typeof item === 'string') : [];
  if (types.length > 0 && !types.some((type) => matchesJsonType(value, type))) return false;

  if (typeof value === 'number') {
    if (typeof schema.minimum === 'number' && value < schema.minimum) return false;
    if (typeof schema.maximum === 'number' && value > schema.maximum) return false;
  }
  if (Array.isArray(value) && isRecord(schema.items)) {
    return value.every((item) => matchesJsonSchema(item, schema.items as Record<string, unknown>));
  }
  if (isRecord(value)) {
    const properties = isRecord(schema.properties) ? schema.properties : {};
    if (Array.isArray(schema.required)) {
      for (const required of schema.required) {
        if (typeof required !== 'string' || !(required in value)) return false;
      }
    }
    for (const [key, propertyValue] of Object.entries(value)) {
      const propertySchema = properties[key];
      if (isRecord(propertySchema)) {
        if (!matchesJsonSchema(propertyValue, propertySchema)) return false;
      } else if (schema.additionalProperties === false) {
        return false;
      }
    }
  }
  return true;
}

function matchesJsonType(value: unknown, type: string): boolean {
  return type === 'null' ? value === null
    : type === 'object' ? isRecord(value)
      : type === 'array' ? Array.isArray(value)
        : type === 'integer' ? Number.isSafeInteger(value)
          : type === 'number' ? typeof value === 'number' && Number.isFinite(value)
            : type === typeof value;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function currentLocationHref(): string {
  if (typeof window !== 'undefined') return window.location.href;
  return 'http://127.0.0.1/';
}

function throwIfAborted(signal: AbortSignal): void {
  if (!signal.aborted) return;
  if (signal.reason !== undefined) throw signal.reason;
  throw new DOMException('The operation was aborted.', 'AbortError');
}

function rethrowIfAborted(signal: AbortSignal, error: unknown): void {
  if (!signal.aborted) return;
  if (signal.reason !== undefined) throw signal.reason;
  throw error;
}

function contractError(code: string, message: string): CopilotRuntimeContractError {
  return new CopilotRuntimeContractError(code, message);
}
