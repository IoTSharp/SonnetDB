/** 固定的三种公开查询命令观察阶段。 */
export type QueryObservationPhase = 'current-statement' | 'exact-selection' | 'explain';

/** 安全投影无法确认输入时使用固定 unknown 值。 */
export type QueryObservationMatch = boolean | 'unknown';

/** 不包含错误正文、SQL、连接名称或 token 的阶段观察。 */
export interface QueryHistoryPhaseObservation {
  phase: QueryObservationPhase;
  publicHistory: {
    entryCount: number | 'unknown';
    currentLabelMatches: QueryObservationMatch;
    currentContextMatches: QueryObservationMatch;
  };
  notifications: { count: number; queryFailed: number; other: number; unknown: number; overflow: boolean };
}

/** 公开标签和固定通知类别只能提供观察，不能证明持久化确认或失败因果。 */
export interface QueryHistoryObservationSnapshot {
  phases: QueryHistoryPhaseObservation[];
  notificationCount: number;
  notificationOverflow: boolean;
  notificationObservationUnknown: boolean;
  publicLabelsOnly: true;
  persistenceAcknowledgementVerified: false;
  causalityEstablished: false;
}

const Phases: readonly QueryObservationPhase[] = ['current-statement', 'exact-selection', 'explain'];
const MaximumNotifications = 16;
const MaximumHistoryEntries = 50;

/** 对最多三阶段、十六个通知及五十条公开标签作有界且不执行 getter 的观察。 */
export class QueryHistoryObservation {
  private readonly phases: QueryHistoryPhaseObservation[] = [];
  private active: QueryHistoryPhaseObservation | undefined;
  private observingQuery = false;
  private notificationCount = 0;
  private notificationOverflow = false;
  private notificationObservationUnknown = false;

  /** 使用宿主既有的绝对墙钟截止时间，不延长原执行窗口。 */
  public constructor(private readonly deadline: number) { }

  /** 按固定顺序打开一次查询观察；非法阶段、重复或过期输入均不获准。 */
  public begin(phase: unknown): boolean {
    if (!this.withinDeadline() || this.active || this.phases.length >= Phases.length
      || phase !== Phases[this.phases.length]) return false;
    this.active = {
      phase: Phases[this.phases.length],
      publicHistory: unknownHistory(),
      notifications: { count: 0, queryFailed: 0, other: 0, unknown: 0, overflow: false },
    };
    this.phases.push(this.active);
    this.observingQuery = true;
    return true;
  }

  /** 只分类同步通知调用；未知对象不会被转换为字符串或读取属性。 */
  public observeError(message: unknown): void {
    if (!this.active || !this.observingQuery || !this.withinDeadline()) return;
    if (this.notificationCount >= MaximumNotifications) {
      this.notificationOverflow = true;
      this.active.notifications.overflow = true;
      return;
    }
    this.notificationCount += 1;
    this.active.notifications.count += 1;
    if (typeof message !== 'string') this.active.notifications.unknown += 1;
    else if (message.startsWith('SonnetDB query failed:')) this.active.notifications.queryFailed += 1;
    else this.active.notifications.other += 1;
  }

  /** 观察后原样转发公开 API；不检查、等待或替换其返回值与拒绝。 */
  public forwardError<T>(message: unknown, invokeOriginal: () => T): T {
    try { this.observeError(message); }
    catch { this.notificationObservationUnknown = true; }
    return invokeOriginal();
  }

  /** 查询命令已返回，停止把后续 history UI 通知计作该查询通知。 */
  public endQuery(): void {
    this.observingQuery = false;
  }

  /** 记录单次公开快照；未打开 picker、非法输入或观察异常保持 unknown。 */
  public observeHistory(items: unknown, expectedLabel: unknown, expectedContext: unknown): void {
    if (!this.active) return;
    this.observingQuery = false;
    try {
      if (this.withinDeadline()) {
        this.active.publicHistory = this.projectHistory(items, expectedLabel, expectedContext);
      }
    } catch {
      this.active.publicHistory = unknownHistory();
    } finally {
      this.active = undefined;
    }
  }

  /** 返回固定字段的副本；不保留任何观察输入或任意错误文本。 */
  public snapshot(): QueryHistoryObservationSnapshot {
    return {
      phases: this.phases.map((phase) => ({
        phase: phase.phase,
        publicHistory: { ...phase.publicHistory },
        notifications: { ...phase.notifications },
      })),
      notificationCount: this.notificationCount,
      notificationOverflow: this.notificationOverflow,
      notificationObservationUnknown: this.notificationObservationUnknown,
      publicLabelsOnly: true,
      persistenceAcknowledgementVerified: false,
      causalityEstablished: false,
    };
  }

  private withinDeadline(): boolean {
    return Number.isFinite(this.deadline) && Date.now() < this.deadline;
  }

  private projectHistory(items: unknown, expectedLabel: unknown, expectedContext: unknown): QueryHistoryPhaseObservation['publicHistory'] {
    if (!Array.isArray(items) || typeof expectedLabel !== 'string' || expectedLabel.length > 100
      || typeof expectedContext !== 'string' || expectedContext.length > 256) return unknownHistory();
    const length = ownData(items, 'length');
    if (typeof length !== 'number' || !Number.isInteger(length) || length < 0 || length > MaximumHistoryEntries) return unknownHistory();
    let labelMatches: QueryObservationMatch = false;
    let contextMatches: QueryObservationMatch = false;
    // 最大 50 项且沿用宿主绝对截止时间；仅用比较退出，超时丢弃本次投影。
    for (let index = 0; index < length && index < MaximumHistoryEntries && this.withinDeadline(); index += 1) {
      const item = ownData(items, `${index}`);
      const label = ownData(item, 'label');
      if (typeof label !== 'string' || label.length > 100) {
        if (labelMatches !== true) labelMatches = 'unknown';
        if (contextMatches !== true) contextMatches = 'unknown';
        continue;
      }
      if (label !== expectedLabel) continue;
      labelMatches = true;
      const detail = ownData(item, 'detail');
      if (typeof detail !== 'string' || detail.length > 256) {
        if (contextMatches !== true) contextMatches = 'unknown';
      } else if (detail === expectedContext || detail.startsWith(`${expectedContext} / `)) {
        contextMatches = true;
      }
    }
    if (!this.withinDeadline()) return unknownHistory();
    return { entryCount: length, currentLabelMatches: labelMatches, currentContextMatches: contextMatches };
  }
}

function unknownHistory(): QueryHistoryPhaseObservation['publicHistory'] {
  return { entryCount: 'unknown', currentLabelMatches: 'unknown', currentContextMatches: 'unknown' };
}

function ownData(value: unknown, name: string): unknown {
  if (value === null || typeof value !== 'object') return undefined;
  const descriptor = Object.getOwnPropertyDescriptor(value, name);
  return descriptor && Object.prototype.hasOwnProperty.call(descriptor, 'value') ? descriptor.value : undefined;
}
