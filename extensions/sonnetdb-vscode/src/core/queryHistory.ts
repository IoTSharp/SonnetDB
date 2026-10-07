/** 与既有本地查询历史保持兼容的记录。 */
export interface QueryHistoryEntry {
  /** 记录标识符。 */
  id: string;
  /** 查询完成时的 Unix 毫秒时间戳。 */
  timestamp: number;
  /** 执行的 SQL 文本。 */
  sql: string;
  /** 查询所属连接的显示名称。 */
  connectionLabel?: string;
  /** 查询所属数据库名称。 */
  database?: string;
  /** 查询结果行数。 */
  rowCount: number;
  /** 查询耗时，单位毫秒。 */
  elapsedMs?: number;
  /** 查询结果是否包含错误。 */
  failed: boolean;
}

/** 查询历史所需的 Memento 最小持久化合同。 */
export interface QueryHistoryStorage {
  /** 读取记录，键不存在时返回指定默认值。 */
  get<T>(key: string, defaultValue: T): T;
  /** 持久化记录；返回的 Promise 完成后本次写入才算完成。 */
  update(key: string, value: unknown): PromiseLike<void>;
}

const QueryHistoryStorageKey = 'sonnetdb.queryHistory';
const MaximumHistoryEntries = 50;
const MaximumPendingWrites = 50;

/** 串行持久化查询历史，避免并发读改写覆盖和无界排队。 */
export class QueryHistoryStore {
  private tail: Promise<void> = Promise.resolve();
  private pendingWrites = 0;

  /** 使用现有宿主的 Memento 存储。 */
  public constructor(private readonly storage: QueryHistoryStorage) { }

  /** 排队写入记录；当前调用保留自己的失败，后续写入仍可继续。 */
  public append(entry: QueryHistoryEntry): Promise<void> {
    if (this.pendingWrites >= MaximumPendingWrites) {
      return Promise.reject(new Error('SonnetDB query history queue is full (50 pending writes).'));
    }
    const acceptedEntry = { ...entry };
    this.pendingWrites += 1;
    const write = this.tail.then(async () => {
      const history = this.storage.get<QueryHistoryEntry[]>(QueryHistoryStorageKey, []);
      await this.storage.update(QueryHistoryStorageKey, [acceptedEntry, ...history.slice(0, MaximumHistoryEntries - 1)]);
    });
    const observedWrite = write.finally(() => { this.pendingWrites -= 1; });
    this.tail = observedWrite.then(() => undefined, () => undefined);
    return observedWrite;
  }

  /** 等待调用时已接受的写入，再读取最新的至多 50 条历史快照。 */
  public async read(): Promise<QueryHistoryEntry[]> {
    await this.tail;
    return this.storage.get<QueryHistoryEntry[]>(QueryHistoryStorageKey, [])
      .slice(0, MaximumHistoryEntries)
      .map((entry) => ({ ...entry }));
  }
}
