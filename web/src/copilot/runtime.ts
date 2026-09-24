export const CopilotRuntimeModes = [
  'ServerRelay',
  'BrowserDirect',
  'StudioNative',
  'Disabled',
] as const;

export type CopilotRuntimeMode = typeof CopilotRuntimeModes[number];

export type CopilotEndpointReadinessStatus = 'ready' | 'unavailable' | 'not-required';

export interface CopilotEndpointReadiness {
  status: CopilotEndpointReadinessStatus;
  reason?: string;
}

export interface CopilotRuntimeReadiness {
  local?: CopilotEndpointReadiness;
  public?: CopilotEndpointReadiness;
}

export interface CopilotEventPayload {
  type: string;
  answer?: string;
  toolName?: string;
  toolArguments?: string;
  toolResult?: string;
}

export interface CopilotTransportEvent<TEvent extends CopilotEventPayload> {
  runId: string;
  sequence: number;
  cursor: string;
  toolCallId?: string;
  event: TEvent;
}

export interface CopilotTransport<TRequest, TEvent extends CopilotEventPayload> {
  readonly mode: Exclude<CopilotRuntimeMode, 'Disabled'>;

  probeReadiness(signal: AbortSignal): Promise<CopilotRuntimeReadiness>;

  stream(
    runId: string,
    request: TRequest,
    signal: AbortSignal,
  ): AsyncIterable<CopilotTransportEvent<TEvent>>;
}

export interface CopilotRuntimeRunOptions {
  signal?: AbortSignal;
  runId?: string;
}

export type CopilotPendingRunMode = 'read-only' | 'read-write';
export type CopilotPendingRunCloudMode =
  | 'sql_assist'
  | 'sql_analyze'
  | 'db_maintenance'
  | 'knowledge_qa';

/**
 * Non-secret state needed to replay a completed ServerRelay run after a page
 * refresh. Message bodies, credentials and tool results are deliberately not
 * persisted here; the conversation is reloaded from the authenticated server.
 */
export interface CopilotPendingServerRelayRun {
  version: 1;
  runId: string;
  sessionId: string;
  database: string;
  requestFingerprint: string;
  mode: CopilotPendingRunMode;
  cloudMode: CopilotPendingRunCloudMode;
  model?: string;
  createdAtUtc: string;
}

export interface CopilotPendingRunStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export const CopilotPendingServerRelayRunStorageKey = 'sndb.copilot.pending-server-relay.v1';

/** Calculate a stable SHA-256 fingerprint without retaining the request body. */
export async function createCopilotRequestFingerprint(value: unknown): Promise<string | null> {
  const subtle = globalThis.crypto?.subtle;
  if (!subtle) return null;

  const bytes = new TextEncoder().encode(stableStringify(value));
  const digest = await subtle.digest('SHA-256', bytes);
  return [...new Uint8Array(digest)]
    .map((item) => item.toString(16).padStart(2, '0'))
    .join('');
}

export function savePendingServerRelayRun(
  pending: CopilotPendingServerRelayRun,
  storage: CopilotPendingRunStorage | undefined = getSessionStorage(),
): boolean {
  if (!isValidPendingServerRelayRun(pending) || !storage) return false;
  try {
    storage.setItem(CopilotPendingServerRelayRunStorageKey, JSON.stringify(pending));
    return true;
  } catch {
    return false;
  }
}

export function loadPendingServerRelayRun(
  storage: CopilotPendingRunStorage | undefined = getSessionStorage(),
): CopilotPendingServerRelayRun | null {
  if (!storage) return null;
  try {
    const raw = storage.getItem(CopilotPendingServerRelayRunStorageKey);
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    return isValidPendingServerRelayRun(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

export function clearPendingServerRelayRun(
  expectedRunId?: string,
  storage: CopilotPendingRunStorage | undefined = getSessionStorage(),
): void {
  if (!storage) return;
  try {
    if (expectedRunId) {
      const pending = loadPendingServerRelayRun(storage);
      if (pending && pending.runId !== expectedRunId) return;
    }
    storage.removeItem(CopilotPendingServerRelayRunStorageKey);
  } catch {
    // Disabled or quota-exhausted storage cannot be used for resume.
  }
}

export class CopilotRuntimeContractError extends Error {
  constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = 'CopilotRuntimeContractError';
  }
}

const EventTypePattern = /^[a-z][a-z0-9_]{0,63}$/u;

/** Resolve the configured mode without probing or silently changing data paths. */
export function resolveCopilotRuntimeMode(value: unknown): CopilotRuntimeMode {
  if (value === undefined || value === null || value === '') return 'ServerRelay';
  if (typeof value !== 'string') {
    throw contractError('runtime_mode_invalid', 'Copilot runtime mode 配置必须是字符串。');
  }

  const normalized = value.trim();
  if ((CopilotRuntimeModes as readonly string[]).includes(normalized)) {
    return normalized as CopilotRuntimeMode;
  }

  throw contractError(
    'runtime_mode_invalid',
    `未知的 Copilot runtime mode：${normalized || '(empty)'}。`,
  );
}

/** Validate the independent local/public readiness required by a fixed mode. */
export function assertCopilotRuntimeReadiness(
  mode: CopilotRuntimeMode,
  readiness: CopilotRuntimeReadiness,
): void {
  if (mode === 'Disabled') {
    throw contractError('runtime_disabled', 'Copilot 客户端运行模式已禁用。');
  }

  if (!isEndpointReadiness(readiness.local) || !isEndpointReadiness(readiness.public)) {
    throw contractError(
      'runtime_readiness_missing',
      'Copilot runtime 缺少独立的本地或公网 readiness 结果。',
    );
  }

  if (readiness.local.status !== 'ready') {
    throw contractError(
      'runtime_local_unavailable',
      readiness.local.reason || 'SonnetDB 本地端点尚未就绪。',
    );
  }

  if (mode === 'ServerRelay') {
    if (readiness.public.status !== 'not-required') {
      throw contractError(
        'runtime_public_readiness_invalid',
        'ServerRelay 模式的客户端公网 readiness 必须标记为 not-required。',
      );
    }
    return;
  }

  if (readiness.public.status !== 'ready') {
    throw contractError(
      'runtime_public_unavailable',
      readiness.public.reason || '外部 AI 端点尚未就绪。',
    );
  }
}

export class CopilotRuntime<TRequest, TEvent extends CopilotEventPayload> {
  private readonly transports = new Map<CopilotRuntimeMode, CopilotTransport<TRequest, TEvent>>();

  constructor(
    private readonly mode: CopilotRuntimeMode,
    transports: Iterable<CopilotTransport<TRequest, TEvent>>,
  ) {
    for (const transport of transports) {
      if (this.transports.has(transport.mode)) {
        throw contractError(
          'runtime_transport_duplicate',
          `Copilot runtime mode ${transport.mode} 注册了多个 transport。`,
        );
      }
      this.transports.set(transport.mode, transport);
    }
  }

  async *run(
    request: TRequest,
    options: CopilotRuntimeRunOptions = {},
  ): AsyncGenerator<CopilotTransportEvent<TEvent>, void, unknown> {
    if (this.mode === 'Disabled') {
      throw contractError('runtime_disabled', 'Copilot 客户端运行模式已禁用。');
    }

    const transport = this.transports.get(this.mode);
    if (!transport) {
      throw contractError(
        'runtime_transport_unavailable',
        `Copilot runtime mode ${this.mode} 尚未注册 transport，拒绝回退到其它模式。`,
      );
    }

    const signal = options.signal ?? new AbortController().signal;
    throwIfAborted(signal);

    const runId = options.runId?.trim() || createCopilotRunId();
    const readiness = await transport.probeReadiness(signal);
    throwIfAborted(signal);
    assertCopilotRuntimeReadiness(this.mode, readiness);

    const state = new CopilotEventStateMachine<TEvent>(runId);
    for await (const candidate of transport.stream(runId, request, signal)) {
      throwIfAborted(signal);
      const accepted = state.accept(candidate);
      if (accepted) yield accepted;
    }

    throwIfAborted(signal);
    if (!state.isComplete) {
      throw contractError(
        'runtime_stream_incomplete',
        `Copilot run ${runId} 在 done 事件之前结束。`,
      );
    }
  }
}

export class CopilotEventStateMachine<TEvent extends CopilotEventPayload> {
  private readonly cursors = new Map<string, string>();
  private readonly toolCalls = new Map<string, string>();
  private readonly toolNames = new Map<string, string>();
  private readonly toolResults = new Map<string, string>();
  private readonly completedToolCalls = new Set<string>();
  private lastSequence = 0;
  private hasOutcome = false;
  private complete = false;

  constructor(private readonly runId: string) {
    if (!runId.trim()) {
      throw contractError('runtime_run_id_invalid', 'Copilot runId 不能为空。');
    }
  }

  get isComplete(): boolean {
    return this.complete;
  }

  accept(candidate: CopilotTransportEvent<TEvent>): CopilotTransportEvent<TEvent> | null {
    this.validateEnvelope(candidate);

    const eventFingerprint = stableStringify({
      sequence: candidate.sequence,
      toolCallId: candidate.toolCallId ?? null,
      event: candidate.event,
    });
    const existingCursor = this.cursors.get(candidate.cursor);
    if (existingCursor !== undefined) {
      if (existingCursor === eventFingerprint) return null;
      throw contractError(
        'runtime_cursor_conflict',
        `Copilot cursor ${candidate.cursor} 被用于不同事件。`,
      );
    }

    if (this.complete) {
      throw contractError('runtime_event_after_done', 'Copilot done 事件之后仍收到新的事件。');
    }

    const expectedSequence = this.lastSequence + 1;
    if (candidate.sequence !== expectedSequence) {
      throw contractError(
        'runtime_sequence_invalid',
        `Copilot 事件 sequence=${candidate.sequence}，期望 ${expectedSequence}。`,
      );
    }

    const type = candidate.event.type;
    // CopilotChatEvent is extend-only. Existing relay events include risk_review,
    // and future informational events remain safe as long as their type is bounded.
    if (!EventTypePattern.test(type)) {
      throw contractError('runtime_event_type_invalid', `无效的 Copilot 事件类型：${type || '(empty)'}。`);
    }

    if (this.hasOutcome && type !== 'done') {
      if (type === 'final' || type === 'error') {
        throw contractError('runtime_outcome_conflict', 'Copilot run 返回了多个 final/error 终态。');
      }
      throw contractError(
        'runtime_event_after_outcome',
        'Copilot final/error 终态之后只允许 done 或完全重复的既有事件。',
      );
    }

    if (type === 'final' && !candidate.event.answer?.trim()) {
      throw contractError('runtime_final_invalid', 'Copilot final 终态缺少非空 answer。');
    }

    this.cursors.set(candidate.cursor, eventFingerprint);
    this.lastSequence = candidate.sequence;

    if (type === 'tool_call') {
      return this.acceptToolCall(candidate);
    }
    if (type === 'tool_retry') {
      const toolCallId = this.requireKnownToolCall(candidate);
      this.requireMatchingToolName(candidate, toolCallId);
      if (this.completedToolCalls.has(toolCallId)) {
        throw contractError(
          'runtime_tool_already_completed',
          `Copilot toolCallId ${toolCallId} 已返回结果，不能继续 retry。`,
        );
      }
      return candidate;
    }
    if (type === 'tool_result') {
      return this.acceptToolResult(candidate);
    }
    if (type === 'final' || type === 'error') {
      this.hasOutcome = true;
      return candidate;
    }
    if (type === 'done') {
      if (!this.hasOutcome) {
        throw contractError(
          'runtime_done_without_outcome',
          'Copilot run 在 final/error 终态之前收到 done 事件。',
        );
      }
      this.complete = true;
    }

    return candidate;
  }

  private validateEnvelope(candidate: CopilotTransportEvent<TEvent>): void {
    if (candidate.runId !== this.runId) {
      throw contractError(
        'runtime_run_id_mismatch',
        `Copilot 事件 runId=${candidate.runId || '(empty)'}，期望 ${this.runId}。`,
      );
    }
    if (!Number.isSafeInteger(candidate.sequence) || candidate.sequence <= 0) {
      throw contractError('runtime_sequence_invalid', 'Copilot 事件 sequence 必须是正安全整数。');
    }
    if (!candidate.cursor?.trim()) {
      throw contractError('runtime_cursor_invalid', 'Copilot 事件 cursor 不能为空。');
    }
    if (!candidate.event || typeof candidate.event.type !== 'string' || !candidate.event.type.trim()) {
      throw contractError('runtime_event_invalid', 'Copilot transport 返回了无效事件。');
    }
  }

  private acceptToolCall(
    candidate: CopilotTransportEvent<TEvent>,
  ): CopilotTransportEvent<TEvent> | null {
    const toolCallId = requireToolCallId(candidate);
    const toolName = candidate.event.toolName?.trim();
    if (!toolName) {
      throw contractError('runtime_tool_call_invalid', 'Copilot tool_call 缺少 toolName。');
    }

    const fingerprint = stableStringify({
      toolName,
      toolArguments: replayJsonFingerprint(candidate.event.toolArguments),
    });
    const existing = this.toolCalls.get(toolCallId);
    if (existing === undefined) {
      this.toolCalls.set(toolCallId, fingerprint);
      this.toolNames.set(toolCallId, toolName);
      return candidate;
    }
    if (existing === fingerprint) return null;

    throw contractError(
      'runtime_tool_call_conflict',
      `Copilot toolCallId ${toolCallId} 被用于不同参数。`,
    );
  }

  private acceptToolResult(
    candidate: CopilotTransportEvent<TEvent>,
  ): CopilotTransportEvent<TEvent> | null {
    const toolCallId = this.requireKnownToolCall(candidate);
    const toolName = this.requireMatchingToolName(candidate, toolCallId);
    const fingerprint = stableStringify({
      toolName,
      toolResult: replayJsonFingerprint(candidate.event.toolResult),
    });
    const existing = this.toolResults.get(toolCallId);
    if (existing === undefined) {
      this.toolResults.set(toolCallId, fingerprint);
      this.completedToolCalls.add(toolCallId);
      return candidate;
    }
    if (existing === fingerprint) return null;

    throw contractError(
      'runtime_tool_result_conflict',
      `Copilot toolCallId ${toolCallId} 返回了互相冲突的结果。`,
    );
  }

  private requireKnownToolCall(candidate: CopilotTransportEvent<TEvent>): string {
    const toolCallId = requireToolCallId(candidate);
    if (!this.toolCalls.has(toolCallId)) {
      throw contractError(
        'runtime_tool_call_unknown',
        `Copilot 工具事件引用了未知 toolCallId：${toolCallId}。`,
      );
    }
    return toolCallId;
  }

  private requireMatchingToolName(
    candidate: CopilotTransportEvent<TEvent>,
    toolCallId: string,
  ): string {
    const expected = this.toolNames.get(toolCallId);
    const actual = candidate.event.toolName?.trim();
    if (!actual || actual !== expected) {
      throw contractError(
        'runtime_tool_name_mismatch',
        `Copilot 工具事件的 toolName=${actual || '(missing)'} 与 toolCallId ${toolCallId} 不匹配。`,
      );
    }
    return actual;
  }
}

function isEndpointReadiness(value: CopilotEndpointReadiness | undefined): value is CopilotEndpointReadiness {
  return value !== undefined
    && (value.status === 'ready' || value.status === 'unavailable' || value.status === 'not-required');
}

function requireToolCallId<TEvent extends CopilotEventPayload>(
  candidate: CopilotTransportEvent<TEvent>,
): string {
  const toolCallId = candidate.toolCallId?.trim();
  if (!toolCallId) {
    throw contractError('runtime_tool_call_id_missing', `${candidate.event.type} 事件缺少 toolCallId。`);
  }
  return toolCallId;
}

type ReplayJsonFingerprint =
  | readonly ['missing']
  | readonly ['json', string]
  | readonly ['text', string];

function replayJsonFingerprint(value: unknown): ReplayJsonFingerprint {
  if (value === undefined || value === null) return ['missing'];
  if (typeof value !== 'string') {
    throw contractError(
      'runtime_tool_payload_invalid',
      'Copilot 工具参数和结果必须是 JSON 文本或字符串。',
    );
  }

  try {
    return ['json', canonicalizeCopilotJson(value)];
  } catch (error) {
    if (error instanceof CopilotRuntimeContractError) throw error;
    // The server accepts byte-identical non-JSON payload replays before attempting
    // semantic JSON comparison. Keep the same exact, fail-closed fallback here.
    return ['text', value];
  }
}

type CanonicalJson =
  | readonly ['null']
  | readonly ['boolean', boolean]
  | readonly ['string' | 'number', string]
  | readonly ['array', CanonicalJson[]]
  | readonly ['object', Array<readonly [string, CanonicalJson]>];

const MaximumReplayJsonDepth = 64;
const MaximumReplayJsonNodes = 16_384;
const MaximumReplayJsonCharacters = 1024 * 1024;
const MaximumCanonicalMilliseconds = 5_000;

/** Bounded, precise JSON fingerprint preserving duplicate-key order and decimal numbers. */
export function canonicalizeCopilotJson(value: string, limits: {
  maximumDepth?: number;
  maximumNodes?: number;
  maximumCharacters?: number;
} = {}): string {
  const maximumDepth = limits.maximumDepth ?? MaximumReplayJsonDepth;
  const maximumNodes = limits.maximumNodes ?? MaximumReplayJsonNodes;
  const maximumCharacters = limits.maximumCharacters ?? MaximumReplayJsonCharacters;
  if (!Number.isSafeInteger(maximumDepth) || maximumDepth < 1 || maximumDepth > MaximumReplayJsonDepth
    || !Number.isSafeInteger(maximumNodes) || maximumNodes < 1 || maximumNodes > MaximumReplayJsonNodes
    || !Number.isSafeInteger(maximumCharacters) || maximumCharacters < 1 || maximumCharacters > MaximumReplayJsonCharacters) {
    throw contractError('runtime_json_budget_invalid', 'Copilot JSON 预算无效。');
  }
  return new JsonReplayCanonicalizer(value, maximumDepth, maximumNodes, maximumCharacters).canonicalize();
}

class JsonReplayCanonicalizer {
  private offset = 0;
  private nodes = 0;
  private readonly deadline = performance.now() + MaximumCanonicalMilliseconds;

  constructor(
    private readonly input: string,
    private readonly maximumDepth: number,
    private readonly maximumNodes: number,
    private readonly maximumCharacters: number,
  ) {}

  canonicalize(): string {
    if (this.input.length > this.maximumCharacters) this.rejectBudget();
    this.checkDeadline();
    this.skipWhitespace();
    const value = this.parseValue(0);
    this.skipWhitespace();
    if (this.offset !== this.input.length) throw new Error('Trailing JSON content.');
    // Serialize once. Repeatedly serializing a serialized child causes
    // exponentially repeated escaping for a small deeply nested input.
    const canonical = JSON.stringify(value);
    this.checkDeadline();
    return canonical;
  }

  private parseValue(depth: number): CanonicalJson {
    this.nodes += 1;
    if (depth > this.maximumDepth || this.nodes > this.maximumNodes) this.rejectBudget();
    this.checkDeadline();
    const token = this.input[this.offset];
    if (token === '{') return this.parseObject(depth);
    if (token === '[') return this.parseArray(depth);
    if (token === '"') return ['string', this.parseString()];
    if (token === 't') return this.parseLiteral('true', ['boolean', true]);
    if (token === 'f') return this.parseLiteral('false', ['boolean', false]);
    if (token === 'n') return this.parseLiteral('null', ['null']);
    if (token === '-' || (token !== undefined && token >= '0' && token <= '9')) {
      return ['number', this.parseNumber()];
    }
    throw new Error('Invalid JSON value.');
  }

  private parseObject(depth: number): CanonicalJson {
    this.offset += 1;
    this.skipWhitespace();
    const entries: Array<{ key: string; value: CanonicalJson; ordinal: number }> = [];
    if (this.consume('}')) return ['object', []];

    for (let property = 0; property < this.maximumNodes; property += 1) {
      if (this.input[this.offset] !== '"') throw new Error('Invalid JSON property name.');
      const key = this.parseString();
      this.skipWhitespace();
      this.expect(':');
      this.skipWhitespace();
      entries.push({ key, value: this.parseValue(depth + 1), ordinal: entries.length });
      this.skipWhitespace();
      if (this.consume('}')) {
        entries.sort((left, right) => {
          if (left.key < right.key) return -1;
          if (left.key > right.key) return 1;
          return left.ordinal - right.ordinal;
        });
        return ['object', entries.map(({ key, value }) => [key, value] as const)];
      }
      this.expect(',');
      this.skipWhitespace();
    }

    return this.rejectBudget();
  }

  private parseArray(depth: number): CanonicalJson {
    this.offset += 1;
    this.skipWhitespace();
    const items: CanonicalJson[] = [];
    if (this.consume(']')) return ['array', items];

    for (let index = 0; index < this.maximumNodes; index += 1) {
      items.push(this.parseValue(depth + 1));
      this.skipWhitespace();
      if (this.consume(']')) return ['array', items];
      this.expect(',');
      this.skipWhitespace();
    }
    return this.rejectBudget();
  }

  private parseString(): string {
    const start = this.offset;
    this.offset += 1;
    let scanned = 0;
    while (this.offset < this.input.length) {
      if ((scanned++ & 255) === 0) this.checkDeadline();
      const code = this.input.charCodeAt(this.offset);
      if (code < 0x20) throw new Error('Invalid JSON string.');
      if (code === 0x22) {
        this.offset += 1;
        const parsed: unknown = JSON.parse(this.input.slice(start, this.offset));
        if (typeof parsed !== 'string') throw new Error('Invalid JSON string.');
        return parsed;
      }
      this.offset += code === 0x5c ? 2 : 1;
    }
    throw new Error('Unterminated JSON string.');
  }

  private parseNumber(): string {
    const match = /^(-?)(0|[1-9]\d*)(?:\.(\d+))?(?:[eE]([+-]?\d+))?/u
      .exec(this.input.slice(this.offset));
    this.checkDeadline();
    if (!match) throw new Error('Invalid JSON number.');
    this.offset += match[0].length;

    const fraction = match[3] ?? '';
    let digits = `${match[2]}${fraction}`.replace(/^0+/u, '');
    if ((match[4]?.length ?? 0) > 512) this.rejectBudget();
    if (!digits) return '0e0';

    let exponent = BigInt(match[4] ?? '0') - BigInt(fraction.length);
    let significantEnd = digits.length;
    for (let scanned = 0; scanned < digits.length; scanned += 1) {
      if ((scanned & 255) === 0) this.checkDeadline();
      if (digits.charCodeAt(significantEnd - 1) !== 0x30) break;
      significantEnd -= 1;
    }
    exponent += BigInt(digits.length - significantEnd);
    digits = digits.slice(0, significantEnd);
    return `${match[1]}${digits}e${exponent.toString()}`;
  }

  private parseLiteral(literal: string, canonical: CanonicalJson): CanonicalJson {
    if (!this.input.startsWith(literal, this.offset)) throw new Error('Invalid JSON literal.');
    this.offset += literal.length;
    return canonical;
  }

  private rejectBudget(): never {
    throw contractError('runtime_tool_payload_budget_exceeded', 'Copilot 工具 JSON 超过深度、节点、文本或处理时间预算。');
  }

  private checkDeadline(): void {
    if (performance.now() > this.deadline) this.rejectBudget();
  }

  private skipWhitespace(): void {
    while (this.offset < this.input.length) {
      if ((this.offset & 255) === 0) this.checkDeadline();
      const code = this.input.charCodeAt(this.offset);
      if (code !== 0x20 && code !== 0x09 && code !== 0x0a && code !== 0x0d) return;
      this.offset += 1;
    }
  }

  private expect(token: string): void {
    if (!this.consume(token)) throw new Error(`Expected ${token}.`);
  }

  private consume(token: string): boolean {
    if (this.input[this.offset] !== token) return false;
    this.offset += 1;
    return true;
  }
}

function stableStringify(value: unknown): string {
  const budget = {
    remaining: MaximumReplayJsonNodes, deadline: performance.now() + MaximumCanonicalMilliseconds,
  };
  const fingerprint = JSON.stringify(sortJsonValue(value, 0, budget));
  if (performance.now() > budget.deadline) {
    throw contractError('runtime_event_budget_exceeded', 'Copilot 事件超过处理时间预算。');
  }
  return fingerprint;
}

function isValidPendingServerRelayRun(value: unknown): value is CopilotPendingServerRelayRun {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<CopilotPendingServerRelayRun>;
  if (candidate.version !== 1
    || !isBoundedIdentifier(candidate.runId, 128)
    || !isBoundedIdentifier(candidate.sessionId, 128)
    || typeof candidate.database !== 'string'
    || candidate.database.length > 128
    || !/^[0-9a-f]{64}$/u.test(candidate.requestFingerprint ?? '')
    || (candidate.mode !== 'read-only' && candidate.mode !== 'read-write')
    || !isCloudMode(candidate.cloudMode)
    || typeof candidate.createdAtUtc !== 'string'
    || !Number.isFinite(Date.parse(candidate.createdAtUtc))) {
    return false;
  }
  return candidate.model === undefined
    || (typeof candidate.model === 'string' && candidate.model.length <= 256);
}

function isBoundedIdentifier(value: unknown, maxLength: number): value is string {
  return typeof value === 'string'
    && value.length > 0
    && value.length <= maxLength
    && /^[A-Za-z0-9_-]+$/u.test(value);
}

function isCloudMode(value: unknown): value is CopilotPendingRunCloudMode {
  return value === 'sql_assist'
    || value === 'sql_analyze'
    || value === 'db_maintenance'
    || value === 'knowledge_qa';
}

function getSessionStorage(): CopilotPendingRunStorage | undefined {
  if (typeof globalThis === 'undefined' || !('sessionStorage' in globalThis)) return undefined;
  try {
    return globalThis.sessionStorage;
  } catch {
    return undefined;
  }
}

function sortJsonValue(value: unknown, depth: number, budget: { remaining: number; deadline: number }): unknown {
  budget.remaining -= 1;
  if (depth > MaximumReplayJsonDepth || budget.remaining < 0 || performance.now() > budget.deadline) {
    throw contractError('runtime_event_budget_exceeded', 'Copilot 事件超过深度、节点或处理时间预算。');
  }
  if (Array.isArray(value)) {
    if (value.length > budget.remaining) {
      throw contractError('runtime_event_budget_exceeded', 'Copilot 事件超过节点预算。');
    }
    return value.map((item) => sortJsonValue(item, depth + 1, budget));
  }
  if (value === null || typeof value !== 'object') return value;

  const record = value as Record<string, unknown>;
  const keys = Object.keys(record);
  if (keys.length > budget.remaining) throw contractError('runtime_event_budget_exceeded', 'Copilot 事件超过节点预算。');
  return Object.fromEntries(
    keys.sort().map((key) => [key, sortJsonValue(record[key], depth + 1, budget)]),
  );
}

export function createCopilotRunId(): string {
  const id = globalThis.crypto?.randomUUID?.();
  if (id) return `run_${id.replaceAll('-', '')}`;
  return `run_${Date.now().toString(36)}_${Math.random().toString(36).slice(2)}`;
}

function throwIfAborted(signal: AbortSignal): void {
  if (!signal.aborted) return;
  if (signal.reason !== undefined) throw signal.reason;
  throw new DOMException('The operation was aborted.', 'AbortError');
}

function contractError(code: string, message: string): CopilotRuntimeContractError {
  return new CopilotRuntimeContractError(code, message);
}
